namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

public class PlayerDisconnectedEvent : ServerEventBase
{
    public required string PlayerGuid { get; init; }
    public required string Username { get; init; }
    public required int SlotId { get; init; }
}
