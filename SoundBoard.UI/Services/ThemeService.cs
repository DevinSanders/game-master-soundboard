using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using SoundBoard.Core.Services;

namespace SoundBoard.UI.Services;

/// <summary>
/// Hot-swappable theme application. Plugin theme palettes can be applied
/// (and reverted) at runtime without an app restart: the service snapshots
/// the host's <see cref="Application.Resources"/> at startup, and on
/// every <see cref="ApplyTheme"/> call it restores that snapshot before
/// merging the new palette's resources / styles in. Avalonia's
/// <c>DynamicResource</c> bindings pick the new values up live.
///
/// <para>What gets snapshotted: the contents of every
/// <see cref="ResourceDictionary"/> registered under
/// <c>Application.Resources.ThemeDictionaries</c> (variant-scoped keys
/// like <c>PrimaryAccent[Dark]</c>) plus the top-level
/// <c>Application.Resources</c> entries. Styles added by plugin themes
/// are tracked separately so the service can <c>Styles.Remove</c> them
/// on theme change without touching host styles.</para>
///
/// <para><b>Why a service.</b> Pre-refactor the same code lived inline in
/// <see cref="App"/>'s <c>ApplySelectedTheme</c>. Moving it here lets
/// <see cref="ViewModels.SettingsViewModel"/> swap themes without a
/// restart — the SelectedTheme setter just calls
/// <see cref="ApplyTheme"/>. The "Restart Required" banner is now
/// reserved for plugin enable/disable changes that genuinely need an
/// app relaunch.</para>
/// </summary>
public interface IThemeService
{
    /// <summary>Snapshot the host's current resources / styles. Call
    /// exactly once at app startup BEFORE any plugin theme has been
    /// applied — otherwise the snapshot captures the plugin's overrides
    /// as if they were host defaults, and they can never be reverted.</summary>
    void Initialize();

    /// <summary>Restore the host snapshot, then merge the chosen
    /// (plugin, palette) into the live resources. Passing
    /// <c>null</c>/<c>null</c> means "host default" — restores the
    /// snapshot and applies nothing. If the plugin or palette can't be
    /// resolved (uninstalled, broken), the service logs a warning and
    /// reverts to the host default; the caller doesn't need to detect
    /// or guard against that case.</summary>
    void ApplyTheme(string? pluginId, string? paletteId);
}

public sealed class ThemeService : IThemeService
{
    /// <summary>Palette id of the host's built-in light theme. The only
    /// id whose name still encodes a variant — built-ins ARE the app's
    /// native light/dark, defined directly in App.axaml's
    /// ThemeDictionaries. Plugin themes never encode a variant; the host
    /// derives one from their colours (see <see cref="InferVariant"/>).</summary>
    private const string BuiltInLightPaletteId = "gm-light";

    private readonly IPluginService _pluginService;
    private readonly ISettingsService _settingsService;

    /// <summary>Snapshot of variant-scoped host resources keyed by
    /// theme variant. Each inner dict mirrors what
    /// <c>Application.Resources.ThemeDictionaries[variant]</c> contained
    /// at <see cref="Initialize"/> time.</summary>
    private Dictionary<ThemeVariant, Dictionary<object, object?>>? _hostThemeSnapshot;

    /// <summary>Snapshot of top-level keys directly on
    /// <c>Application.Resources</c> (non-variant resources like fonts).</summary>
    private Dictionary<object, object?>? _hostRootSnapshot;

    /// <summary>Styles we added on the last ApplyTheme call. Tracked so
    /// the next call can remove them without disturbing host-defined
    /// styles loaded from App.axaml.</summary>
    private readonly List<IStyle> _appliedThemeStyles = new();

    public ThemeService(IPluginService pluginService, ISettingsService settingsService)
    {
        _pluginService = pluginService;
        _settingsService = settingsService;
    }

    public void Initialize()
    {
        if (_hostThemeSnapshot != null) return;
        if (Application.Current is not { } app) return;

        _hostThemeSnapshot = new Dictionary<ThemeVariant, Dictionary<object, object?>>();
        foreach (var pair in app.Resources.ThemeDictionaries)
        {
            if (pair.Value is not ResourceDictionary dict) continue;
            var snap = new Dictionary<object, object?>();
            foreach (var key in dict.Keys.Cast<object>().ToList())
                snap[key] = dict[key];
            _hostThemeSnapshot[pair.Key] = snap;
        }

        _hostRootSnapshot = new Dictionary<object, object?>();
        foreach (var key in app.Resources.Keys.Cast<object>().ToList())
            _hostRootSnapshot[key] = app.Resources[key];
    }

