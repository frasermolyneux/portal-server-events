namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

public class PlayerConnectedEvent : ServerEventBase
{
    public required string PlayerGuid { get; init; }
    public string? SteamId { get; init; }
    public required string Username { get; init; }
    public required string IpAddress { get; init; }
    public required int SlotId { get; init; }
    public Guid? ConnectionId { get; init; }
    public bool? ProtectionEvaluatedByPlugin { get; init; }
    public Guid? ObservationId { get; init; }
    public ulong? ConnectionGeneration { get; init; }
    public RuleRuntimeDescriptor? Runtime { get; init; }
    public bool? RulesEvaluatedByPlugin { get; init; }
}
