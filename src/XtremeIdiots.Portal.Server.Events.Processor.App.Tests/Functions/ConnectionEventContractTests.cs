using System.Text.Json;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class ConnectionEventContractTests
{
    [Fact]
    public void PlayerEvents_ReadUniversalRuntimeIdentityWithoutLosingGenerationPrecision()
    {
        const string common = """
            {"eventGeneratedUtc":"2026-11-24T00:00:00Z","eventPublishedUtc":"2026-11-24T00:00:01Z",
             "serverId":"11111111-1111-1111-1111-111111111111","gameType":"CallOfDuty4x",
             "sequenceId":1,"playerGuid":"abc","ipAddress":"8.8.8.8","slotId":2,
             "connectionId":"22222222-2222-2222-2222-222222222222",
             "observationId":"33333333-3333-3333-3333-333333333333",
             "connectionGeneration":18446744073709551615,"rulesEvaluatedByPlugin":true,
             "runtime":{"id":"cod4x-plugin","version":"2.0.0","protocolVersion":1,
               "capabilities":["cod4x.player.drop","cod4x.chat.private"]}
            """;

        var connected = JsonSerializer.Deserialize<PlayerConnectedEvent>(common + ",\"username\":\"player\"}", JsonOptions.Default)!;
        var resolved = JsonSerializer.Deserialize<PlayerIpResolvedEvent>(common + "}", JsonOptions.Default)!;

        Assert.Equal(ulong.MaxValue, connected.ConnectionGeneration);
        Assert.Equal(connected.ConnectionGeneration, resolved.ConnectionGeneration);
        Assert.Equal(connected.ObservationId, resolved.ObservationId);
        Assert.True(connected.RulesEvaluatedByPlugin);
        Assert.True(resolved.RulesEvaluatedByPlugin);
        Assert.Equal("cod4x-plugin", connected.Runtime!.Id);
        Assert.Equal(["cod4x.player.drop", "cod4x.chat.private"], resolved.Runtime!.Capabilities);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlayerEvents_ReadConnectionIdentityAndLegacyPayloads(bool includeIdentity)
    {
        var connectionId = includeIdentity
            ? ",\"connectionId\":\"22222222-2222-2222-2222-222222222222\",\"protectionEvaluatedByPlugin\":true"
            : "";
        var common = """
            {"eventGeneratedUtc":"2026-01-01T00:00:00Z","eventPublishedUtc":"2026-01-01T00:00:01Z",
             "serverId":"11111111-1111-1111-1111-111111111111","gameType":"CallOfDuty4",
             "sequenceId":1,"playerGuid":"abc"
            """;
        var expected = includeIdentity ? Guid.Parse("22222222-2222-2222-2222-222222222222") : (Guid?)null;

        var connected = JsonSerializer.Deserialize<PlayerConnectedEvent>(
            common + ",\"username\":\"player\",\"ipAddress\":\"\",\"slotId\":2" + connectionId + "}", JsonOptions.Default);
        var disconnected = JsonSerializer.Deserialize<PlayerDisconnectedEvent>(
            common + ",\"username\":\"player\",\"slotId\":2" + connectionId + "}", JsonOptions.Default);
        var resolved = JsonSerializer.Deserialize<PlayerIpResolvedEvent>(
            common + ",\"ipAddress\":\"1.2.3.4\"" + connectionId +
            (includeIdentity ? ",\"slotId\":2" : "") + "}", JsonOptions.Default);

        Assert.Equal(expected, connected!.ConnectionId);
        Assert.Equal(expected, disconnected!.ConnectionId);
        Assert.Equal(expected, resolved!.ConnectionId);
        Assert.Equal(includeIdentity ? 2 : null, resolved.SlotId);
        Assert.Equal(includeIdentity ? true : null, connected.ProtectionEvaluatedByPlugin);
        Assert.Equal(includeIdentity ? true : null, resolved.ProtectionEvaluatedByPlugin);
    }
}
