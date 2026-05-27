using CommunityToolkit.Mvvm.Messaging.Messages;

namespace SoundBoard.UI.Messages;

/// <summary>
/// Sent when a playlist's item collection changes (add / remove / reorder)
/// so list views can refresh the "N item(s)" badge for that playlist row.
/// </summary>
public class PlaylistItemsChangedMessage : ValueChangedMessage<int>
{
    /// <param name="playlistId">Id of the playlist whose items changed.</param>
    public PlaylistItemsChangedMessage(int playlistId) : base(playlistId) { }
}
