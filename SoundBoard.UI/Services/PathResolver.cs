using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SoundBoard.UI.Services;

/// <summary>
/// Locates audio files for a library import when the exported paths no longer
/// resolve verbatim (different machine, different drive letter, mounted Drive
/// at a different path). Strategy:
///
/// <list type="number">
///   <item>If the original path still exists on disk, use it.</item>
///   <item>Otherwise look up by filename in the index of every file under
///         the user-supplied search roots. If exactly one match: use it.</item>
///   <item>If multiple files share the name, score each candidate by the
///         number of trailing path components that match the imported path
///         and pick the longest match. So a stored
///         <c>D:\Music\Ambient\rain.mp3</c> matching against search roots
///         that contain both <c>...\Combat\rain.mp3</c> and
///         <c>...\Ambient\rain.mp3</c> picks the latter.</item>
/// </list>
/// </summary>
public sealed class PathResolver
{
    private readonly Dictionary<string, List<string>> _byFilename =
        new(StringComparer.OrdinalIgnoreCase);

    public PathResolver(IEnumerable<string> searchPaths)
    {
        foreach (var root in searchPaths ?? Array.Empty<string>())
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (!_byFilename.TryGetValue(name, out var list))
                        _byFilename[name] = list = new List<string>();
                    list.Add(file);
                }
            }
            catch
            {
                // Unreadable subdir / access denied — skip silently. The user
                // will see missing files in the result and can re-import.
            }
        }
    }

    public string? Resolve(string originalPath)
    {
        if (string.IsNullOrEmpty(originalPath)) return null;

        // 1. Verbatim path still works (e.g. same machine).
        if (File.Exists(originalPath)) return originalPath;

        // Cross-platform filename extraction. The imported path may have
        // been exported from any OS — a Windows export carries paths like
        // "F:\My Drive\...\file.ogg" with NO forward slashes. On Unix,
        // Path.GetFileName looks ONLY for '/' and would therefore return
        // the entire input as the "filename" on macOS / Linux, breaking
        // the dictionary lookup below (which was keyed off real local
        // filenames extracted normally). Treat BOTH separators uniformly
        // before splitting — matches how the suffix-match Split helper
        // below already works.
        var name = ExtractFileName(originalPath);
        if (!_byFilename.TryGetValue(name, out var candidates) || candidates.Count == 0)
            return null;

        if (candidates.Count == 1) return candidates[0];

        // 2. Multiple candidates — pick the one whose path suffix matches the
        //    imported path the longest.
        var importedParts = Split(originalPath);
        string? best = null;
        int bestScore = -1;
        foreach (var c in candidates)
        {
            var actualParts = Split(c);
            int score = 0;
            int n = Math.Min(importedParts.Length, actualParts.Length);
            for (int i = 1; i <= n; i++)
            {
                if (string.Equals(importedParts[^i], actualParts[^i], StringComparison.OrdinalIgnoreCase))
                    score++;
                else
                    break;
            }
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    private static string[] Split(string p) =>
        p.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Extract the filename portion of a path treating BOTH '/' and
    /// '\\' as separators, regardless of the current platform. Necessary
    /// because exported libraries can carry foreign-format paths (Windows
    /// '\\' on macOS, etc.) that the platform-aware
    /// <see cref="Path.GetFileName(string)"/> mishandles.</summary>
    private static string ExtractFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        int lastSlash = path.LastIndexOf('/');
        int lastBackslash = path.LastIndexOf('\\');
        int cut = Math.Max(lastSlash, lastBackslash);
        return cut < 0 ? path : path.Substring(cut + 1);
    }
}
