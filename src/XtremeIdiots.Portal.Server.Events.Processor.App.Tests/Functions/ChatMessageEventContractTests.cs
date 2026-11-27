using System.Text.Json;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class ChatMessageEventContractTests
{
    [Fact]
    public void LegacyPayload_WithoutOccurrenceId_RemainsReadable()
    {
        const string json = """
            {"eventGeneratedUtc":"2026-01-01T00:00:00Z","eventPublishedUtc":"2026-01-01T00:00:01Z",
             "serverId":"11111111-1111-1111-1111-111111111111","gameType":"CallOfDuty4",
             "sequenceId":1,"playerGuid":"abc","username":"player","slotId":2,"message":"hello","type":"All",
             "messageId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}
            """;

        var result = JsonSerializer.Deserialize<ChatMessageEvent>(json, JsonOptions.Default);

        Assert.NotNull(result);
        Assert.Null(result.OccurrenceId);
        Assert.Equal("hello", result.Message);
    }

    [Fact]
    public void CaptureIdentity_RoundTripsAsCamelCaseGuid()
    {
        var occurrenceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var evt = new ChatMessageEvent
        {
            EventGeneratedUtc = DateTime.UnixEpoch,
            EventPublishedUtc = DateTime.UnixEpoch,
            ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            GameType = "CallOfDuty4",
            SequenceId = 1,
            OccurrenceId = occurrenceId,
            PlayerGuid = "abc",
            Username = "player",
            SlotId = 2,
            Message = "hello",
            Type = ChatMessageType.All
        };

        var serialized = JsonSerializer.Serialize(evt, JsonOptions.Default);
        using var json = JsonDocument.Parse(serialized);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", json.RootElement.GetProperty("occurrenceId").GetString());
        Assert.Equal(occurrenceId, JsonSerializer.Deserialize<ChatMessageEvent>(serialized, JsonOptions.Default)!.OccurrenceId);
    }
}
