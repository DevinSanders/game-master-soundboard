using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundBoard.Core.Plugins;

/// <summary>
/// On-disk manifest that every plugin folder must contain (<c>plugin.json</c>
/// at the folder root). Replaces the older "name the DLL after the folder /
/// the zip" conventions: the manifest is the single source of truth for
/// identity, presentation, and which DLL to load.
///
/// <para><b>Why a manifest, not just reflection on the DLL.</b> Reading the
/// runtime <see cref="SoundBoard.PluginApi.IPlugin"/> properties requires
/// actually executing the plugin (<see cref="System.Activator.CreateInstance"/>
/// runs the constructor). The installer is invoked during a drag-and-drop
/// UX where users have NOT yet consented to run plugin code — we want to be
/// able to display "what is this?" without giving the plugin a chance to
/// touch the process. The manifest is plain JSON; reading it is harmless.
/// The runtime DLL is still loaded later, at which point its IPlugin
/// properties remain authoritative for everything but folder placement.</para>
///
/// <para><b>Field semantics.</b></para>
/// <list type="bullet">
///   <item><description><c>publisher</c> — author / organisation that
///   owns this plugin's release lineage. Required. Recommended
///   convention: a reverse-DNS domain (<c>com.acme</c>,
///   <c>org.example</c>) or a stable handle (<c>github.username</c>).
///   The installer uses <c>(publisher, id)</c> as the upgrade key — an
///   incoming install replaces an existing folder only when BOTH match.
///   A different publisher claiming the same <c>id</c> installs
///   side-by-side instead of clobbering. Spoofable in the absence of
///   signing, but enough to keep honest authors from accidentally
///   stomping each other.</description></item>
///   <item><description><c>id</c> — stable identifier within
///   <c>publisher</c>'s namespace. Must match the plugin instance's
///   <see cref="SoundBoard.PluginApi.IPlugin.Id"/>; used for
///   <c>AppSettings.EnabledPluginIds</c>, settings persistence, FX
///   attachment rows, etc. Combined with <c>publisher</c> (sanitized)
///   to form the on-disk folder name in <c>Plugins\</c> / <c>Themes\</c>.</description></item>
///   <item><description><c>name</c> — display name shown in the
///   settings UI. Free-form.</description></item>
///   <item><description><c>entryDll</c> — file name of the DLL that
///   contains the <see cref="SoundBoard.PluginApi.IPlugin"/>
///   implementation, relative to the plugin folder. No path traversal —
///   the loader rejects values containing <c>..</c> or directory
///   separators.</description></item>
///   <item><description><c>isTheme</c> — declared by the author so the
///   installer can route the folder to <c>Themes\</c> vs <c>Plugins\</c>
///   without loading the DLL. Validated against the actual implemented
///   interface at first runtime load; a mismatch is logged but doesn't
///   reject the plugin.</description></item>
/// </list>
/// </summary>
public sealed class PluginManifestFile
{
    /// <summary>Conventional filename of the manifest inside a plugin
    /// folder. Always lower-case <c>plugin.json</c> — case-sensitive
    /// file systems (Linux) require it.</summary>
    public const string FileName = "plugin.json";

