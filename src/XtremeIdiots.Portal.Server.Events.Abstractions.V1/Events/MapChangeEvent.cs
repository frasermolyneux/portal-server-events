namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

public class MapChangeEvent : ServerEventBase
{
    public required string MapName { get; init; }
    public required string GameName { get; init; }
}
