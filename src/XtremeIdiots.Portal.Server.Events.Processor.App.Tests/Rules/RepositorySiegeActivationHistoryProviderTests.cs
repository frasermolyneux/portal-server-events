using System.Net;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.SiegeMode;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RepositorySiegeActivationHistoryProviderTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task PreservesTimestampAndAvailabilityInsteadOfInventingMembershipDates(bool available, bool seen)
    {
        var fixture = new Fixture();
        fixture.Facts = fixture.Facts with
        {
            GuidBaselineAvailable = available,
            GuidBaselineFirstSeenAtUtc = seen ? fixture.Trigger.OccurredAtUtc.AddDays(-1) : null
        };

        var result = await fixture.Provider.GetAsync(fixture.Session, fixture.Trigger, default);

        Assert.Equal(available, result.Guid.Available);
        Assert.Equal(fixture.Facts.GuidBaselineFirstSeenAtUtc, result.Guid.FirstSeenAtUtc);
        Assert.False(result.Ip.Available);
        Assert.Null(result.Ip.FirstSeenAtUtc);
        fixture.Api.Verify(item => item.CreateFactSnapshot(fixture.Trigger.GameServerId,
            It.Is<RuleFactSnapshotRequestDto>(request =>
                request.SiegeSessionId == fixture.Session.SessionId &&
                request.ObservationId == fixture.Trigger.ObservationId &&
                request.ConnectionId == fixture.Trigger.ConnectionId &&
                request.ObservedAtUtc == fixture.Trigger.OccurredAtUtc), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HistoryObservedAfterActivationCannotBecomeActivationBaseline()
    {
        var fixture = new Fixture();
        fixture.Facts = fixture.Facts with
        {
            GuidBaselineAvailable = true,
            GuidBaselineFirstSeenAtUtc = fixture.Trigger.OccurredAtUtc
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Provider.GetAsync(fixture.Session, fixture.Trigger, default));
    }

    [Fact]
    public async Task AnotherSessionCannotSupplyTheFrozenBaseline()
    {
        var fixture = new Fixture();
        fixture.Facts = fixture.Facts with { SiegeSessionId = Guid.NewGuid() };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Provider.GetAsync(fixture.Session, fixture.Trigger, default));
    }

    private sealed class Fixture
    {
        public Mock<IRuleExecutionsApi> Api { get; } = new();
        public RuleTriggerEnvelope Trigger { get; } = new()
        {
            GameServerId = Guid.NewGuid(),
            ConnectionId = Guid.NewGuid(),
            ObservationId = Guid.NewGuid(),
            GameType = GameType.CallOfDuty4x,
            PlayerGuid = "player",
            PlayerIpAddress = "8.8.8.8",
            OccurredAtUtc = DateTimeOffset.UtcNow
        };
        public SiegeSessionDto Session { get; }
        public RuleFactSnapshotDto Facts { get; set; }
        public RepositorySiegeActivationHistoryProvider Provider { get; }

        public Fixture()
        {
            Session = new()
            {
                SessionId = Guid.NewGuid(),
                GameServerId = Trigger.GameServerId,
                GameType = (int)Trigger.GameType,
                StartedAtUtc = Trigger.OccurredAtUtc.UtcDateTime.AddHours(-1)
            };
            Facts = new()
            {
                SnapshotId = Guid.NewGuid(),
                GameServerId = Trigger.GameServerId,
                ConnectionId = Trigger.ConnectionId,
                GameType = Trigger.GameType,
                PlayerGuid = Trigger.PlayerGuid,
                PlayerIpAddress = Trigger.PlayerIpAddress,
                SiegeSessionId = Session.SessionId
            };
            var repository = new Mock<IRepositoryApiClient>();
            var api = new Mock<IVersionedRuleExecutionsApi>();
            api.Setup(item => item.V1).Returns(Api.Object);
            repository.Setup(item => item.RuleExecutions).Returns(api.Object);
            Api.Setup(item => item.CreateFactSnapshot(
                    It.IsAny<Guid>(), It.IsAny<RuleFactSnapshotRequestDto>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(new ApiResult<RuleFactSnapshotDto>(
                    HttpStatusCode.OK, new ApiResponse<RuleFactSnapshotDto>(Facts))));
            Provider = new(repository.Object);
        }
    }
}
