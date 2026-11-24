using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

using EventRuntime = XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events.RuleRuntimeDescriptor;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RuleConnectionEvidenceTests
{
    [Fact]
    public async Task StatusPreservesObservedIdentityAndUnknownRowsWithoutInventingTombstones()
    {
        var fixture = new Fixture();
        var source = fixture.Status();

        await RuleConnectionEvidence.ObserveAsync(fixture.Repository.Object, source, NullLogger.Instance, default);

        var snapshot = Assert.IsType<RuleConnectionStatusSnapshotDto>(fixture.Snapshot);
        Assert.Equal(source.SnapshotId, snapshot.SnapshotId);
        Assert.Equal(source.ObservedAtUtc, snapshot.ObservedAtUtc);
        Assert.Equal((long)source.Sequence!.Value, snapshot.SequenceId);
        Assert.Equal(RuleRuntimeIdentityModel.AgentObservedConnection, snapshot.Runtime.IdentityModel);
        Assert.Equal(source.Runtime!.InstanceId, snapshot.Runtime.InstanceId);
        Assert.Equal(fixture.ConnectionId, snapshot.Players[0].ConnectionId);
        Assert.Null(snapshot.Players[0].ConnectionGeneration);
        Assert.Equal(RuleConnectionState.Unknown, snapshot.Players[1].State);
        Assert.Null(snapshot.Players[1].ConnectionId);
        Assert.DoesNotContain(snapshot.Players, player => player.State == RuleConnectionState.Disconnected);
        var facts = Assert.IsType<RuleFactSnapshotRequestDto>(fixture.Facts);
        Assert.Equal(fixture.ConnectionId, facts.ConnectionId);
        Assert.Equal(source.ObservedAtUtc, facts.ObservedAtUtc);
        Assert.Equal(source.SnapshotId, facts.ObservationId);
        Assert.Equal("8.8.8.8", facts.PlayerIpAddress);
        fixture.Executions.Verify(item => item.CreateFactSnapshot(
            It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisconnectForwardsExactObservedConnectionWithoutNativeGeneration()
    {
        var fixture = new Fixture();
        var source = new PlayerDisconnectedEvent
        {
            ServerId = fixture.ServerId,
            GameType = "CallOfDuty4",
            PlayerGuid = "player",
            Username = "Player",
            SlotId = 4,
            ConnectionId = fixture.ConnectionId,
            SnapshotId = Guid.NewGuid(),
            Sequence = 43,
            ObservedAtUtc = fixture.ObservedAtUtc,
            Runtime = fixture.Runtime,
            EventGeneratedUtc = fixture.ObservedAtUtc.UtcDateTime,
            EventPublishedUtc = fixture.ObservedAtUtc.UtcDateTime.AddSeconds(1),
            SequenceId = 43
        };

        await RuleConnectionEvidence.ObserveAsync(fixture.Repository.Object, source, NullLogger.Instance, default);

        var snapshot = Assert.IsType<RuleConnectionStatusSnapshotDto>(fixture.Snapshot);
        var player = Assert.Single(snapshot.Players);
        Assert.Equal(RuleConnectionState.Disconnected, player.State);
        Assert.Equal(source.ConnectionId, player.ConnectionId);
        Assert.Equal(source.PlayerGuid, player.PlayerGuid);
        Assert.Equal(source.SlotId, player.SlotId);
        Assert.Null(player.ConnectionGeneration);
        Assert.Equal(source.ObservedAtUtc, snapshot.ObservedAtUtc);
        Assert.Equal(source.Runtime.InstanceId, snapshot.Runtime.InstanceId);
        fixture.Executions.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task RejectedSnapshotCannotSupplyFreshFacts(HttpStatusCode rejection)
    {
        var fixture = new Fixture();
        fixture.Bindings.Setup(item => item.ObserveSnapshot(
                It.IsAny<Guid>(), It.IsAny<RuleConnectionStatusSnapshotDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RuleConnectionStatusSnapshotResultDto>(rejection));

        await RuleConnectionEvidence.ObserveAsync(fixture.Repository.Object, fixture.Status(), NullLogger.Instance, default);

        fixture.Executions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DefaultObservedTimestampIsNotValidSourceEvidence()
    {
        var fixture = new Fixture();

        await RuleConnectionEvidence.ObserveAsync(
            fixture.Repository.Object, fixture.Status(DateTimeOffset.MinValue), NullLogger.Instance, default);

        fixture.Bindings.VerifyNoOtherCalls();
        fixture.Executions.VerifyNoOtherCalls();
    }

    private sealed class Fixture
    {
        public Guid ServerId { get; } = Guid.NewGuid();
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public DateTimeOffset ObservedAtUtc { get; } = DateTimeOffset.UtcNow.AddSeconds(-1);
        public EventRuntime Runtime { get; } = new()
        {
            Id = "server-agent",
            Version = "1.0",
            InstanceId = Guid.NewGuid(),
            StartedAtUtc = DateTimeOffset.UtcNow.AddHours(-1)
        };
        public Mock<IRepositoryApiClient> Repository { get; } = new();
        public Mock<IRuleConnectionBindingsApi> Bindings { get; } = new();
        public Mock<IRuleExecutionsApi> Executions { get; } = new();
        public RuleConnectionStatusSnapshotDto? Snapshot { get; private set; }
        public RuleFactSnapshotRequestDto? Facts { get; private set; }

        public Fixture()
        {
            var bindings = new Mock<IVersionedRuleConnectionBindingsApi>();
            bindings.Setup(item => item.V1).Returns(Bindings.Object);
            Repository.Setup(item => item.RuleConnectionBindings).Returns(bindings.Object);
            var executions = new Mock<IVersionedRuleExecutionsApi>();
            executions.Setup(item => item.V1).Returns(Executions.Object);
            Repository.Setup(item => item.RuleExecutions).Returns(executions.Object);
            Bindings.Setup(item => item.ObserveSnapshot(
                    It.IsAny<Guid>(), It.IsAny<RuleConnectionStatusSnapshotDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, RuleConnectionStatusSnapshotDto snapshot, CancellationToken _) =>
                {
                    Snapshot = snapshot;
                    return Task.FromResult(new ApiResult<RuleConnectionStatusSnapshotResultDto>(HttpStatusCode.OK,
                        new ApiResponse<RuleConnectionStatusSnapshotResultDto>(new RuleConnectionStatusSnapshotResultDto
                        {
                            SnapshotId = snapshot.SnapshotId,
                            SequenceId = snapshot.SequenceId
                        })));
                });
            Executions.Setup(item => item.CreateFactSnapshot(
                    It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, RuleFactSnapshotRequestDto facts, CancellationToken _) =>
                {
                    Facts = facts;
                    return Task.FromResult(new ApiResult<RuleFactSnapshotDto>(HttpStatusCode.OK,
                        new ApiResponse<RuleFactSnapshotDto>(new RuleFactSnapshotDto { SnapshotId = Guid.NewGuid() })));
                });
        }

        public ServerStatusEvent Status(DateTimeOffset? observedAtUtc = null) => new()
        {
            ServerId = ServerId,
            GameType = "CallOfDuty4",
            MapName = "mp_crash",
            GameName = "war",
            PlayerCount = 2,
            SnapshotId = Guid.NewGuid(),
            Sequence = 42,
            ObservedAtUtc = observedAtUtc ?? ObservedAtUtc,
            Runtime = Runtime,
            EventGeneratedUtc = ObservedAtUtc.UtcDateTime,
            EventPublishedUtc = ObservedAtUtc.UtcDateTime.AddSeconds(1),
            SequenceId = 42,
            Players =
            [
                new()
                {
                    PlayerGuid = "player",
                    Username = "Player",
                    IpAddress = "8.8.8.8",
                    SlotId = 4,
                    ConnectedAtUtc = ObservedAtUtc.UtcDateTime,
                    ConnectionId = ConnectionId,
                    State = "Connected"
                },
                new()
                {
                    PlayerGuid = "unbound",
                    Username = "Unbound",
                    IpAddress = "0.0.0.0",
                    SlotId = 5,
                    ConnectedAtUtc = ObservedAtUtc.UtcDateTime,
                    State = "Unknown"
                }
            ]
        };
    }
}
