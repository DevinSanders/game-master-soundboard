using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Data;
using SoundBoard.Core.Logging;
using SoundBoard.UI.Services;
using SoundBoard.UI.ViewModels;
using SoundBoard.UI.Views;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace SoundBoard.UI;

/// <summary>
/// Avalonia application entry point and DI composition root. Wires up
/// every service and view model, loads settings, discovers and loads
/// plugins, bootstraps the database (<c>EnsureCreated</c> followed by
/// <see cref="SoundBoard.Core.Data.SchemaMigrations"/>.Apply — schema
/// changes live there, not as raw SQL here), and installs the
/// dispatcher-level error handler that logs unhandled exceptions instead
/// of letting them tear the process down.
/// </summary>
public partial class App : Application
{
    public new static App? Current => Application.Current as App;
    public IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Catch anything Avalonia's dispatcher loop would otherwise eat silently.
        // Mark handled so the app doesn't tear down on a recoverable error;
        // true crashes still surface via AppDomain.UnhandledException.
        Dispatcher.UIThread.UnhandledException += (s, e) =>
        {
            Log.WriteCrash("Dispatcher", e.Exception);
            e.Handled = true;
        };

        // Class-level mouse-wheel handler — every Slider in the app
        // (host + plugins) becomes wheel-adjustable when hovered.
        // Registered here once; no per-view wiring needed.
        SliderWheelInput.Register();

