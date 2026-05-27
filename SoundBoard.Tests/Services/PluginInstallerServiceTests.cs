using System.IO;
using System.IO.Compression;
using NSubstitute;
using SoundBoard.Core;
using SoundBoard.Core.Plugins;
using SoundBoard.Core.Services;

namespace SoundBoard.Tests.Services;

/// <summary>
/// Smoke tests for the plugin installer's input validation. The
/// MetadataLoadContext path is exercised by the real sample plugins in
/// manual smoke runs — covering it here would require a fake plugin DLL
/// in a test-fixture zip, which is over-engineering for now. These
/// tests pin the up-front rejections that protect against bad zips.
///
/// <para><b>Collection membership:</b> joined to
/// <c>AppPathsGlobalState</c> because <c>LineageFixture</c> below calls
/// <see cref="SoundBoard.Core.AppPaths.OverrideForTests"/>, which
/// mutates a process-static field. Without the collection, xUnit v3's
/// per-class parallelism races with <c>AppPathsTests</c> (also a
/// member of this collection) and the override gets stomped — observed
/// as a Linux-only flaky failure where <c>AppPathsTests</c> read this
/// fixture's sandbox path instead of its own.</para>
/// </summary>
[Collection(SoundBoard.Tests.Collections.AppPathsGlobalState.Name)]
public class PluginInstallerServiceTests
{
    private static PluginInstallerService CreateService()
    {
        // PluginService has a parameterless constructor that picks
        // %LocalAppData% paths. These tests only exercise input
        // validation (file-not-found, empty-zip, bad-zip) — the real
        // file move never happens — so the production folder paths
        // are fine.
        var pluginService = new PluginService();
        return new PluginInstallerService(pluginService);
    }

