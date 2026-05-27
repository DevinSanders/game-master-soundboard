using Avalonia.Headless.XUnit;
using SoundBoard.UI.Services;

namespace SoundBoard.Tests.UI;

/// <summary>
/// Pins <see cref="EditPersistence"/>'s contract: scheduled saves are
/// last-write-wins per key, burst gates the timer, Flush is unconditional.
/// Every editor VM in the app relies on this for "no Save button" behavior,
/// so a regression here would silently start dropping user edits.
///
/// <para><see cref="EditPersistence"/> uses an Avalonia <see cref="DispatcherTimer"/>,
/// so tests must run on the dispatcher thread — hence <c>[AvaloniaFact]</c>.</para>
/// </summary>
public class EditPersistenceTests
{
    [AvaloniaFact]
    public void Flush_RunsPendingActions()
    {
        var persist = new EditPersistence();
        int calls = 0;
        persist.Schedule("k", () => calls++);

        persist.Flush();

        calls.Should().Be(1);
    }

    [AvaloniaFact]
    public void Schedule_SameKey_LastWriteWins()
    {
        // 100 slider ticks under the same key should collapse to 1 DB write
        // with the latest value — that's how "drag the slider, get one save"
        // works without spamming the database.
        var persist = new EditPersistence();
        int firstCalls = 0, lastCalls = 0;

        for (int i = 0; i < 99; i++) persist.Schedule("track-volume", () => firstCalls++);
        persist.Schedule("track-volume", () => lastCalls++);

        persist.Flush();

        firstCalls.Should().Be(0, "older actions for the same key are replaced");
        lastCalls.Should().Be(1, "only the latest scheduled action under each key runs");
    }

    [AvaloniaFact]
    public void DifferentKeys_BothRunOnFlush()
    {
        var persist = new EditPersistence();
        int a = 0, b = 0;
        persist.Schedule("a", () => a++);
        persist.Schedule("b", () => b++);

        persist.Flush();

        a.Should().Be(1);
        b.Should().Be(1);
    }

    [AvaloniaFact]
    public void Burst_HoldsSavesUntilEnd()
    {
        var persist = new EditPersistence();
        int calls = 0;

        persist.BeginBurst();
        persist.Schedule("k", () => calls++);

        // During burst, the debounce timer is stopped — waiting wouldn't
        // fire the action. Verify it stays pending.
        Thread.Sleep(400); // > PersistDebounce
        calls.Should().Be(0, "burst is in progress; nothing should have fired yet");

        persist.EndBurst();
        calls.Should().Be(1);
    }

    [AvaloniaFact]
    public void Burst_IsReentrant()
    {
        var persist = new EditPersistence();
        int calls = 0;
        persist.Schedule("k", () => calls++);

        persist.BeginBurst();
        persist.BeginBurst();
        persist.EndBurst();

        // One outer burst still active.
        calls.Should().Be(0);

        persist.EndBurst();
        calls.Should().Be(1, "EndBurst flushes only when depth returns to zero");
    }

    [AvaloniaFact]
    public void Flush_WithNoPending_IsNoop()
    {
        var persist = new EditPersistence();
        var act = () => persist.Flush();
        act.Should().NotThrow();
    }

    [AvaloniaFact]
    public void Flush_SwallowsActionExceptions()
    {
        // A throwing save callback must not prevent other keys from
        // running, or leave the queue in a bad state.
        var persist = new EditPersistence();
        bool secondRan = false;
        persist.Schedule("bad", () => throw new InvalidOperationException("boom"));
        persist.Schedule("good", () => secondRan = true);

        var act = () => persist.Flush();
        act.Should().NotThrow("the EditPersistence catches per-action exceptions");
        secondRan.Should().BeTrue();
    }
}
