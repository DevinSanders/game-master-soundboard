using SoundBoard.Core.Logging;
using System;
using System.Reflection;
using System.Runtime.Loader;

namespace SoundBoard.Core.Plugins;

/// <summary>
/// Per-plugin <see cref="AssemblyLoadContext"/> that gives each plugin its
/// own dependency graph (collectible, so disabled plugins unload cleanly).
///
/// <para>One subtle thing this class has to get right: contract assemblies
/// (<c>SoundBoard.PluginApi</c>, <c>NAudio.Core</c>, etc.) that BOTH the
/// host and the plugin reference must resolve to the host's already-loaded
/// copy — not to whatever copy the plugin's publish output dropped next
/// to its DLL. If we let the plugin load its own copy, the plugin's
/// <c>IPlugin</c> type comes from a different ALC than the host's
/// <c>IPlugin</c>, and <c>typeof(IPlugin).IsAssignableFrom(pluginType)</c>
/// in <c>PluginService</c> returns false even though the type names match.
/// The plugin then "silently fails to discover" with no obvious error.
///
/// Fix: before consulting <see cref="AssemblyDependencyResolver"/>, check
/// whether the host's default ALC already has an assembly with the same
/// simple name. If yes, return null so the runtime falls back to the
/// default ALC and both sides share one <see cref="Type"/> identity.</para>
/// </summary>
public class PluginLoadContext : AssemblyLoadContext
{
    private static int _nextId;

