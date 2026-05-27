using SoundBoard.Core.Logging;
using System;
using System.IO;
using System.Linq;

namespace SoundBoard.Core.Services;

/// <summary>
/// First-launch importer for plugin <c>.zip</c> files shipped alongside
/// the host's installer. Inno Setup / the macOS .app / the Linux packagers
/// all copy the published <c>bundled-plugins/</c> subfolder verbatim into
/// the install directory; this importer scans it on startup and, on the
/// first launch that matches a given host version, installs each zip via
/// <see cref="IPluginInstallerService.InstallFromZipAsync"/> — the same
/// pipeline the user's drag-drop install uses. After that the plugins
/// live in the user's per-user plugin folder and the bundled-plugins
/// folder under the install dir is dead weight (but harmless to keep
/// around for re-import after data folder wipes).
///
/// <para><b>Why this runs BEFORE PluginService.DiscoverAndLoad.</b> Discovery
/// scans the user's plugin folder once at startup. If we ran the importer
/// after, the freshly-installed plugins wouldn't load until next launch.
/// Running before means: drop in, install, then immediately discover, so
/// bundled plugins are active from the first frame the user sees.</para>
///
/// <para><b>Idempotency.</b> A marker file at
/// <c>&lt;app-data&gt;/.bundled-imported</c> records the host version
/// that did the last import. If the marker version matches the current
/// host version, the importer is a no-op. Bumping the host version
/// re-runs the import (lineage-dedup in <see cref="PluginInstallerService"/>
/// means already-installed plugins of the same <c>(publisher, id)</c>
/// get upgraded if the bundled zip is newer; otherwise it's a fast
/// "replace with self" no-op).</para>
///
/// <para><b>Non-fatal.</b> Every error is logged and swallowed. A
/// corrupted bundled zip cannot prevent the rest of the app from
/// starting; worst case the affected plugin is missing on first launch
/// and the user installs it manually via Settings → Plugin Manager.</para>
/// </summary>
public static class BundledPluginImporter
{
    /// <summary>Subfolder under the install directory where the
    /// installer drops bundled <c>.zip</c> files. Hard-coded here and
    /// in every packaging script — single string, no overrides.</summary>
    public const string BundledPluginsSubfolder = "bundled-plugins";

    /// <summary>Marker file written under <see cref="AppPaths.Root"/>
    /// after a successful import pass. Contents: one line carrying the
    /// host version that ran the import. Used as an idempotency key.</summary>
    public const string MarkerFileName = ".bundled-imported";

    /// <summary>Scan <c>&lt;install-dir&gt;/bundled-plugins/*.zip</c> and
    /// install each through <paramref name="installer"/> if we haven't
    /// already done so for this <paramref name="hostVersion"/>. Returns
    /// the number of zips that were processed (0 on no-op runs or if the
    /// bundled-plugins folder is missing — both normal in dev builds and
    /// portable extracts).</summary>
    public static int ImportIfNeeded(IPluginInstallerService installer, string hostVersion)
    {
        var bundledDir = Path.Combine(AppContext.BaseDirectory, BundledPluginsSubfolder);
        if (!Directory.Exists(bundledDir))
        {
            // Common in dev builds, in portable extracts that don't ship
            // bundled-plugins, and in older installers built before this
            // feature existed. Not an error — just nothing to import.
            return 0;
        }

        var markerPath = Path.Combine(AppPaths.Root, MarkerFileName);
        var markerVersion = TryReadMarkerVersion(markerPath);
        if (string.Equals(markerVersion, hostVersion, StringComparison.Ordinal))
        {
            Log.Debug("BundledPlugins",
                $"Marker at {markerPath} already records host v{hostVersion}; skipping import.");
            return 0;
        }

        var zips = Directory.GetFiles(bundledDir, "*.zip", SearchOption.TopDirectoryOnly);
        if (zips.Length == 0)
        {
            Log.Debug("BundledPlugins", $"No .zip files under {bundledDir}; recording marker and skipping.");
            WriteMarker(markerPath, hostVersion);
            return 0;
        }

        Log.Info("BundledPlugins",
            markerVersion == null
                ? $"First launch with bundled plugins — importing {zips.Length} zip(s) from {bundledDir}"
                : $"Host upgraded from v{markerVersion} to v{hostVersion} — re-importing {zips.Length} bundled zip(s) from {bundledDir}");

        int processed = 0;
        foreach (var zipPath in zips)
        {
            try
            {
                var result = installer.InstallFromZipAsync(zipPath).GetAwaiter().GetResult();
                processed++;
                if (result.Success)
                {
                    Log.Info("BundledPlugins",
                        $"  ✓ {Path.GetFileName(zipPath)} → {result.PluginName} ({result.Action})");
                }
                else
                {
                    Log.Warn("BundledPlugins",
                        $"  ✗ {Path.GetFileName(zipPath)} — {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                Log.Error("BundledPlugins",
                    $"  ✗ {Path.GetFileName(zipPath)} threw during install", ex);
            }
        }

        WriteMarker(markerPath, hostVersion);
        return processed;
    }

    private static string? TryReadMarkerVersion(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var first = File.ReadLines(path).FirstOrDefault();
            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void WriteMarker(string path, string hostVersion)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path,
                $"{hostVersion}{Environment.NewLine}" +
                $"# Recorded {DateTime.UtcNow:O}{Environment.NewLine}" +
                $"# Bundled-plugin import marker. Delete this file to force a{Environment.NewLine}" +
                $"# re-import on the next launch (e.g. after manually wiping the{Environment.NewLine}" +
                $"# Plugins/ or Themes/ folders).{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            // Non-fatal. Worst case we re-import every launch, which the
            // PluginInstallerService lineage dedup handles as a no-op.
            Log.Warn("BundledPlugins", $"Could not write marker {path}: {ex.Message}");
        }
    }
}
