namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

/// <summary>
/// Published when a ban has been successfully applied to a game server.
/// </summary>
public sealed class BanAppliedEvent : ServerEventBase
{
    public required string PlayerGuid { get; init; }

    public required string PlayerName { get; init; }

    public required bool IsTemporary { get; init; }

    public DateTime? ExpiresUtc { get; init; }

    public required string Source { get; init; }

    public required string Reason { get; init; }

    public string? CorrelationId { get; init; }
}