    private readonly int _id;
    private readonly string _pluginPath;
    private readonly string _pluginDir;
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true, name: $"Plugin#{System.Threading.Interlocked.Increment(ref _nextId)}:{System.IO.Path.GetFileNameWithoutExtension(pluginPath)}")
    {
        _id = _nextId;
        _pluginPath = pluginPath;
        _pluginDir = System.IO.Path.GetDirectoryName(pluginPath) ?? "";
        _resolver = new AssemblyDependencyResolver(pluginPath);
        Log.Debug("Plugin", $"PluginLoadContext '{Name}' created for {pluginPath}");
        Unloading += ctx => Log.Debug("Plugin", $"PluginLoadContext '{ctx.Name}' Unloading event fired");
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Defer to the default ALC for anything the host already has —
        // ensures shared contract types are the SAME Type object on both
        // sides of the plugin/host boundary.
        if (TryGetDefaultLoaded(assemblyName, out var hostLoaded))
        {
            // If the plugin asked for a NEWER version than what the host
            // has loaded, surface a warning. The plugin will still bind
            // to the host's older copy; if it relies on a member that
            // only exists in the newer version it'll get a clear
            // MissingMethodException at runtime. The log line at load
            // time turns that into a debuggable signal instead of a
            // mystery.
            if (assemblyName.Version != null
                && hostLoaded.GetName().Version is { } hostVer
                && assemblyName.Version > hostVer)
            {
                Log.Warn("Plugin",
                    $"Plugin requested '{assemblyName.Name}' v{assemblyName.Version}, " +
                    $"but host has v{hostVer} loaded. Plugin will bind to the host's older copy; " +
                    $"MissingMethodException at runtime is the symptom of an actual incompatibility.");
            }
            return null;
        }

        // Resolver-driven path (uses .deps.json). Returns null if the dep
        // isn't listed in deps.json (e.g. a transitive dep that wasn't
        // captured at publish time).
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);

        // Fallback: a plain "<assembly>.dll" next to the plugin's own
        // entry-point DLL. This is the path the deployed plugin actually
        // sits at; the deps.json points at the NuGet cache, which exists
        // on dev machines but not always at runtime, so we hedge.
        if (assemblyPath == null && !string.IsNullOrEmpty(_pluginDir))
        {
            var candidate = System.IO.Path.Combine(_pluginDir, assemblyName.Name + ".dll");
            if (System.IO.File.Exists(candidate))
            {
                Log.Debug("Plugin", $"[{Name}] deps.json missed '{assemblyName.Name}'; using local-folder fallback at {candidate}");
                assemblyPath = candidate;
            }
        }

        if (assemblyPath != null)
        {
            Log.Debug("Plugin", $"[{Name}] resolving '{assemblyName.Name}' -> {assemblyPath}");
            return LoadFromAssemblyPath(assemblyPath);
        }

        Log.Debug("Plugin", $"[{Name}] could not resolve '{assemblyName.Name}'; deferring to default probing");
        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // Path 1: deps.json-driven resolver. Works when the NuGet package
        // registers its natives the standard way (NativeLibraryDependency
        // metadata in deps.json pointing at `runtimes/<rid>/native/<lib>`).
        // NAudio's native packages do this; SkiaSharp does this;
        // Silk.NET.OpenAL.Soft.Native does this.
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null)
        {
            Log.Debug("Plugin", $"[{Name}] native '{unmanagedDllName}' -> {libraryPath} (via deps.json)");
            return LoadUnmanagedDllFromPath(libraryPath);
        }

        // Path 2: manual probe of <plugin-dir>/runtimes/<rid>/native/<lib>.
        //
        // Some NuGet packages (notably `libdave`, Discord's E2EE library)
        // ship native binaries under a non-standard layout and don't
        // register them in deps.json — they rely on the runtime's default
        // probing for the host EXE, which DOESN'T fire when the assembly
        // calling [DllImport] was loaded via a custom ALC. Without this
        // fallback, the host loads Discord.Net.Dave fine but the actual
        // libdave.dll P/Invoke target is invisible and voice fails with
        // "libdave couldn't be found".
        //
        // Probing every plausible <rid> + filename combination here is
        // safe because it only fires on the slow path (resolver miss).
        var probed = ProbeRuntimesNative(unmanagedDllName);
        if (probed != null)
        {
            Log.Debug("Plugin", $"[{Name}] native '{unmanagedDllName}' -> {probed} (via runtimes/ probe)");
            return LoadUnmanagedDllFromPath(probed);
        }

        Log.Debug("Plugin", $"[{Name}] native '{unmanagedDllName}' not resolved by plugin; deferring to default native probing");
        return IntPtr.Zero;
    }

    /// <summary>Look for <paramref name="libName"/> under
    /// <c>&lt;plugin-dir&gt;/runtimes/&lt;rid&gt;/native/</c> for the
    /// current process RID. Tries common name variants (raw,
    /// platform-prefixed, platform-suffixed) because [DllImport] strings
    /// don't always match the filename on disk exactly — e.g. a Linux
    /// build might P/Invoke <c>"dave"</c> while the file is
    /// <c>libdave.so</c>.</summary>
    private string? ProbeRuntimesNative(string libName)
    {
        if (string.IsNullOrEmpty(_pluginDir)) return null;

        // Best-fit RID. We don't need to walk the full RID fallback graph
        // (win-x64 → win → any); the libdave-style packages publish at
        // the leaf RID and that's what we ship at publish time.
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var nativeDir = System.IO.Path.Combine(_pluginDir, "runtimes", rid, "native");
        if (!System.IO.Directory.Exists(nativeDir)) return null;

        // Build candidate filenames. On Windows the DllImport name is
        // usually bare ("libdave"); on Unix the file is conventionally
        // prefixed with "lib" and suffixed with ".so" / ".dylib".
        string[] candidates;
        if (OperatingSystem.IsWindows())
        {
            candidates = new[] { libName, libName + ".dll" };
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates = new[]
            {
                libName, libName + ".dylib",
                "lib" + libName, "lib" + libName + ".dylib",
            };
        }
        else
        {
            candidates = new[]
            {
                libName, libName + ".so",
                "lib" + libName, "lib" + libName + ".so",
            };
        }

        foreach (var name in candidates)
        {
            var path = System.IO.Path.Combine(nativeDir, name);
            if (System.IO.File.Exists(path)) return path;
        }
        return null;
    }

    private static bool TryGetDefaultLoaded(AssemblyName name, out Assembly hostLoaded)
    {
        foreach (var asm in Default.Assemblies)
        {
            // Match on simple name only. The host and the plugin may carry
            // different versions of (say) NAudio.Core; we still want the
            // host's copy to win for type-identity reasons. The version
            // check happens at the call site so a plugin requesting a
            // higher version gets a clear warning rather than a silent
            // host-wins.
            if (string.Equals(asm.GetName().Name, name.Name, StringComparison.Ordinal))
            {
                hostLoaded = asm;
                return true;
            }
        }
        hostLoaded = null!;
        return false;
    }
}
