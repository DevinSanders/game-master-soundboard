using CommunityToolkit.Mvvm.Messaging.Messages;

namespace SoundBoard.UI.Messages;

/// <summary>
/// Sent after a shortcut page's button order is persisted (drag-reorder).
/// Both the main soundboard and any popped-out view of the same page
/// reload their button list on receipt so they stay in sync.
/// </summary>
public class ShortcutsReorderedMessage : ValueChangedMessage<int>
{
    /// <param name="pageId">The ID of the ShortcutPage whose order changed.</param>
    public ShortcutsReorderedMessage(int pageId) : base(pageId) { }
}