        // macOS draws the traffic-light buttons (close/min/max) on top of
        // the client area's top-left corner. Our ExtendClientAreaToDecorationsHint
        // title bars (MainWindow + AppWindow) would otherwise put the app
        // icon and title underneath them. Override the TitleBarContentMargin
        // resource to indent ~78 px so the title content clears the
        // traffic lights. Windows and Linux keep the App.axaml default
        // (15,0) — their OS chrome is either absent (we own it via the
        // hint) or on the right side. See App.axaml for the resource
        // declaration and rationale.
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.OSX))
        {
            Resources["TitleBarContentMargin"] = new Avalonia.Thickness(78, 0, 15, 0);
        }

        var services = new ServiceCollection();
        
        // Register Core Services. Database access goes exclusively through
        // ISoundBoardDbContextFactory — each operation owns its own context
        // and its own change tracker, so a failed SaveChanges can't poison
        // anyone else's state.
        services.AddSingleton<SoundBoard.Core.Services.ISoundBoardDbContextFactory, SoundBoard.Core.Services.SoundBoardDbContextFactory>();
        
        // Audio Engine
        services.AddSingleton<MasterMixer>();
        services.AddSingleton<LocalAudioPlayer>();
        
        // Settings & Plugins
        services.AddSingleton<SoundBoard.Core.Services.ISettingsService, SoundBoard.Core.Services.SettingsService>();
        services.AddSingleton<SoundBoard.Core.Services.IPluginService, SoundBoard.Core.Services.PluginService>();
        services.AddSingleton<SoundBoard.Core.Services.IPluginInstallerService, SoundBoard.Core.Services.PluginInstallerService>();
        services.AddSingleton<SoundBoard.Core.Services.ISamplerChainService, SoundBoard.Core.Services.SamplerChainService>();
        services.AddSingleton<SoundBoard.Core.Services.SidechainRegistry>();
        services.AddSingleton<SoundBoard.PluginApi.ISidechainRegistry>(sp =>
            sp.GetRequiredService<SoundBoard.Core.Services.SidechainRegistry>());
        services.AddSingleton<ISamplerLauncherService, SamplerLauncherService>();
        // Bridge plugins (Discord / Zoom / Mumble / …) are wired through
        // AudioBridgeHost — see RegisterBridges call after plugin discovery.
        services.AddSingleton<SoundBoard.Core.Services.AudioBridgeHost>();

        // UI Services
        services.AddSingleton<SoundBoard.UI.Services.IFileService, SoundBoard.UI.Services.FileService>();
        services.AddSingleton<SoundBoard.UI.Services.IWindowManagerService, SoundBoard.UI.Services.WindowManagerService>();
        services.AddSingleton<SoundBoard.UI.Services.IAudioPlaybackEngine, SoundBoard.UI.Services.AudioPlaybackEngine>();
        services.AddSingleton<SoundBoard.UI.Services.ILibraryTransferService, SoundBoard.UI.Services.LibraryTransferService>();
        services.AddSingleton<SoundBoard.UI.Services.ISettingsTransferService, SoundBoard.UI.Services.SettingsTransferService>();
        services.AddSingleton<SoundBoard.UI.Services.LibraryManagerService>();
        services.AddSingleton<SoundBoard.UI.Services.UriActivationHandler>();
        services.AddSingleton<SoundBoard.UI.Services.UriSchemeRegistrar>();
        services.AddSingleton<SoundBoard.UI.Services.IThemeService, SoundBoard.UI.Services.ThemeService>();

        // Register ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<TrackEditorViewModel>();
        services.AddTransient<PlaylistsViewModel>();
        services.AddTransient<PresetsViewModel>();
        services.AddTransient<PresetEditorViewModel>();
        // Singleton so other windows (Presets) can reach the live ShortcutsViewModel
        // and target the currently-selected page when adding shortcut buttons.
        services.AddSingleton<ShortcutsViewModel>();
        // MixerViewModel + SettingsViewModel are documented as singleton-
        // lifetime — they own collection state (BridgeStrips, Buses, etc.)
        // that must NOT diverge across resolutions. Pre-Phase-R4 they
        // were AddTransient but stored as singleton-by-accident via
        // MainWindowViewModel properties; a future direct GetService
        // call would have silently built a fresh-state copy.
        services.AddSingleton<MixerViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<BusMixerViewModel>();
        services.AddTransient<AboutViewModel>();
        services.AddTransient<UriBuilderViewModel>();
        // Factories so multiple instances can be opened on demand.
        services.AddTransient<Func<PresetEditorViewModel>>(sp => () => sp.GetRequiredService<PresetEditorViewModel>());
        services.AddTransient<Func<UriBuilderViewModel>>(sp => () => sp.GetRequiredService<UriBuilderViewModel>());
        services.AddTransient<Func<TrackEditorViewModel>>(sp => () => sp.GetRequiredService<TrackEditorViewModel>());
        services.AddTransient<Func<BusMixerViewModel>>(sp => () => sp.GetRequiredService<BusMixerViewModel>());

        Services = services.BuildServiceProvider();

        // Pre-load native libe_sqlite3.so into glibc's link_map BEFORE the
        // plugin probe-and-unload storm corrupts the dynamic linker state.
        //
        // Background: PluginService.DiscoverAndLoad below creates a fresh
        // AssemblyLoadContext per discovered plugin (≈13–20 of them on a
        // typical install) and unloads each ALC whose plugin id isn't in
        // EnabledPluginIds. Transitively-referenced DLLs (Discord.Net,
        // NAudio.Wasapi, etc.) get pulled in and torn down on every probe.
        // On Linux (esp. recent glibc / Mesa userspace, observed on
        // Ubuntu 25.10+), that ALC + native-library teardown churn can
        // leave a dangling l_name pointer in glibc's loaded-library
        // link_map. The next dlopen call walks the list via
        // _dl_name_match_p → strcmp() and SIGSEGVs on the dangling
        // pointer — confirmed via core dump of a fresh-install run.
        //
        // The escape hatch: if libe_sqlite3.so is ALREADY in the link_map
        // before the plugin storm, EF Core's EnsureCreated() short-circuits
        // ("already loaded") instead of walking the now-corrupt list.
        // Opening an in-memory SqliteConnection here forces SQLitePCL's
        // Batteries.Init() → dlopen("e_sqlite3.so") path while the
        // link_map is still pristine.
        //
        // Catch any exception so a missing native lib doesn't crash the
        // whole app — the real DbContext open below will surface a
        // managed exception with a proper crash log if the native is
        // genuinely unloadable. The try/catch here is for "load failed
        // for reasons we'll diagnose later"; we don't want it to mask
        // anything.
        try
        {
            using var sqliteWarmup = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            sqliteWarmup.Open();
            Log.Checkpoint("Startup", "libe_sqlite3.so warmed into link_map before plugin scan.");
        }
        catch (Exception ex)
        {
            // Promoted from Warn to Error: the warmup IS the defense against
            // glibc link_map corruption from plugin ALC churn. A silent
            // failure here is exactly the diagnostic we'd need in a bug
            // report — if EnsureCreated later SIGSEGV's inside dlopen,
            // this line is the breadcrumb that proves the warmup didn't run.
            Log.Error("Startup",
                "SQLite native warmup failed; continuing. EnsureCreated will surface the real error if the lib is truly broken.",
                ex);
        }

        // Apply settings
        var settingsService = Services.GetRequiredService<SoundBoard.Core.Services.ISettingsService>();
        var pluginService = Services.GetRequiredService<SoundBoard.Core.Services.IPluginService>();
        var masterMixer = Services.GetRequiredService<MasterMixer>();
        var settings = settingsService.Current;

        // First-launch bundled-plugin import (no-op on dev builds and on
        // subsequent launches with a matching marker). Runs BEFORE
        // DiscoverAndLoad so freshly-imported plugins are active on the
        // very first frame, not the second launch. See BundledPluginImporter
        // for the marker / idempotency contract.
        //
        // IMPORTANT: wrapped in Task.Run + GetResult to avoid a
        // sync-over-async deadlock. InstallFromZipAsync internally does
        // `await Task.Run(() => ZipFile.ExtractToDirectory(...))`. Without
        // a ConfigureAwait(false) on every await in that chain, the
        // continuation captures whatever SynchronizationContext the
        // calling thread had — which is the Avalonia Dispatcher when
        // OnFrameworkInitializationCompleted runs. If we then block the
        // UI thread on GetResult, the continuation can never get back
        // onto the UI thread (it's blocked on us!) and the importer
        // hangs forever. Wrapping in Task.Run puts the whole chain on a
        // thread-pool thread with no captured SyncContext, so the inner
        // awaits' continuations run freely on the pool. The UI thread
        // is still blocked the entire time the import runs (~20–30s for
        // 19 plugins on first launch), so an obvious UX improvement is
        // a splash screen with progress — out of scope for now.
        try
        {
            var hostVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var installer = Services.GetRequiredService<SoundBoard.Core.Services.IPluginInstallerService>();
            Task.Run(() =>
                SoundBoard.Core.Services.BundledPluginImporter.ImportIfNeeded(installer, hostVersion))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Importer is best-effort — the rest of startup must run even if
            // every bundled zip is corrupt. The user can fall back to
            // dragging plugin zips onto Settings → Plugin Manager manually.
            Log.Warn("Startup", "Bundled plugin import threw — continuing without bundled plugins", ex);
        }

        // Load Plugins
        var windowManager = Services.GetRequiredService<IWindowManagerService>();
        pluginService.DiscoverAndLoad(settings.EnabledPluginIds, masterMixer, windowManager);

        // Bridge plugins (Discord / Zoom / Mumble / …): hand each one to
        // a per-bridge worker thread that pulls broadcast PCM and forwards
        // it. Must run AFTER DiscoverAndLoad so PluginService.BridgePlugins
        // is populated. AudioBridgeHost also flips MasterMixer.BroadcastEnabled
        // based on what's connected, so non-Discord users pay nothing.
        var bridgeHost = Services.GetRequiredService<SoundBoard.Core.Services.AudioBridgeHost>();
        bridgeHost.RegisterBridges(pluginService.BridgePlugins);

        // Synchronous startup checkpoints below. The native code we're
        // about to enter (Skia for theme apply, OpenAL Soft / WASAPI for
        // audio init, libe_sqlite3 for the DB) can SIGSEGV if a system
        // dependency is missing — and a native crash bypasses every
        // managed exception handler we have.
        // Log.Info goes through an async drainer; Log.Checkpoint writes
        // synchronously so the last marker before the crash makes it to
        // disk for the next bug-report read. Cost: ~1 ms each on the UI
        // thread. Cheap insurance.
        Log.Checkpoint("Startup", "Bridge registration complete; entering post-plugin init.");

        try
        {
            // Auto-disable anything that failed to load on startup so a single
            // broken plugin can't keep crashing the app on every launch. The
            // metadata stays in AvailablePlugins (with LoadFailed=true and the
            // error message), so the settings UI can still surface it as a
            // failed entry. Re-enabling requires the user to flip the toggle.
            PruneFailedPlugins(settings, pluginService, settingsService);
        }
        catch (Exception ex)
        {
            Log.WriteCrash("PruneFailedPlugins", ex);
            throw;
        }
        Log.Checkpoint("Startup", "PruneFailedPlugins done.");

        // Apply the single selected theme's resources, if any.
        // ThemeService owns the Avalonia variant now: built-in themes
        // (PluginId == null) map gm-light → Light and everything else →
        // Dark; plugin themes are variant-free and the service derives
        // the variant from their background luminance (for un-themed
        // Fluent chrome only). ThemeService also supports runtime
        // re-application — see SettingsViewModel's SelectedTheme setter.
        SoundBoard.UI.Services.IThemeService themeService;
        try
        {
            themeService = Services.GetRequiredService<SoundBoard.UI.Services.IThemeService>();
            themeService.Initialize();
        }
        catch (Exception ex)
        {
            Log.WriteCrash("ThemeService.Initialize", ex);
            throw;
        }
        Log.Checkpoint("Startup", "ThemeService.Initialize done.");

        try
        {
            themeService.ApplyTheme(settings.SelectedThemeId, settings.SelectedThemePaletteId);
        }
        catch (Exception ex)
        {
            Log.WriteCrash("ThemeService.ApplyTheme", ex);
            throw;
        }
        Log.Checkpoint("Startup", "ThemeService.ApplyTheme done.");

        var localPlayer = Services.GetRequiredService<LocalAudioPlayer>();
        if (!string.IsNullOrEmpty(settings.PreferredOutputDeviceId))
        {
            localPlayer.PreferredDeviceId = settings.PreferredOutputDeviceId;
        }

        // Diagnostic escape hatch: set GMSOUND_SKIP_AUDIO=1 to bypass the
        // native audio device init entirely. Useful for isolating
        // heap-corruption-during-startup on weird Linux audio stacks, or
        // for UI/data work on a machine with no working audio backend.
        // With this flag set the app launches without audio output —
        // every other subsystem (UI, plugins, DB) still runs.
        //
        // Originally introduced to bisect a miniaudio heap-corruption bug
        // on Ubuntu 25.10 (since fixed by migrating to OpenAL Soft);
        // retained because the same diagnostic shape applies if a future
        // platform-native audio lib starts misbehaving.
        //
        // Production code should never gate on this variable. If a real
        // user needs to launch without audio on a broken setup we'd add
        // a proper Settings UI; today this is a developer diagnostic only.
        var skipAudio = string.Equals(
            Environment.GetEnvironmentVariable("GMSOUND_SKIP_AUDIO"), "1",
            StringComparison.Ordinal);
        if (skipAudio)
        {
            Log.Checkpoint("Startup", "GMSOUND_SKIP_AUDIO=1 — bypassing LocalAudioPlayer.Init for diagnostics.");
        }
        else
        {
            Log.Checkpoint("Startup", "About to call LocalAudioPlayer.Init (NAudio/WASAPI on Windows, OpenAL Soft elsewhere).");
            try
            {
                localPlayer.Init();
            }
            catch (Exception ex)
            {
                Log.WriteCrash("LocalAudioPlayer.Init", ex);
                throw;
            }
            Log.Checkpoint("Startup", "LocalAudioPlayer.Init returned.");
        }

        // Restore the persisted main-output volume BEFORE the audio chain
        // starts emitting samples. Without this the user's "ducked to 30 %
        // last session" preference resets to 100 % at launch and the first
        // playback is at full volume. MixerViewModel's slider then reads
        // back the restored value and the UI shows the saved level too.
        masterMixer.LocalVolume = settings.LocalVolume;

        // Diagnostic log so "no sound" reports have a smoking gun. A user-
        // dragged 0.0 LocalVolume that got persisted silences everything
        // downstream without any other observable symptom; surfacing it at
        // startup makes the cause obvious instead of forcing a slider-by-
        // slider check in Settings.
        Log.Info("Startup", $"Restored LocalVolume = {settings.LocalVolume:0.00} " +
                            $"(0 = muted, 1 = unity, up to 2.0 from the UI slider).");

        // Ensure database is created + apply versioned migrations.
        //
        // First-run fresh-install path (no settings.json, no default.db):
        // SoundBoardDbContext.OnConfiguring synthesises the default
        // library path, writes it back through SettingsService.Save(),
        // and ultimately drops into Microsoft.Data.Sqlite which loads
        // the native libe_sqlite3.so. A glibc / libstdc++ ABI mismatch
        // between the SQLitePCLRaw shipped natives and the host system
        // can SIGSEGV the process at that point with no managed
        // exception. Checkpoints + per-step try/catch below pinpoint
        // which sub-step died for the next bug report.
        Log.Checkpoint("Startup", "About to resolve ISoundBoardDbContextFactory.");
        SoundBoard.Core.Services.ISoundBoardDbContextFactory dbFactory;
        try
        {
            dbFactory = Services.GetRequiredService<SoundBoard.Core.Services.ISoundBoardDbContextFactory>();
        }
        catch (Exception ex)
        {
            Log.WriteCrash("DbContextFactory.Resolve", ex);
            throw;
        }
        Log.Checkpoint("Startup", "ISoundBoardDbContextFactory resolved.");

        // Probe the SQLite native library with a managed try/catch BEFORE
        // EF Core's EnsureCreated touches it. Without this probe, a native
        // crash inside libe_sqlite3.so (observed on Linux fresh installs)
        // SIGSEGVs the process with no managed exception, no crash log, and
        // no terminal output beyond the preceding checkpoint. The in-memory
        // probe forces the native load on a path where:
        //   - DllNotFoundException / BadImageFormatException become catchable
        //   - We can surface the actual error in a Log.WriteCrash before
        //     EF Core gets involved (so the user knows it's a SQLite
        //     native-lib issue, not an EF schema issue)
        //   - If the native lib WILL crash, we still die here, but the
        //     checkpoint just before tells the bug-report reader exactly
        //     which line to suspect.
        Log.Checkpoint("Startup", "About to probe SQLite native library (managed catch).");
        try
        {
            using var probe = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            probe.Open();
            // Sanity: actually issue a SQL command so we exercise the
            // prepare/step path, not just connection allocation.
            using var cmd = probe.CreateCommand();
            cmd.CommandText = "SELECT sqlite_version()";
            var version = cmd.ExecuteScalar() as string ?? "<unknown>";
            Log.Checkpoint("Startup", $"SQLite native library OK (version {version}).");
        }
        catch (Exception ex)
        {
            Log.WriteCrash("SqliteNativeProbe", ex);
            throw;
        }

        try
        {
            using var db = dbFactory.CreateDbContext();
            Log.Checkpoint("Startup", "DbContext created; about to EnsureCreated (loads native libe_sqlite3).");
            db.Database.EnsureCreated();
            Log.Checkpoint("Startup", "EnsureCreated returned; about to apply SchemaMigrations.");
            SchemaMigrations.Apply(db);
            Log.Checkpoint("Startup", "SchemaMigrations.Apply returned.");
        }
        catch (Exception ex)
        {
            Log.WriteCrash("Database.Initialize", ex);
            throw;
        }

        // Materialise persisted sampler attachments. Must run after plugin
        // discovery (so the lookup for plugin id succeeds) and after the
        // database is created (so the table exists).
        Log.Checkpoint("Startup", "About to initialize SamplerChainService.");
        try
        {
            Services.GetRequiredService<SoundBoard.Core.Services.ISamplerChainService>().Initialize();
        }
        catch (Exception ex)
        {
            Log.WriteCrash("SamplerChainService.Initialize", ex);
            throw;
        }
        Log.Checkpoint("Startup", "SamplerChainService.Initialize returned.");

        // Bus mixers now exist + every bus row has been pre-created on the
        // MasterMixer. Build the SidechainRegistry (which snapshots the
        // BusMixer list) and inject it into every plugin context so plugins
        // that read IPluginContext.Sidechain see a populated registry.
        // Must run AFTER SamplerChainService.Initialize for the same reason
        // CodecRegistry waits — plugin Initialize ran earlier and may have
        // seen null. Plugins that query lazily (the canonical case for
        // sidechain consumers, which only need the registry at effect-
        // create time) will see the complete set.
        Log.Checkpoint("Startup", "About to attach SidechainRegistry to plugin contexts.");
        var sidechainRegistry = Services.GetRequiredService<SoundBoard.Core.Services.SidechainRegistry>();
        pluginService.AttachSidechainRegistry(sidechainRegistry);
        Log.Checkpoint("Startup", "SidechainRegistry attached. About to build MainWindow.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Views.MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>()
            };
            Log.Checkpoint("Startup", "MainWindow constructed and assigned to ApplicationLifetime.");
            // Close everything (Library, Mixer, Presets, etc.) when the main window goes away
            // — otherwise child AppWindows would keep the app alive under the default OnLastWindowClose.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // Ordered shutdown. Fires after the user clicks the close
            // button but before windows actually start tearing down — the
            // last chance we have to flush in-flight state. Each step is
            // try/catch so a failure in one doesn't strand the others.
            desktop.ShutdownRequested += (s, e) => RunOrderedShutdown();
        }

        // Register gmsound:// once per launch (HKCU on Windows; no-op elsewhere).
        try { Services.GetRequiredService<SoundBoard.UI.Services.UriSchemeRegistrar>().EnsureRegistered(); }
        catch (Exception ex) { Log.Error("URI", "scheme registration failed", ex); }

        // Drain any URI args that arrived before DI was ready (initial launch
        // args, or pipe messages received during startup).
        Services.GetRequiredService<SoundBoard.UI.Services.UriActivationHandler>().DrainPending();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Tear down audio + plugin state in a deterministic order
    /// on app shutdown. Idempotent and exception-safe — each step runs
    /// inside a try/catch so a single subsystem failure doesn't strand
    /// the others. Order matters: stop playback first so the audio
    /// thread isn't still pulling from providers we're about to dispose;
    /// engine + Discord next; plugins last (they may hold native handles
    /// the engine depended on); logger drainer absolutely last so all
    /// shutdown diagnostics get flushed to disk.</summary>
    private void RunOrderedShutdown()
    {
        if (Services == null)
        {
            try { Log.Shutdown(); } catch { }
            return;
        }

        // 0. Flush pending debounced writes from singleton-style VMs.
        //    MixerViewModel owns an EditPersistence with up to 300 ms of
        //    pending LocalVolume + BridgeVolume saves; without an explicit
        //    flush, dragging the slider and closing the window within the
        //    debounce window drops the write. Singleton VMs aren't
        //    disposed by WindowManagerService (intentional — they're
        //    reused across window opens), so the flush has to happen
        //    here. Editor VMs (Track, Bus, Preset, Sampler) are
        //    IDisposable and get flushed via their own Dispose path
        //    when their windows close.
        try
        {
            var mixerVm = Services.GetService<SoundBoard.UI.ViewModels.MixerViewModel>();
            mixerVm?.FlushPendingWrites();
        }
        catch (Exception ex) { Log.Warn("Shutdown", "MixerViewModel.FlushPendingWrites threw", ex); }

        // 1. Stop every active playback so the audio thread quiets.
        try
        {
            var engine = Services.GetService<SoundBoard.UI.Services.IAudioPlaybackEngine>();
            engine?.StopAll();
        }
        catch (Exception ex) { Log.Warn("Shutdown", "engine.StopAll threw", ex); }

        // 2. Dispose the engine itself (releases its DispatcherTimer, etc.).
        try
        {
            if (Services.GetService<SoundBoard.UI.Services.IAudioPlaybackEngine>() is IDisposable engineDisposable)
                engineDisposable.Dispose();
        }
        catch (Exception ex) { Log.Warn("Shutdown", "engine.Dispose threw", ex); }

        // 3. Cleanly disconnect every connected bridge (Discord / Zoom /
        //    Mumble …) BEFORE tearing down the host worker threads. Sends
        //    the proper "leaving voice channel" handshake so the remote
        //    doesn't show the bot ghosting until its session timeout
        //    fires. 5 s per bridge is generous — Discord's clean
        //    disconnect is typically <500ms; we block this long only on a
        //    hung network. Beyond that the warning lands in the log and
        //    shutdown continues regardless.
        var bridgeHost = Services.GetService<SoundBoard.Core.Services.AudioBridgeHost>();
        try
        {
            bridgeHost?.DisconnectAllBridges(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) { Log.Warn("Shutdown", "Bridge disconnect threw", ex); }

        // 3b. Tear down the worker threads + the bridge host itself.
        //     By this point every bridge is either cleanly disconnected
        //     or abandoned with a logged warning; this just stops the
        //     local plumbing.
        try
        {
            bridgeHost?.Dispose();
        }
        catch (Exception ex) { Log.Warn("Shutdown", "AudioBridgeHost.Dispose threw", ex); }

        // 4. Plugins — gives each one a chance to release file handles,
        // close audio devices, etc.
        try
        {
            Services.GetService<SoundBoard.Core.Services.IPluginService>()?.Shutdown();
        }
        catch (Exception ex) { Log.Warn("Shutdown", "PluginService.Shutdown threw", ex); }

        // 5. MasterMixer (drains its deferred-disposal channel + stops
        // the drainer thread).
        try
        {
            Services.GetService<MasterMixer>()?.Dispose();
        }
        catch (Exception ex) { Log.Warn("Shutdown", "MasterMixer.Dispose threw", ex); }

        // 6. Flush remaining log lines to disk before the drainer exits.
        try { Log.Shutdown(); } catch { /* logger is shutting down anyway */ }
    }

    /// <summary>Remove plugin IDs from settings whose load failed this
    /// session. Themes are exempt — they aren't in EnabledPluginIds
    /// (selection lives in SelectedThemeId, handled separately).</summary>
    private static void PruneFailedPlugins(
        SoundBoard.Core.Models.AppSettings settings,
        SoundBoard.Core.Services.IPluginService pluginService,
        SoundBoard.Core.Services.ISettingsService settingsService)
    {
        var failedIds = pluginService.AvailablePlugins
            .Where(p => p.LoadFailed && !p.IsTheme)
            .Select(p => p.Id)
            .Where(id => !string.IsNullOrEmpty(id) && settings.EnabledPluginIds.Contains(id))
            .ToList();

        if (failedIds.Count == 0) return;

        foreach (var id in failedIds)
        {
            settings.EnabledPluginIds.Remove(id);
            Log.Warn("Plugin", $"Auto-disabling '{id}' after startup load failure; re-enable from Settings once fixed.");
        }
        settingsService.Save();
    }

}