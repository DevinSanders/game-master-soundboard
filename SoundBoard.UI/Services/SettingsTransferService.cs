using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SoundBoard.UI.Services;

/// <summary>
/// Exports / imports the application's <see cref="AppSettings"/> as a JSON
/// document so users can move their preferences between machines (theme,
/// preferred audio device, Discord credentials, enabled plugin list, etc.).
///
/// <see cref="ImportSettingsAsync"/> applies the loaded values to the active
/// <see cref="ISettingsService"/> instance. Some settings (e.g. enabled
/// plugins, library path) require a restart to take effect — the caller
/// surfaces that to the user.
/// </summary>
public class SettingsTransferService : ISettingsTransferService
{
    private readonly ISettingsService _settings;

    public SettingsTransferService(ISettingsService settings)
    {
        _settings = settings;
    }

    public async Task ExportSettingsAsync(string filePath)
    {
        // Export a copy with the Discord bot token blanked out — settings
        // bundles are intended to be shareable across machines, and an
        // accidentally-shared export must not leak the token. Users who
        // genuinely want to move their bot setup re-enter the token after
        // importing on the destination machine.
        var src = _settings.Current;
        var sanitized = new AppSettings
        {
            SelectedThemeId = src.SelectedThemeId,
            SelectedThemePaletteId = src.SelectedThemePaletteId,
            PreferredOutputDeviceId = src.PreferredOutputDeviceId,
            EnabledPluginIds = src.EnabledPluginIds,
            CurrentLibraryPath = src.CurrentLibraryPath,
            // Discord credentials used to live here; they're now owned by
            // the bridge.discord plugin (PluginDataPath/config.json) and
            // are not transferred by host settings export.
        };
        var json = JsonSerializer.Serialize(sanitized, JsonOpts);
        await File.WriteAllTextAsync(filePath, json);
        Log.Info("Transfer", $"Exported settings to {filePath}");
    }

    public async Task<bool> ImportSettingsAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            if (loaded == null) return false;

            // Copy field-by-field rather than swapping the instance — anything
            // that already holds a reference to Settings.Current keeps seeing
            // the same object and updated values.
            var s = _settings.Current;
            s.SelectedThemeId = loaded.SelectedThemeId;
            s.SelectedThemePaletteId = loaded.SelectedThemePaletteId ?? "gm-dark";
            s.PreferredOutputDeviceId = loaded.PreferredOutputDeviceId;
            // Discord credentials are no longer part of host settings —
            // bridge.discord owns its own config.json under PluginDataPath.
            s.EnabledPluginIds = loaded.EnabledPluginIds ?? new();
            // Intentionally NOT importing CurrentLibraryPath — the source
            // machine's library path almost never makes sense on the target.
            // User can switch library separately via the Settings UI.

            _settings.Save();
            Log.Info("Transfer", $"Imported settings from {filePath}");
            return true;
        }
        catch (System.Exception ex)
        {
            Log.Error("Transfer", "Settings import failed", ex);
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
