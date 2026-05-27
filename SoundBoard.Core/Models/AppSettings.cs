using System;
using System.Collections.Generic;

namespace SoundBoard.Core.Models;

/// <summary>
/// Application-wide settings persisted to <c>settings.json</c> in the
/// user's local app-data folder. Distinct from the per-library SQLite
/// database — these fields configure the app shell (audio device, Discord
/// credentials, enabled plugins, current library path) rather than its
/// content.
/// </summary>
public class AppSettings
{
    // Appearance
    //
    // Theme is a single (SelectedThemeId, SelectedThemePaletteId) pair
    // covering both built-in and plugin themes. The host's built-ins
    // are encoded with PluginId = null and PaletteId =
    //   - "gm-light" → Game Master Light (variant Light)
    //   - "gm-dark"  → Game Master Dark  (variant Dark)
    // Plugin themes use real plugin/palette ids and are variant-free:
    // they declare a flat set of colours. The host derives Avalonia's
    // RequestedThemeVariant for the two built-ins directly from the
    // palette id, and for plugin themes from the theme's own background
    // luminance (for un-themed Fluent chrome only) — see ThemeService.

    /// <summary>Id of the active theme plugin, or <c>null</c> for the
    /// host's built-in (Light / Dark) themes. Pairs with
    /// <see cref="SelectedThemePaletteId"/> to identify exactly one
    /// palette inside one plugin. Themes are discovered in the Themes
    /// folder but selected here rather than via
    /// <see cref="EnabledPluginIds"/> — themes are a one-of-N choice,
    /// not an enable-list. Cleared on startup if the previously-selected
    /// plugin theme can no longer be loaded.</summary>
    public string? SelectedThemeId { get; set; }

    /// <summary>Id of the palette within <see cref="SelectedThemeId"/>'s
    /// plugin that's currently active. For host built-ins (where
    /// <see cref="SelectedThemeId"/> is null) this is <c>"gm-light"</c>
    /// or <c>"gm-dark"</c>. For plugin themes it's whatever id the plugin
    /// gave its palette. Defaults to <c>"gm-dark"</c> on first launch.</summary>
    public string? SelectedThemePaletteId { get; set; } = "gm-dark";

    // Audio Settings
    public string? PreferredOutputDeviceId { get; set; } // Device name or ID

    /// <summary>Main output (local-speaker) volume, persisted across
    /// launches. 0.0–2.0 linear gain, same range every other volume
    /// slider in the app uses. Read at startup into
    /// <c>MasterMixer.LocalVolume</c>; written via debounced save when
    /// the Mixer window's main-output slider moves.</summary>
    public float LocalVolume { get; set; } = 1.0f;

    /// <summary>Per-bridge volumes, keyed by the bridge plugin's
    /// <see cref="SoundBoard.PluginApi.IPlugin.Id"/>. Persisted so a
    /// Discord-bridge call at 50 % stays at 50 % across app restarts.
    /// Bridges absent from the dictionary default to unity (1.0). A
    /// bridge that was uninstalled keeps its entry — when reinstalled,
    /// the user's last level comes back automatically rather than
    /// resetting to 100 %.</summary>
    public Dictionary<string, float> BridgeVolumes { get; set; } = new();

    // Discord credentials previously lived here. They've moved into the
    // bridge.discord plugin's data folder (PluginDataPath/config.json) so
    // bridges can be added/removed without touching host settings.

    // Plugins
    public List<string> EnabledPluginIds { get; set; } = new();

    // Database
    public string? CurrentLibraryPath { get; set; }
}
