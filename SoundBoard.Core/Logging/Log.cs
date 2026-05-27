using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SoundBoard.Core.Logging;

/// <summary>Severity for <see cref="Log"/> messages. Debug entries are
/// suppressed unless the process was launched with <c>--debug</c>.</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>
/// Thread-safe app-wide logger.
///
/// Normal mode (the default): only <see cref="LogLevel.Error"/> entries are
/// persisted to <c>gmsound.log</c>; nothing goes to stdout. Crashes always
/// write a separate per-event file in the same directory.
///
/// Debug mode (enabled via <c>--debug</c>): every level is mirrored to both
/// the log file and the console.
///
/// Output directory: <c>%LocalAppData%\GameMasterSoundBoard\logs\</c>.
///
/// <para><b>Audio-thread safe.</b> The producer side (<see cref="Info"/>,
/// <see cref="Warn"/>, <see cref="Error"/>) formats the line on the
/// calling thread (cheap, allocation-free for short messages) and writes
/// it to an unbounded <see cref="Channel{T}"/>. A dedicated background
/// thread drains the channel and runs the actual <c>File.AppendAllText</c>.
/// This keeps the audio thread (which calls <see cref="Error"/> when a
/// plugin throws, or <see cref="Warn"/> from <c>MasterMixer</c>) off the
/// disk I/O critical path — a slow disk no longer blocks the mixer.</para>
///
/// <para>Crash dumps (<see cref="WriteCrash"/>) bypass the channel and
/// write synchronously: by definition the process is about to die, so we
/// can't trust the drainer to flush in time.</para>
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _logFilePath;
    private static string? _crashDir;
    private static bool _initialized;

    // Drainer infrastructure. Lazily started inside EnsureInitialized.
    // Unbounded so the producer never blocks. SingleReader because the
    // drainer Task is the only consumer.
    private static readonly Channel<LogRecord> _writeChannel = Channel.CreateUnbounded<LogRecord>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private static Task? _drainer;
    private static readonly CancellationTokenSource _drainerCts = new();

    public static bool DebugEnabled { get; private set; }

    public static string LogFilePath => _logFilePath ?? "";
    public static string CrashDirectory => _crashDir ?? "";

    /// <summary>One-shot startup. Safe to call again — re-initialization is a no-op.</summary>
    public static void Initialize(bool debug)
    {
        lock (Gate)
        {
            if (_initialized)
            {
                // Allow --debug to be toggled on later if Program saw it before App did.
                if (debug) DebugEnabled = true;
                return;
            }

            DebugEnabled = debug;

            var dir = AppPaths.LogsFolder;
            Directory.CreateDirectory(dir);

            _crashDir = dir;
            _logFilePath = Path.Combine(dir, "gmsound.log");
            _initialized = true;

            // Start the drainer thread before the first Write enqueues.
            // Background thread so it doesn't block process exit if the
            // user never calls Shutdown.
            _drainer = Task.Factory.StartNew(
                DrainAsync,
                _drainerCts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            // Boundary marker so successive runs are easy to tell apart inside one file.
            // Done synchronously (the drainer isn't running yet at this point) and
            // through the same File.AppendAllText path the drainer uses.
            try
            {
                File.AppendAllText(_logFilePath,
                    $"{Environment.NewLine}===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} — process start (debug={debug}) ====={Environment.NewLine}");
            }
            catch { /* logging must never throw out */ }
        }
    }

    /// <summary>Stop the drainer and flush any pending writes. Call on
    /// app shutdown to avoid losing the last few log lines.</summary>
    public static void Shutdown(int timeoutMs = 2000)
    {
        _writeChannel.Writer.TryComplete();
        if (_drainer != null)
        {
            try { _drainer.Wait(timeoutMs); }
            catch { /* nothing actionable on shutdown */ }
        }
        _drainerCts.Cancel();
    }

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);
    public static void Info (string category, string message) => Write(LogLevel.Info,  category, message, null);
    public static void Warn (string category, string message, Exception? ex = null) => Write(LogLevel.Warn,  category, message, ex);
    public static void Error(string category, string message, Exception? ex = null) => Write(LogLevel.Error, category, message, ex);

    /// <summary>Synchronous "I got this far" startup checkpoint. Writes
    /// directly to the log file on the calling thread, bypassing the
    /// async channel — so even a hard native crash (SIGSEGV) milliseconds
    /// later still leaves this line on disk. Use SPARINGLY: only in the
    /// narrow startup window between subsystem init steps where a crash
    /// is hard to diagnose otherwise.
    ///
    /// <para>Always writes regardless of <see cref="DebugEnabled"/>, but
    /// only at INFO level so it shows up in the normal log filter.
    /// Trade-off: ~1 ms of disk I/O per checkpoint, which is fine for a
    /// dozen startup markers but not for hot-path tracing.</para></summary>
    public static void Checkpoint(string category, string message)
    {
        EnsureInitialized();
        if (_logFilePath == null) return;

        var line = Format(LogLevel.Info, category, message, null);
        try { File.AppendAllText(_logFilePath, line + Environment.NewLine); }
        catch { /* never throw from logger */ }

        if (DebugEnabled)
        {
            try { Console.WriteLine(line); } catch { }
        }
    }

    /// <summary>Dump an unhandled exception to its own timestamped file in the
    /// crash directory. Always written regardless of debug mode.</summary>
    public static void WriteCrash(string source, Exception ex)
    {
        // We may be called before Initialize() in extreme cases (very early
        // AppDomain.UnhandledException). Set up directories lazily.
        EnsureInitialized();

        try
        {
            var fileName = $"crash-{DateTime.Now:yyyy-MM-dd-HHmmss}-{source}.log";
            var path = Path.Combine(_crashDir!, SanitizeFileName(fileName));

            var sb = new StringBuilder();
            sb.AppendLine($"Game Master Sound Board crash report");
            sb.AppendLine($"Timestamp:   {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
            sb.AppendLine($"Source:      {source}");
            sb.AppendLine($"OS:          {Environment.OSVersion}");
            sb.AppendLine($"CLR:         {Environment.Version}");
            sb.AppendLine($"Cmd line:    {Environment.CommandLine}");
            sb.AppendLine();
            sb.AppendLine(FormatException(ex));

            // Synchronous write: by the time we're in a crash handler, the
            // process may have only milliseconds before it dies. Don't
            // trust the drainer to flush in time.
            File.WriteAllText(path, sb.ToString());

            // Also append a one-liner to the main log so users grep'ing know to look here.
            // Goes through the normal channel — drainer may or may not flush it depending
            // on how quickly the process exits. Crash file above is the source of truth.
            Write(LogLevel.Error, "Crash", $"{source}: {ex.GetType().Name} — see {path}", null);
        }
        catch
        {
            // Last-ditch: keep going. We can't crash inside a crash handler.
        }
    }

    private readonly record struct LogRecord(string Line, bool ToFile, bool ToConsole);

    private static void Write(LogLevel level, string category, string message, Exception? ex)
    {
        EnsureInitialized();

        // Normal mode filters out everything below Error from the log file.
        // Debug mode writes everything. Console only fires under --debug.
        bool toFile    = DebugEnabled || level >= LogLevel.Error;
        bool toConsole = DebugEnabled;
        if (!toFile && !toConsole) return;

        var line = Format(level, category, message, ex);

        // Enqueue. Unbounded channel writes never fail; this returns
        // immediately without touching disk or contending on any lock.
        // The drainer Task picks the record up and runs the actual I/O.
        _writeChannel.Writer.TryWrite(new LogRecord(line, toFile, toConsole));
    }

    private static async Task DrainAsync()
    {
        var reader = _writeChannel.Reader;
        try
        {
            await foreach (var record in reader.ReadAllAsync(_drainerCts.Token).ConfigureAwait(false))
            {
                if (record.ToFile && _logFilePath != null)
                {
                    try { File.AppendAllText(_logFilePath, record.Line + Environment.NewLine); }
                    catch { /* swallow — never throw from logger */ }
                }
                if (record.ToConsole)
                {
                    try { Console.WriteLine(record.Line); } catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path. Drain remaining synchronously.
        }

        // Flush anything left after the channel completes/is canceled.
        while (reader.TryRead(out var record))
        {
            if (record.ToFile && _logFilePath != null)
            {
                try { File.AppendAllText(_logFilePath, record.Line + Environment.NewLine); }
                catch { }
            }
            if (record.ToConsole)
            {
                try { Console.WriteLine(record.Line); } catch { }
            }
        }
    }

    private static string Format(LogLevel level, string category, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(']');
        sb.Append(" [").Append(LevelTag(level)).Append(']');
        sb.Append(" [").Append(category).Append("] ");
        sb.Append(message);
        if (ex != null)
        {
            sb.AppendLine();
            sb.Append(FormatException(ex));
        }
        return sb.ToString();
    }

    private static string FormatException(Exception ex)
    {
        var sb = new StringBuilder();
        var e = ex;
        int depth = 0;
        while (e != null)
        {
            if (depth > 0) sb.AppendLine().Append("---- Inner exception ----").AppendLine();
            sb.Append(e.GetType().FullName).Append(": ").AppendLine(e.Message);
            if (!string.IsNullOrEmpty(e.StackTrace)) sb.AppendLine(e.StackTrace);
            e = e.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    private static string LevelTag(LogLevel l) => l switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info  => "INFO ",
        LogLevel.Warn  => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "?    "
    };

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        // Default to non-debug if Initialize() wasn't called yet.
        Initialize(false);
    }

    private static string SanitizeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
    }
}
