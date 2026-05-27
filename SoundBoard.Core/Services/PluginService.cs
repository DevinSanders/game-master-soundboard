using SoundBoard.Core.Plugins;
using SoundBoard.PluginApi;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace SoundBoard.Core.Services;

/// <summary>
/// Discovers, loads, and manages the lifecycle of third-party plugin DLLs
/// from the user's plugins folder. Wires each loaded plugin into the right
/// subsystem (codec registry, master DSP chain, theme resources, UI
/// extension points) based on which marker interfaces it implements.
/// </summary>
public interface IPluginService
{
    /// <summary>Plugins currently loaded into the process.</summary>
    IEnumerable<IPlugin> LoadedPlugins { get; }

    /// <summary>Just the <see cref="IAudioBridgePlugin"/> instances among
    /// <see cref="LoadedPlugins"/>. Used by <c>AudioBridgeHost</c> at
    /// startup to wire each bridge to a worker thread, and by the settings
    /// page to render the Bridges section. Empty when no bridge plugin is
    /// installed — the bridges section then renders an "install a bridge
    /// plugin to use this feature" hint.</summary>
    IEnumerable<IAudioBridgePlugin> BridgePlugins { get; }

    /// <summary>Look up the <see cref="IPluginContext"/> we handed to
    /// <paramref name="plugin"/> at <see cref="IPlugin.Initialize"/>.
    /// Used by the settings UI when calling
    /// <see cref="IAudioBridgePlugin.CreateSettingsControl"/> so the
    /// bridge gets the same context it had during init. Returns
    /// <c>null</c> if the plugin was never loaded by this service (test
    /// paths, race during shutdown).</summary>
    IPluginContext? GetContextFor(IPlugin plugin);

    /// <summary>Every plugin discovered in the plugins folder — including
    /// disabled ones — so the settings UI can list them with enable
    /// checkboxes.</summary>
    IEnumerable<IPluginMetadata> AvailablePlugins { get; }

    /// <summary>Scan the plugins folder; load each plugin whose Id is in
    /// <paramref name="enabledIds"/>; wire it into the subsystems passed in.
    /// Called once during startup from the DI composition root.</summary>
    void DiscoverAndLoad(IEnumerable<string> enabledIds, MasterMixer? mixer = null, IWindowService? windowService = null);

    /// <summary>Calls <see cref="IPlugin.Shutdown"/> on every loaded
    /// plugin. Invoked when the app is closing.</summary>
    void Shutdown();

    /// <summary>Return every <see cref="IUIExtensionPlugin"/>-supplied
    /// control whose <c>Placement</c> includes the given slot. Used by
    /// MixerView / TrackEditorView etc. to populate their plugin-extension
    /// panels. Null returns from <c>CreateControl</c> are filtered out.</summary>
    IEnumerable<object> GetExtensionControls(UIPlacement placement);

    /// <summary>Load a freshly-installed theme plugin into the live
    /// process — create an ALC for the plugin folder, instantiate its
    /// <see cref="IThemePlugin"/>, and register it in
    /// <see cref="LoadedPlugins"/> + <see cref="AvailablePlugins"/> so
    /// the settings dropdown can offer its palettes without a restart.
    /// Returns <c>null</c> on success, or a human-readable error string
    /// if the load failed (e.g. malformed manifest, missing entry DLL,
    /// the type doesn't implement <see cref="IThemePlugin"/>). Caller
    /// surfaces the error; non-theme plugins must NOT go through this
    /// path because the audio chain / codec registry don't support
    /// mid-session re-wiring.</summary>
    string? HotLoadTheme(string pluginFolder);

    /// <summary>Inject the sidechain registry into every plugin context
    /// already created. Called by the composition root AFTER the bus
    /// mixers exist (i.e. after <see cref="ISamplerChainService.Initialize"/>)
    /// so plugins that read <see cref="IPluginContext.Sidechain"/> at
    /// effect-create time see the populated registry. Idempotent.</summary>
    void AttachSidechainRegistry(ISidechainRegistry registry);
}

