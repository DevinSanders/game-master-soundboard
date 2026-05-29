using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoard.Core.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Backs the About window — app version (read from the assembly), author
/// info, license blurb, and the third-party attribution list shown in the
/// scrollable section.
/// </summary>
public partial class AboutViewModel : ViewModelBase
{
    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("About", $"Failed to open URL '{url}'", ex);
        }
    }

    public string AppName => "Game Master Sound Board";

    // Read AssemblyInformationalVersion, NOT AssemblyVersion. The release
    // workflow passes -p:Version=<tag-derived semver>, which MSBuild splits:
    //   • AssemblyVersion / FileVersion: strict Major.Minor.Build.Revision
    //     (numeric only — prerelease suffixes like "-beta.1" get stripped,
    //     so a v1.0.0-beta.1 tag produces AssemblyVersion="1.0.0.0").
    //   • InformationalVersion: free-form, preserves the full semver
    //     including the "-beta.1" / "-rc.2" / "+sha" suffixes.
    // SourceLink may also append "+<git-sha>" to InformationalVersion —
    // strip everything from the `+` onward so the About page shows the
    // user-facing semver only (e.g. "1.0.0-beta.1" not "1.0.0-beta.1+abcd").
    public string AppVersion
    {
        get
        {
            var info = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (string.IsNullOrEmpty(info)) return "1.0.0";
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }
    public string ReleaseDate => "May 16, 2026";
    public string Author => "Devin Sanders";
    public string AuthorEmail => "devin.sanders64+gmsound@gmail.com";
    
    public string AppDescription => "A powerful, cross-platform soundboard designed specifically for tabletop RPG game masters. " +
                                    "It allows you to manage a library of ambient sounds, music, and sound effects, " +
                                    "play them locally, and stream them to Discord (or any other voice service) via installable bridge plugins.";

    public string LicenseType => "GPL-3.0-only";
    public string LicenseName => "GNU General Public License v3.0";
    public string LicenseException => "This application includes a special exception that lets plugins and themes be licensed independently of the GPLv3, provided they interact with the application only by implementing one of the five extension interfaces in the SoundBoard.PluginApi SDK (IAudioCodecPlugin, IAudioSamplerPlugin, IAudioBridgePlugin, IThemePlugin, IUIExtensionPlugin). See the LICENSE-EXCEPTION file for the full text.";

    public List<LibraryAttribution> Attributions { get; } = new()
    {
        new("Avalonia UI", "MIT", "https://github.com/AvaloniaUI/Avalonia"),
        new("NAudio", "MIT", "https://github.com/naudio/NAudio"),
        new("CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new("Microsoft.Extensions.DependencyInjection", "MIT", "https://github.com/dotnet/runtime"),
        new("Entity Framework Core", "MIT", "https://github.com/dotnet/efcore"),
        new("System.Reflection.MetadataLoadContext", "MIT", "https://github.com/dotnet/runtime"),
        new("SQLite (database engine)", "Public Domain", "https://www.sqlite.org/copyright.html"),
        new("OpenAL Soft (cross-platform audio output on macOS / Linux)", "LGPL-2.0-or-later", "https://openal-soft.org/"),
        new("Silk.NET.OpenAL (C# bindings)", "MIT", "https://github.com/dotnet/Silk.NET"),
        // Third-party codec decoders ship with their respective sibling
        // codec plugin repos (gmsb-codec-mp3, gmsb-codec-ogg, etc.) —
        // not with Core. Credited here so users know about them even
        // before installing the plugins. Same for Discord.Net, which is
        // carried by the optional gmsb-bridge-discord plugin.
        new("NLayer (MP3 decoding, via gmsb-codec-mp3)", "MIT", "https://github.com/naudio/NLayer"),
        new("NVorbis (OGG decoding, via gmsb-codec-ogg)", "MIT", "https://github.com/NVorbis/NVorbis"),
        new("BunLabs.NAudio.Flac (FLAC decoding, via gmsb-codec-flac)", "MS-PL", "https://github.com/BunLabs/NAudio.Flac"),
        new("Concentus (Opus encoding/decoding, via gmsb-codec-opus)", "MIT", "https://github.com/lostromb/concentus"),
        new("Discord.Net (used by gmsb-bridge-discord plugin)", "MIT", "https://github.com/discord-net/Discord.Net"),
        new("RPG Awesome (icon font)", "OFL-1.1", "https://github.com/nagoshiashumari/Rpg-Awesome"),
        new("Inter Font", "OFL-1.1", "https://rsms.me/inter/")
    };
}

/// <summary>One row in the About window's third-party attribution list.</summary>
public record LibraryAttribution(string Name, string License, string Url);