    public void ApplyTheme(string? pluginId, string? paletteId)
    {
        if (Application.Current is not { } app) return;

        // Always restore to host defaults first — switching themes
        // means "remove any plugin-applied overrides, then apply the
        // new one." This also handles the "back to Default" case
        // (pluginId=null) automatically.
        RestoreHostSnapshot();

        if (string.IsNullOrEmpty(pluginId) || string.IsNullOrEmpty(paletteId))
        {
            // Built-in selection (or "no theme"): the host's own App.axaml
            // already holds the GM Light + GM Dark colours in its variant
            // dictionaries, so selecting a built-in is purely choosing
            // which Avalonia variant is live. This is the ONE place a
            // variant is named explicitly — the built-ins ARE the app's
            // native light/dark. Everything else (plugin themes) is
            // variant-free and inferred.
            app.RequestedThemeVariant = paletteId == BuiltInLightPaletteId
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
            return;
        }

        var themePlugin = _pluginService.LoadedPlugins
            .OfType<SoundBoard.PluginApi.IThemePlugin>()
            .FirstOrDefault(p => p.Id == pluginId);

        if (themePlugin == null)
        {
            Log.Warn("Theme", $"Selected theme plugin '{pluginId}' is missing or failed to load; reverting to default.");
            ClearPersistedSelection();
            return;
        }

        SoundBoard.PluginApi.ThemePalette? palette;
        try
        {
            palette = themePlugin.GetPalettes().FirstOrDefault(p => p.Id == paletteId);
        }
        catch (Exception ex)
        {
            Log.Error("Theme", $"Theme '{pluginId}' threw from GetPalettes(); reverting to default.", ex);
            ClearPersistedSelection();
            return;
        }

        if (palette == null)
        {
            Log.Warn("Theme", $"Palette '{paletteId}' not found in theme '{pluginId}'; reverting to default.");
            ClearPersistedSelection();
            return;
        }

        try
        {
            foreach (var uri in palette.ResourceUris)
            {
                var loaded = AvaloniaXamlLoader.Load(new Uri(uri));
                if (loaded is IStyle style)
                {
                    app.Styles.Add(style);
                    _appliedThemeStyles.Add(style);
                }
                else if (loaded is ResourceDictionary dict)
                {
                    MergeThemeResources(app.Resources, dict);
                    // A theme declares only colours. The host derives which
                    // Avalonia variant to run — purely so un-themed Fluent
                    // chrome (scrollbars, combo popups, focus rings, text
                    // selection) matches — from the theme's own background
                    // luminance. The theme author never picks light vs dark.
                    app.RequestedThemeVariant = InferVariant(dict);
                }
            }
            Log.Info("Theme", $"Applied palette '{palette.Name}' ({palette.Id}) from theme '{themePlugin.Name}' ({themePlugin.Id}).");
        }
        catch (Exception ex)
        {
            Log.Error("Theme", $"Palette '{paletteId}' from '{pluginId}' failed to apply; reverting to default.", ex);
            RestoreHostSnapshot();
            ClearPersistedSelection();
        }
    }

    private void ClearPersistedSelection()
    {
        _settingsService.Current.SelectedThemeId = null;
        _settingsService.Current.SelectedThemePaletteId = null;
        _settingsService.Save();
    }

    /// <summary>Roll <see cref="Application.Resources"/> back to the
    /// startup snapshot AND remove styles added by the previous theme.
    /// No-op when <see cref="Initialize"/> hasn't been called yet (the
    /// snapshot exists exactly to give us "host defaults" to revert to).</summary>
    private void RestoreHostSnapshot()
    {
        if (Application.Current is not { } app) return;

        // Pull styles we added; leave host styles loaded from App.axaml.
        foreach (var style in _appliedThemeStyles)
        {
            try { app.Styles.Remove(style); }
            catch (Exception ex) { Log.Warn("Theme", "Style.Remove threw during theme switch", ex); }
        }
        _appliedThemeStyles.Clear();

        if (_hostThemeSnapshot == null || _hostRootSnapshot == null) return;

        // Restore each variant dictionary. We delete plugin-added keys
        // (those NOT in the snapshot) and then overwrite remaining
        // entries with the snapshot's original values. Order matters:
        // delete first so any binding pointing at a plugin-added key
        // sees the key disappear (and falls through), then overwrite
        // so binding listeners get notified of the rolled-back value.
        foreach (var pair in _hostThemeSnapshot)
        {
            if (!app.Resources.ThemeDictionaries.TryGetValue(pair.Key, out var provider) ||
                provider is not ResourceDictionary dict) continue;

            var currentKeys = dict.Keys.Cast<object>().ToList();
            foreach (var key in currentKeys)
            {
                if (!pair.Value.ContainsKey(key)) dict.Remove(key);
            }
            foreach (var entry in pair.Value)
            {
                dict[entry.Key] = entry.Value!;
            }
        }

        // Same for top-level (non-variant) entries.
        var currentRootKeys = app.Resources.Keys.Cast<object>().ToList();
        foreach (var key in currentRootKeys)
        {
            if (!_hostRootSnapshot.ContainsKey(key)) app.Resources.Remove(key);
        }
        foreach (var entry in _hostRootSnapshot)
        {
            app.Resources[entry.Key] = entry.Value!;
        }
    }

