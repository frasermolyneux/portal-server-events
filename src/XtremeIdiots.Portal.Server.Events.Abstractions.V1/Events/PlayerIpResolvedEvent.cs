namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

/// <summary>
/// Emitted by the agent when RCON sync discovers a player's IP address.
/// Processed to persist the IP to the Players table via the dedicated UpdatePlayerIpAddress endpoint.
/// </summary>
public class PlayerIpResolvedEvent : ServerEventBase
{
    public required string PlayerGuid { get; init; }
    public required string IpAddress { get; init; }
    public Guid? ConnectionId { get; init; }
    public int? SlotId { get; init; }
    public bool? ProtectionEvaluatedByPlugin { get; init; }
    public Guid? ObservationId { get; init; }
    public ulong? ConnectionGeneration { get; init; }
    public RuleRuntimeDescriptor? Runtime { get; init; }
    public bool? RulesEvaluatedByPlugin { get; init; }
}
