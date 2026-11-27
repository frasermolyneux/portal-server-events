using System.Text.Json.Serialization;

namespace XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

public class ChatMessageEvent : ServerEventBase
{
    /// <summary>
    /// Identifies one captured chat event within a server. Retained across delivery retries,
    /// but a log line captured again after a restart may receive a different identifier.
    /// Null indicates a legacy event without capture identity.
    /// </summary>
    public Guid? OccurrenceId { get; init; }

    public required string PlayerGuid { get; init; }
    public required string Username { get; init; }
    public required int SlotId { get; init; }
    public required string Message { get; init; }
    public required ChatMessageType Type { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChatMessageType
{
    All,
    Team
}
