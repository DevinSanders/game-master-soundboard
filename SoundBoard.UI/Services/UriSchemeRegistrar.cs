using SoundBoard.Core.Activation;
using SoundBoard.Core.Logging;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SoundBoard.UI.Services;

/// <summary>
/// Registers the <c>gmsound://</c> URI scheme with the operating system so
/// clicking a <c>gmsound://...</c> link in a browser, Obsidian, etc. launches
/// (or activates) this app with the URI as a CLI argument.
///
/// Windows: writes to <c>HKCU\Software\Classes\gmsound</c> — no admin needed,
/// scoped to the current user, and trivially undone by deleting that key.
/// macOS/Linux: not implemented yet (those platforms require bundle metadata
/// or .desktop files generated at install time, not at runtime).
/// </summary>
public class UriSchemeRegistrar
{
    public void EnsureRegistered()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { RegisterWindows(); }
            catch (Exception ex)
            {
                Log.Error("URI", "Windows registration failed", ex);
            }
        }
        else
        {
            // macOS: Info.plist CFBundleURLTypes — set at packaging time.
            // Linux: ~/.local/share/applications/*.desktop with MimeType=x-scheme-handler/gmsound
            //        + xdg-mime default. Easiest done from an installer script.
            Log.Info("URI", "Scheme registration not implemented on this platform");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindows()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe))
        {
            Log.Warn("URI", "Could not resolve current exe path; skipping registration");
            return;
        }

        using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{SoundboardUri.SchemeName}");
        if (root == null) return;

        // The "URL Protocol" empty value identifies this key as a custom URI scheme.
        root.SetValue("", $"URL:Game Master Sound Board ({SoundboardUri.SchemeName})");
        root.SetValue("URL Protocol", "");

        using var icon = root.CreateSubKey("DefaultIcon");
        icon?.SetValue("", $"\"{exe}\",0");

        using var shell    = root.CreateSubKey("shell");
        using var open     = shell?.CreateSubKey("open");
        using var command  = open?.CreateSubKey("command");
        // "%1" is the full URI the OS hands to us as a single argument.
        command?.SetValue("", $"\"{exe}\" \"%1\"");
    }
}
