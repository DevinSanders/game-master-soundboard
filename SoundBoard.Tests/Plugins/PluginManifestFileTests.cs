using System.IO;
using SoundBoard.Core.Plugins;

namespace SoundBoard.Tests.Plugins;

/// <summary>
/// Validates the manifest contract — what TryLoad accepts, what it
/// rejects, and the safety guarantees around the entryDll field. The
/// installer and the runtime scanner both depend on these checks being
/// in TryLoad rather than in the call sites, so a regression here would
/// silently relax the contract for both.
/// </summary>
public class PluginManifestFileTests
{
    /// <summary>Write a plugin folder containing a manifest with the
    /// given JSON body, plus (when <paramref name="includeEntryDll"/>)
    /// an empty file at the manifest's claimed entryDll path so the
    /// existence check passes. Returns the folder path; caller deletes.</summary>
    private static string StageFolder(string manifestJson, bool includeEntryDll = true, string entryDllName = "MyPlugin.dll")
    {
        var folder = Path.Combine(Path.GetTempPath(), "GMSBTests-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, PluginManifestFile.FileName), manifestJson);
        if (includeEntryDll)
            File.WriteAllText(Path.Combine(folder, entryDllName), "");
        return folder;
    }

    [Fact]
    public void TryLoad_HappyPath_PopulatesAllFields()
    {
        var folder = StageFolder("""
        {
          "publisher": "com.example",
          "id": "plugin",
          "name": "Example Plugin",
          "version": "2.1.0",
          "author": "Some Author",
          "description": "Adds something useful.",
          "entryDll": "MyPlugin.dll",
          "isTheme": false
        }
        """);
        try
        {
            PluginManifestFile.TryLoad(folder, out var manifest, out var error).Should().BeTrue();
            error.Should().BeNull();
            manifest.Should().NotBeNull();
            manifest!.Publisher.Should().Be("com.example");
            manifest.Id.Should().Be("plugin");
            manifest.Name.Should().Be("Example Plugin");
            manifest.Version.Should().Be("2.1.0");
            manifest.Author.Should().Be("Some Author");
            manifest.Description.Should().Be("Adds something useful.");
            manifest.EntryDll.Should().Be("MyPlugin.dll");
            manifest.IsTheme.Should().BeFalse();
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TryLoad_MissingPublisher_Rejects()
    {
        // Publisher is required. A manifest without it loses the
        // upgrade-vs-side-by-side distinction at install time, so the
        // schema rejects rather than silently treating publisher as "".
        var folder = StageFolder("""{ "id": "x", "entryDll": "MyPlugin.dll" }""");
        try
        {
            PluginManifestFile.TryLoad(folder, out _, out var error).Should().BeFalse();
            error.Should().Contain("'publisher'");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TryLoad_MissingManifest_ReturnsError()
    {
        var folder = Path.Combine(Path.GetTempPath(), "GMSBTests-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            PluginManifestFile.TryLoad(folder, out var manifest, out var error).Should().BeFalse();
            manifest.Should().BeNull();
            error.Should().Contain("plugin.json");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TryLoad_MissingId_Rejects()
    {
        var folder = StageFolder("""{ "publisher": "p", "entryDll": "MyPlugin.dll" }""");
        try
        {
            PluginManifestFile.TryLoad(folder, out _, out var error).Should().BeFalse();
            error.Should().Contain("'id'");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TryLoad_MissingEntryDll_Rejects()
    {
        var folder = StageFolder("""{ "publisher": "p", "id": "x" }""");
        try
        {
            PluginManifestFile.TryLoad(folder, out _, out var error).Should().BeFalse();
            error.Should().Contain("'entryDll'");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TryLoad_EntryDllWithPathTraversal_Rejects()
    {
        // Anything containing '/', '\\', or '..' must be rejected.
        // Otherwise a malicious zip could declare entryDll "../../evil.dll"
        // and have the installer point its scanner outside the plugin folder.
        foreach (var bad in new[] { "../escape.dll", "sub/dir.dll", "back\\slash.dll", "weird..thing.dll" })
        {
            var folder = StageFolder(
                $$"""{ "publisher": "p", "id": "x", "entryDll": "{{bad}}" }""",
                includeEntryDll: false);
            try
            {
                PluginManifestFile.TryLoad(folder, out _, out var error).Should().BeFalse();
                error.Should().NotBeNull();
            }
            finally { Directory.Delete(folder, true); }
        }
    }

    [Fact]
    public void TryLoad_EntryDllPointsAtNonexistentFile_Rejects()
    {
        // Manifest declares MyPlugin.dll but the file isn't there — caller
        // should get a clear "no such file" rather than a runtime
        // FileNotFoundException at load time.
        var folder = StageFolder("""{ "publisher": "p", "id": "x", "entryDll": "MyPlugin.dll" }""", includeEntryDll: false);
        try
        {
            PluginManifestFile.TryLoad(folder, out _, out var error).Should().BeFalse();
            error.Should().Contain("no such file");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void TryLoad_EntryDllWithoutDllExtension_Rejects()
    {
        var folder = StageFolder("""{ "publisher": "p", "id": "x", "entryDll": "MyPlugin.exe" }""", includeEntryDll: false);
        try
        {
            PluginManifestFile.TryLoad(folder, out _, out var error).Should().BeFalse();
            error.Should().Contain(".dll");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void GetSafeFolderName_CombinesPublisherAndId()
    {
        // The folder name format `publisher__id` is the install layer's
        // collision-avoidance scheme: different publishers with the
        // same id land in different folders. Pin the shape so it
        // doesn't quietly change (would orphan existing installs).
        var manifest = new PluginManifestFile { Publisher = "com.acme", Id = "EQ", EntryDll = "x.dll" };
        manifest.GetSafeFolderName().Should().Be("com_acme__EQ");
    }

    [Fact]
    public void GetSafeFolderName_DifferentPublishersDontCollideOnSameId()
    {
        var acme = new PluginManifestFile { Publisher = "com.acme",  Id = "EQ", EntryDll = "x.dll" }.GetSafeFolderName();
        var bobs = new PluginManifestFile { Publisher = "com.bobs",  Id = "EQ", EntryDll = "x.dll" }.GetSafeFolderName();
        acme.Should().NotBe(bobs);
    }

    [Fact]
    public void GetSafeFolderName_StripsInvalidChars()
    {
        var manifest = new PluginManifestFile
        {
            Publisher = "com.example",
            Id = "weird/:id with spaces.",
            EntryDll = "x.dll",
        };
        var safe = manifest.GetSafeFolderName();
        safe.Should().NotContain("/");
        safe.Should().NotContain(":");
        safe.Should().NotContain(" ");
        // Double-underscore separator is the only place __ appears.
        safe.IndexOf("__").Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public void IsSameLineageAs_MatchesOnPublisherAndId()
    {
        var v1 = new PluginManifestFile { Publisher = "com.acme", Id = "eq", Version = "1.0", EntryDll = "x.dll" };
        var v2 = new PluginManifestFile { Publisher = "com.acme", Id = "eq", Version = "1.1", EntryDll = "x.dll" };
        v1.IsSameLineageAs(v2).Should().BeTrue();
    }

    [Fact]
    public void IsSameLineageAs_DoesNotMatchAcrossPublishers()
    {
        // Two devs with their own EQ plugin — must be treated as
        // different plugins by the installer (side-by-side, not replace).
        var acme = new PluginManifestFile { Publisher = "com.acme", Id = "eq", EntryDll = "x.dll" };
        var bobs = new PluginManifestFile { Publisher = "com.bobs", Id = "eq", EntryDll = "x.dll" };
        acme.IsSameLineageAs(bobs).Should().BeFalse();
    }

    [Fact]
    public void IsSameLineageAs_DoesNotMatchAcrossIds()
    {
        var eq  = new PluginManifestFile { Publisher = "com.acme", Id = "eq",     EntryDll = "x.dll" };
        var rev = new PluginManifestFile { Publisher = "com.acme", Id = "reverb", EntryDll = "x.dll" };
        eq.IsSameLineageAs(rev).Should().BeFalse();
    }

    [Fact]
    public void TryLoad_DefaultsBlankNameToId()
    {
        // Plugin authors who omit optional fields shouldn't end up with a
        // blank settings row.
        var folder = StageFolder("""{ "publisher": "p", "id": "just-an-id", "entryDll": "MyPlugin.dll" }""");
        try
        {
            PluginManifestFile.TryLoad(folder, out var manifest, out _).Should().BeTrue();
            manifest!.Name.Should().Be("just-an-id");
            manifest.Version.Should().Be("?");
            manifest.Author.Should().Be("(unspecified)");
        }
        finally { Directory.Delete(folder, true); }
    }
}
