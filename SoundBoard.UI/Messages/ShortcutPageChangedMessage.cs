using CommunityToolkit.Mvvm.Messaging.Messages;

namespace SoundBoard.UI.Messages;

/// <summary>
/// Sent when soundboard pages are added, renamed, or deleted.
/// </summary>
public class ShortcutPageChangedMessage : ValueChangedMessage<bool>
{
    public ShortcutPageChangedMessage() : base(true) { }
}
