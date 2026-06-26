using SoundBoard.UI.ViewModels;

namespace SoundBoard.Tests.UI;

/// <summary>
/// Pins the tag-facet parsing contract: <c>:</c> and <c>/</c> both delimit
/// a namespace; the first occurrence wins; case-insensitive grouping
/// collapses different casings into the same facet; missing halves or
/// missing delimiters fall through to a "Tags" miscellany facet; ordering
/// is alphabetical with miscellany last.
/// </summary>
public class TagFacetParserTests
{
    [Fact]
    public void Parse_GroupsByNamespaceDetectedViaColon()
    {
        var result = TagFacetParser.Parse(new[]
        {
            "mood:tense, mood:somber",
            "mood:victorious",
        });

        result.Should().HaveCount(1);
        var mood = result[0];
        mood.Name.Should().Be("Mood");
        mood.IsMiscellany.Should().BeFalse();
        mood.Values.Select(v => v.Display)
            .Should().BeEquivalentTo(new[] { "somber", "tense", "victorious" });
        mood.Values.Select(v => v.RawTag)
            .Should().BeEquivalentTo(new[] { "mood:somber", "mood:tense", "mood:victorious" });
    }

    [Fact]
    public void Parse_GroupsByNamespaceDetectedViaSlash()
    {
        var result = TagFacetParser.Parse(new[] { "genre/fantasy, genre/sci-fi" });

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Genre");
        result[0].Values.Select(v => v.RawTag)
            .Should().BeEquivalentTo(new[] { "genre/fantasy", "genre/sci-fi" });
    }

    [Fact]
    public void Parse_SplitsOnFirstDelimiterRegardlessOfWhichTypeAppearsFirst()
    {
        // "mood:happy/sub" — colon comes first; namespace = "mood", value
        // = "happy/sub". The trailing slash is part of the value, not a
        // nested namespace.
        var result = TagFacetParser.Parse(new[] { "mood:happy/sub" });

        result.Should().ContainSingle(f => f.Name == "Mood");
        result.Single(f => f.Name == "Mood").Values.Single().Display.Should().Be("happy/sub");
    }

    [Fact]
    public void Parse_FirstSlashWinsWhenSlashAppearsBeforeColon()
    {
        // "genre/sub:detail" — slash comes first; namespace = "genre",
        // value = "sub:detail".
        var result = TagFacetParser.Parse(new[] { "genre/sub:detail" });

        result.Should().ContainSingle(f => f.Name == "Genre");
        result.Single(f => f.Name == "Genre").Values.Single().Display.Should().Be("sub:detail");
    }

    [Fact]
    public void Parse_NamespaceComparisonIsCaseInsensitive()
    {
        // "Mood:Tense" and "mood:somber" must end up in the same facet.
        // First-seen casing of the namespace wins for the display name.
        var result = TagFacetParser.Parse(new[] { "Mood:Tense, mood:somber, MOOD:victorious" });

        result.Should().HaveCount(1);
        // First-seen "Mood" wins → already title-cased so it stays "Mood".
        result[0].Name.Should().Be("Mood");
        result[0].Values.Should().HaveCount(3);
    }

    [Fact]
    public void Parse_LowerCaseNamespaceIsTitleCased()
    {
        var result = TagFacetParser.Parse(new[] { "mood:tense" });
        result[0].Name.Should().Be("Mood");
    }

    [Fact]
    public void Parse_DuplicateValuesAreCollapsed()
    {
        // Same raw tag appearing on multiple tracks shows once in the facet.
        var result = TagFacetParser.Parse(new[]
        {
            "mood:tense",
            "mood:tense",
            "mood:tense, mood:somber",
        });

        result.Single().Values.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_WhitespaceAroundTagsAndNamespaceIsTrimmed()
    {
        var result = TagFacetParser.Parse(new[] { "  mood : tense  ,   mood :  somber " });

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Mood");
        result[0].Values.Select(v => v.Display)
            .Should().BeEquivalentTo(new[] { "somber", "tense" });
    }

    [Fact]
    public void Parse_TagWithNoDelimiter_GoesToMiscellany()
    {
        var result = TagFacetParser.Parse(new[] { "ambient, mood:tense, drums" });

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("Mood");
        result[1].Name.Should().Be("Tags");
        result[1].IsMiscellany.Should().BeTrue();
        result[1].Values.Select(v => v.RawTag).Should().BeEquivalentTo(new[] { "ambient", "drums" });
    }

    [Fact]
    public void Parse_TagWithEmptyNamespaceOrValue_GoesToMiscellany()
    {
        // ":foo" — empty namespace. "bar:" — empty value. Both should be
        // treated as flat tags rather than malformed-namespace facets.
        var result = TagFacetParser.Parse(new[] { ":foo, bar:, /baz, qux/" });

        result.Should().ContainSingle();
        result[0].IsMiscellany.Should().BeTrue();
        result[0].Values.Select(v => v.RawTag)
            .Should().BeEquivalentTo(new[] { ":foo", "/baz", "bar:", "qux/" });
    }

    [Fact]
    public void Parse_OrderingPutsFacetsAlphabeticallyWithMiscellanyLast()
    {
        var result = TagFacetParser.Parse(new[]
        {
            "ambient",
            "zone:dungeon, mood:tense, genre:fantasy",
        });

        result.Select(f => f.Name).Should().Equal("Genre", "Mood", "Zone", "Tags");
    }

    [Fact]
    public void Parse_NullAndEmptyTagFields_AreSkipped()
    {
        var result = TagFacetParser.Parse(new string?[] { null, "", "   ", "mood:tense" });
        result.Should().ContainSingle(f => f.Name == "Mood");
    }
}
