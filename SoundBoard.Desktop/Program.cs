using Avalonia;
using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using SoundBoard.Core.Activation;
using SoundBoard.Core.Logging;
using SoundBoard.UI;
using SoundBoard.UI.Services;

namespace SoundBoard.Desktop;

class Program
{
    private const string PipeName = "GMSoundBoardInstancePipe";
    private const string MutexName = "Global\\GMSoundBoard_SingleInstance_Mutex";
    private static Mutex? _mutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // 1. Parse meta-flags out of args (everything that isn't a CLI flag
        //    is forwarded to Avalonia / second-instance pipe).
        bool debug = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));
        Log.Initialize(debug);
        InstallCrashHandlers();

        Log.Info("Startup", $"Game Master Sound Board starting (debug={debug})");

        bool isFirstInstance;
        _mutex = new Mutex(true, MutexName, out isFirstInstance);

        var uriArgs = args.Where(a =>
            a.StartsWith(SoundboardUri.SchemeName + ":", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (!isFirstInstance)
        {
            Log.Info("Startup", $"Second instance — forwarding {uriArgs.Length} URI arg(s) and exiting");
            SendArgsToExistingInstance(uriArgs.Length > 0 ? uriArgs : args);
            return;
        }

        foreach (var u in uriArgs) UriActivationHandler.PendingUris.Enqueue(u);

        Task.Run(ListenForOtherInstances);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Anything that escapes Avalonia's own handlers ends up here.
            Log.WriteCrash("Main", ex);
            throw;
        }

        Log.Info("Startup", "Shutdown clean");
        _mutex.ReleaseMutex();
    }

    private static void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.WriteCrash("AppDomain", ex);
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log.WriteCrash("UnobservedTask", e.Exception);
            e.SetObserved();
        };
    }

    private static void SendArgsToExistingInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            using var writer = new StreamWriter(client);
            foreach (var a in args) writer.WriteLine(a);
            writer.Flush();
        }
        catch (Exception ex)
        {
            Log.Error("Pipe", "Failed to send args to existing instance", ex);
        }
    }

    private static async Task ListenForOtherInstances()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                await server.WaitForConnectionAsync();

                using var reader = new StreamReader(server);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    DispatchActivation(line.Trim());
                }
            }
            catch (Exception ex)
            {
                Log.Error("Pipe", "Listener iteration failed", ex);
            }
        }
    }

    private static void DispatchActivation(string arg)
    {
        if (!arg.StartsWith(SoundboardUri.SchemeName + ":", StringComparison.OrdinalIgnoreCase))
            return;

        var services = App.Current?.Services;
        if (services == null)
        {
            UriActivationHandler.PendingUris.Enqueue(arg);
            return;
        }

        var handler = services.GetService<UriActivationHandler>();
        handler?.Handle(arg);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
