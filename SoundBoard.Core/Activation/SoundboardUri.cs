using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SoundBoard.Core.Activation;

/// <summary>Action verb parsed from the <c>gmsound://</c> URI scheme.</summary>
public enum SoundboardUriAction
{
    Play,
    Stop,
    Toggle,
    StopAll,
    /// <summary>Playlist-only: stop current item so the playlist auto-advances to the next.</summary>
    Next,
    /// <summary>Playlist-only: rewind session pointer by one and stop current so the previous item plays.</summary>
    Previous,
}

/// <summary>The kind of library entity a <c>gmsound://</c> URI targets.</summary>
public enum SoundboardUriItemType
{
    Track,
    Preset,
    Playlist
}

/// <summary>
/// Strongly-typed representation of a <c>gmsound://</c> activation URI.
///
/// Format:
/// <code>
/// gmsound://&lt;action&gt;/&lt;type&gt;/&lt;id&gt;?param=value&amp;...
/// </code>
/// where <c>action</c> ∈ {play, stop, toggle}, <c>type</c> ∈ {track, preset, playlist}.
/// The <c>stopAll</c> action takes no item: <c>gmsound://stopAll</c>.
///
/// Query parameters are optional and only meaningful for <c>play</c>:
/// <list type="bullet">
///   <item><c>volume</c>      — 0.0–1.0 (or 0–100 if &gt; 1, auto-rescaled).</item>
///   <item><c>loop</c>        — true/false.</item>
///   <item><c>fadeIn</c>      — seconds.</item>
///   <item><c>fadeOut</c>     — seconds.</item>
///   <item><c>startDelay</c>  — seconds before the track starts (and between loops if looping).
///                              Older links may use <c>loopDelay</c>; the parser accepts both.</item>
///   <item><c>stopPlaying</c> — true to stop everything else before this fires.</item>
/// </list>
/// </summary>
public sealed class SoundboardUri
{
    public const string SchemeName = "gmsound";

    public SoundboardUriAction Action { get; set; }
    public SoundboardUriItemType? ItemType { get; set; }
    public int? ItemId { get; set; }

    public float? Volume { get; set; }
    public bool? Loop { get; set; }
    public TimeSpan? FadeIn { get; set; }
    public TimeSpan? FadeOut { get; set; }
    public TimeSpan? StartDelay { get; set; }
    public bool StopPlaying { get; set; }