/// <summary>
/// Read-only view of a plugin's metadata used by the settings UI. Includes
/// load status and any error string so failed plugins can show diagnostics.
/// </summary>
public interface IPluginMetadata
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Author { get; }
    string Description { get; }
    string FilePath { get; }
    bool IsLoaded { get; }
    bool LoadFailed { get; }
    string? ErrorMessage { get; }

    /// <summary>True if the plugin implements <see cref="IThemePlugin"/>.
    /// Themes are picked via <c>AppSettings.SelectedThemeId</c> in a
    /// dedicated UI rather than the per-plugin enable list, so the
    /// settings page filters these rows out of the plugin DataGrid.</summary>
    bool IsTheme { get; }
}

/// <summary>Concrete <see cref="IPluginMetadata"/> populated as
/// <see cref="PluginService"/> walks the plugins folder.</summary>
public class PluginMetadata : IPluginMetadata
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string FilePath { get; set; } = "";
    public bool IsLoaded { get; set; }
    public bool LoadFailed { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsTheme { get; set; }
}

/// <inheritdoc cref="IPluginService"/>
public class PluginService : IPluginService
{
    private readonly List<IPlugin> _loadedPlugins = new();
    private readonly List<IPluginMetadata> _availablePlugins = new();

    /// <summary>Strong references to every <see cref="PluginLoadContext"/>
    /// we keep loaded for the app's lifetime. The plugin instance itself
    /// transitively holds the ALC alive (instance → Type → Assembly →
    /// ALC), but for COLLECTIBLE ALCs the runtime can be aggressive about
    /// considering that chain "weak" — leaving the ALC eligible for
    /// unload between the moment <c>Activator.CreateInstance</c> returns
    /// and the moment the plugin is actually called from outside. Holding
    /// the context explicitly here makes the lifetime indistinguishable
    /// from non-collectible without giving up the ability to <c>Unload</c>
    /// disabled-plugin contexts (those don't get parked here).</summary>
    private readonly List<PluginLoadContext> _loadedContexts = new();

    /// <summary>Every <see cref="PluginContext"/> we handed to a plugin's
    /// Initialize. Tracked so <see cref="DiscoverAndLoad"/> can
    /// retro-inject the completed <see cref="AudioCodecRegistry"/> snapshot
    /// after every codec finishes registering. Without this back-fill,
    /// a transport plugin (like codec.webstream) that ran Initialize
    /// EARLY in the pass would only see codecs loaded BEFORE it; with
    /// the back-fill, every plugin reads the same complete registry
    /// regardless of load order.</summary>
    private readonly List<PluginContext> _pluginContexts = new();

    /// <summary>Folder for non-theme plugins (codecs, samplers, UI
    /// extensions). Auto-created on first launch.</summary>
    public string PluginsFolder { get; }

    /// <summary>Folder for theme plugins. Themes are loaded separately
    /// from this directory so a misbehaving theme can't take down a
    /// codec/sampler, and so users have an obvious place to drop theme
    /// DLLs.</summary>
    public string ThemesFolder { get; }

    public IEnumerable<IPlugin> LoadedPlugins => _loadedPlugins;
    public IEnumerable<IPluginMetadata> AvailablePlugins => _availablePlugins;
    public IEnumerable<IAudioBridgePlugin> BridgePlugins => _loadedPlugins.OfType<IAudioBridgePlugin>();

    public IPluginContext? GetContextFor(IPlugin plugin)
    {
        // Plugins and PluginContexts are appended in lockstep inside the
        // discovery loop, so a linear scan is fine for the small N we
        // ever load (typically <20).
        for (int i = 0; i < _loadedPlugins.Count; i++)
        {
            if (ReferenceEquals(_loadedPlugins[i], plugin))
                return i < _pluginContexts.Count ? _pluginContexts[i] : null;
        }
        return null;
    }

    /// <summary>Surface a freshly-installed plugin in <see cref="AvailablePlugins"/>
    /// before the next discovery pass runs. Used by
    /// <see cref="PluginInstallerService"/> so the settings DataGrid
    /// shows the new row immediately after install; the plugin doesn't
    /// actually load (no ALC, no Initialize) until the next launch
    /// triggers <see cref="DiscoverAndLoad"/>. Idempotent against
    /// re-installs of the same id.</summary>
    public void AddPendingInstall(IPluginMetadata meta)
    {
        if (meta == null) return;
        // Replace any existing entry with the same id so a re-install
        // updates the row instead of stacking duplicates.
        var existingIndex = -1;
        for (int i = 0; i < _availablePlugins.Count; i++)
        {
            if (string.Equals(_availablePlugins[i].Id, meta.Id, StringComparison.Ordinal))
            {
                existingIndex = i;
                break;
            }
        }
        if (existingIndex >= 0) _availablePlugins[existingIndex] = meta;
        else                    _availablePlugins.Add(meta);
    }

