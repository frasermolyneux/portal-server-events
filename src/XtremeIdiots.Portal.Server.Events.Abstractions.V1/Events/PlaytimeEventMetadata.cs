namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

/// <summary>
/// Metadata for a playtime observation carried by a connect, disconnect, map change,
/// or server status event. Preserve all values when retrying or replaying an event.
/// </summary>
public sealed class PlaytimeEventMetadata
{
    public required Guid ObservationId { get; init; }
    public required DateTime EventUtc { get; init; }
    public required PlaytimeTimestampQuality TimestampQuality { get; init; }
    public PlaytimeObservationFreshness Freshness { get; init; }
    public Guid? ProducerEpoch { get; init; }
    public long? ProducerSequence { get; init; }
    public Guid? ConnectionCorrelationId { get; init; }
    public PlaytimeRosterCompleteness RosterCompleteness { get; init; }
}

public enum PlaytimeObservationFreshness
{
    Unknown = 0,
    Live = 1
}

public enum PlaytimeTimestampQuality
{
    Unknown = 0,
    ParsedLogTimestamp = 1,
    ProducerTimestamp = 2,
    RosterSnapshotTimestamp = 3,
    ParseTime = 4
}

public enum PlaytimeRosterCompleteness
{
    NotApplicable = 0,
    Complete = 1,
    Partial = 2,
    Failed = 3
}