    [Fact]
    public async Task InstallFromZipAsync_MissingFile_ReturnsFailure()
    {
        var installer = CreateService();
        var result = await installer.InstallFromZipAsync(@"X:\does\not\exist.zip", Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("File not found");
        result.ZipFileName.Should().Be("exist.zip");
    }

    [Fact]
    public async Task InstallFromZipAsync_EmptyZip_RejectsForMissingManifest()
    {
        // Manifest is required. An empty zip has no plugin.json, so the
        // installer must reject before doing anything else.
        var tempZip = Path.GetTempFileName();
        File.Delete(tempZip);
        tempZip = Path.ChangeExtension(tempZip, ".zip");
        try
        {
            using (var fs = File.Create(tempZip))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create)) { }

            var installer = CreateService();
            var result = await installer.InstallFromZipAsync(tempZip, Xunit.TestContext.Current.CancellationToken);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("plugin.json");
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    [Fact]
    public async Task InstallFromZipAsync_ZipWithDllButNoManifest_Rejects()
    {
        // A zip that contains a DLL but no plugin.json was the
        // pre-manifest happy path; the new contract requires the
        // manifest, so this must now fail with a clear message.
        var tempZip = Path.GetTempFileName();
        File.Delete(tempZip);
        tempZip = Path.ChangeExtension(tempZip, ".zip");
        try
        {
            using (var fs = File.Create(tempZip))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // Add a single empty "dll" entry — content doesn't matter,
                // we never get as far as actually loading it.
                var entry = archive.CreateEntry("FakePlugin.dll");
                using var _ = entry.Open();
            }

            var installer = CreateService();
            var result = await installer.InstallFromZipAsync(tempZip, Xunit.TestContext.Current.CancellationToken);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("plugin.json");
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    [Fact]
    public async Task InstallFromZipAsync_NotAZipFile_ReturnsFailure()
    {
        // Random non-zip bytes — should be caught by the extract step
        // and surfaced as "Could not extract zip: ...".
        var tempFile = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            File.WriteAllText(tempFile, "this is definitely not a zip archive");

            var installer = CreateService();
            var result = await installer.InstallFromZipAsync(tempFile, Xunit.TestContext.Current.CancellationToken);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("extract");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ─── Lineage detection ─────────────────────────────────────────
    //
    // The full install path requires a real plugin DLL (the
    // MetadataLoadContext validation step needs to read IPlugin
    // metadata). For unit tests we exercise the lineage-detection
    // helpers in isolation — they only consume plugin.json files, no
    // DLLs needed — and trust the manifest-level IsSameLineageAs
    // tests to cover the decision logic. The full install flow is
    // exercised end-to-end via the manual drop-zone test path.

    /// <summary>Create an isolated sandbox app-data root for the test,
    /// pin AppPaths to it, and seed a fake "installed" plugin folder
    /// containing a plugin.json (no DLL — lineage detection doesn't
    /// need one). Returns an IDisposable that restores the original
    /// AppPaths root on teardown.</summary>
    private sealed class LineageFixture : System.IDisposable
    {
        public string Sandbox { get; }
        public PluginService PluginService { get; }
        private readonly string _previousRoot;

        public LineageFixture()
        {
            Sandbox = Path.Combine(Path.GetTempPath(), "GMSBTests-lineage-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Sandbox);
            _previousRoot = AppPaths.OverrideForTests(Sandbox);
            // PluginService reads AppPaths in its ctor, so this is the
            // moment we capture the sandboxed paths.
            PluginService = new PluginService();
        }

        public void SeedInstalled(string folderName, PluginManifestFile manifest, bool inThemesFolder = false)
        {
            var root = inThemesFolder ? PluginService.ThemesFolder : PluginService.PluginsFolder;
            var folder = Path.Combine(root, folderName);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, PluginManifestFile.FileName), $$"""
            {
              "publisher": "{{manifest.Publisher}}",
              "id": "{{manifest.Id}}",
              "version": "{{manifest.Version}}",
              "entryDll": "{{manifest.EntryDll}}",
              "isTheme": {{(manifest.IsTheme ? "true" : "false")}}
            }
            """);
            // Touch a fake entry DLL so TryLoad's existence check passes.
            File.WriteAllText(Path.Combine(folder, manifest.EntryDll), "");
        }

        public void Dispose()
        {
            AppPaths.OverrideForTests(_previousRoot);
            try { if (Directory.Exists(Sandbox)) Directory.Delete(Sandbox, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FindExistingLineage_SamePublisherSameId_ReturnsExistingFolder()
    {
        using var fixture = new LineageFixture();
        fixture.SeedInstalled("com_acme__eq", new PluginManifestFile
        {
            Publisher = "com.acme", Id = "eq", Version = "1.0.0", EntryDll = "Eq.dll",
        });

        var incoming = new PluginManifestFile
        {
            Publisher = "com.acme", Id = "eq", Version = "1.1.0", EntryDll = "Eq.dll",
        };
        var (folder, existing) = PluginInstallerService.FindExistingLineage(fixture.PluginService, incoming);

        folder.Should().NotBeNull();
        existing.Should().NotBeNull();
        existing!.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void FindExistingLineage_SameIdDifferentPublisher_ReturnsNull()
    {
        // com.acme's EQ exists; com.bobs's EQ is incoming — must be
        // treated as a different lineage so the installer goes
        // side-by-side instead of replacing.
        using var fixture = new LineageFixture();
        fixture.SeedInstalled("com_acme__eq", new PluginManifestFile
        {
            Publisher = "com.acme", Id = "eq", Version = "1.0.0", EntryDll = "Eq.dll",
        });

        var incoming = new PluginManifestFile
        {
            Publisher = "com.bobs", Id = "eq", Version = "1.0.0", EntryDll = "Eq.dll",
        };
        var (folder, existing) = PluginInstallerService.FindExistingLineage(fixture.PluginService, incoming);

        folder.Should().BeNull();
        existing.Should().BeNull();
    }

    [Fact]
    public void FindSameIdDifferentPublisher_SurfacesTheOtherPublisher()
    {
        // The installer uses this to populate the "installed alongside
        // com.acme/eq" status message. Confirm it finds the conflict.
        using var fixture = new LineageFixture();
        fixture.SeedInstalled("com_acme__eq", new PluginManifestFile
        {
            Publisher = "com.acme", Id = "eq", Version = "1.0.0", EntryDll = "Eq.dll",
        });

        var incoming = new PluginManifestFile
        {
            Publisher = "com.bobs", Id = "eq", Version = "1.0.0", EntryDll = "Eq.dll",
        };
        var other = PluginInstallerService.FindSameIdDifferentPublisher(fixture.PluginService, incoming);
        other.Should().NotBeNull();
        other!.Publisher.Should().Be("com.acme");
    }

    [Fact]
    public void FindSameIdDifferentPublisher_SameLineageReturnsNull()
    {
        // When the lineage matches, we shouldn't report a "side-by-side"
        // conflict — the install is a straight upgrade.
        using var fixture = new LineageFixture();
        fixture.SeedInstalled("com_acme__eq", new PluginManifestFile
        {
            Publisher = "com.acme", Id = "eq", Version = "1.0.0", EntryDll = "Eq.dll",
        });

        var incoming = new PluginManifestFile
        {
            Publisher = "com.acme", Id = "eq", Version = "1.1.0", EntryDll = "Eq.dll",
        };
        var other = PluginInstallerService.FindSameIdDifferentPublisher(fixture.PluginService, incoming);
        other.Should().BeNull();
    }

    [Fact]
    public void FindExistingLineage_ScansBothPluginsAndThemesFolders()
    {
        // A plugin that flipped its isTheme flag across versions
        // (rare but possible — codec turned into a UI extension)
        // should still be detected as the same lineage even though
        // it's now sitting in the opposite folder.
        using var fixture = new LineageFixture();
        fixture.SeedInstalled("com_acme__skin", new PluginManifestFile
        {
            Publisher = "com.acme", Id = "skin", Version = "1.0.0", EntryDll = "Skin.dll", IsTheme = true,
        }, inThemesFolder: true);

        var incoming = new PluginManifestFile
        {
            Publisher = "com.acme", Id = "skin", Version = "1.1.0", EntryDll = "Skin.dll", IsTheme = false,
        };
        var (folder, existing) = PluginInstallerService.FindExistingLineage(fixture.PluginService, incoming);

        folder.Should().NotBeNull();
        folder!.Should().Contain("Themes");
        existing.Should().NotBeNull();
    }
}
