using System.Diagnostics;
using SoundBoard.Core.Logging;

namespace SoundBoard.Tests.Logging;

/// <summary>
/// Smoke tests for the Phase 1 #4 fix: the static <see cref="Log"/>
/// producer side must NOT block on disk I/O. The drainer thread runs
/// the actual <c>File.AppendAllText</c> off the calling thread, so
/// even a slow disk doesn't stall the audio thread that called
/// <c>Log.Warn</c> / <c>Log.Error</c>.
///
/// <para>These tests hit real disk (the production log file). That's
/// tolerable for a one-time test run — the cost is a few hundred extra
/// lines in <c>gmsound.log</c>, drained asynchronously.</para>
/// </summary>
public class LogTests
{
    [Fact]
    public void Producer_DoesNotBlockOnIO_EvenForManyCalls()
    {
        // Prime the drainer: first call triggers lazy init.
        Log.Info("test", "warmup");

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
            Log.Error("test", $"perf-probe {i}");
        sw.Stop();

        // 1000 enqueues + 1000 string formats should run in single-digit
        // milliseconds. Pre-fix this was 1000 × (lock + AppendAllText)
        // which on a slow disk could be hundreds of ms.
        // Generous threshold to avoid flakes on slow CI.
        sw.ElapsedMilliseconds.Should().BeLessThan(200,
            "the producer side must enqueue without blocking on disk I/O");
    }

    [Fact]
    public void Warn_AcceptsNullException()
    {
        // Defensive contract check: callers commonly pass an Exception or
        // null. Both must round-trip without throwing.
        Action withNull = () => Log.Warn("test", "no exception");
        Action withEx = () => Log.Warn("test", "with exception", new InvalidOperationException("boom"));
        withNull.Should().NotThrow();
        withEx.Should().NotThrow();
    }
}
