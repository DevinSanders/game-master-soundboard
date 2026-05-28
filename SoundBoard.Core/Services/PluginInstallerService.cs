using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Plugins;
using SoundBoard.PluginApi;

namespace SoundBoard.Core.Services;

/// <summary>
/// One row of <see cref="IPluginInstallerService.InstallFromZipAsync"/>
/// output. Surfaced to the Settings UI so each dropped <c>.zip</c> gets
/// its own success / failure line.
/// </summary>
/// <summary>What the installer did with this zip. Drives the
/// settings UI's per-row status message so the user knows whether
/// they got an upgrade, a side-by-side coexistence install, or a
/// fresh install of a plugin they didn't have.</summary>
public enum PluginInstallAction
{
    /// <summary>Install failed; no folder was created or modified.
    /// See <see cref="PluginInstallResult.ErrorMessage"/>.</summary>
    Failed = 0,

    /// <summary>The plugin was new to this host — no other folder
    /// claimed the same <c>(publisher, id)</c> or even the same id.</summary>
    Installed,

    /// <summary>An existing install with the same
    /// <c>(publisher, id)</c> was replaced (the upgrade path). The
    /// previous version's folder was deleted before the new one moved
    /// in. <see cref="PluginInstallResult.ReplacedVersion"/> carries
    /// what the old manifest reported, when readable.</summary>
    Replaced,

    /// <summary>Another publisher already shipped a plugin with the
    /// same <c>id</c>. The incoming install was placed in its own
    /// folder so both can load side-by-side, and the existing install
    /// was left untouched.
    /// <see cref="PluginInstallResult.SideBySideWith"/> names the
    /// other publisher so the user can disambiguate.</summary>
    InstalledAlongside,
}

public sealed record PluginInstallResult(
    /// <summary>File name of the zip the user dropped (no path).</summary>
    string ZipFileName,
    bool Success,
    /// <summary>Plugin display name from the manifest, on success.
    /// Null on failure.</summary>
    string? PluginName,
    string? PluginId,
    /// <summary>Publisher segment of the install lineage. Null on failure.</summary>
    string? PluginPublisher,
    bool IsTheme,
    /// <summary>Folder the plugin was extracted to
    /// (<c>…/Plugins/&lt;publisher__id&gt;</c> or
    /// <c>…/Themes/&lt;publisher__id&gt;</c>), or null on failure.</summary>
    string? DestinationFolder,
    /// <summary>What happened — surfaced to the UI as a per-row
    /// status line. <see cref="PluginInstallAction.Failed"/> when
    /// <see cref="Success"/> is false.</summary>
    PluginInstallAction Action,
    /// <summary>For <see cref="PluginInstallAction.Replaced"/>: the
    /// version string the old manifest reported, when we could read
    /// it. Null otherwise.</summary>
    string? ReplacedVersion,
    /// <summary>For <see cref="PluginInstallAction.InstalledAlongside"/>:
    /// the publisher of the other plugin that already claimed this id.
    /// Null otherwise.</summary>
    string? SideBySideWith,
    /// <summary>Human-readable failure reason. Null on success.</summary>
    string? ErrorMessage);

