using SoundBoard.UI.Services;
using SoundBoard.UI.ViewModels;

namespace SoundBoard.Tests.UI;

/// <summary>
/// Pins the Add-Track dialog's validation contract: a missing URI
/// surfaces an inline error; a URI no codec claims surfaces the
/// "unsupported" warning and refuses to close; a supported URI
/// confirms and captures the Result. Browse populates the URI from
/// the file picker; editing clears stale errors.
/// </summary>
public class AddTrackViewModelTests
{
    private static AddTrackViewModel BuildVm(bool supported = true, IFileService? files = null) =>
        new(files ?? Substitute.For<IFileService>(), _ => supported);

    [Fact]
    public void Confirm_WithEmptyUri_SetsErrorAndDoesNotClose()
    {
        var vm = BuildVm();
        bool closed = false;
        vm.Closed += () => closed = true;

        vm.ConfirmCommand.Execute(null);

        vm.UriError.Should().NotBeNullOrEmpty();
        vm.Result.Should().BeNull();
        closed.Should().BeFalse();
    }

    [Fact]
    public void Confirm_WithUnsupportedUri_SetsWarningAndDoesNotClose()
    {
        // The codec predicate returns false for any input, simulating a
        // URI whose extension or scheme no installed codec claims.
        var vm = BuildVm(supported: false);
        vm.Uri = "https://example.com/stream.weirdformat";
        bool closed = false;
        vm.Closed += () => closed = true;

        vm.ConfirmCommand.Execute(null);

        vm.UriError.Should().Contain("Unsupported",
            "the error must specifically mention the supported-set, not a generic 'invalid input'");
        vm.Result.Should().BeNull();
        closed.Should().BeFalse();
    }

    [Fact]
    public void Confirm_WithSupportedUri_CapturesResultAndCloses()
    {
        var vm = BuildVm(supported: true);
        vm.Uri = "https://example.com/stream.mp3";
        vm.Name = "Tavern Radio";
        vm.Tags = "ambient, tavern";
        bool closed = false;
        vm.Closed += () => closed = true;

        vm.ConfirmCommand.Execute(null);

        vm.UriError.Should().BeNull();
        vm.Result.Should().NotBeNull();
        vm.Result!.Uri.Should().Be("https://example.com/stream.mp3");
        vm.Result.Name.Should().Be("Tavern Radio");
        vm.Result.Tags.Should().Be("ambient, tavern");
        closed.Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\Music\Tavern\drinking-song.mp3")]   // Windows-style
    [InlineData("/home/user/Music/drinking-song.mp3")]   // Unix-style
    [InlineData("https://example.com/audio/drinking-song.mp3")]  // URL
    public void Confirm_WithBlankName_DerivesStemRegardlessOfPathStyle(string uri)
    {
        // Stem extraction must work the same way on Windows / Linux /
        // macOS — a user can paste any URI from anywhere. The dialog
        // walks back to the last \ OR / instead of relying on
        // Path.GetFileNameWithoutExtension (which only recognises the
        // platform-native separator).
        var vm = BuildVm();
        vm.Uri = uri;

        vm.ConfirmCommand.Execute(null);

        vm.Result!.Name.Should().Be("drinking-song");
    }

    [Fact]
    public void Confirm_WithBlankNameAndRootUrl_FallsBackToHost()
    {
        // No path stem to scrape — fall back to the URL's host so the
        // library row at least reads as the source rather than something
        // arbitrary.
        var vm = BuildVm();
        vm.Uri = "https://icecast.example.com/";

        vm.ConfirmCommand.Execute(null);

        vm.Result!.Name.Should().Be("icecast.example.com");
    }

    [Fact]
    public async Task Browse_SetsUriFromFirstPickedFile()
    {
        // Single-track dialog: even when the picker returns multiple
        // files the dialog only honours the first — multi-add goes
        // through drag-drop, not this entry point.
        var files = Substitute.For<IFileService>();
        files.OpenFileDialogAsync(Arg.Any<string>(), Arg.Any<string[]>())
             .Returns(new[] { @"C:\Music\one.mp3", @"C:\Music\two.mp3" });

        var vm = BuildVm(files: files);

        await vm.BrowseCommand.ExecuteAsync(null);

        vm.Uri.Should().Be(@"C:\Music\one.mp3");
    }

    [Fact]
    public async Task Browse_WithCancelledPicker_LeavesUriUntouched()
    {
        // The real IFileService returns an empty enumerable when the
        // user cancels the OS picker (not null — the interface returns
        // Task<IEnumerable<string>>, not nullable). Simulating that.
        var files = Substitute.For<IFileService>();
        files.OpenFileDialogAsync(Arg.Any<string>(), Arg.Any<string[]>())
             .Returns(Array.Empty<string>());

        var vm = BuildVm(files: files);
        vm.Uri = "previously-typed";

        await vm.BrowseCommand.ExecuteAsync(null);

        vm.Uri.Should().Be("previously-typed");
    }

    [Fact]
    public void EditingUri_ClearsStaleError()
    {
        var vm = BuildVm(supported: false);
        vm.Uri = "https://example.com/stream.weirdformat";
        vm.ConfirmCommand.Execute(null);
        vm.UriError.Should().NotBeNullOrEmpty("precondition: error set");

        vm.Uri = "https://example.com/stream.mp3";

        vm.UriError.Should().BeNull();
    }

    [Fact]
    public void Cancel_LeavesResultNullAndCloses()
    {
        var vm = BuildVm();
        vm.Uri = "https://example.com/stream.mp3";
        bool closed = false;
        vm.Closed += () => closed = true;

        vm.CancelCommand.Execute(null);

        vm.Result.Should().BeNull();
        closed.Should().BeTrue();
    }
}
