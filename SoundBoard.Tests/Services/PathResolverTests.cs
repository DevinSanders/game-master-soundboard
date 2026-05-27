using System;
using System.IO;
using SoundBoard.UI.Services;

namespace SoundBoard.Tests.Services;

/// <summary>
/// Pins the cross-platform path-resolution contract for
/// <see cref="PathResolver"/>. The trap these tests guard against:
/// <c>Path.GetFileName</c> is platform-aware (uses '/' on Unix, '\\' on
/// Windows), but library exports carry whatever separator the SOURCE OS
/// used. A library exported from Windows lands on a Mac with paths like
/// <c>"F:\\Music\\rain.ogg"</c> — no '/' anywhere — and the platform
/// <c>Path.GetFileName</c> returns the whole string as the "filename",
/// breaking the resolver's dictionary lookup. Verbatim regression test
/// for the May 2026 import bug.
/// </summary>
public class PathResolverTests
{
    [Fact]
    public void Resolve_VerbatimPath_ReturnsAsIs_WhenFileExists()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "exists.wav");
        File.WriteAllText(file, "x");

        var resolver = new PathResolver(new[] { temp.Path });
        resolver.Resolve(file).Should().Be(file);
    }

    [Fact]
    public void Resolve_MissingFile_FindsByFilenameInSearchRoot()
    {
        using var temp = new TempDir();
        var realPath = Path.Combine(temp.Path, "castle_bg.ogg");
        File.WriteAllText(realPath, "audio");

        // Original path is some other (now-invalid) location with the same
        // filename. Resolver should find the real one by filename lookup.
        var resolver = new PathResolver(new[] { temp.Path });
        var resolved = resolver.Resolve(Path.Combine("X:", "missing", "castle_bg.ogg"));
        resolved.Should().Be(realPath);
    }

    [Fact]
    public void Resolve_WindowsPathOnAnyPlatform_ExtractsFilenameCorrectly()
    {
        // THIS IS THE REGRESSION TEST.
        // A library exported from Windows carries paths like
        // "F:\\My Drive\\...\\castle_bg.ogg" — only '\\' separators. On
        // Unix, the platform Path.GetFileName looks ONLY for '/', so it
        // would return the entire string as the filename, defeating the
        // dictionary lookup. The PathResolver must use a separator-agnostic
        // filename extraction so cross-OS imports work.
        using var temp = new TempDir();
        // Unique filename so a real file on the test machine with the
        // generic name "castle_bg.ogg" can't accidentally satisfy the
        // verbatim File.Exists short-circuit at the top of Resolve.
        var unique = "castle_bg_" + Guid.NewGuid().ToString("N") + ".ogg";
        var realPath = Path.Combine(temp.Path, unique);
        File.WriteAllText(realPath, "audio");

        var resolver = new PathResolver(new[] { temp.Path });

        // Windows-formatted "imported" path (backslashes only). The
        // leading drive letter is irrelevant as long as the path doesn't
        // happen to exist on the test machine — the unique GUID in the
        // filename guarantees it never will.
        var winPath = @"F:\My Drive\DnD\RPG Sounds and Music\DM Tools\backgrounds ogg\" + unique;
        resolver.Resolve(winPath).Should().Be(realPath);
    }

    [Fact]
    public void Resolve_UnixPathOnAnyPlatform_ExtractsFilenameCorrectly()
    {
        // Symmetric to the Windows case: a library exported from Mac /
        // Linux on a Windows machine should also resolve cleanly. On
        // Windows, Path.GetFileName accepts BOTH separators so this case
        // works today even without the fix — but we pin the contract
        // explicitly so it doesn't regress.
        using var temp = new TempDir();
        var unique = "city_bg_" + Guid.NewGuid().ToString("N") + ".ogg";
        var realPath = Path.Combine(temp.Path, unique);
        File.WriteAllText(realPath, "audio");

        var resolver = new PathResolver(new[] { temp.Path });

        var unixPath = "/Users/admin/Music/DnD/" + unique;
        resolver.Resolve(unixPath).Should().Be(realPath);
    }

    [Fact]
    public void Resolve_MultipleCandidates_PicksLongestPathSuffixMatch()
    {
        using var temp = new TempDir();
        var ambient = Path.Combine(temp.Path, "Ambient");
        var combat  = Path.Combine(temp.Path, "Combat");
        Directory.CreateDirectory(ambient);
        Directory.CreateDirectory(combat);

        var ambientRain = Path.Combine(ambient, "rain.mp3");
        var combatRain  = Path.Combine(combat,  "rain.mp3");
        File.WriteAllText(ambientRain, "x");
        File.WriteAllText(combatRain,  "x");

        var resolver = new PathResolver(new[] { temp.Path });

        // Imported path's deepest matching directory is "Ambient" → that
        // candidate wins over the same-named file in Combat.
        var imported = @"D:\Music\Ambient\rain.mp3";
        resolver.Resolve(imported).Should().Be(ambientRain);
    }

    [Fact]
    public void Resolve_FilenameNotInIndex_ReturnsNull()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "present.wav"), "x");

        var resolver = new PathResolver(new[] { temp.Path });
        resolver.Resolve(@"F:\nowhere\absent.wav").Should().BeNull();
    }

    [Fact]
    public void Resolve_NullOrEmptyInput_ReturnsNull()
    {
        var resolver = new PathResolver(Array.Empty<string>());
        resolver.Resolve(null!).Should().BeNull();
        resolver.Resolve("").Should().BeNull();
    }

    // --- temp-folder helper (mirrors the pattern in SettingsServiceTests) ----
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "GMSBTests-pathresolver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
