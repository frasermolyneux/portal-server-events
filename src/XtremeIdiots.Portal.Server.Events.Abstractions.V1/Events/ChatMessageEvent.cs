using System.Text.Json.Serialization;

namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

public class ChatMessageEvent : ServerEventBase
{
    public required string PlayerGuid { get; init; }
    public required string Username { get; init; }
    public required string Message { get; init; }
    public required ChatMessageType Type { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChatMessageType
{
    All,
    Team
}
