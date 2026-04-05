namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

public class BanFileChangedEvent : ServerEventBase
{
    public required string FilePath { get; init; }
    public required long FileSize { get; init; }
    public required byte[] FileContent { get; init; }
}
