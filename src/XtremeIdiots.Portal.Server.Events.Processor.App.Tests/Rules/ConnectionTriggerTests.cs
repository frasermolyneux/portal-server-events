using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class ConnectionTriggerTests
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly Guid ConnectionId = Guid.NewGuid();
    private static readonly DateTime ObservedAtUtc = new(2026, 11, 24, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ConnectAndLateIp_ShareConnectionRegardlessOfTransportMetadata()
    {
        var connected = ConnectionTriggerNormalizer.From(Connected());
        var enriched = ConnectionTriggerNormalizer.From(new PlayerIpResolvedEvent
        {
            ServerId = ServerId,
            GameType = "CallOfDuty4x",
            PlayerGuid = "player",
            ConnectionId = ConnectionId,
            SlotId = 4,
            IpAddress = "8.8.8.8",
            EventGeneratedUtc = ObservedAtUtc.AddSeconds(5),
            EventPublishedUtc = ObservedAtUtc.AddSeconds(10),
            SequenceId = 99
        });

        Assert.Equal(connected.ConnectionId, enriched.ConnectionId);
        Assert.NotEqual(connected.TriggerId, enriched.TriggerId);
        Assert.Equal(ObservedAtUtc.AddSeconds(5), enriched.OccurredAtUtc.UtcDateTime);
    }

    [Fact]
    public void Reconnection_DoesNotReusePreviousConnection()
    {
        var first = ConnectionTriggerNormalizer.From(Connected());
        var second = ConnectionTriggerNormalizer.From(Connected(connectionId: Guid.NewGuid()));

        Assert.NotEqual(first.ConnectionId, second.ConnectionId);
    }

    [Fact]
    public void MissingConnection_DoesNotInventUnsafeCorrelation()
    {
        Assert.Throws<ArgumentException>(() => ConnectionTriggerNormalizer.From(Connected(connectionId: Guid.Empty)));
    }

    [Fact]
    public void PlaceholderIp_RemainsUnavailable()
    {
        Assert.Null(ConnectionTriggerNormalizer.From(Connected(ipAddress: "0.0.0.0")).PlayerIpAddress);
    }

    [Theory]
    [InlineData("::ffff:8.8.8.8", "8.8.8.8")]
    [InlineData("2001:0DB8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    public void IpAddressUsesRepositoryCanonicalRepresentation(string address, string expected)
    {
        Assert.Equal(expected, ConnectionTriggerNormalizer.From(Connected(ipAddress: address)).PlayerIpAddress);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("99999")]
    public void InvalidGame_IsRejected(string gameType)
    {
        Assert.Throws<ArgumentException>(() => ConnectionTriggerNormalizer.From(Connected(gameType: gameType)));
    }

    private static PlayerConnectedEvent Connected(
        Guid? connectionId = null,
        string ipAddress = "8.8.8.8",
        string gameType = "CallOfDuty4x") => new()
        {
            ServerId = ServerId,
            GameType = gameType,
            PlayerGuid = "player",
            Username = "Player",
            ConnectionId = connectionId ?? ConnectionId,
            SlotId = 4,
            IpAddress = ipAddress,
            EventGeneratedUtc = ObservedAtUtc,
            EventPublishedUtc = ObservedAtUtc.AddSeconds(1),
            SequenceId = 1
        };
}
