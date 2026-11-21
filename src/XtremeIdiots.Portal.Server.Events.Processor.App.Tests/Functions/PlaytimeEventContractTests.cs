using System.Text.Json;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class PlaytimeEventContractTests
{
    [Fact]
    public void LegacyEvent_WithoutPlaytime_RemainsReadable()
    {
        const string json = """
            {"eventGeneratedUtc":"2026-01-01T00:00:00Z","eventPublishedUtc":"2026-01-01T00:00:01Z",
             "serverId":"11111111-1111-1111-1111-111111111111","gameType":"CallOfDuty4",
             "sequenceId":1,"playerGuid":"abc","username":"player","slotId":2}
            """;

        var result = JsonSerializer.Deserialize<PlayerDisconnectedEvent>(json, JsonOptions.Default);

        Assert.NotNull(result);
        Assert.Equal("abc", result.PlayerGuid);
    }

    [Fact]
    public void ProducerPayload_WithPlaytime_RetainsMetadata()
    {
        const string json = """
            {"eventGeneratedUtc":"2026-01-01T00:00:00Z","eventPublishedUtc":"2026-01-01T00:00:01Z",
             "serverId":"11111111-1111-1111-1111-111111111111","gameType":"CallOfDuty4",
             "sequenceId":2,"kind":3,"mapName":"mp_crash",
             "playtime":{"observationId":"22222222-2222-2222-2222-222222222222",
             "eventUtc":"2026-01-01T00:00:00Z","timestampQuality":4,
             "producerEpoch":"33333333-3333-3333-3333-333333333333","producerSequence":2,
             "connectionCorrelationId":"44444444-4444-4444-4444-444444444444","rosterCompleteness":0}}
            """;

        var result = JsonSerializer.Deserialize<PlaytimeObservationEvent>(json, JsonOptions.Default);

        Assert.NotNull(result?.Playtime);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), result.Playtime.ObservationId);
        Assert.Equal(DateTimeKind.Utc, result.Playtime.EventUtc.Kind);
        Assert.Equal(PlaytimeTimestampQuality.ParseTime, result.Playtime.TimestampQuality);
        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), result.Playtime.ProducerEpoch);
        Assert.Equal(2, result.Playtime.ProducerSequence);
        Assert.Equal(Guid.Parse("44444444-4444-4444-4444-444444444444"), result.Playtime.ConnectionCorrelationId);
    }

    [Fact]
    public void DedicatedStatusPayload_WithPartialRoster_IsReadable()
    {
        const string json = """
            {"eventGeneratedUtc":"2026-01-01T00:00:00Z","eventPublishedUtc":"2026-01-01T00:00:01Z",
             "serverId":"11111111-1111-1111-1111-111111111111","gameType":"CallOfDuty4x",
             "sequenceId":3,"kind":4,"mapName":"mp_crash",
             "players":[{"playerGuid":"abc","username":"player","slotId":2,"isBot":true}],
             "playtime":{"observationId":"22222222-2222-2222-2222-222222222222",
             "eventUtc":"2026-01-01T00:00:00Z","timestampQuality":3,"rosterCompleteness":2,
             "producerEpoch":"33333333-3333-3333-3333-333333333333"},
             "playtimeCoverage":{"producerStartedUtc":"2026-01-01T00:00:00Z",
             "droppedCapacity":2,"droppedAttempts":1,"droppedAge":3}}
            """;

        var result = JsonSerializer.Deserialize<PlaytimeObservationEvent>(json, JsonOptions.Default);

        Assert.NotNull(result?.Playtime);
        Assert.Equal(PlaytimeRosterCompleteness.Partial, result.Playtime.RosterCompleteness);
        var player = Assert.Single(result.Players!);
        Assert.Equal("abc", player.PlayerGuid);
        Assert.True(player.IsBot);
        Assert.NotNull(result.PlaytimeCoverage);
        Assert.Equal(2, result.PlaytimeCoverage.DroppedCapacity);
        Assert.Equal(DateTimeKind.Utc, result.PlaytimeCoverage.ProducerStartedUtc.Kind);
    }

    [Fact]
    public void AgentSerialization_UsesCamelCaseAndNumericEnums()
    {
        var evt = new PlaytimeObservationEvent
        {
            EventGeneratedUtc = DateTime.UnixEpoch,
            EventPublishedUtc = DateTime.UnixEpoch,
            ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            GameType = "CallOfDuty4",
            SequenceId = 1,
            MapName = "mp_crash",
            Kind = PlaytimeObservationKind.MapChanged,
            Playtime = new PlaytimeEventMetadata
            {
                ObservationId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                EventUtc = DateTime.UnixEpoch,
                TimestampQuality = PlaytimeTimestampQuality.ParseTime,
                RosterCompleteness = PlaytimeRosterCompleteness.NotApplicable
            }
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(evt, JsonOptions.Default));
        var playtime = json.RootElement.GetProperty("playtime");

        Assert.Equal("22222222-2222-2222-2222-222222222222", playtime.GetProperty("observationId").GetString());
        Assert.Equal(4, playtime.GetProperty("timestampQuality").GetInt32());
        Assert.Equal(0, playtime.GetProperty("rosterCompleteness").GetInt32());
    }
}