/// <summary>
/// Installs plugins from <c>.zip</c> archives dropped by the user. The
/// goal is to give plugin distribution a friction-free "drag a zip into
/// Settings" UX so users don't have to know which AppData folder a
/// theme vs codec vs sampler belongs in.
///
/// <para><b>Manifest contract.</b> Every plugin zip MUST contain a
/// <c>plugin.json</c> file at the plugin folder root — see
/// <see cref="PluginManifestFile"/> for the schema. The manifest is the
/// installer's single source of truth: it dictates the identifier, the
/// destination folder (Plugins vs Themes is read from
/// <see cref="PluginManifestFile.IsTheme"/>), and which DLL inside the
/// zip is the entry point. Zips without a manifest are rejected with a
/// clear error message rather than guessed at.</para>
///
/// <para><b>Why no DLL execution.</b> The installer reads the manifest
/// JSON and uses <see cref="MetadataLoadContext"/> for the optional
/// validation pass (does the entry DLL really declare an
/// <see cref="IPlugin"/>?). At no point does it
/// <see cref="System.Activator.CreateInstance"/> the type — that would
/// run plugin code before the user has consented to enable the plugin.
/// Construction happens at app startup via <see cref="PluginService"/>,
/// or sooner for themes via the hot-load path
/// (<see cref="IPluginService.HotLoadTheme"/>).</para>
///
/// <para><b>Activation timing.</b> Theme plugins are hot-loaded the
/// moment the install completes: their ALC is created, the
/// <see cref="IThemePlugin"/> is instantiated, and the palettes become
/// available to the settings dropdown. The user can pick the new theme
/// without restarting. Non-theme plugins (codecs, samplers, UI
/// extensions) still require a restart — they wire into the audio
/// chain / codec registry / extension panels at startup, and rebuilding
/// those mid-session is out of scope for the install path.</para>
/// </summary>
public interface IPluginInstallerService
{
    /// <summary>Extract one <c>.zip</c>, read its <c>plugin.json</c>,
    /// move the extracted folder into <c>Plugins/</c> or <c>Themes/</c>
    /// per the manifest, and (for themes) hot-load the plugin. Never
    /// throws — failures surface via <see cref="PluginInstallResult.ErrorMessage"/>.</summary>
    Task<PluginInstallResult> InstallFromZipAsync(string zipPath, CancellationToken ct = default);
}

public sealed class PluginInstallerService : IPluginInstallerService
{
    /// <summary>Suffix appended to a destination folder when the installer
    /// can't replace it directly because the existing copy's DLLs are
    /// locked by the running process (Windows holds loaded assemblies
    /// open). The host's startup sweep — see
    /// <see cref="PluginService.ApplyPendingSwaps"/> — completes the
    /// rename before any plugin loads, at the point where no DLLs are
    /// locked yet. Same pattern VS Code / Visual Studio use for extension
    /// updates: stage now, swap on next launch.</summary>
    internal const string PendingSuffix = ".pending";

    private readonly IPluginService _pluginService;

    public PluginInstallerService(IPluginService pluginService)
    {
        _pluginService = pluginService;
    }

    public async Task<PluginInstallResult> InstallFromZipAsync(string zipPath, CancellationToken ct = default)
    {
        // Cross-platform-safe filename extraction. zipPath comes from the
        // user (drag-drop, CLI, bundled-plugins import) and may have been
        // typed or pasted with the "wrong" separator for the current OS
        // — most commonly a Windows-formatted path used on macOS. The
        // platform Path.GetFileName looks ONLY for the current OS's
        // separator, so an "X:\\path\\foo.zip" on Unix would come back as
        // the entire string (no '/' in it). Use a separator-agnostic
        // helper for everything user-facing; once we're under our own
        // %TEMP%/GMSoundBoard-Install/<guid>/ root, OS-native paths are
        // fine and Path.GetFileName works normally.
        var zipName = ExtractFileNameCrossPlatform(zipPath);
        if (!File.Exists(zipPath))
            return Fail(zipName, $"File not found: {zipPath}");

        // Stage extraction under %TEMP%/GMSoundBoard-Install/<guid>/ so a
        // failed install leaves no half-extracted residue in the real
        // plugin folder. Cleaned up in the finally block.
        var stagingRoot = Path.Combine(
            Path.GetTempPath(),
            "GMSoundBoard-Install",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(stagingRoot);
            try
            {
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, stagingRoot, overwriteFiles: true), ct);
            }
            catch (Exception ex)
            {
                return Fail(zipName, $"Could not extract zip: {ex.Message}");
            }

            // Normalize the staging folder: if the zip wrapped its
            // contents in a single top-level folder, drill into it.
            // Otherwise treat the staging root itself as the plugin
            // folder (flat zip — Send-to-Compressed-folder on the
            // publish output).
            var sourceFolder = NormalizeStaging(stagingRoot);

