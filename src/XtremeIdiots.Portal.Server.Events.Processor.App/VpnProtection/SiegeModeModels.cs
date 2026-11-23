using MX.GeoLocation.Abstractions.Models.V1_1;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.SiegeMode;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public sealed record SiegeModeEvaluationContext
{
    public required Guid ServerId { get; init; }
    public required GameType GameType { get; init; }
    public required string PlayerGuid { get; init; }
    public required string Username { get; init; }
    public required int? SlotId { get; init; }
    public required Guid? ConnectionId { get; init; }
    public required string? IpAddress { get; init; }
    public required DateTime EventGeneratedUtc { get; init; }
    public required IReadOnlyCollection<string>? PlayerTags { get; init; }
    public required IpIntelligenceDto? Intelligence { get; init; }
    public required bool IsPluginPath { get; init; }
    public required bool SuppressEnforcement { get; init; }
    public Guid? ObservationPlayerId { get; init; }
    public RecordPlayerIpObservationDto? IpObservation { get; init; }
}

public sealed record SiegeModeProcessingResult
{
    public SiegeSessionDto? Session { get; init; }
    public SiegeActionDto? Action { get; init; }
    public bool Matched { get; init; }
    public bool ShouldDispatch { get; init; }
    public bool WasEnforced { get; init; }
    public string Reason { get; init; } = string.Empty;

    public static SiegeModeProcessingResult NoMatch { get; } = new();
}

public interface ISiegeModeService
{
    Task<SiegeModeProcessingResult> EvaluateAsync(
        SiegeModeEvaluationContext context,
        CancellationToken ct = default);
}
