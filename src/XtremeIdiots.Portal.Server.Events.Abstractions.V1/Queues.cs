namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1;

/// <summary>
/// Service Bus queue name constants. Used by both publisher (agent) and consumer (processor).
/// </summary>
public static class Queues
{
    public const string PlayerConnected = "player-connected";
    public const string PlayerDisconnected = "player-disconnected";
    public const string ChatMessage = "chat-message";
    public const string MapVote = "map-vote";
    public const string ServerConnected = "server-connected";
    public const string MapChange = "map-change";
    public const string ServerStatus = "server-status";
    public const string BanFileChanged = "ban-file-changed";
}