            // Manifest is mandatory. Read + validate before touching any
            // DLL or moving any files.
            if (!PluginManifestFile.TryLoad(sourceFolder, out var manifest, out var manifestError) || manifest == null)
                return Fail(zipName, manifestError ?? "Could not read plugin manifest.");

            // Optional sanity pass: reflect on the entry DLL with
            // MetadataLoadContext to confirm it declares an IPlugin and
            // that the manifest's IsTheme flag matches the actual
            // implemented interface. Mismatches are warnings, not hard
            // failures — the runtime scanner will surface a load error
            // later if the type really is broken.
            var entryDllPath = Path.Combine(sourceFolder, manifest.EntryDll);
            try
            {
                ValidateEntryDll(entryDllPath, sourceFolder, manifest);
            }
            catch (Exception ex)
            {
                return Fail(zipName, $"Could not validate entry DLL '{manifest.EntryDll}': {ex.Message}");
            }

            // Resolve the install destination from the manifest's
            // IsTheme flag.
            var pluginService = _pluginService as PluginService
                ?? throw new InvalidOperationException("PluginService is not the expected concrete type — installer can't resolve install paths.");
            var destRoot = manifest.IsTheme ? pluginService.ThemesFolder : pluginService.PluginsFolder;
            Directory.CreateDirectory(destRoot);
            var destFolder = Path.Combine(destRoot, manifest.GetSafeFolderName());

            // Decide replace-vs-side-by-side by scanning existing
            // installed plugin folders for their manifests. The match
            // key is (publisher, id), NOT the destination folder path —
            // a legacy folder might have a different layout, or sit in
            // the opposite folder (Plugins vs Themes) if the author's
            // isTheme flag changed across versions, and we still want
            // to treat that as an upgrade.
            var (existingLineageFolder, existingLineageManifest) = FindExistingLineage(pluginService, manifest);
            var sameIdOtherPublisher = existingLineageFolder == null
                ? FindSameIdDifferentPublisher(pluginService, manifest)
                : null;

            PluginInstallAction action;
            string? replacedVersion = null;
            string? sideBySideWith = null;
            bool stagedForRestart = false;

            if (existingLineageFolder != null)
            {
                // Upgrade path: same (publisher, id). Try to wipe the
                // existing folder so the new one can move in cleanly.
                // On Windows the existing copy's DLLs may be locked by
                // the running process's PluginLoadContext — direct delete
                // throws "Access denied." On that failure we fall back
                // to the staged-replace path below.
                try
                {
                    Directory.Delete(existingLineageFolder, recursive: true);
                }
                catch (Exception ex) when (IsFileLockException(ex))
                {
                    Log.Info("Plugin",
                        $"Existing plugin at {existingLineageFolder} is locked by the running process — staging for replace on next restart.");
                    stagedForRestart = true;
                }
                catch (Exception ex)
                {
                    return Fail(zipName, $"Failed to remove previous version at {existingLineageFolder}: {ex.Message}");
                }
                replacedVersion = existingLineageManifest?.Version;
                action = PluginInstallAction.Replaced;
            }
            else if (sameIdOtherPublisher != null)
            {
                // Side-by-side: someone else's plugin already claims
                // this id. The folder name is publisher-prefixed so
                // there's no collision on disk. Note the existing
                // publisher in the status so the user can tell the two
                // apart in the settings UI.
                sideBySideWith = sameIdOtherPublisher.Publisher;
                action = PluginInstallAction.InstalledAlongside;
            }
            else
            {
                action = PluginInstallAction.Installed;
            }

