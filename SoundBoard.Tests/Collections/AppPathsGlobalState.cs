namespace SoundBoard.Tests.Collections;

/// <summary>
/// xUnit v3 collection definition that forces every test class touching
/// <see cref="SoundBoard.Core.AppPaths"/>'s process-static <c>_root</c>
/// field to run sequentially.
///
/// <para><b>Why a collection.</b> xUnit v3 parallelises test classes by
/// default (a perf win — runs ~4× faster on a typical 8-core box). The
/// downside is that any two classes mutating the same process-static
/// state can stomp each other under parallel execution. <c>AppPaths</c>
/// is one such hotspot: <see cref="SoundBoard.Core.AppPaths.OverrideForTests"/>
/// swaps the shared <c>_root</c> field, so a test class that calls it
/// during its own [Fact] will see the wrong root if another class also
/// overrides between its own override and its assertion.</para>
///
/// <para>The race surfaced as a Linux-only failure: scheduler ordering
/// happens to interleave <see cref="SoundBoard.Tests.Services.AppPathsTests"/>
/// with the <c>LineageFixture</c> in
/// <see cref="SoundBoard.Tests.Services.PluginInstallerServiceTests"/>
/// in a way that exposes the stomp. Windows's scheduler tends to
/// finish one class before starting the other, hiding the bug. The
/// shared collection name pins both classes onto the same xUnit
/// "collection," which by definition runs sequentially.</para>
///
/// <para><b>Membership policy:</b> add a class to this collection
/// whenever it (or any of its fixtures) calls
/// <see cref="SoundBoard.Core.AppPaths.OverrideForTests"/> or
/// <see cref="SoundBoard.Core.AppPaths.RefreshFromEnvironment"/>.
/// Classes that only READ <c>AppPaths.Root</c> are safe and don't need
/// to join — the override-followed-by-read is the racy pattern.</para>
/// </summary>
[CollectionDefinition(Name)]
public class AppPathsGlobalState
{
    public const string Name = "AppPaths global state";
}
