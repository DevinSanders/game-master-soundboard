using System;
using System.Reflection;

namespace SoundBoard.PluginApi;

/// <summary>
/// Single source of truth for a plugin's runtime <see cref="IPlugin.Version"/>.
/// Resolves to the version baked into the plugin assembly by the build
/// pipeline — so a plugin's C# code never carries a hand-maintained
/// version literal that can drift from <c>plugin.json</c> or the release
/// tag.
///
/// <para><b>How the version arrives in the assembly.</b> The plugin's
/// release workflow extracts the version from the tag that triggered it
/// (e.g. tag <c>v1.2.3</c> → version <c>1.2.3</c>) and passes it through
/// to <c>dotnet publish -p:Version=1.2.3</c>. MSBuild then bakes the
/// value into <see cref="AssemblyInformationalVersionAttribute"/> +
/// <see cref="AssemblyVersionAttribute"/> at compile time. The same
/// version is written into <c>plugin.json</c> by the packaging script
/// before the zip is built, so the two stay in lockstep.</para>
///
/// <para><b>Local builds.</b> When no <c>-p:Version=</c> is supplied,
/// MSBuild uses the csproj's <c>&lt;Version&gt;</c> property as the
/// default (typically pinned to the plugin's current stable version
/// for convenient developer builds).</para>
///
/// <para><b>Usage in a plugin:</b></para>
/// <code>
/// using SoundBoard.PluginApi;
///
/// public sealed class Mp3CodecPlugin : IAudioCodecPlugin
/// {
///     public string Version =&gt; PluginVersion.OfAssembly(typeof(Mp3CodecPlugin));
///     // ...
/// }
/// </code>
/// </summary>
public static class PluginVersion
{
    /// <summary>Read the published version of the assembly that defines
    /// <paramref name="pluginType"/>. Prefers
    /// <see cref="AssemblyInformationalVersionAttribute"/> (preserves
    /// pre-release suffixes like <c>-beta.1</c>) and falls back to
    /// <see cref="AssemblyName.Version"/> (Major.Minor.Build). Always
    /// returns something — falls back to <c>"0.0.0"</c> if both are
    /// missing.</summary>
    public static string OfAssembly(Type pluginType)
    {
        if (pluginType == null) throw new ArgumentNullException(nameof(pluginType));

        var asm = pluginType.Assembly;

        // InformationalVersion is the right answer when set — MSBuild
        // populates it from -p:Version=. It can carry a "+build" suffix
        // (SemVer build metadata) which isn't part of the semantic
        // version — strip everything after '+'.
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        // Fallback: 3-part AssemblyVersion. ToString(3) drops the
        // trailing ".0" revision so "1.2.3.0" → "1.2.3", matching the
        // SemVer shape users expect.
        var v = asm.GetName().Version;
        return v?.ToString(3) ?? "0.0.0";
    }
}