            // Stale-folder cleanup (skipped on the staged-replace path —
            // the locked folder IS the destination there). For the normal
            // path: if a destination folder exists despite no lineage
            // match (broken manifest, manual fiddling), clear it.
            if (!stagedForRestart && Directory.Exists(destFolder))
            {
                try
                {
                    Directory.Delete(destFolder, recursive: true);
                }
                catch (Exception ex) when (IsFileLockException(ex))
                {
                    // Same handling — a locked stale folder means we
                    // restart-swap the same way.
                    Log.Info("Plugin",
                        $"Destination folder {destFolder} is locked by the running process — staging for swap on next restart.");
                    stagedForRestart = true;
                }
                catch (Exception ex)
                {
                    return Fail(zipName, $"Failed to remove stale folder at {destFolder}: {ex.Message}");
                }
            }

            // Pick the actual on-disk target. Staged-replace paths land
            // under `<destFolder>.pending`; the host's startup sweep
            // promotes them to `destFolder` after the existing DLLs
            // unlock (i.e. after the process restarts).
            var finalTarget = stagedForRestart
                ? destFolder + PendingSuffix
                : destFolder;

            // A previous failed install may have left an old .pending
            // around. Clear it so we don't append to it.
            if (Directory.Exists(finalTarget) && stagedForRestart)
            {
                try { Directory.Delete(finalTarget, recursive: true); }
                catch (Exception ex)
                {
                    return Fail(zipName, $"Failed to clear stale pending folder at {finalTarget}: {ex.Message}");
                }
            }

            try
            {
                MoveDirectory(sourceFolder, finalTarget);
            }
            catch (Exception ex)
            {
                return Fail(zipName, $"Failed to move plugin into {finalTarget}: {ex.Message}");
            }

            var actionDescriptor = action switch
            {
                PluginInstallAction.Replaced            =>
                    stagedForRestart
                        ? $"replaces v{replacedVersion ?? "?"} on next restart (existing DLLs locked)"
                        : $"replaces v{replacedVersion ?? "?"}",
                PluginInstallAction.InstalledAlongside  => $"installed alongside {sideBySideWith}/{manifest.Id}",
                _                                       => "fresh install",
            };
            Log.Info("Plugin", $"Installed '{manifest.Name}' ({manifest.Publisher}/{manifest.Id}) v{manifest.Version} → {finalTarget} ({actionDescriptor})");

