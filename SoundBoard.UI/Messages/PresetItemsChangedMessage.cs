using CommunityToolkit.Mvvm.Messaging.Messages;

namespace SoundBoard.UI.Messages;

/// <summary>
/// Sent when a preset's tracks change (add / remove) so the Presets list view
/// can refresh the "N track(s)" badge for that preset row.
/// </summary>
public class PresetItemsChangedMessage : ValueChangedMessage<int>
{
    /// <param name="presetId">Id of the preset whose tracks changed.</param>
    public PresetItemsChangedMessage(int presetId) : base(presetId) { }
}
