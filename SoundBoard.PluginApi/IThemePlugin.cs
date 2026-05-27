using System.Collections.Generic;

namespace SoundBoard.PluginApi;

/// <summary>
/// A theme plugin packages one or more named <see cref="ThemePalette"/>s.
/// Each palette is a separately-selectable look (e.g. "Tomorrow", "Tomorrow
/// Night", "Tomorrow Night Eighties") backed by Avalonia style or resource
/// dictionaries compiled into the plugin DLL. Themes live in the host's
/// <c>Themes</c> folder (not <c>Plugins</c>) and the host's Appearance
/// dropdown lists every palette across every installed theme plugin.
///
/// <para>How resource URIs work: each plugin's compiled <c>.axaml</c>
/// resources are exposed via the <c>avares://&lt;AssemblyName&gt;/&lt;path&gt;</c>
/// scheme that Avalonia uses for embedded resources. The
/// <c>&lt;AssemblyName&gt;</c> must match your plugin DLL's
/// <c>AssemblyName</c> MSBuild property; the <c>&lt;path&gt;</c> is the
/// repo-relative path of the <c>&lt;AvaloniaResource&gt;</c> item in your
/// csproj.</para>
///
/// <para><b>A theme is a flat set of colours — no light/dark variants.</b>
/// Ship a plain <c>&lt;ResourceDictionary&gt;</c> of the host's named
/// semantic brushes (<c>PrimaryAccent</c>, <c>ContentBackground</c>,
/// <c>TextPrimary</c>, …) as <c>SolidColorBrush</c> entries. Do NOT wrap
/// them in <c>ThemeDictionaries</c> or split them into <c>Dark</c>/<c>Light</c>
/// blocks: the host has no user-facing light/dark toggle. It copies your
/// brushes into wherever Avalonia looks first so they always win, and it
/// derives the Avalonia chrome variant (used only for un-themed Fluent
/// controls — scrollbars, popups, focus rings) from your background's
/// luminance. You declare the colours; the host figures out the rest.
/// (Legacy themes that still use explicit <c>Dark</c>/<c>Light</c>
/// <c>ThemeDictionaries</c> blocks continue to load, but that form is no
/// longer recommended.)</para>
/// </summary>
public interface IThemePlugin : IPlugin
{
    /// <summary>
    /// Returns every selectable palette this plugin ships. Must return at
    /// least one entry; palette ids must be unique within this plugin
    /// (they don't need to be unique across plugins — the host combines
    /// the plugin id with the palette id to identify a selection).
    /// </summary>
    IEnumerable<ThemePalette> GetPalettes();
}

/// <summary>
/// One selectable look within a theme plugin.
/// </summary>
/// <param name="Id">Stable, plugin-scoped identifier (e.g. "night-eighties").
/// Persisted in the host's settings so the user's choice survives restarts;
/// must not change between versions of the same palette.</param>
/// <param name="Name">Display name shown in the host's theme dropdown
/// (e.g. "Tomorrow Night Eighties"). The host prefixes the plugin's name
/// so the user always sees which package a palette belongs to.</param>
/// <param name="ResourceUris">The <c>avares://</c> URIs of every style or
/// resource dictionary this palette contributes. Each is loaded via
/// <c>AvaloniaXamlLoader</c> and either added to <c>Application.Styles</c>
/// (if it loads as <c>IStyle</c>) or, for a flat <c>ResourceDictionary</c>
/// of brushes, applied by the host so the colours win regardless of the
/// active Avalonia variant.</param>
public sealed record ThemePalette(string Id, string Name, IEnumerable<string> ResourceUris);