            // Themes get hot-loaded right away so their palettes appear
            // in the settings dropdown without restart. Non-theme
            // plugins are surfaced as pending-install rows; their ALC
            // doesn't spin up until the next launch's DiscoverAndLoad.
            //
            // Staged-for-restart installs SKIP hot-load: the new DLL is
            // sitting in <destFolder>.pending and won't move into place
            // until the next launch sweeps. Hot-loading from there would
            // either fail (manifest scanner looks for the canonical
            // folder name) or succeed but leave us double-loaded once the
            // swap fires.
            if (manifest.IsTheme && !stagedForRestart)
            {
                var hotLoadError = pluginService.HotLoadTheme(destFolder);
                if (hotLoadError != null)
                {
                    // Don't fail the install — the folder is in place,
                    // so a restart will still pick it up. Just warn so
                    // the user knows a restart is needed for THIS theme
                    // specifically.
                    Log.Warn("Plugin", $"Theme installed at {destFolder} but hot-load failed: {hotLoadError}. Restart to activate.");
                }
            }
            else
            {
                pluginService.AddPendingInstall(new PluginMetadata
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version,
                    Author = manifest.Author,
                    Description = manifest.Description,
                    FilePath = Path.Combine(destFolder, manifest.EntryDll),
                    IsLoaded = false,
                    LoadFailed = false,
                    IsTheme = false,
                });
            }

            return new PluginInstallResult(
                ZipFileName: zipName,
                Success: true,
                PluginName: manifest.Name,
                PluginId: manifest.Id,
                PluginPublisher: manifest.Publisher,
                IsTheme: manifest.IsTheme,
                DestinationFolder: destFolder,
                Action: action,
                ReplacedVersion: replacedVersion,
                SideBySideWith: sideBySideWith,
                ErrorMessage: null);
        }
        finally
        {
            // Best-effort cleanup of staging — leftover temp files are
            // tolerable but undesirable.
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); }
            catch (Exception ex) { Log.Warn("Plugin", $"Couldn't clean staging folder {stagingRoot}: {ex.Message}"); }
        }
    }

    /// <summary>If the staging folder has exactly one subfolder and no
    /// top-level files, the zip used the wrapper-folder shape — return
    /// the inner folder. Otherwise return the staging folder itself
    /// (flat zip with files at the root).</summary>
    private static string NormalizeStaging(string staging)
    {
        var dirs = Directory.GetDirectories(staging);
        var topLevelFiles = Directory.GetFiles(staging, "*", SearchOption.TopDirectoryOnly);
        if (dirs.Length == 1 && topLevelFiles.Length == 0)
            return dirs[0];
        return staging;
    }

    /// <summary>Sanity-check the entry DLL declared by the manifest.
    /// Loads the DLL via <see cref="MetadataLoadContext"/> (read-only
    /// metadata reflection — never executes plugin code) and verifies
    /// it contains a concrete type implementing
    /// <c>SoundBoard.PluginApi.IPlugin</c>. Mismatches between the
    /// manifest's <c>isTheme</c> and the actual implemented interface
    /// produce a warning but don't fail — the runtime scanner is the
    /// authoritative check.</summary>
    private static void ValidateEntryDll(string entryDllPath, string pluginFolder, PluginManifestFile manifest)
    {
        if (!File.Exists(entryDllPath))
            throw new FileNotFoundException($"Entry DLL not found: {entryDllPath}");

        var candidatePaths = new List<string>();
        candidatePaths.AddRange(Directory.GetFiles(pluginFolder, "*.dll", SearchOption.AllDirectories));
        // Probe every DLL sitting in the host's runtime directory. Plugins
        // that ExcludeAssets="runtime" on shared deps (NAudio.Core, Avalonia,
        // SoundBoard.PluginApi, etc.) intentionally omit those DLLs from their
        // own zip — they rely on the host's copies being there at runtime.
        // The metadata validator has to see those copies too, otherwise it
        // fails with "Could not find assembly 'NAudio.Core, Version=2.3.0.0,
        // ...'" even though the plugin is perfectly valid against the deployed
        // host. We use AppContext.BaseDirectory rather than
        // typeof(IPlugin).Assembly.Location because the latter returns an
        // empty string for assemblies embedded in a single-file published
        // app (IL3000), and the release publishes with
        // PublishSingleFile=true + IncludeNativeLibrariesForSelfExtract=true.
        // For both single-file and multi-file builds, BaseDirectory is the
        // host's runtime directory where SoundBoard.PluginApi.dll and its
        // siblings live.
        var hostBinDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(hostBinDir) && Directory.Exists(hostBinDir))
            candidatePaths.AddRange(Directory.GetFiles(hostBinDir, "*.dll"));
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        if (Directory.Exists(runtimeDir))
            candidatePaths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));

        // Plugin zips routinely ship their own copy of
        // SoundBoard.PluginApi.dll (CopyLocalLockFileAssemblies=true is
        // the standard setup for codec plugins so transitive deps
        // travel), so we now have at least TWO physical paths for the
        // same assembly identity — the staged plugin folder's copy and
        // the host's copy. PathAssemblyResolver refuses to accept that
        // and throws "has already been loaded into this
        // MetadataLoadContext". Dedupe by AssemblyName and keep the
        // first occurrence — the plugin folder is added first, so the
        // plugin's own copies of any matching deps win the resolution,
        // which is what we want for accurate metadata of the actual
        // shipped DLLs.
        var resolverPaths = DedupeByAssemblyName(candidatePaths);

        var resolver = new PathAssemblyResolver(resolverPaths);
        using var mlc = new MetadataLoadContext(resolver);
        var asm = mlc.LoadFromAssemblyPath(entryDllPath);

        bool foundIPlugin = false;
        bool foundIThemePlugin = false;
        foreach (var t in asm.GetTypes())
        {
            if (t.IsInterface || t.IsAbstract) continue;
            foreach (var iface in t.GetInterfaces())
            {
                // Match by full name — types in a MetadataLoadContext
                // live in a separate type system, so reference equality
                // against typeof(IPlugin) wouldn't work.
                if (iface.FullName == "SoundBoard.PluginApi.IPlugin") foundIPlugin = true;
                if (iface.FullName == "SoundBoard.PluginApi.IThemePlugin") foundIThemePlugin = true;
            }
        }

        if (!foundIPlugin)
            throw new InvalidOperationException(
                $"Entry DLL '{manifest.EntryDll}' does not declare a type implementing SoundBoard.PluginApi.IPlugin.");

        if (manifest.IsTheme && !foundIThemePlugin)
            Log.Warn("Plugin", $"Manifest for '{manifest.Id}' declares isTheme=true but '{manifest.EntryDll}' " +
                               "does not implement SoundBoard.PluginApi.IThemePlugin. Installing anyway; the " +
                               "runtime scanner will surface any load failure.");
        else if (!manifest.IsTheme && foundIThemePlugin)
            Log.Warn("Plugin", $"Manifest for '{manifest.Id}' declares isTheme=false but '{manifest.EntryDll}' " +
                               "implements IThemePlugin. Installing into the Plugins folder per the manifest, " +
                               "but the plugin should probably set isTheme=true.");
    }

    /// <summary>Filter a list of candidate assembly paths down to a
    /// list <see cref="PathAssemblyResolver"/> will accept: one path per
    /// <see cref="AssemblyName.Name"/>, preserving input order so
    /// earlier-listed copies win the de-duplication. Non-assembly DLLs
    /// (native bins, malformed PE images) are silently skipped — they
    /// can't be referenced from a managed plugin entry point anyway.</summary>
    private static List<string> DedupeByAssemblyName(IEnumerable<string> paths)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unidentified = new List<string>();
        foreach (var path in paths)
        {
            string? name = null;
            try { name = AssemblyName.GetAssemblyName(path).Name; }
            catch { /* not a managed assembly; skip identity check and pass through */ }

            if (string.IsNullOrEmpty(name))
            {
                unidentified.Add(path);
                continue;
            }
            if (!byName.ContainsKey(name))
                byName[name] = path;
        }
        var result = new List<string>(byName.Values);
        result.AddRange(unidentified);
        return result;
    }

    /// <summary>Cross-platform <c>Directory.Move</c> that falls back to
    /// copy + delete when source and destination are on different
    /// volumes (the BCL <c>Directory.Move</c> throws IOException in that
    /// case). On Linux/macOS this is rare; on Windows it happens when
    /// %TEMP% is on C: and %LocalAppData% is on a different drive.</summary>
    private static void MoveDirectory(string source, string dest)
    {
        try
        {
            Directory.Move(source, dest);
        }
        catch (IOException)
        {
            CopyDirectoryRecursive(source, dest);
            Directory.Delete(source, recursive: true);
        }
    }

    private static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(source))
            CopyDirectoryRecursive(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    /// <summary>Heuristic: did this exception come from a Windows file
    /// lock (DLL is open in a running process) rather than a real
    /// IO/permissions error? We classify <see cref="UnauthorizedAccessException"/>,
    /// <see cref="IOException"/> with HResult <c>0x80070020</c>
    /// (ERROR_SHARING_VIOLATION), and any IOException whose message
    /// contains "Access to the path" / "being used by another process"
    /// as "file-lock" so the installer can fall back to staged-replace
    /// instead of bubbling up a confusing error. Anything more exotic
    /// (disk full, network unreachable, antivirus quarantine) falls
    /// through to the normal error path.</summary>
    private static bool IsFileLockException(Exception ex)
    {
        if (ex is UnauthorizedAccessException) return true;
        if (ex is IOException io)
        {
            // ERROR_SHARING_VIOLATION (0x80070020) — the canonical
            // "file is in use" HRESULT on Windows.
            unchecked
            {
                if (io.HResult == (int)0x80070020) return true;
                if (io.HResult == (int)0x80070021) return true; // LOCK_VIOLATION
            }
            var msg = io.Message ?? "";
            if (msg.Contains("Access to the path", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Extract the filename from a possibly-foreign-format path,
    /// treating both '/' and '\\' as separators regardless of the current
    /// platform. <see cref="Path.GetFileName"/> looks only for the
    /// CURRENT OS's separator, so a Windows path on Unix
    /// (e.g. "X:\\does\\not\\exist.zip" — no '/' anywhere) would come
    /// back as the entire string. Necessary because zip paths passed
    /// into <see cref="InstallFromZipAsync"/> may originate from
    /// drag-drop or CLI input that doesn't match the host OS's
    /// conventions.</summary>
    private static string ExtractFileNameCrossPlatform(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        int lastSlash = path.LastIndexOf('/');
        int lastBackslash = path.LastIndexOf('\\');
        int cut = Math.Max(lastSlash, lastBackslash);
        return cut < 0 ? path : path.Substring(cut + 1);
    }

    private static PluginInstallResult Fail(string zipName, string reason)
    {
        Log.Warn("Plugin", $"Install rejected for '{zipName}': {reason}");
        return new PluginInstallResult(
            ZipFileName: zipName,
            Success: false,
            PluginName: null,
            PluginId: null,
            PluginPublisher: null,
            IsTheme: false,
            DestinationFolder: null,
            Action: PluginInstallAction.Failed,
            ReplacedVersion: null,
            SideBySideWith: null,
            ErrorMessage: reason);
    }

    /// <summary>Find the installed plugin folder (in either <c>Plugins\</c>
    /// or <c>Themes\</c>) whose manifest matches <paramref name="incoming"/>'s
    /// <c>(publisher, id)</c> lineage. Returns <c>(null, null)</c> if no
    /// match — the install is a fresh one. Returns the matching folder
    /// path and parsed manifest otherwise; caller deletes the folder and
    /// surfaces the old version string in the install result. Internal
    /// so the test suite can verify lineage detection without going
    /// through the full extract → validate → move pipeline.</summary>
    internal static (string? folder, PluginManifestFile? manifest) FindExistingLineage(
        PluginService pluginService, PluginManifestFile incoming)
    {
        foreach (var candidate in EnumeratePluginFolders(pluginService))
        {
            if (!PluginManifestFile.TryLoad(candidate, out var existing, out _) || existing == null)
                continue;
            if (incoming.IsSameLineageAs(existing))
                return (candidate, existing);
        }
        return (null, null);
    }

    /// <summary>Find the first installed plugin claiming the same
    /// <c>id</c> as <paramref name="incoming"/> but a different
    /// <c>publisher</c>. Used purely to populate the status message
    /// ("installed alongside com.acme/eq") — the install always
    /// proceeds because the destination folder name is namespaced by
    /// the incoming publisher.</summary>
    internal static PluginManifestFile? FindSameIdDifferentPublisher(
        PluginService pluginService, PluginManifestFile incoming)
    {
        foreach (var candidate in EnumeratePluginFolders(pluginService))
        {
            if (!PluginManifestFile.TryLoad(candidate, out var existing, out _) || existing == null)
                continue;
            if (string.Equals(existing.Id, incoming.Id, StringComparison.Ordinal) &&
                !string.Equals(existing.Publisher, incoming.Publisher, StringComparison.Ordinal))
                return existing;
        }
        return null;
    }

    private static IEnumerable<string> EnumeratePluginFolders(PluginService pluginService)
    {
        foreach (var root in new[] { pluginService.PluginsFolder, pluginService.ThemesFolder })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var sub in Directory.GetDirectories(root))
            {
                // Skip the reserved per-plugin data subfolder
                // (Plugins\Data\<id> / Themes\Data\<id>) — never a
                // plugin in its own right.
                if (string.Equals(Path.GetFileName(sub), "Data", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return sub;
            }
        }
    }
}
