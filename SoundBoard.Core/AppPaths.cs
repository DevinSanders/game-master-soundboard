using System;
using System.IO;

namespace SoundBoard.Core;

/// <summary>
/// Single source of truth for every well-known on-disk path the app
/// uses (settings.json, plugin folders, the libraries folder, the log
/// directory). One indirection means tests / dev runs / sandboxed
/// deployments can override the data root in one place instead of every
/// caller separately.
///
/// <para><b>Default location.</b> <c>%LocalAppData%\GameMasterSoundBoard\</c>
/// on Windows, the platform equivalent of
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> elsewhere
/// (<c>~/.local/share/</c> on Linux, <c>~/Library/Application Support/</c>
/// on macOS).</para>
///
/// <para><b>Dev override.</b> The
/// <c>GMSOUNDBOARD_APPDATA</c> environment variable, when set, fully
/// replaces the default root. This is how the Desktop / UI
/// <c>launchSettings.json</c> profiles isolate their data into
/// <c>./.debug-appdata/</c> next to the running binary — contributors
/// can build + run the app without polluting (or being polluted by)
/// their installed copy's settings, plugins, libraries, or logs.
/// Relative paths in the env var are resolved against
/// <see cref="AppContext.BaseDirectory"/> (the running executable's
/// folder), so a single <c>"./.debug-appdata"</c> entry works
/// regardless of the user's working directory.</para>
///
/// <para>The lookup happens once at static init; subsequent env-var
/// changes don't affect the resolved <see cref="Root"/>. Tests that
/// need a different root should call <see cref="OverrideForTests"/>.</para>
/// </summary>
public static class AppPaths
{
    /// <summary>Env var name. Set this to redirect the entire app-data
    /// tree to an alternative folder. Empty / unset means "use the
    /// platform default."</summary>
    public const string AppDataEnvVar = "GMSOUNDBOARD_APPDATA";

    private static string _root = ResolveRoot();

    /// <summary>Root folder for all app data. Subfolders are exposed by
    /// the named properties below — prefer those over building paths
    /// out of <see cref="Root"/> by hand.</summary>
    public static string Root => _root;

    public static string PluginsFolder  => Path.Combine(_root, "Plugins");
    public static string ThemesFolder   => Path.Combine(_root, "Themes");
    public static string LibrariesFolder => Path.Combine(_root, "Libraries");
    public static string LogsFolder     => Path.Combine(_root, "logs");
    public static string SettingsFilePath => Path.Combine(_root, "settings.json");

    /// <summary>Default database file used when
    /// <c>AppSettings.CurrentLibraryPath</c> hasn't been set yet
    /// (first launch).</summary>
    public static string DefaultDatabasePath => Path.Combine(LibrariesFolder, "default.db");

    /// <summary>Re-resolve the root from the current environment. Tests
    /// can call this after mutating <c>GMSOUNDBOARD_APPDATA</c>;
    /// production code never needs it (the env var is set before
    /// process start).</summary>
    public static void RefreshFromEnvironment()
    {
        _root = ResolveRoot();
    }

    /// <summary>Pin the root to an explicit path for the lifetime of a
    /// test. Returns the previous root so the caller can restore it in
    /// teardown. Not for production use.</summary>
    public static string OverrideForTests(string path)
    {
        var previous = _root;
        _root = Path.GetFullPath(path);
        return previous;
    }

    private static string ResolveRoot()
    {
        var envOverride = Environment.GetEnvironmentVariable(AppDataEnvVar);
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            // Relative paths in the env var resolve against the running
            // binary's folder, NOT the working directory — keeps the
            // override stable regardless of how the app was launched
            // (CLI, double-click, IDE debugger, scheduled task).
            if (!Path.IsPathRooted(envOverride))
                envOverride = Path.Combine(AppContext.BaseDirectory, envOverride);
            return Path.GetFullPath(envOverride);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "GameMasterSoundBoard");
    }
}
