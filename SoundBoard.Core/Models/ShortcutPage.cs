using System.Collections.Generic;

namespace SoundBoard.Core.Models;

/// <summary>
/// One tab of the main-window soundboard grid — a named collection of
/// <see cref="ShortcutButton"/>s arranged on a page that the user can switch
/// to at runtime.
/// </summary>
public class ShortcutPage
{
    public int Id { get; set; }
    public string Name { get; set; } = "New Page";
    public int OrderIndex { get; set; }

    /// <summary>Hidden from the tab strip but still present in the library
    /// (preserves the page's buttons + position). Surfaced via the
    /// "Hidden tabs ▾" overflow popup in the soundboard view so the user
    /// can show it again at any time. Persistent — a page hidden in one
    /// session stays hidden until explicitly shown.</summary>
    public bool IsHidden { get; set; }

    public List<ShortcutButton> Buttons { get; set; } = new();
}
