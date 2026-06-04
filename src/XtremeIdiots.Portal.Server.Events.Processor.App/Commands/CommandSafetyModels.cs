using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed record MapValidationResult(bool IsValid, string? Reason = null, bool IsLiveMapListMismatch = false);

public sealed record PlayerResolutionResult(
    bool Success,
    ResolvePlayerResponseDto? Response = null,
    string? Reason = null);

public sealed record PlayerSlotVerificationResult(bool IsValid, string? Reason = null);
