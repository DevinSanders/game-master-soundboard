using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

// Avalonia.Headless.XUnit looks for this assembly-level attribute to find
// the test Application. [AvaloniaFact] / [AvaloniaTheory] then call
// BuildAvaloniaApp() to bootstrap the headless platform per test, complete
// with a real Avalonia dispatcher and pointer source — no hand-rolled
// fixture / dispatcher thread needed.
[assembly: AvaloniaTestApplication(typeof(SoundBoard.Tests.Fakes.TestApplication))]

namespace SoundBoard.Tests.Fakes;

/// <summary>
/// Minimal Avalonia <see cref="Application"/> for headless tests. The
/// FluentTheme is included so visual-tree lookups don't trip on missing
/// default styles. Data templates / app-specific styles from
/// <c>App.axaml</c> are NOT loaded — the tests we care about exercise
/// VMs, services, and pointer-event discriminators, not rendering.
/// </summary>
public class TestApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    /// <summary>Discovered by reflection by Avalonia.Headless.XUnit's
    /// test framework. Configures the headless platform and returns the
    /// <see cref="AppBuilder"/> for the runner to start.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
