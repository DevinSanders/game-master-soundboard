using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using System;
using System.IO;
using System.Text.Json;

namespace SoundBoard.Core.Services;

/// <summary>
/// Reads and writes <see cref="AppSettings"/> to <c>settings.json</c> in the
/// user's local app-data folder. Treated as the single source of truth for
/// app-level configuration; mutations are made on <see cref="Current"/> and
/// flushed with <see cref="Save"/>.
/// </summary>
public interface ISettingsService
{
    /// <summary>The currently-loaded settings. Mutating fields is allowed;
    /// call <see cref="Save"/> to persist.</summary>
    AppSettings Current { get; }

    /// <summary>Write <see cref="Current"/> to disk.</summary>
    void Save();

    /// <summary>Reload from disk, replacing <see cref="Current"/>.</summary>
    void Load();
}

/// <inheritdoc cref="ISettingsService"/>
public class SettingsService : ISettingsService
{
    private readonly string _filePath;
    private AppSettings _current = new();

    // Serialises every Save / Load call. Phase R3: multiple callers
    // (Mixer's debounced LocalVolume + BridgeVolume saves, the Bus
    // Mixer's per-bus volume saves, Settings page directly calling
    // Save, Restart Now flushing) can all hit Save concurrently. Without
    // this lock, JsonSerializer.Serialize would walk the BridgeVolumes
    // dictionary while another writer mutates it — throwing
    // InvalidOperationException and leaving settings.json half-written.
    private readonly object _saveLock = new();

    public AppSettings Current => _current;

    public SettingsService()
    {
        Directory.CreateDirectory(AppPaths.Root);
        _filePath = AppPaths.SettingsFilePath;
        Load();
    }

    public void Save()
    {
        // Atomic write: serialise to settings.json.tmp, then rename over
        // settings.json. A crash mid-write leaves the previous valid
        // file in place rather than a half-written one that fails to
        // deserialise on the next launch (which previously caused Load
        // to swap in a fresh AppSettings and silently lose every setting,
        // including CurrentLibraryPath — the user would think their
        // library disappeared).
        //
        // Serialize under _saveLock so concurrent callers don't race
        // each other through JsonSerializer.Serialize (which walks the
        // BridgeVolumes Dictionary internally — that crashes if another
        // thread mutates it mid-walk). The lock is fine-grained: only
        // the serialize + rename runs inside it; mutations of Current
        // happen outside.
        var tmpPath = _filePath + ".tmp";
        try
        {
            lock (_saveLock)
            {
                var json = JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tmpPath, json);
                // File.Move with overwrite is atomic on every supported
                // filesystem we target (NTFS, APFS, ext4). The OS guarantees
                // the destination always resolves to either the old contents
                // or the new — never half-written.
                File.Move(tmpPath, _filePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings", "Error saving settings", ex);
            // Clean up the temp file if it's still around so we don't
            // accumulate detritus on repeated failures.
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }
    }

    public void Load()
    {
        try
        {
            lock (_saveLock)
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        _current = settings;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings", "Error loading settings", ex);
            _current = new AppSettings();
        }
    }
}