    public IEnumerable<object> GetExtensionControls(UIPlacement placement)
    {
        // Identical filter + foreach previously duplicated in MixerVM
        // and TrackEditorVM. Plugins may host stateful UI in CreateControl
        // (event subscriptions, etc.) so each caller should consume the
        // result eagerly — we yield rather than materialize to keep
        // call-site control over enumeration.
        foreach (var plugin in _loadedPlugins)
        {
            if (plugin is IUIExtensionPlugin uiExt && uiExt.Placement.HasFlag(placement))
            {
                var control = uiExt.CreateControl();
                if (control != null) yield return control;
            }
        }
    }

    public PluginService()
    {
        PluginsFolder = AppPaths.PluginsFolder;
        ThemesFolder  = AppPaths.ThemesFolder;
        Directory.CreateDirectory(PluginsFolder);
        Directory.CreateDirectory(ThemesFolder);
        SeedReadmeIfMissing(PluginsFolder, PluginsReadmeText);
        SeedReadmeIfMissing(ThemesFolder, ThemesReadmeText);
    }

    private static void SeedReadmeIfMissing(string folder, string content)
    {
        // Drop a README on first launch only. If the user deletes it,
        // we don't recreate — assume they read it. If they edit it, we
        // don't overwrite their changes.
        var path = Path.Combine(folder, "README.txt");
        if (File.Exists(path)) return;
        try { File.WriteAllText(path, content); }
        catch (Exception ex) { Log.Warn("Plugin", $"Could not seed README at {path}: {ex.Message}"); }
    }

    private const string PluginsReadmeText =
        "Game Master Sound Board — Plugins folder\r\n" +
        "=========================================\r\n" +
        "\r\n" +
        "Each plugin lives in its own subfolder containing a plugin.json\r\n" +
        "manifest plus the plugin DLL and its dependencies. Example:\r\n" +
        "\r\n" +
        "  Plugins/\r\n" +
        "    Mp3CodecPlugin/\r\n" +
        "      plugin.json\r\n" +
        "      Mp3CodecPlugin.dll\r\n" +
        "      NLayer.dll\r\n" +
        "\r\n" +
        "Plugins extend the app with:\r\n" +
        "  - additional audio codecs (IAudioCodecPlugin)\r\n" +
        "  - real-time DSP effects (IAudioSamplerPlugin)\r\n" +
        "  - extra UI panels (IUIExtensionPlugin)\r\n" +
        "\r\n" +
        "Themes go in the sibling Themes folder, not here.\r\n" +
        "\r\n" +
        "The easiest install path is to drop the plugin's .zip onto the\r\n" +
        "Plugin Manager drop-zone in Settings — that does the extract +\r\n" +
        "routing for you. For codec/sampler/UI plugins, restart the app\r\n" +
        "and enable the plugin under Settings -> Plugins. A plugin that\r\n" +
        "fails to load is auto-disabled so it cannot crash the app on\r\n" +
        "subsequent launches; the error is shown in the settings row.\r\n" +
        "\r\n" +
        "See docs/PLUGIN-DEV.md in the source repo for the SDK reference\r\n" +
        "and the plugin.json schema.\r\n";

    private const string ThemesReadmeText =
        "Game Master Sound Board — Themes folder\r\n" +
        "========================================\r\n" +
        "\r\n" +
        "Each theme lives in its own subfolder containing a plugin.json\r\n" +
        "manifest plus the theme DLL. Example:\r\n" +
        "\r\n" +
        "  Themes/\r\n" +
        "    SunsetTheme/\r\n" +
        "      plugin.json   (with \"isTheme\": true)\r\n" +
        "      SunsetTheme.dll\r\n" +
        "\r\n" +
        "A theme is a plugin that implements IThemePlugin and returns one or\r\n" +
        "more ThemePalette entries pointing at Avalonia ResourceDictionary\r\n" +
        "or Styles resources compiled into the theme DLL.\r\n" +
        "\r\n" +
        "Themes can be picked up live — drop the zip on the Plugin Manager\r\n" +
        "drop-zone and the new palette appears in Settings -> Appearance ->\r\n" +
        "Theme without a restart. Themes are one-of-N (not enable lists) —\r\n" +
        "the dropdown lets you pick exactly one. A theme that fails to load\r\n" +
        "is auto-reset to Default on the next launch so a broken theme\r\n" +
        "cannot brick the UI.\r\n" +
        "\r\n" +
        "See docs/PLUGIN-DEV.md in the source repo for the theme SDK guide.\r\n";

