using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Services;
using SoundBoard.PluginApi;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Threading.Tasks;
using NAudio.Wave;
using SoundBoard.UI.Services;
using System.Linq;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Backs the Settings window — output device selection, library
/// import/export, libraries folder management, Discord credentials, and
/// the installed-plugins list with enable toggles. Mutations persist
/// through <see cref="ISettingsService"/>. Audio device and theme
/// changes hot-swap at runtime (see <see cref="LocalAudioPlayer.Init"/>
/// and <see cref="IThemeService.ApplyTheme"/>). The "Restart Required"
/// banner is reserved for plugin enable/disable changes — those load
/// assemblies into ALCs at startup and can't be undone without a
/// process relaunch.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly LocalAudioPlayer _localPlayer;
    private readonly IPluginService _pluginService;
    private readonly IPluginInstallerService _pluginInstaller;
    private readonly IFileService _fileService;
    private readonly IThemeService _themeService;
    private readonly ISoundBoardDbContextFactory _dbFactory;
    private readonly MasterMixer _masterMixer;
    private readonly ISamplerChainService _samplerChain;
    private readonly SidechainRegistry _sidechainRegistry;
    private readonly IAudioPlaybackEngine _playbackEngine;

    // ── Appearance ────────────────────────────────────────────
    
    [ObservableProperty]
    private string? _currentLibraryPath;

    // ── Appearance ────────────────────────────────────────────
    
    [ObservableProperty]
    private ObservableCollection<PluginItemViewModel> _plugins = new();

    [ObservableProperty]
    private bool _restartRequired;

    // ── Appearance ────────────────────────────────────────────

    /// <summary>Palette ids identifying the two host-built-in themes in
    /// <see cref="AppSettings.SelectedThemePaletteId"/>.
    /// <see cref="ThemeOption.PluginId"/> is <c>null</c> for both. The
    /// built-ins ARE the app's native light/dark (defined in App.axaml's
    /// ThemeDictionaries); plugin themes are variant-free and the host
    /// derives a variant from their colours — see
    /// <see cref="Services.ThemeService"/>.</summary>
    public const string BuiltInLightPaletteId = "gm-light";
    public const string BuiltInDarkPaletteId  = "gm-dark";

    /// <summary>One row in the theme dropdown. For the host-default
    /// entries ("Game Master Light" / "Game Master Dark"),
    /// <see cref="PluginId"/> is null and <see cref="PaletteId"/> is one
    /// of <see cref="BuiltInLightPaletteId"/> / <see cref="BuiltInDarkPaletteId"/>.
    /// For plugin-provided themes both ids identify the
    /// (plugin, palette) pair. <c>IsFailed</c> is set when the theme
    /// plugin loaded with errors — the row stays visible so the user
    /// sees the package is broken rather than having it silently vanish.</summary>
    public sealed record ThemeOption(string? PluginId, string? PaletteId, string Name, bool IsFailed = false);

    [ObservableProperty]
    private ObservableCollection<ThemeOption> _availableThemes = new();

    private ThemeOption? _selectedTheme;
    public ThemeOption? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetProperty(ref _selectedTheme, value)) return;
            if (value == null) return;

            var newPluginId  = value.PluginId;
            var newPaletteId = value.PaletteId;
            bool changed =
                _settingsService.Current.SelectedThemeId        != newPluginId  ||
                _settingsService.Current.SelectedThemePaletteId != newPaletteId;

            if (!changed) return;

            // Persist the (plugin, palette) pair.
            _settingsService.Current.SelectedThemeId        = newPluginId;
            _settingsService.Current.SelectedThemePaletteId = newPaletteId;
            _settingsService.Save();

            // Apply the theme. ThemeService owns the Avalonia variant:
            // built-in selections (null, "gm-light"/"gm-dark") map to
            // Light/Dark directly; plugin themes are variant-free and the
            // service derives the variant from the theme's own background
            // colour (for un-themed Fluent chrome only).
            _themeService.ApplyTheme(newPluginId, newPaletteId);
        }
    }

    // ── Audio Output ──────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<string> _audioDevices = new();

    private string? _selectedAudioDevice;
    public string? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            Log.Debug("Settings", $"SelectedAudioDevice setter: {value ?? "NULL"}");
            if (SetProperty(ref _selectedAudioDevice, value))
            {
                // LocalAudioPlayer.Init replaces the underlying IWavePlayer
                // on the new device, keeping the same MasterMixer source —
                // hot-swap, no restart needed. Anything currently playing
                // continues seamlessly on the new device.
                UpdateLocalPlayerDevice(value);
            }
        }
    }

    private void UpdateLocalPlayerDevice(string? deviceName)
    {
        Log.Debug("Settings", $"Updating audio device to: {deviceName ?? "NULL"}");
        if (string.IsNullOrEmpty(deviceName) || deviceName == "Default System Output")
        {
            _localPlayer.PreferredDeviceId = null;
            _settingsService.Current.PreferredOutputDeviceId = null;
        }
        else
        {
            var device = LocalAudioPlayer.GetDevices().FirstOrDefault(d => d.Name == deviceName);
            if (device != null)
            {
                Log.Debug("Settings", $"Resolved name '{deviceName}' to ID: {device.Id}");
                _localPlayer.PreferredDeviceId = device.Id;
                _settingsService.Current.PreferredOutputDeviceId = device.Id;
            }
            else
            {
                Log.Warn("Settings", $"Failed to resolve audio device name '{deviceName}' to an ID");
            }
        }
        _settingsService.Save();
        _localPlayer.Init();
    }

    // ── Bridges ───────────────────────────────────────────────
    //
    // The host's Settings page renders one card per loaded
    // IAudioBridgePlugin in the "Bridges" section. The plugin owns its
    // own settings UI via CreateSettingsControl, so the host VM just
    // exposes the list of bridges + their display metadata; the actual
    // controls come from the plugin at view-construction time.

    /// <summary>Bridges currently loaded. Empty when no bridge plugin is
    /// installed — the view then renders a hint pointing the user at
    /// the plugin catalog.</summary>
    public System.Collections.Generic.IReadOnlyList<IAudioBridgePlugin> LoadedBridges
        => _pluginService.BridgePlugins.ToArray();

    // ── Data import/export ────────────────────────────────────────────────

    private readonly ILibraryTransferService _libraryTransfer;
    private readonly ISettingsTransferService _settingsTransfer;
    private readonly IWindowManagerService _windowManager;
    private readonly LibraryManagerService _libraryManager;

    [ObservableProperty]
    private string _dataStatus = "";

    public System.Collections.Generic.IReadOnlyList<LibraryManagerService.LibraryEntry> AvailableLibraries
        => _libraryManager.ListLibraries();

    public SettingsViewModel(ISettingsService settingsService, LocalAudioPlayer localPlayer,
        IPluginService pluginService, IFileService fileService,
        ILibraryTransferService libraryTransfer, ISettingsTransferService settingsTransfer,
        IWindowManagerService windowManager,
        LibraryManagerService libraryManager,
        IThemeService themeService,
        IPluginInstallerService pluginInstaller,
        ISoundBoardDbContextFactory dbFactory,
        MasterMixer masterMixer,
        ISamplerChainService samplerChain,
        SidechainRegistry sidechainRegistry,
        IAudioPlaybackEngine playbackEngine)
    {
        _settingsService = settingsService;
        _localPlayer = localPlayer;
        _pluginService = pluginService;
        _pluginInstaller = pluginInstaller;
        _fileService = fileService;
        _libraryTransfer = libraryTransfer;
        _settingsTransfer = settingsTransfer;
        _windowManager = windowManager;
        _libraryManager = libraryManager;
        _themeService = themeService;
        _dbFactory = dbFactory;
        _masterMixer = masterMixer;
        _samplerChain = samplerChain;
        _sidechainRegistry = sidechainRegistry;
        _playbackEngine = playbackEngine;

        LoadAudioDevices();
        LoadPlugins();
        LoadThemes();
        LoadBuses();
        _currentLibraryPath = _settingsService.Current.CurrentLibraryPath;
    }

    /// <summary>Create a new empty library in the AppData Libraries folder
    /// (no file picker) and switch to it. Called from the Settings view's
    /// "Create New Library" handler after the user confirms a name.</summary>
    public void CreateLibrary(string name)
    {
        try
        {
            var path = _libraryManager.ReserveLibraryPath(name);
            _settingsService.Current.CurrentLibraryPath = path;
            _settingsService.Save();
            CurrentLibraryPath = path;
            DataStatus = $"Creating library '{LibraryManagerService.SanitizeName(name)}' and restarting…";
            AppRestart.Restart();
        }
        catch (Exception ex)
        {
            DataStatus = $"Could not create library: {ex.Message}";
        }
    }

    /// <summary>Switch the active library to the user's pick from the list dialog.</summary>
    public void OpenLibrary(LibraryManagerService.LibraryEntry entry)
    {
        _settingsService.Current.CurrentLibraryPath = entry.Path;
        _settingsService.Save();
        CurrentLibraryPath = entry.Path;
        DataStatus = $"Switching to '{entry.Name}' and restarting…";
        AppRestart.Restart();
    }

    /// <summary>Relaunch the app immediately. Bound to the "Restart Now"
    /// button that appears when <see cref="RestartRequired"/> is true —
    /// theme switches and plugin toggles only take effect on a fresh
    /// startup (resources merge / DLLs probe), so the user needs a
    /// one-click way to apply their changes.</summary>
    [RelayCommand]
    private void RestartNow()
    {
        // Best-effort save in case any debounced settings are still in
        // flight when the user clicks.
        try { _settingsService.Save(); } catch (Exception ex) { Log.Warn("Settings", "Save before restart failed", ex); }
        AppRestart.Restart();
    }

    [RelayCommand]
    private async Task ExportLibraryAsync()
    {
        var path = await _fileService.SaveFileDialogAsync(
            "Export Library", "library-export.json", new[] { "*.json" });
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            await _libraryTransfer.ExportLibraryAsync(path);
            DataStatus = $"Library exported to {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            DataStatus = $"Library export failed: {ex.Message}";
        }
    }

    /// <summary>Factory for the options dialog VM — keeps IFileService private
    /// on this view-model while still letting the code-behind hand the
    /// dialog its dependencies.</summary>
    public ImportOptionsViewModel CreateImportOptionsVm(string sourceFile) =>
        new(sourceFile, _fileService);

    /// <summary>Surfaces the JSON file picker for the SettingsView import flow.</summary>
    public Task<System.Collections.Generic.IEnumerable<string>> PickImportFileAsync() =>
        _fileService.OpenFileDialogAsync("Import Library", new[] { "*.json" });

    /// <summary>Called from the SettingsView code-behind after the user picks
    /// a JSON export and configures import options. Runs the import, refreshes
    /// open VMs (for merge) or schedules a restart (for new library).</summary>
    public async Task RunImportAsync(string sourceFile, ImportOptions options)
    {
        DataStatus = "Importing library…";
        try
        {
            var result = await _libraryTransfer.ImportLibraryAsync(sourceFile, options);

            var summary = $"{result.SuccessfullyImported.Count} track(s), " +
                          $"{result.PresetsImported} preset(s), " +
                          $"{result.PlaylistsImported} playlist(s), " +
                          $"{result.ShortcutsImported} page(s)";
            if (result.MissingFiles.Count > 0)
                summary += $". {result.MissingFiles.Count} audio file(s) couldn't be located — add a search directory and re-run, or re-import those files manually";

            if (options.Mode == ImportMode.NewLibrary && result.CreatedLibraryPath != null)
            {
                _settingsService.Current.CurrentLibraryPath = result.CreatedLibraryPath;
                _settingsService.Save();
                CurrentLibraryPath = result.CreatedLibraryPath;
                DataStatus = $"Imported into new library '{options.NewLibraryName}'. Restarting… ({summary})";
                AppRestart.Restart();
                return;
            }

            // Merge mode: tell the open windows to reload from the DB so the
            // user sees the new data without restarting.
            DataStatus = $"Imported into the current library — {summary}.";
            CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Send(
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default,
                new SoundBoard.UI.Messages.LibraryRefreshedMessage());
        }
        catch (Exception ex)
        {
            DataStatus = $"Import failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        var path = await _fileService.SaveFileDialogAsync(
            "Export Settings", "settings-export.json", new[] { "*.json" });
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            await _settingsTransfer.ExportSettingsAsync(path);
            DataStatus = $"Settings exported to {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            DataStatus = $"Settings export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        var paths = await _fileService.OpenFileDialogAsync("Import Settings", new[] { "*.json" });
        var path = paths.FirstOrDefault();
        if (string.IsNullOrEmpty(path)) return;

        var ok = await _settingsTransfer.ImportSettingsAsync(path);
        if (!ok)
        {
            DataStatus = "Settings import failed — see logs.";
            return;
        }
        // Apply everything that can hot-swap: audio device
        // (LocalAudioPlayer.Init) and theme (ThemeService, which also
        // sets the Avalonia variant). Plugin enable/disable in the
        // imported settings still needs a restart because plugins are
        // loaded into ALCs at startup.
        UpdateLocalPlayerDevice(
            LocalAudioPlayer.GetDevices()
                .FirstOrDefault(d => d.Id == _settingsService.Current.PreferredOutputDeviceId)?.Name
            ?? "Default System Output");
        _themeService.ApplyTheme(
            _settingsService.Current.SelectedThemeId,
            _settingsService.Current.SelectedThemePaletteId);
        // Refresh the dropdown's selection — the imported settings may
        // point at a different built-in or plugin theme.
        LoadThemes();
        // Discord bot config moved into the bridge.discord plugin's own
        // data folder — see DiscordBridgePlugin / config.json. The host
        // no longer tracks it.

        // Plugin enable list is the only thing left that needs a restart.
        // If the import didn't touch EnabledPluginIds the user could
        // skip the restart; checking that is more bookkeeping than the
        // banner is worth, so we flag conservatively and the user can
        // ignore it.
        RestartRequired = true;
        DataStatus = "Settings imported. Restart for plugin list changes.";
    }


    private void LoadPlugins()
    {
        Plugins.Clear();
        var enabledIds = _settingsService.Current.EnabledPluginIds;

        // Themes live in their own picker (LoadThemes / SelectedTheme),
        // not in the per-plugin enable list, so they're filtered out here.
        foreach (var meta in _pluginService.AvailablePlugins.Where(p => !p.IsTheme))
        {
            var vm = new PluginItemViewModel(meta, enabledIds.Contains(meta.Id));
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(PluginItemViewModel.IsEnabledInSettings))
                {
                    UpdatePluginStatus(vm);
                }
            };
            Plugins.Add(vm);
        }
    }

    // ── Plugin installer (zip drop) ──────────────────────────────────

    /// <summary>Latest install attempt's status — shown beneath the
    /// drop-zone so the user sees "X.zip — installed (Restart to
    /// activate)" or "Y.zip — failed: reason". Multi-line so a batch
    /// of dropped zips all surface their outcomes.</summary>
    [ObservableProperty]
    private string _pluginInstallStatus = "";

    /// <summary>True while one or more zips are being processed. Drives
    /// the drop-zone's "busy" visual state.</summary>
    [ObservableProperty]
    private bool _isInstallingPlugins;

    /// <summary>Browse-button counterpart to the drop zone: opens a
    /// multi-select file picker filtered to <c>.zip</c> and returns the
    /// picked paths (empty if the user cancelled). The view passes the
    /// result to <see cref="InstallPluginsFromZipsAsync"/>.</summary>
    public async Task<IReadOnlyList<string>> PickPluginZipsAsync()
    {
        var paths = await _fileService.OpenFileDialogAsync("Select plugin .zip files", new[] { "*.zip" });
        return paths.ToList();
    }

    /// <summary>Install every <c>.zip</c> at <paramref name="zipPaths"/>
    /// sequentially. Multi-zip drops are common (the user might pull
    /// several plugin releases out of a downloads folder), so we
    /// process them in order and accumulate status. Each install is
    /// independent — one zip's failure doesn't stop the others. Sets
    /// <see cref="RestartRequired"/> if at least one install succeeded.</summary>
    public async Task InstallPluginsFromZipsAsync(System.Collections.Generic.IEnumerable<string> zipPaths)
    {
        var paths = zipPaths.ToList();
        if (paths.Count == 0) return;

        IsInstallingPlugins = true;
        try
        {
            var lines = new System.Text.StringBuilder();
            bool anyThemeSuccess = false;
            bool anyNonThemeSuccess = false;
            foreach (var path in paths)
            {
                var result = await _pluginInstaller.InstallFromZipAsync(path);
                if (result.Success)
                {
                    // Action descriptor tells the user whether this was
                    // an upgrade of their existing plugin, an alongside
                    // install (a different publisher claiming the same
                    // id), or a brand-new plugin. Without it, "✓ X
                    // installed" hides the lineage decision and the
                    // user can't tell if they just replaced their own
                    // work or got a competitor's copy.
                    var actionDescriptor = result.Action switch
                    {
                        SoundBoard.Core.Services.PluginInstallAction.Replaced
                            => $" (replaces v{result.ReplacedVersion ?? "?"})",
                        SoundBoard.Core.Services.PluginInstallAction.InstalledAlongside
                            => $" (installed alongside {result.SideBySideWith}/{result.PluginId})",
                        _ => "",
                    };

                    if (result.IsTheme)
                    {
                        lines.AppendLine($"✓ {result.ZipFileName} — theme '{result.PluginName}' installed{actionDescriptor} (ready to pick under Theme).");
                        anyThemeSuccess = true;
                    }
                    else
                    {
                        lines.AppendLine($"✓ {result.ZipFileName} — plugin '{result.PluginName}' installed{actionDescriptor} (restart to enable).");
                        anyNonThemeSuccess = true;
                    }
                }
                else
                {
                    lines.AppendLine($"✗ {result.ZipFileName} — {result.ErrorMessage}");
                }
            }
            PluginInstallStatus = lines.ToString().TrimEnd();

            // Refresh the displayed plugin / theme lists on any success.
            // Themes that hot-loaded already have their instances in
            // PluginService.LoadedPlugins, so LoadThemes() picks up the
            // new palettes immediately.
            if (anyThemeSuccess || anyNonThemeSuccess)
            {
                LoadPlugins();
                LoadThemes();
            }

            // Restart prompt is reserved for plugins that wire into
            // subsystems at startup (codecs, samplers, UI extensions).
            // A theme-only install activates live, so it doesn't trigger
            // the banner.
            if (anyNonThemeSuccess)
            {
                RestartRequired = true;
            }
        }
        finally
        {
            IsInstallingPlugins = false;
        }
    }

    private void LoadThemes()
    {
        AvailableThemes.Clear();
        // Host-default themes. These replace the single "Default" entry
        // and the standalone Dark Mode toggle — the choice between
        // Light and Dark is now part of theme selection, which makes
        // sense because plugin themes don't necessarily support both
        // variants.
        AvailableThemes.Add(new ThemeOption(PluginId: null, PaletteId: BuiltInLightPaletteId, Name: "Game Master Light"));
        AvailableThemes.Add(new ThemeOption(PluginId: null, PaletteId: BuiltInDarkPaletteId,  Name: "Game Master Dark"));

        // Walk every theme plugin and every palette inside it. The dropdown
        // shows palette-level entries, prefixed with the plugin's name so
        // the user sees which package each palette belongs to (e.g.
        // "Tomorrow Theme: Night Eighties"). A theme plugin that failed
        // to load still appears as a single "(failed)" row — we can't
        // enumerate palettes on a plugin that didn't instantiate, but
        // surfacing the package is better than silence.
        foreach (var meta in _pluginService.AvailablePlugins.Where(p => p.IsTheme))
        {
            if (meta.LoadFailed)
            {
                AvailableThemes.Add(new ThemeOption(meta.Id, PaletteId: null, $"{meta.Name} (failed)", IsFailed: true));
                continue;
            }

            var plugin = _pluginService.LoadedPlugins
                .OfType<SoundBoard.PluginApi.IThemePlugin>()
                .FirstOrDefault(p => p.Id == meta.Id);
            if (plugin == null) continue;

            IEnumerable<SoundBoard.PluginApi.ThemePalette> palettes;
            try { palettes = plugin.GetPalettes().ToList(); }
            catch (Exception ex)
            {
                SoundBoard.Core.Logging.Log.Error("Theme", $"GetPalettes() threw for '{meta.Id}' — listing as failed.", ex);
                AvailableThemes.Add(new ThemeOption(meta.Id, PaletteId: null, $"{meta.Name} (failed)", IsFailed: true));
                continue;
            }

            foreach (var palette in palettes)
            {
                var label = $"{meta.Name}: {palette.Name}";
                AvailableThemes.Add(new ThemeOption(meta.Id, palette.Id, label));
            }
        }

        // Match the persisted (plugin, palette) pair against the row
        // we just populated. If the user's previous plugin theme is
        // missing (uninstalled / failed to load), the save will have
        // been cleared to (null, "gm-dark") by ThemeService and we
        // land on Game Master Dark.
        var savedPluginId  = _settingsService.Current.SelectedThemeId;
        var savedPaletteId = _settingsService.Current.SelectedThemePaletteId;

        _selectedTheme =
            AvailableThemes.FirstOrDefault(t => t.PluginId == savedPluginId && t.PaletteId == savedPaletteId)
            ?? AvailableThemes.First(t => t.PaletteId == BuiltInDarkPaletteId);
        OnPropertyChanged(nameof(SelectedTheme));
    }

    private void UpdatePluginStatus(PluginItemViewModel vm)
    {
        if (vm.IsEnabledInSettings)
        {
            if (!_settingsService.Current.EnabledPluginIds.Contains(vm.Id))
                _settingsService.Current.EnabledPluginIds.Add(vm.Id);
        }
        else
        {
            _settingsService.Current.EnabledPluginIds.Remove(vm.Id);
        }

        _settingsService.Save();
        RestartRequired = true;
    }

    private void LoadAudioDevices()
    {
        var allDevices = LocalAudioPlayer.GetDevices().ToList();
        
        AudioDevices.Clear();
        AudioDevices.Add("Default System Output");
        
        foreach (var device in allDevices)
        {
            AudioDevices.Add(device.Name);
        }

        if (!string.IsNullOrEmpty(_settingsService.Current.PreferredOutputDeviceId))
        {
            var device = allDevices.FirstOrDefault(d => d.Id == _settingsService.Current.PreferredOutputDeviceId);
            if (device != null)
            {
                _selectedAudioDevice = device.Name;
            }
            else
            {
                _selectedAudioDevice = "Default System Output";
            }
        }
        else
        {
            _selectedAudioDevice = "Default System Output";
        }
        
        OnPropertyChanged(nameof(SelectedAudioDevice));
    }

    // Bridge connect/disconnect lifecycle is owned by each
    // IAudioBridgePlugin — the plugin's CreateSettingsControl renders
    // its own buttons. No host-side commands needed.

    // ── Buses management ─────────────────────────────────────────
    //
    // The Buses section lets the user rename built-in buses (Music /
    // Ambient / SFX), add custom buses (the SFX/Music split isn't enough
    // for every soundboard layout), and delete custom buses. Built-ins
    // can be renamed but not deleted — the bus ids are pinned in
    // BuiltInBusIds and Core paths assume they exist. Deleting a custom
    // bus reassigns any tracks pointing at it to the Music bus
    // (BuiltInBusIds.DefaultForNewTracks), also nulls any preset /
    // shortcut override pointing at it, and detaches its FX chain.

    public ObservableCollection<BusRow> Buses { get; } = new();

    [ObservableProperty]
    private BusRow? _selectedBus;

    [ObservableProperty]
    private string _newBusName = "";

    /// <summary>UI model for one bus row in the settings list. Holds the
    /// underlying <see cref="Core.Models.Bus"/> and a debounced rename
    /// setter so a TextBox blur or Enter-key commit doesn't pile up
    /// fire-per-keystroke writes against the DB.</summary>
    public sealed partial class BusRow : ObservableObject
    {
        private readonly Core.Models.Bus _model;
        private readonly Action<int, string> _rename;
        public int Id => _model.Id;
        public bool IsBuiltIn => _model.IsBuiltIn;
        public bool CanDelete => !_model.IsBuiltIn;
        public string Name
        {
            get => _model.Name;
            set
            {
                if (_model.Name == value) return;
                _model.Name = value;
                OnPropertyChanged();
                _rename(_model.Id, value);
            }
        }
        public BusRow(Core.Models.Bus model, Action<int, string> rename)
        {
            _model = model;
            _rename = rename;
        }
    }

    private void LoadBuses()
    {
        Buses.Clear();
        using var db = _dbFactory.CreateDbContext();
        foreach (var b in db.Buses.OrderBy(b => b.Order).ThenBy(b => b.Id))
            Buses.Add(new BusRow(b, RenameBusDirect));
    }

    private void RenameBusDirect(int busId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        _dbFactory.EditorSave<Core.Models.Bus>(busId, b => b.Name = newName.Trim());
        // Propagate the rename:
        //   1. SidechainRegistry.Refresh fires SourcesChanged for plugin
        //      source-pickers (the ducker's dropdown re-binds its labels).
        //   2. BusesChangedMessage fires for any open Bus Mixer / Track
        //      Editor / Preset Editor windows — they re-query the table
        //      and rebuild their bus-bound UI.
        _sidechainRegistry.Refresh();
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default
            .Send(new Messages.BusesChangedMessage(busId));
    }

    [RelayCommand]
    private void AddBus()
    {
        var name = (NewBusName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name)) return;

        using var db = _dbFactory.CreateDbContext();
        // Place after the current max-Order so the new bus sorts to the
        // end of the list naturally. +10 to leave room for the user to
        // re-order via the next custom add without renumbering.
        int maxOrder = db.Buses.Select(b => (int?)b.Order).Max() ?? 0;
        var row = new Core.Models.Bus
        {
            Name = name,
            Order = maxOrder + 10,
            IsBuiltIn = false,
            Volume = 1.0f,
        };
        db.Buses.Add(row);
        db.SaveChanges();

        // Materialise the BusMixer so any track immediately edited to
        // route there lands on a ready mixer (no first-AddMixerInput
        // race with a lazy creation on the audio thread).
        _masterMixer.EnsureBus(row.Id);

        NewBusName = "";
        LoadBuses();
        // Refresh the sidechain registry so plugin source-pickers see
        // the new bus on their next refresh-event handler call.
        _sidechainRegistry.Refresh();
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default
            .Send(new Messages.BusesChangedMessage(row.Id));
        Log.Info("Settings", $"Added custom bus #{row.Id}: '{name}'.");
    }

    [RelayCommand]
    private void DeleteBus(BusRow? row)
    {
        if (row == null || row.IsBuiltIn) return;
        int id = row.Id;

        // Stop all in-flight playback BEFORE tearing down the bus.
        // Pre-fix: any track currently routed to the dying bus had its
        // TrackSampleProvider plugged into that BusMixer's internal
        // MixingSampleProvider. After RemoveBus the BusMixer was
        // detached from the master combine but the provider kept
        // running — orphaned audio that goes nowhere, plus when the
        // track eventually ended OnPlaybackStopped's RemoveMixerInput
        // sweep would no longer find the bus and leak the provider.
        // StopAll is heavy-handed but bus deletion is rare and the
        // alternative requires per-source bus-routing introspection.
        try { _playbackEngine.StopAll(); }
        catch (Exception ex) { Log.Warn("Settings", "StopAll before DeleteBus threw", ex); }

        using var db = _dbFactory.CreateDbContext();
        // Bulk update via EF Core ExecuteUpdate — one round-trip,
        // zero change-tracking, ~100× faster on large libraries than
        // the previous foreach + tracked-entity SaveChanges. The two
        // override columns are nullable so the ExecuteUpdate sets them
        // to null in a single statement each.
        int trackCount = db.Tracks
            .Where(t => t.BusId == id)
            .ExecuteUpdate(s => s.SetProperty(t => t.BusId, Core.Models.BuiltInBusIds.DefaultForNewTracks));
        db.Presets
            .Where(p => p.BusIdOverride == id)
            .ExecuteUpdate(s => s.SetProperty(p => p.BusIdOverride, (int?)null));
        db.ShortcutButtons
            .Where(b => b.BusIdOverride == id)
            .ExecuteUpdate(s => s.SetProperty(b => b.BusIdOverride, (int?)null));
        // Delete the bus row itself (single-row delete, no bulk needed).
        var bus = db.Buses.Find(id);
        if (bus != null)
        {
            db.Buses.Remove(bus);
            db.SaveChanges();
        }

        // Tear down FX attached to this bus + the BusMixer itself.
        _samplerChain.RemoveAttachmentsFor(Core.Models.SamplerOwnerType.Bus, id);
        _masterMixer.RemoveBus(id);

        LoadBuses();
        // Plugin source-picker UIs need to know the bus is gone so
        // they fall back to "(none)" or another source. Bus Mixer +
        // Track Editor + Preset Editor windows also reload via the
        // BusesChangedMessage.
        _sidechainRegistry.Refresh();
        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default
            .Send(new Messages.BusesChangedMessage(id));
        Log.Info("Settings", $"Deleted custom bus #{id}; reassigned {trackCount} track(s) to the Music bus.");
    }
}
