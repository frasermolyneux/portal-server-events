namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

public class PlayerDisconnectedEvent : ServerEventBase
{
    public required string PlayerGuid { get; init; }
    public required string Username { get; init; }
    public required int SlotId { get; init; }
    public Guid? ConnectionId { get; init; }
    public ulong? ConnectionGeneration { get; init; }
    public Guid? SnapshotId { get; init; }
    public ulong? Sequence { get; init; }
    public DateTimeOffset? ObservedAtUtc { get; init; }
    public RuleRuntimeDescriptor? Runtime { get; init; }
}
