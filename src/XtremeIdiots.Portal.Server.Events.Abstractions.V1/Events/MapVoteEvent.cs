namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

public class MapVoteEvent : ServerEventBase
{
    public required string PlayerGuid { get; init; }
    public required string MapName { get; init; }
    public required bool Like { get; init; }
}
