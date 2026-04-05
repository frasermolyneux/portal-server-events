namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

/// <summary>
/// Context passed to chat commands with all relevant event data.
/// </summary>
public sealed record CommandContext
{
    public required Guid ServerId { get; init; }
    public required string GameType { get; init; }
    public required string PlayerGuid { get; init; }
    public required string Username { get; init; }
    public required string Message { get; init; }
    public required DateTime EventGeneratedUtc { get; init; }
    public required DateTime EventPublishedUtc { get; init; }

    /// <summary>
    /// The player's Repository API ID (resolved from GameType + PlayerGuid).
    /// May be null if player lookup failed.
    /// </summary>
    public Guid? PlayerId { get; init; }
}