    /// <summary>Author / organisation. Required. See class summary for
    /// the upgrade-lineage semantics this enables.</summary>
    [JsonPropertyName("publisher")]
    public string Publisher { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>File name of the entry-point DLL inside the plugin folder
    /// (e.g. <c>"Mp3CodecPlugin.dll"</c>). Path separators and parent
    /// references are rejected by <see cref="TryLoad"/>.</summary>
    [JsonPropertyName("entryDll")]
    public string EntryDll { get; set; } = "";

    /// <summary>True for theme plugins. Authoritative for install-time
    /// routing (the installer can't execute the DLL to ask). At runtime,
    /// the scanner still checks <see cref="SoundBoard.PluginApi.IThemePlugin"/>
    /// on the type — a mismatch is logged but the plugin still loads.</summary>
    [JsonPropertyName("isTheme")]
    public bool IsTheme { get; set; }

    private static readonly JsonSerializerOptions ParseOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Attempt to load + validate the manifest from a plugin
    /// folder. On failure returns <c>false</c> with a human-readable
    /// reason in <paramref name="error"/>; callers (installer, scanner)
    /// surface the message verbatim. <paramref name="manifest"/> is set
    /// only on success.</summary>
    public static bool TryLoad(string pluginFolder, out PluginManifestFile? manifest, out string? error)
    {
        manifest = null;
        error = null;
        var path = Path.Combine(pluginFolder, FileName);
        if (!File.Exists(path))
        {
            error = $"Missing '{FileName}' at the plugin folder root.";
            return false;
        }

        PluginManifestFile? parsed;
        try
        {
            var json = File.ReadAllText(path);
            parsed = JsonSerializer.Deserialize<PluginManifestFile>(json, ParseOpts);
        }
        catch (Exception ex)
        {
            error = $"Failed to parse '{FileName}': {ex.Message}";
            return false;
        }

        if (parsed == null)
        {
            error = $"'{FileName}' is empty or null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Publisher))
        {
            error = $"'{FileName}' is missing the required 'publisher' field. " +
                    "Add something like \"publisher\": \"com.your-domain\" or " +
                    "\"github.your-handle\". The installer uses (publisher, id) " +
                    "to decide whether an incoming install is an upgrade of an " +
                    "existing plugin or a different plugin that happens to share an id.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Id))
        {
            error = $"'{FileName}' is missing the required 'id' field.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.EntryDll))
        {
            error = $"'{FileName}' is missing the required 'entryDll' field.";
            return false;
        }

        // Reject path traversal — the manifest's entryDll must resolve to
        // a file directly inside the plugin folder. Disallow separators
        // and parent-references; the installer/scanner can then combine
        // pluginFolder + entryDll without further sanitisation.
        if (parsed.EntryDll.Contains('/') ||
            parsed.EntryDll.Contains('\\') ||
            parsed.EntryDll.Contains("..", StringComparison.Ordinal))
        {
            error = $"'entryDll' must be a bare file name (no path separators or '..'). Got: '{parsed.EntryDll}'.";
            return false;
        }

        if (!parsed.EntryDll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            error = $"'entryDll' must end in .dll. Got: '{parsed.EntryDll}'.";
            return false;
        }

        if (!File.Exists(Path.Combine(pluginFolder, parsed.EntryDll)))
        {
            error = $"'entryDll' points at '{parsed.EntryDll}' but no such file exists in the plugin folder.";
            return false;
        }

        // Default the display name to the id when omitted — better than
        // showing a blank row in the settings UI.
        if (string.IsNullOrWhiteSpace(parsed.Name)) parsed.Name = parsed.Id;
        if (string.IsNullOrWhiteSpace(parsed.Version)) parsed.Version = "?";
        if (string.IsNullOrWhiteSpace(parsed.Author)) parsed.Author = "(unspecified)";
        if (string.IsNullOrWhiteSpace(parsed.Description)) parsed.Description = "";

        manifest = parsed;
        return true;
    }

    /// <summary>Return a file-system-safe folder name derived from
    /// <c>(publisher, id)</c>. Two segments joined by <c>__</c> (double
    /// underscore) — the inner sanitisation collapses dots / spaces /
    /// invalid chars to single underscores, so the double-underscore
    /// separator stays unambiguous as the publisher↔id boundary.
    ///
    /// <para>Examples:
    ///   <c>publisher="com.acme",       id="EQ"</c>  → <c>com_acme__EQ</c><br/>
    ///   <c>publisher="github.alice",   id="reverb"</c> → <c>github_alice__reverb</c><br/>
    ///   <c>publisher="org.example",    id="EQ"</c>  → <c>org_example__EQ</c>
    ///   (coexists with the first one — different publishers).
    /// </para>
    ///
    /// Keeps Unicode letters/digits so non-ASCII identifiers stay
    /// readable.</summary>
    public string GetSafeFolderName()
        => $"{Sanitize(Publisher)}__{Sanitize(Id)}";

    /// <summary>Char-level sanitiser for one folder-name segment.
    /// Internal because the public folder-name composition is
    /// <c>GetSafeFolderName</c>; callers shouldn't sanitise pieces
    /// individually (they'd lose the publisher/id boundary).
    ///
    /// <para>Uses an EXPLICIT cross-platform-safe invalid-char set rather
    /// than <see cref="Path.GetInvalidFileNameChars"/>, because that API
    /// is platform-aware: on Unix it returns only <c>'/'</c> and <c>'\0'</c>,
    /// so characters like <c>:</c> / <c>*</c> / <c>?</c> sneak through on
    /// macOS / Linux. A plugin folder built on macOS would then break when
    /// the user moves it to Windows (or to a SMB-mounted Windows share),
    /// silently failing the next scan. The hard-coded set below is the
    /// union of Windows + Unix restrictions plus space and dot, which
    /// keeps the folder name portable across every supported OS.</para></summary>
    private static string Sanitize(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return "plugin";
        var chars = segment.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (IsInvalidForCrossPlatformFolderName(c))
                chars[i] = '_';
        }
        var name = new string(chars).Trim('_');
        return string.IsNullOrEmpty(name) ? "plugin" : name;
    }

    /// <summary>True for any character we won't allow in a folder-name
    /// segment regardless of the host OS. Covers Windows' restricted set
    /// (<c>&lt; &gt; : " / \\ | ? *</c>), Unix's separators
    /// (<c>/</c>, <c>\0</c>), all control characters (0–31), and the two
    /// extra "looks-like-trouble" characters space and dot.</summary>
    private static bool IsInvalidForCrossPlatformFolderName(char c)
    {
        if (c < ' ') return true;             // control chars + NUL
        switch (c)
        {
            case '<': case '>': case ':':     // Windows-reserved
            case '"': case '/': case '\\':
            case '|': case '?': case '*':
            case ' ': case '.':               // portable-folder-name hygiene
                return true;
            default:
                return false;
        }
    }

    /// <summary>Test whether two manifests are the same upgrade lineage
    /// — i.e. the installer should treat an incoming install as a
    /// replacement of an existing folder bearing this manifest. Match
    /// requires both <c>publisher</c> and <c>id</c> to be equal
    /// (case-sensitive — manifest ids are programmatic identifiers
    /// chosen by the author).</summary>
    public bool IsSameLineageAs(PluginManifestFile? other)
        => other != null
        && string.Equals(Publisher, other.Publisher, StringComparison.Ordinal)
        && string.Equals(Id,        other.Id,        StringComparison.Ordinal);
}
