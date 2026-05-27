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
    
    public List<ShortcutButton> Buttons { get; set; } = new();
}
