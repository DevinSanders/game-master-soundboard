using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using SoundBoard.Core.Logging;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SoundBoard.UI.Services;

/// <summary>
/// Schedules a clean restart of the application. Used after the user switches
/// the active library file — the running <see cref="SoundBoard.Core.Data.SoundBoardDbContext"/>
/// is bound to the old path, so a fresh process is the only reliable way to
/// pick up the new one.
///
/// Implementation detail: we can't just <c>Process.Start</c> the exe and exit
/// in one step — the single-instance Mutex in Program.cs is still held until
/// the current process actually unwinds, so the new instance would see itself
/// as a "second instance" and exit immediately. Instead we hand off to a
/// detached shell that sleeps briefly (giving the old process time to release
/// the mutex) and then launches the exe.
/// </summary>
public static class AppRestart
{
    public static void Restart()
    {
        var exe = Environment.ProcessPath
                  ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe))
        {
            Log.Error("Restart", "Could not resolve current exe path; cannot restart.");
            return;
        }

        Log.Info("Restart", $"Scheduling restart of {exe}");

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // `timeout` blocks; the trailing `start "" "<exe>"` detaches the
                // launched app from the shell so cmd exits cleanly afterwards.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout /t 1 /nobreak >nul & start \"\" \"{exe}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "sh",
                    Arguments = $"-c \"sleep 1 && '{exe.Replace("'", "'\\''")}'\"",
                    UseShellExecute = false,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Restart", "Failed to spawn relaunch helper", ex);
            return;
        }

        // Tear down the current app. Avalonia's main loop unwinds, Program.cs
        // releases the mutex, the launcher shell wakes up and starts the
        // exe ~1s later, the new instance acquires the mutex unopposed.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            // Headless / unusual lifetime — fall back to a hard exit.
            Environment.Exit(0);
        }
    }
}