    /// <summary>Marker for which folder's DLLs are currently being
    /// probed — controls what's accepted (themes vs non-themes) and the
    /// warning emitted when a mismatched plugin is found.</summary>
    private enum FolderKind { Plugins, Themes }

    public void DiscoverAndLoad(IEnumerable<string> enabledIds, MasterMixer? mixer = null, IWindowService? windowService = null)
    {
        _availablePlugins.Clear();
        _pluginContexts.Clear();
        // Process-global codec registry. Clear before scanning so a
        // future reload-without-restart flow doesn't leave entries from
        // a previous plugin generation lingering. Today DiscoverAndLoad
        // only runs once at startup so this is a no-op in practice; the
        // call documents the contract for when reload lands.
        SoundBoard.Core.Audio.AudioFileReaderCrossPlatform.ClearPlugins();
        var enabledSet = new HashSet<string>(enabledIds ?? Enumerable.Empty<string>());

        // ── Staged-replace promotion ──────────────────────────────────
        //
        // Before scanning either folder, complete any pending replaces
        // that the installer staged because the previous version's DLLs
        // were locked by the running process. We're early enough in
        // startup that no plugin ALCs exist yet — every DLL in the
        // Plugins / Themes tree is unlocked, so the rename succeeds.
        // See PluginInstallerService.PendingSuffix for the staging
        // convention.
        ApplyPendingSwaps(PluginsFolder);
        ApplyPendingSwaps(ThemesFolder);

        ScanFolder(PluginsFolder, FolderKind.Plugins, enabledSet, mixer, windowService);
        ScanFolder(ThemesFolder, FolderKind.Themes, enabledSet, mixer, windowService);

        // Now that every codec plugin has registered itself with
        // AudioFileReaderCrossPlatform, snapshot the registry and inject
        // it into every PluginContext we handed out earlier in this
        // pass. Plugins whose Initialize read CodecRegistry will have
        // seen null then; transport plugins that query at CreateStream
        // time will see the complete list. See the doc comment on
        // PluginContext.SetCodecRegistry for the lifetime story.
        var registry = AudioCodecRegistry_Snapshot();
        if (registry != null)
        {
            foreach (var ctx in _pluginContexts)
                ctx.SetCodecRegistry(registry);
        }
    }

    /// <summary>Inject the sidechain registry into every PluginContext.
    /// Called by the composition root AFTER the bus mixers have been
    /// pre-created (i.e. after <see cref="ISamplerChainService.Initialize"/>)
    /// so the registry is non-empty by the time any plugin looks at
    /// <see cref="IPluginContext.Sidechain"/>. Cheap when called twice
    /// — each context just gets the same reference re-assigned.</summary>
    public void AttachSidechainRegistry(ISidechainRegistry registry)
    {
        if (registry == null) return;
        foreach (var ctx in _pluginContexts)
            ctx.SetSidechainRegistry(registry);
    }