    public static bool TryParse(string text, out SoundboardUri result)
    {
        result = new SoundboardUri();
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, SchemeName, StringComparison.OrdinalIgnoreCase)) return false;

        // System.Uri parses gmsound://play/track/123 as
        //   Host = "play", AbsolutePath = "/track/123"
        // and gmsound://stopAll as
        //   Host = "stopall", AbsolutePath = "/" (or "")
        var action = uri.Host;
        if (string.IsNullOrEmpty(action)) return false;

        if (!TryParseAction(action, out var act)) return false;
        result.Action = act;

        if (act != SoundboardUriAction.StopAll)
        {
            // Need /type/id from the path.
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) return false;
            if (!TryParseItemType(segments[0], out var itemType)) return false;
            if (!int.TryParse(segments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return false;

            result.ItemType = itemType;
            result.ItemId = id;
        }

        // Parse query string manually — query is "?a=b&c=d" or empty.
        foreach (var (key, value) in ParseQuery(uri.Query))
        {
            switch (key.ToLowerInvariant())
            {
                case "volume":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vol))
                    {
                        // Accept 0–1 or 0–100; if > 1 treat as percentage.
                        result.Volume = vol > 1f ? vol / 100f : vol;
                    }
                    break;
                case "loop":
                    if (bool.TryParse(value, out var loop)) result.Loop = loop;
                    break;
                case "fadein":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fi))
                        result.FadeIn = TimeSpan.FromSeconds(fi);
                    break;
                case "fadeout":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fo))
                        result.FadeOut = TimeSpan.FromSeconds(fo);
                    break;
                case "startdelay":
                case "loopdelay": // legacy alias — older links emitted this name
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sd))
                        result.StartDelay = TimeSpan.FromSeconds(sd);
                    break;
                case "stopplaying":
                    if (bool.TryParse(value, out var sp)) result.StopPlaying = sp;
                    break;
            }
        }

        return true;
    }

    public string ToUriString()
    {
        var sb = new StringBuilder();
        sb.Append(SchemeName).Append("://").Append(ActionToString(Action));

        if (Action != SoundboardUriAction.StopAll)
        {
            if (ItemType == null || ItemId == null)
                throw new InvalidOperationException("Action requires ItemType and ItemId.");

            sb.Append('/').Append(ItemTypeToString(ItemType.Value));
            sb.Append('/').Append(ItemId.Value.ToString(CultureInfo.InvariantCulture));
        }

        // Build query string in deterministic order.
        var parts = new List<string>();
        if (Volume.HasValue)    parts.Add("volume=" + Volume.Value.ToString("0.###", CultureInfo.InvariantCulture));
        if (Loop.HasValue)      parts.Add("loop=" + (Loop.Value ? "true" : "false"));
        if (FadeIn.HasValue)    parts.Add("fadeIn=" + FadeIn.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        if (FadeOut.HasValue)   parts.Add("fadeOut=" + FadeOut.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        if (StartDelay.HasValue) parts.Add("startDelay=" + StartDelay.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        if (StopPlaying)        parts.Add("stopPlaying=true");

        if (parts.Count > 0)
        {
            sb.Append('?');
            sb.Append(string.Join("&", parts));
        }

        return sb.ToString();
    }

    public override string ToString() => ToUriString();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool TryParseAction(string s, out SoundboardUriAction action)
    {
        switch (s.ToLowerInvariant())
        {
            case "play":     action = SoundboardUriAction.Play;     return true;
            case "stop":     action = SoundboardUriAction.Stop;     return true;
            case "toggle":   action = SoundboardUriAction.Toggle;   return true;
            case "stopall":  action = SoundboardUriAction.StopAll;  return true;
            case "next":     action = SoundboardUriAction.Next;     return true;
            case "previous": action = SoundboardUriAction.Previous; return true;
            default:         action = default;                      return false;
        }
    }

    private static bool TryParseItemType(string s, out SoundboardUriItemType type)
    {
        switch (s.ToLowerInvariant())
        {
            case "track":    type = SoundboardUriItemType.Track;    return true;
            case "preset":   type = SoundboardUriItemType.Preset;   return true;
            case "playlist": type = SoundboardUriItemType.Playlist; return true;
            default:         type = default;                        return false;
        }
    }

    private static string ActionToString(SoundboardUriAction a) => a switch
    {
        SoundboardUriAction.Play     => "play",
        SoundboardUriAction.Stop     => "stop",
        SoundboardUriAction.Toggle   => "toggle",
        SoundboardUriAction.StopAll  => "stopAll",
        SoundboardUriAction.Next     => "next",
        SoundboardUriAction.Previous => "previous",
        _ => throw new ArgumentOutOfRangeException(nameof(a))
    };

    private static string ItemTypeToString(SoundboardUriItemType t) => t switch
    {
        SoundboardUriItemType.Track    => "track",
        SoundboardUriItemType.Preset   => "preset",
        SoundboardUriItemType.Playlist => "playlist",
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };

    private static IEnumerable<(string Key, string Value)> ParseQuery(string query)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        var q = query.StartsWith('?') ? query.Substring(1) : query;
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) yield return (Uri.UnescapeDataString(pair), "true");
            else        yield return (Uri.UnescapeDataString(pair.Substring(0, eq)),
                                       Uri.UnescapeDataString(pair.Substring(eq + 1)));
        }
    }
}
