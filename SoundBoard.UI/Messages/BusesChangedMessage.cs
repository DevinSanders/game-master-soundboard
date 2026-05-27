using CommunityToolkit.Mvvm.Messaging.Messages;

namespace SoundBoard.UI.Messages;

/// <summary>
/// Sent when the Buses table is mutated — add, rename, or delete — so
/// open windows that show bus state (the Bus Mixer page's strips, the
/// Track Editor's bus dropdown, the Preset Editor's bus-override
/// dropdown) can reload. Carries no payload because the receivers
/// re-query the table; a per-bus signal would push complexity onto the
/// senders for little gain.
/// </summary>
public class BusesChangedMessage : ValueChangedMessage<int>
{
    /// <param name="busId">Id of the bus that triggered the change, or
    /// 0 if not bus-specific (e.g. add). Receivers may use this for
    /// targeted refresh but most just reload the list.</param>
    public BusesChangedMessage(int busId = 0) : base(busId) { }
}
