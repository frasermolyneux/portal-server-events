namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

/// <summary>
/// Published when a ban sync operation fails and requires operator visibility.
/// </summary>
public sealed class BanSyncFailedEvent : ServerEventBase
{
    public required string Operation { get; init; }

    public required string FailureReason { get; init; }

    public required string Source { get; init; }

    public string? PlayerGuid { get; init; }

    public string? PlayerName { get; init; }

    public string? CorrelationId { get; init; }
}
