using System.Text.Json;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class ConnectionEventContractTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlayerEvents_ReadConnectionIdentityAndLegacyPayloads(bool includeIdentity)
    {
        var connectionId = includeIdentity
            ? ",\"connectionId\":\"22222222-2222-2222-2222-222222222222\""
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
    }
}
