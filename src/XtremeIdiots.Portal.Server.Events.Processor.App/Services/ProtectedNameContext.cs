namespace XtremeIdiots.Portal.Server.Events.Processor.App.Services;

/// <summary>
/// Context passed to the protected name check when a player connects.
/// </summary>
public sealed record ProtectedNameContext
{
    public required Guid ServerId { get; init; }
    public required string GameType { get; init; }
    public required string Username { get; init; }
    public required Guid PlayerId { get; init; }
    public required int SlotId { get; init; }
}