    /// <summary>Look for <c>&lt;folder&gt;/&lt;x&gt;.pending</c> subfolders
    /// the installer staged because the previous version's DLLs were
    /// locked by the running process. For each pending folder:
    /// delete the canonical folder if it exists (now safe — no plugins
    /// are loaded yet at this point in startup), then rename
    /// <c>&lt;x&gt;.pending → &lt;x&gt;</c>. Errors are logged but never
    /// fatal — a partially-applied swap leaves the user's previous
    /// install in place, which is the safe default.</summary>
    private static void ApplyPendingSwaps(string folder)
    {
        if (!Directory.Exists(folder)) return;

        // The string we look for. Kept in sync with
        // PluginInstallerService.PendingSuffix; copied as a literal here
        // because that constant is internal to a sibling type and
        // referencing it across the file would be opaque.
        const string suffix = ".pending";

        foreach (var pendingPath in Directory.GetDirectories(folder, "*" + suffix))
        {
            var pendingName = Path.GetFileName(pendingPath);
            if (!pendingName.EndsWith(suffix, StringComparison.Ordinal)) continue;

            var canonicalName = pendingName.Substring(0, pendingName.Length - suffix.Length);
            var canonicalPath = Path.Combine(folder, canonicalName);

            try
            {
                if (Directory.Exists(canonicalPath))
                    Directory.Delete(canonicalPath, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warn("Plugin",
                    $"Could not clear locked plugin folder {canonicalPath} during pending-swap promotion. " +
                    $"The pending install at {pendingPath} will retry on the next launch. {ex.Message}");
                continue;
            }

            try
            {
                Directory.Move(pendingPath, canonicalPath);
                Log.Info("Plugin", $"Promoted staged install: {pendingPath} → {canonicalPath}");
            }
            catch (Exception ex)
            {
                Log.Warn("Plugin",
                    $"Could not promote staged install {pendingPath} → {canonicalPath}: {ex.Message}. " +
                    "The pending folder will be retried on the next launch.");
            }
        }
    }

    /// <summary>Build a fresh <see cref="AudioCodecRegistry"/> from the
    /// currently-registered codec plugins. Returns null if there are no
    /// codec plugins (which would just give every context a useless
    /// empty registry — leaving it null is more informative).</summary>
    private static AudioCodecRegistry? AudioCodecRegistry_Snapshot()
    {
        var snap = SoundBoard.Core.Audio.AudioFileReaderCrossPlatform.SnapshotPlugins();
        if (snap.Count == 0) return null;
        return new AudioCodecRegistry(snap);
    }

    /// <summary>Discover plugins in <paramref name="folder"/>. Each plugin
    /// must live in its own subfolder containing a <c>plugin.json</c>
    /// manifest at the folder root — see <see cref="PluginManifestFile"/>.
    /// The manifest's <c>entryDll</c> field names the DLL that's actually
    /// probed; dependency DLLs alongside are NOT scanned.
    ///
    /// <para><b>Why the entry-point-only rule.</b> Pre-manifest, the scanner
    /// derived the entry-DLL name from the subfolder name and walked every
    /// other DLL hoping to find more <see cref="IPlugin"/> types. Probing
    /// dep DLLs (NLayer, NAudio.Core, etc.) creates short-lived
    /// <see cref="PluginLoadContext"/>s for each and unloads them when no
    /// IPlugin type is found. That side-loads the deps into unloading ALCs,
    /// which later causes "AssemblyLoadContext is unloading" when the real
    /// plugin's ALC tries to resolve the same dep — even though the
    /// plugin's own ALC is perfectly healthy. The manifest scheme is a
    /// strict improvement: the entry DLL is named explicitly, so there's
    /// no need to probe anything else.</para>
    ///
    /// <para><b>No loose-DLL back-compat.</b> Plugins dropped directly
    /// in <c>Plugins\</c> / <c>Themes\</c> without a containing folder
    /// (and without <c>plugin.json</c>) are ignored with a one-line
    /// warning. The canonical layout is
    /// <c>Plugins\&lt;PluginName&gt;\plugin.json + &lt;PluginName&gt;.dll</c>.</para></summary>
    private void ScanFolder(string folder, FolderKind kind, HashSet<string> enabledSet, MasterMixer? mixer, IWindowService? windowService)
    {
        if (!Directory.Exists(folder))
        {
            Log.Info("Plugin", $"Skipping scan: folder does not exist: {folder}");
            return;
        }

        // Surface a noisy warning when the user drops a bare DLL — the
        // pre-manifest layout — so the failure mode is obvious instead
        // of "why isn't my plugin showing up?".
        var looseDlls = Directory.GetFiles(folder, "*.dll", SearchOption.TopDirectoryOnly);
        foreach (var loose in looseDlls)
        {
            Log.Warn("Plugin", $"Ignoring loose DLL '{Path.GetFileName(loose)}' — plugins must live in their own subfolder " +
                               $"with a {PluginManifestFile.FileName} manifest. See docs/PLUGIN-DEV.md.");
        }

        // Each subfolder with a plugin.json is a candidate. The "Data"
        // subfolder is reserved for plugin-context private state and is
        // never a plugin folder.
        var folderManifests = new List<(string Folder, PluginManifestFile Manifest)>();
        foreach (var subfolder in Directory.GetDirectories(folder))
        {
            var subName = Path.GetFileName(subfolder);
            if (string.Equals(subName, "Data", StringComparison.OrdinalIgnoreCase)) continue;

            // Pending-replace staging from a previous session's install
            // that we couldn't promote (the canonical folder was still
            // locked). Skip — ApplyPendingSwaps will retry next launch.
            // Loading the .pending folder directly would double-load
            // the same plugin under two folder names.
            if (subName.EndsWith(".pending", StringComparison.Ordinal)) continue;

            if (!PluginManifestFile.TryLoad(subfolder, out var manifest, out var error) || manifest == null)
            {
                Log.Warn("Plugin", $"Skipping '{subName}': {error}");
                _availablePlugins.Add(new PluginMetadata
                {
                    Id = subName,
                    Name = subName,
                    FilePath = subfolder,
                    LoadFailed = true,
                    ErrorMessage = error,
                    IsTheme = kind == FolderKind.Themes,
                });
                continue;
            }
            folderManifests.Add((subfolder, manifest));
        }

        Log.Info("Plugin", $"Scanning {kind} folder '{folder}' — {folderManifests.Count} plugin(s).");

        foreach (var (sub, manifest) in folderManifests)
        {
            var entryDll = Path.Combine(sub, manifest.EntryDll);

            // Fast path for disabled non-theme plugins: build the metadata
            // row from the manifest alone and SKIP ALC creation entirely.
            //
            // Rationale: creating + immediately unloading a PluginLoadContext
            // (and the Activator.CreateInstance of the plugin type) just to
            // read fields we already have in plugin.json is the residual
            // ALC-churn pattern responsible for the glibc link_map
            // corruption we hit on Ubuntu 25.10 (since mitigated by the
            // SQLite-warmup defense in App.OnFrameworkInitializationCompleted).
            // The manifest carries the same id/name/version/author/description
            // the runtime IPlugin instance would have, so for a disabled
            // plugin the ALC create→Activator→Shutdown→Unload round-trip
            // is pure waste.
            //
            // Themes still go through ProbeAndMaybeLoad because the host
            // applies a selected theme at startup — we need the live
            // IThemePlugin instance, not just metadata.
            if (!manifest.IsTheme && !enabledSet.Contains(manifest.Id))
            {
                _availablePlugins.Add(new PluginMetadata
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version,
                    Author = manifest.Author,
                    Description = manifest.Description,
                    FilePath = entryDll,
                    IsTheme = false,
                    IsLoaded = false,
                    LoadFailed = false,
                });
                Log.Info("Plugin", $"Discovered disabled plugin '{manifest.Name}' ({manifest.Id}) v{manifest.Version} via manifest — ALC not created.");
                continue;
            }

            try
            {
                ProbeAndMaybeLoad(entryDll, kind, enabledSet, mixer, windowService);
            }
            catch (Exception ex)
            {
                Log.Error("Plugin", $"Error discovering plugin {entryDll}", ex);
                _availablePlugins.Add(new PluginMetadata { FilePath = entryDll, LoadFailed = true, ErrorMessage = ex.Message });
            }
        }
    }

