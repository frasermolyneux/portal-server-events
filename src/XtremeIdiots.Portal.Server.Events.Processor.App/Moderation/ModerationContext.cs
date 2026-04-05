namespace XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

public sealed record ModerationContext
{
    public required Guid ServerId { get; init; }
    public required string GameType { get; init; }
    public required string PlayerGuid { get; init; }
    public required string Username { get; init; }
    public required string Message { get; init; }
    public required Guid PlayerId { get; init; }
    public required DateTime PlayerFirstSeen { get; init; }
    public required bool HasModerateChatTag { get; init; }
}
