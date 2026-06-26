using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// One row in the Library's "Filters ▾" popup — the user checks the
/// facet names they want surfaced as visible chip groups. Per-session
/// state only; not persisted.
/// </summary>
public sealed partial class FacetVisibilityChoice : ObservableObject
{
    public string Name { get; }
    [ObservableProperty] private bool _isVisible;

    public FacetVisibilityChoice(string name, bool isVisible = false)
    {
        Name = name;
        IsVisible = isVisible;
    }
}

/// <summary>
/// One namespaced filter group surfaced in the Library window — e.g. tags
/// like <c>mood:tense</c>, <c>mood:somber</c>, <c>mood:victorious</c> roll up
/// into a single facet named "Mood" whose values are "tense", "somber",
/// "victorious". Tags with no namespace land in the miscellany facet (named
/// "Tags", marked with <see cref="IsMiscellany"/>).
/// </summary>
public sealed partial class TagFacetViewModel : ObservableObject
{
    public string Name { get; }
    public bool IsMiscellany { get; }
    public ObservableCollection<TagFacetValueViewModel> Values { get; } = new();

    /// <summary>Whether the chip row is expanded in the Library view.
    /// Defaults to true on construction so a freshly-revealed facet is
    /// usable immediately; the header chevron toggles this.</summary>
    [ObservableProperty] private bool _isExpanded = true;

    public TagFacetViewModel(string name, bool isMiscellany)
    {
        Name = name;
        IsMiscellany = isMiscellany;
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>
/// One selectable value within a <see cref="TagFacetViewModel"/>. The
/// ToggleButton chip in the view binds two-way to <see cref="IsSelected"/>;
/// the library VM subscribes to that change to re-run filtering.
/// <see cref="RawTag"/> is the exact string stored on
/// <see cref="SoundBoard.Core.Models.Track.Tags"/> (e.g. "mood:tense"), used
/// for the actual match. <see cref="Display"/> is the user-facing label
/// (e.g. "tense").
/// </summary>
public sealed partial class TagFacetValueViewModel : ObservableObject
{
    public string Display { get; }
    public string RawTag { get; }

    [ObservableProperty] private bool _isSelected;

    public TagFacetValueViewModel(string display, string rawTag)
    {
        Display = display;
        RawTag = rawTag;
    }
}

/// <summary>
/// Splits a flat tag set into facet groups by namespace prefix. Recognised
/// delimiters are <c>:</c> and <c>/</c>; the first occurrence in a tag is
/// used as the split point. Tags with no delimiter, an empty namespace, or
/// an empty value go into the miscellany group. Namespace comparison is
/// case-insensitive (so <c>Mood:Tense</c> and <c>mood:somber</c> roll up
/// together) — the first-seen casing of the namespace becomes the facet's
/// display name, title-cased.
///
/// <para>Output ordering: structured facets first, alphabetical by name;
/// miscellany last. Within each facet, values are alphabetical by display
/// label.</para>
/// </summary>
public static class TagFacetParser
{
    // Both delimiters at once so a single IndexOfAny picks whichever comes
    // first. Splitting on the first delimiter (vs. the last) means
    // "mood:happy/sub" parses as Mood = "happy/sub" — consistent with how
    // a namespaced tag scheme nests.
    private static readonly char[] Delimiters = { ':', '/' };

    public readonly record struct ParsedFacet(string Name, bool IsMiscellany, IReadOnlyList<ParsedValue> Values);
    public readonly record struct ParsedValue(string Display, string RawTag);

    /// <summary>
    /// Walk every tag string from every track (each may be a comma-separated
    /// list), bucket them by detected namespace, and produce the ordered
    /// facet list the Library view will render.
    /// </summary>
    public static IReadOnlyList<ParsedFacet> Parse(IEnumerable<string?> trackTagFields)
    {
        // Per-facet: lower-cased namespace key → (display name, values map).
        // values map: lower-cased value → (display value, first-seen raw tag).
        // Lower-casing for the lookup key folds Mood/mood/MOOD together;
        // first-seen casing wins for what we actually show.
        var byNamespace = new Dictionary<string, FacetBucket>(StringComparer.OrdinalIgnoreCase);
        var miscellany = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in trackTagFields)
        {
            if (string.IsNullOrWhiteSpace(field)) continue;
            foreach (var raw in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var tag = raw.Trim();
                if (tag.Length == 0) continue;

                var splitIdx = tag.IndexOfAny(Delimiters);
                if (splitIdx <= 0 || splitIdx >= tag.Length - 1)
                {
                    // No delimiter, leading delimiter (":foo"), or trailing
                    // delimiter ("mood:") — keep the tag as-is in miscellany.
                    if (!miscellany.ContainsKey(tag)) miscellany[tag] = tag;
                    continue;
                }

                var ns = tag.Substring(0, splitIdx).Trim();
                var value = tag.Substring(splitIdx + 1).Trim();
                if (ns.Length == 0 || value.Length == 0)
                {
                    if (!miscellany.ContainsKey(tag)) miscellany[tag] = tag;
                    continue;
                }

                if (!byNamespace.TryGetValue(ns, out var bucket))
                {
                    bucket = new FacetBucket(TitleCase(ns));
                    byNamespace[ns] = bucket;
                }
                if (!bucket.Values.ContainsKey(value))
                    bucket.Values[value] = (value, tag);
            }
        }

        var result = new List<ParsedFacet>();
        foreach (var ns in byNamespace.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var bucket = byNamespace[ns];
            var values = bucket.Values.Values
                .OrderBy(v => v.Display, StringComparer.OrdinalIgnoreCase)
                .Select(v => new ParsedValue(v.Display, v.RawTag))
                .ToList();
            result.Add(new ParsedFacet(bucket.DisplayName, IsMiscellany: false, values));
        }

        if (miscellany.Count > 0)
        {
            var values = miscellany.Values
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Select(v => new ParsedValue(v, v))
                .ToList();
            result.Add(new ParsedFacet("Tags", IsMiscellany: true, values));
        }

        return result;
    }

    private static string TitleCase(string s)
    {
        // Cheap title-case: uppercase first letter, leave the rest alone.
        // Good enough for tag namespaces like "mood" → "Mood" without
        // pulling CultureInfo's full TextInfo for one-character flips.
        if (s.Length == 0) return s;
        if (char.IsUpper(s[0])) return s;
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    private sealed class FacetBucket
    {
        public string DisplayName { get; }
        public Dictionary<string, (string Display, string RawTag)> Values { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public FacetBucket(string displayName) { DisplayName = displayName; }
    }
}
