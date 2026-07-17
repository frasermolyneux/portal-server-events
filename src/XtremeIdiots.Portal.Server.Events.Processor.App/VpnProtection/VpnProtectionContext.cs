using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

public sealed record VpnProtectionContext
{
    public required Guid ServerId { get; init; }

    public required GameType GameType { get; init; }

    public required Guid PlayerId { get; init; }

    public required string PlayerGuid { get; init; }

    public required string Username { get; init; }

    public required IReadOnlyCollection<string> PlayerTags { get; init; }

    public int? SlotId { get; init; }
}