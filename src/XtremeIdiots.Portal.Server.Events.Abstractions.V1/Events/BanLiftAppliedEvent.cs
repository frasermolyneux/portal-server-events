namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

/// <summary>
/// Published when an existing ban has been lifted on a game server.
/// </summary>
public sealed class BanLiftAppliedEvent : ServerEventBase
{
    public required string PlayerGuid { get; init; }

    public required string PlayerName { get; init; }

    public required string Source { get; init; }

    public required string LiftReason { get; init; }

    public string? CorrelationId { get; init; }
}