    /// <inheritdoc cref="IPluginService.HotLoadTheme"/>
    public string? HotLoadTheme(string pluginFolder)
    {
        if (!Directory.Exists(pluginFolder))
            return $"Plugin folder does not exist: {pluginFolder}";

        if (!PluginManifestFile.TryLoad(pluginFolder, out var manifest, out var manifestError) || manifest == null)
            return manifestError ?? "Could not read plugin manifest.";

        if (!manifest.IsTheme)
            return "HotLoadTheme refused: manifest.isTheme is false. Only theme plugins can be hot-loaded; " +
                   "codec/sampler/UI plugins require a restart.";

        var entryDll = Path.Combine(pluginFolder, manifest.EntryDll);
        if (!File.Exists(entryDll))
            return $"Entry DLL does not exist: {entryDll}";

        // If a previous load failed for this id, drop the failed
        // metadata row before retrying so we don't end up with two
        // entries for the same plugin.
        for (int i = _availablePlugins.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_availablePlugins[i].Id, manifest.Id, StringComparison.Ordinal))
                _availablePlugins.RemoveAt(i);
        }

        var context = new PluginLoadContext(entryDll);
        try
        {
            var assembly = context.LoadFromAssemblyPath(entryDll);
            var pluginType = assembly.GetTypes().FirstOrDefault(t =>
                typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
            if (pluginType == null)
            {
                try { context.Unload(); } catch { }
                return $"No type implementing IPlugin found in '{manifest.EntryDll}'.";
            }
            var instance = (IPlugin)Activator.CreateInstance(pluginType)!;
            if (instance is not IThemePlugin)
            {
                try { instance.Shutdown(); } catch { }
                try { context.Unload(); } catch { }
                return $"Entry type '{pluginType.FullName}' does not implement IThemePlugin.";
            }

            // Themes get a data dir under Themes\Data\<id> just like
            // startup-loaded themes do.
            var dataPath = Path.Combine(ThemesFolder, "Data", instance.Id);
            Directory.CreateDirectory(dataPath);
            var pluginContext = new PluginContext(windowService: null, pluginDataPath: dataPath);
            _pluginContexts.Add(pluginContext);

            instance.Initialize(pluginContext);
            // Hot-loaded themes happen after startup, so the registry is
            // already built — inject it immediately.
            var registryNow = AudioCodecRegistry_Snapshot();
            if (registryNow != null) pluginContext.SetCodecRegistry(registryNow);

            _loadedPlugins.Add(instance);
            _loadedContexts.Add(context);

            _availablePlugins.Add(new PluginMetadata
            {
                Id = instance.Id,
                Name = instance.Name,
                Version = instance.Version,
                Author = instance.Author,
                Description = instance.Description,
                FilePath = entryDll,
                IsLoaded = true,
                LoadFailed = false,
                IsTheme = true,
            });

            Log.Info("Plugin", $"Hot-loaded theme '{instance.Name}' ({instance.Id}) v{instance.Version}.");
            return null;
        }
        catch (Exception ex)
        {
            try { context.Unload(); } catch { }
            // Surface the load failure as a row in the settings UI so
            // the user sees it next to working plugins.
            _availablePlugins.Add(new PluginMetadata
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Version = manifest.Version,
                Author = manifest.Author,
                Description = manifest.Description,
                FilePath = entryDll,
                IsLoaded = false,
                LoadFailed = true,
                ErrorMessage = ex.Message,
                IsTheme = true,
            });
            return ex.Message;
        }
    }

    /// <summary>
    /// One pass per plugin: load the assembly once, instantiate the plugin
    /// type to read its metadata, then either (a) wire it into the
    /// subsystems if enabled, keeping its <see cref="PluginLoadContext"/>
    /// alive, or (b) call <c>Unload()</c> on the context if disabled so the
    /// transitively-loaded assemblies can be reclaimed promptly.
    /// </summary>
    private void ProbeAndMaybeLoad(string filePath, FolderKind kind, HashSet<string> enabledSet, MasterMixer? mixer, IWindowService? windowService)
    {
        var context = new PluginLoadContext(filePath);
        Assembly assembly;
        Type? pluginType;
        IPlugin instance;
        try
        {
            assembly = context.LoadFromAssemblyPath(filePath);
            pluginType = assembly.GetTypes().FirstOrDefault(t =>
                typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
            if (pluginType == null)
            {
                // Either a dependency DLL the user copied alongside the plugin
                // (NAudio.Core.dll, etc.) or a plugin DLL whose IPlugin type
                // we can't see. The latter usually means a type-identity
                // mismatch between the host's IPlugin and the plugin's —
                // PluginLoadContext is supposed to prevent that by deferring
                // to the default ALC; if you see this for a DLL you authored,
                // check that AssemblyLoadContext.Default has SoundBoard.PluginApi.
                Log.Info("Plugin", $"No IPlugin type found in {Path.GetFileName(filePath)} — treating as a dependency DLL.");
                context.Unload();
                return;
            }
            instance = (IPlugin)Activator.CreateInstance(pluginType)!;
            Log.Info("Plugin", $"Discovered '{instance.Name}' ({instance.Id}) v{instance.Version} from {Path.GetFileName(filePath)}.");
        }
        catch (Exception ex)
        {
            // Probing failed; record the failure for the settings UI and
            // unload the (possibly partial) context so a corrupt DLL doesn't
            // linger in memory.
            try { context.Unload(); } catch { }
            _availablePlugins.Add(new PluginMetadata { FilePath = filePath, LoadFailed = true, ErrorMessage = ex.Message });
            return;
        }

        var isTheme = instance is IThemePlugin;

        // Enforce the folder split: a theme DLL dropped in Plugins/ (or a
        // non-theme dropped in Themes/) is ignored with a warning so the
        // user can move it. Keeping metadata for the misfile would clutter
        // the settings UI with rows that can never load — better to surface
        // the mismatch in the log and stay quiet in the UI.
        if (kind == FolderKind.Plugins && isTheme)
        {
            Log.Warn("Plugin", $"Skipping theme '{instance.Name}' at {filePath} — themes belong in the Themes folder, not Plugins.");
            try { context.Unload(); } catch { }
            return;
        }
        if (kind == FolderKind.Themes && !isTheme)
        {
            Log.Warn("Plugin", $"Skipping non-theme plugin '{instance.Name}' at {filePath} — only themes belong in the Themes folder.");
            try { context.Unload(); } catch { }
            return;
        }

        var meta = new PluginMetadata
        {
            Id = instance.Id,
            Name = instance.Name,
            Version = instance.Version,
            Author = instance.Author,
            Description = instance.Description,
            FilePath = filePath,
            IsTheme = isTheme,
        };
        _availablePlugins.Add(meta);

        // Themes are always kept loaded — selection happens via
        // AppSettings.SelectedThemeId at startup, not via the enable
        // list. Other plugin types follow the per-plugin enable rule.
        if (!isTheme && !enabledSet.Contains(meta.Id))
        {
            // Discovered but disabled. Give the instance a Shutdown call
            // before we drop it — Activator.CreateInstance may have run a
            // ctor that grabbed file handles / native libs / Discord
            // clients; skipping Shutdown leaks them. Best-effort: a
            // throwing Shutdown shouldn't block unload.
            try { instance.Shutdown(); }
            catch (Exception ex) { Log.Warn("Plugin", $"Shutdown of disabled plugin {meta.Name} threw", ex); }

            // Unload the ALC so the assembly + its transitive references
            // become eligible for collection on the next GC cycle. The
            // metadata we already captured stays valid (it's POCO data).
            try { context.Unload(); }
            catch (Exception ex) { Log.Warn("Plugin", $"Unload of disabled plugin {meta.Name} failed", ex); }
            return;
        }

        // Enabled (or theme) — wire into subsystems and keep the context alive.
        try
        {
            // Per-plugin data dir lives next to the plugin (Plugins/Data/<id>
            // or Themes/Data/<id>) so a user backing up one folder gets the
            // associated data too.
            var dataRoot = kind == FolderKind.Themes ? ThemesFolder : PluginsFolder;
            var dataPath = Path.Combine(dataRoot, "Data", instance.Id);
            Directory.CreateDirectory(dataPath);
            var pluginContext = new PluginContext(windowService, dataPath);
            _pluginContexts.Add(pluginContext);

            instance.Initialize(pluginContext);
            // CodecRegistry stays null at this point. DiscoverAndLoad's
            // post-pass back-fills every context with the completed
            // snapshot once every codec has registered. See the comment
            // on _pluginContexts above for the rationale.

            _loadedPlugins.Add(instance);
            _loadedContexts.Add(context); // pin the ALC against premature collection
            meta.IsLoaded = true;

            if (instance is IAudioCodecPlugin codec)
            {
                AudioFileReaderCrossPlatform.RegisterPlugin(codec);
            }
            // Sampler plugins are NOT auto-attached here. The host's
            // SamplerAttachmentService reads persisted attachments from
            // the database after plugin discovery and creates one
            // ISamplerInstance per attachment, then wires it into the
            // correct target (master bus / shortcut / preset / playlist).
            // A loaded sampler plugin just sits there until something
            // attaches it.

            Log.Info("Plugin", $"Loaded: {instance.Name} ({instance.Version})");
        }
        catch (Exception ex)
        {
            meta.LoadFailed = true;
            meta.ErrorMessage = ex.Message;
            Log.Error("Plugin", $"Failed to load {meta.Name}", ex);
            try { context.Unload(); } catch { }
        }
    }

    public void Shutdown()
    {
        foreach (var plugin in _loadedPlugins)
        {
            try { plugin.Shutdown(); } catch { }
        }
        _loadedPlugins.Clear();
    }
}
