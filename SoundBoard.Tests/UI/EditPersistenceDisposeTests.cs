using Avalonia.Headless.XUnit;
using SoundBoard.UI.Services;

namespace SoundBoard.Tests.UI;

/// <summary>
/// Phase R1 tests for <see cref="EditPersistence.Dispose"/>. Pins the
/// lifecycle contract that closes the latent leak identified in the
/// architectural audit: every editor VM owns an <c>EditPersistence</c>;
/// without an explicit teardown its <c>DispatcherTimer</c> (whose
/// <c>Tick</c> handler captures <c>this</c>) holds the VM alive forever
/// via the global dispatcher's timer list.
/// </summary>
public class EditPersistenceDisposeTests
{
    [AvaloniaFact]
    public void Dispose_FlushesPendingSaves()
    {
        // Flush-on-Dispose is the entire reason Dispose exists. A
        // debounced volume save sitting in _pending must not be dropped
        // just because the user closed the editor before the 300 ms
        // timer fired.
        var p = new EditPersistence();
        int saves = 0;
        p.Schedule("k", () => saves++);

        p.Dispose();

        saves.Should().Be(1);
    }

    [AvaloniaFact]
    public void Dispose_StopsTimer_FurtherScheduleIsNoop()
    {
        // After Dispose, a late slider tick that races a window close
        // shouldn't resurrect the torn-down timer or pin the VM via
        // a new pending action.
        var p = new EditPersistence();
        p.Dispose();
        int saves = 0;

        p.Schedule("k", () => saves++);

        // The Schedule call must NOT have fired the action synchronously,
        // and Flush() (which Schedule would have started a timer for) is
        // disabled because Dispose is idempotent + already cleared.
        saves.Should().Be(0);
    }

    [AvaloniaFact]
    public void Dispose_IsIdempotent()
    {
        var p = new EditPersistence();
        p.Schedule("k", () => { });

        var act = () =>
        {
            p.Dispose();
            p.Dispose();
            p.Dispose();
        };

        act.Should().NotThrow();
    }

    [AvaloniaFact]
    public void Dispose_DuringBurst_StillFlushesPending()
    {
        // BeginBurst → Schedule → Dispose (without EndBurst): the
        // pending saves should still flush, because the user closed
        // the editor while holding the slider — losing those edits
        // would be a regression.
        var p = new EditPersistence();
        int saves = 0;
        p.BeginBurst();
        p.Schedule("k", () => saves++);

        p.Dispose();

        saves.Should().Be(1);
    }
}
