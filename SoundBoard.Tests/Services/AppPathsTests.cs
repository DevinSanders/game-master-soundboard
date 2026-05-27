using System;
using System.IO;
using SoundBoard.Core;

namespace SoundBoard.Tests.Services;

/// <summary>
/// Pins the contract that <see cref="AppPaths"/> respects the
/// <c>GMSOUNDBOARD_APPDATA</c> env var — the mechanism contributors
/// rely on to run sandboxed dev sessions without polluting their
/// installed copy. A regression here would silently start writing dev
/// runs back into <c>%LocalAppData%</c>; manual smoke testing wouldn't
/// catch it because the path resolution looks identical until you
/// notice your real settings got overwritten.
///
/// <para>Each test snapshots the current root, runs an override, and
/// restores afterwards so test ordering doesn't matter.</para>
///
/// <para><b>Collection membership:</b> joined to
/// <c>AppPathsGlobalState</c> because <see cref="AppPaths.OverrideForTests"/>
/// mutates a process-static field (<c>_root</c>). Any test that touches
/// that field must serialize with every other such test; xUnit v3
/// parallelises test classes by default, and on Linux the resulting
/// race surfaced as a flaky AppPaths.Root mismatch with the
/// LineageFixture used inside <c>PluginInstallerServiceTests</c>. The
/// shared collection forces both classes to run sequentially.</para>
/// </summary>
[Collection(SoundBoard.Tests.Collections.AppPathsGlobalState.Name)]
public class AppPathsTests
{
    [Fact]
    public void OverrideForTests_PointsAllSubpathsAtNewRoot()
    {
        var temp = Path.Combine(Path.GetTempPath(), "AppPathsTest-" + Guid.NewGuid().ToString("N"));
        var previous = AppPaths.OverrideForTests(temp);
        try
        {
            AppPaths.Root.Should().Be(Path.GetFullPath(temp));
            AppPaths.PluginsFolder.Should().Be(Path.Combine(AppPaths.Root, "Plugins"));
            AppPaths.ThemesFolder.Should().Be(Path.Combine(AppPaths.Root, "Themes"));
            AppPaths.LibrariesFolder.Should().Be(Path.Combine(AppPaths.Root, "Libraries"));
            AppPaths.LogsFolder.Should().Be(Path.Combine(AppPaths.Root, "logs"));
            AppPaths.SettingsFilePath.Should().Be(Path.Combine(AppPaths.Root, "settings.json"));
            AppPaths.DefaultDatabasePath.Should().Be(Path.Combine(AppPaths.Root, "Libraries", "default.db"));
        }
        finally
        {
            AppPaths.OverrideForTests(previous);
        }
    }

    [Fact]
    public void RefreshFromEnvironment_AbsoluteEnvVar_ReplacesRoot()
    {
        var previousEnv = Environment.GetEnvironmentVariable(AppPaths.AppDataEnvVar);
        var previousRoot = AppPaths.Root;
        var temp = Path.Combine(Path.GetTempPath(), "AppPathsEnvTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.AppDataEnvVar, temp);
            AppPaths.RefreshFromEnvironment();
            AppPaths.Root.Should().Be(Path.GetFullPath(temp));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.AppDataEnvVar, previousEnv);
            AppPaths.OverrideForTests(previousRoot);
        }
    }

    [Fact]
    public void RefreshFromEnvironment_RelativeEnvVar_ResolvesAgainstBinaryFolder()
    {
        // The "./.debug-appdata" launchSettings.json default — a relative
        // path that must land next to the running binary, not at the
        // working directory. Otherwise a contributor running tests with
        // a different CWD would get different data folders.
        var previousEnv = Environment.GetEnvironmentVariable(AppPaths.AppDataEnvVar);
        var previousRoot = AppPaths.Root;
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.AppDataEnvVar, "./.debug-appdata");
            AppPaths.RefreshFromEnvironment();
            AppPaths.Root.Should().Be(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".debug-appdata")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.AppDataEnvVar, previousEnv);
            AppPaths.OverrideForTests(previousRoot);
        }
    }

    [Fact]
    public void RefreshFromEnvironment_EmptyEnvVar_FallsBackToPlatformDefault()
    {
        // Clearing the env var should restore the LocalApplicationData
        // path — the documented escape hatch when a contributor wants
        // their dev run to use their real install's data.
        var previousEnv = Environment.GetEnvironmentVariable(AppPaths.AppDataEnvVar);
        var previousRoot = AppPaths.Root;
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.AppDataEnvVar, "");
            AppPaths.RefreshFromEnvironment();

            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameMasterSoundBoard");
            AppPaths.Root.Should().Be(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.AppDataEnvVar, previousEnv);
            AppPaths.OverrideForTests(previousRoot);
        }
    }
}
