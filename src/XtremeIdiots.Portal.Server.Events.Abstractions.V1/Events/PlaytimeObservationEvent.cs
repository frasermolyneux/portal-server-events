namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

public enum PlaytimeObservationKind
{
    PlayerJoined = 1,
    PlayerLeft = 2,
    MapChanged = 3,
    ServerRoster = 4
}

public sealed class PlaytimeObservationEvent : ServerEventBase
{
    public required PlaytimeObservationKind Kind { get; init; }
    public required PlaytimeEventMetadata Playtime { get; init; }
    public string? PlayerGuid { get; init; }
    public string? Username { get; init; }
    public string? SteamId { get; init; }
    public int? SlotId { get; init; }
    public bool? IsBot { get; init; }
    public string? MapName { get; init; }
    public IReadOnlyList<PlaytimeRosterPlayer>? Players { get; init; }
    public PlaytimeCoverage? PlaytimeCoverage { get; init; }
}

public sealed class PlaytimeRosterPlayer
{
    public required string PlayerGuid { get; init; }
    public required string Username { get; init; }
    public required int SlotId { get; init; }
    public bool? IsBot { get; init; }
}

public sealed class PlaytimeCoverage
{
    public required DateTime ProducerStartedUtc { get; init; }
    public required long DroppedCapacity { get; init; }
    public required long DroppedAttempts { get; init; }
    public required long DroppedAge { get; init; }
}
