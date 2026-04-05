namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

/// <summary>
/// Base class for all server events. Carries timing, identity, and ordering metadata.
/// </summary>
public abstract class ServerEventBase
{
    /// <summary>
    /// When the event occurred on the game server (derived from log line timestamp).
    /// Used for staleness checks and out-of-order resolution.
    /// </summary>
    public required DateTime EventGeneratedUtc { get; init; }

    /// <summary>
    /// When the agent published this event to Service Bus.
    /// </summary>
    public required DateTime EventPublishedUtc { get; init; }

    /// <summary>
    /// Portal game server identifier (from Repository API).
    /// </summary>
    public required Guid ServerId { get; init; }

    /// <summary>
    /// Game type string matching the portal GameType (e.g. "CallOfDuty4").
    /// </summary>
    public required string GameType { get; init; }

    /// <summary>
    /// Monotonically increasing counter per server within the agent.
    /// Allows processors to detect gaps and reordering. Resets on agent restart.
    /// </summary>
    public required long SequenceId { get; init; }

    /// <summary>
    /// Check if this event is older than the specified maximum age.
    /// </summary>
    public bool IsStale(TimeSpan maxAge) => DateTime.UtcNow - EventGeneratedUtc > maxAge;
}
