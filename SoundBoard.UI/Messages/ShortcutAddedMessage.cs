using CommunityToolkit.Mvvm.Messaging.Messages;

namespace SoundBoard.UI.Messages;

/// <summary>
/// Sent when a new shortcut button is added to the database, 
/// so the ShortcutsViewModel can reload and display it immediately.
/// </summary>
public class ShortcutAddedMessage : ValueChangedMessage<int>
{
    /// <param name="pageId">The ID of the ShortcutPage the button was added to.</param>
    public ShortcutAddedMessage(int pageId) : base(pageId) { }
}