    /// <summary>Merge a plugin theme's <see cref="ResourceDictionary"/> into
    /// <see cref="Application.Resources"/>. The naïve approach — adding the
    /// plugin dict to <c>MergedDictionaries</c> — silently fails for any key
    /// the host defined inside its own <c>ThemeDictionaries</c>, because
    /// Avalonia's <c>DynamicResource</c> lookup checks the OWNING resource
    /// dictionary's <c>ThemeDictionaries</c> for the active variant before
    /// descending into <c>MergedDictionaries</c>. The host's variant-scoped
    /// brushes therefore always win.
    ///
    /// <para><b>Flat themes (the model).</b> A theme is just a flat
    /// <see cref="ResourceDictionary"/> of brushes — no
    /// <c>ThemeDictionaries</c>, no Dark/Light split. We copy those keys
    /// into EVERY host variant dictionary, so the theme renders identically
    /// regardless of which Avalonia variant happens to be active. The app
    /// genuinely doesn't care whether it's "light" or "dark"; it just
    /// renders the colours the theme specifies.</para>
    ///
    /// <para><b>Legacy variant-scoped themes.</b> Themes built against the
    /// older contract (with explicit <c>Dark</c>/<c>Light</c> blocks) still
    /// work: each block is copied into the matching host variant dict.</para></summary>
    private static void MergeThemeResources(IResourceDictionary target, ResourceDictionary source)
    {
        // Legacy path: explicit variant blocks → matching host variant dict.
        foreach (var pair in source.ThemeDictionaries)
        {
            var variant = pair.Key;
            if (pair.Value is not ResourceDictionary pluginVariantDict)
                continue;

            if (!target.ThemeDictionaries.TryGetValue(variant, out var hostProvider) ||
                hostProvider is not ResourceDictionary hostVariantDict)
            {
                hostVariantDict = new ResourceDictionary();
                target.ThemeDictionaries[variant] = hostVariantDict;
            }

            foreach (var key in pluginVariantDict.Keys.Cast<object>().ToList())
            {
                hostVariantDict[key] = pluginVariantDict[key];
            }
        }

        // Flat path: top-level keys are the variant-free authoring model.
        // Fan them out into every host variant dictionary so they win the
        // DynamicResource lookup no matter which variant is active, and
        // also keep them at top-level for any non-brush resource.
        var flatKeys = source.Keys.Cast<object>().ToList();
        if (flatKeys.Count > 0)
        {
            foreach (var variantKey in target.ThemeDictionaries.Keys.Cast<ThemeVariant>().ToList())
            {
                if (target.ThemeDictionaries[variantKey] is not ResourceDictionary hostVariantDict)
                    continue;
                foreach (var key in flatKeys)
                    hostVariantDict[key] = source[key];
            }
            foreach (var key in flatKeys)
                target[key] = source[key];
        }
    }

    /// <summary>Pick the Avalonia <see cref="ThemeVariant"/> that best
    /// matches a flat theme's surfaces, so un-themed Fluent control chrome
    /// (scrollbars, combo-box popups, focus rings, text selection) renders
    /// in a brightness that agrees with the theme. Derived from the
    /// theme's background luminance — the author declares nothing.
    /// Falls back to Dark (the GM-tool default) when no background brush
    /// is present.</summary>
    private static ThemeVariant InferVariant(ResourceDictionary themeDict)
    {
        foreach (var key in new[] { "ContentBackground", "PanelBackground1", "SidebarBackground" })
        {
            if (TryGetBrushColor(themeDict, key, out var color))
                return IsLight(color) ? ThemeVariant.Light : ThemeVariant.Dark;
        }
        return ThemeVariant.Dark;
    }

    private static bool TryGetBrushColor(ResourceDictionary dict, string key, out Color color)
    {
        color = default;
        // Flat theme: brush is a top-level entry.
        if (dict.TryGetValue(key, out var v) && v is ISolidColorBrush b)
        {
            color = b.Color;
            return true;
        }
        // Legacy variant-scoped theme: look inside any variant block.
        foreach (var pair in dict.ThemeDictionaries)
        {
            if (pair.Value is ResourceDictionary vd && vd.TryGetValue(key, out var v2) && v2 is ISolidColorBrush b2)
            {
                color = b2.Color;
                return true;
            }
        }
        return false;
    }

    /// <summary>sRGB relative luminance &gt; 0.5 ⇒ a light surface.</summary>
    private static bool IsLight(Color c)
        => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 > 0.5;
}
