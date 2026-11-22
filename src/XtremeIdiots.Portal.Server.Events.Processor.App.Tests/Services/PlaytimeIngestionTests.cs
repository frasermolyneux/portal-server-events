using System.Net;
using System.Text.Json;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Playtime;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

using EventRosterCompleteness = XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events.PlaytimeRosterCompleteness;
using EventTimestampQuality = XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events.PlaytimeTimestampQuality;
using EventKind = XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events.PlaytimeObservationKind;
using RepositoryKind = XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Playtime.PlaytimeObservationKind;
using RepositoryRosterCompleteness = XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Playtime.PlaytimeRosterCompleteness;
using RepositoryTimestampQuality = XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Playtime.PlaytimeTimestampQuality;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Services;

public class PlaytimeIngestionTests
{
    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ObservationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PlayerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly Mock<IRepositoryApiClient> repository = new();
    private readonly Mock<IVersionedPlayersApi> versionedPlayers = new();
    private readonly Mock<IPlayersApi> players = new();
    private readonly Mock<IVersionedPlaytimeApi> versionedPlaytime = new();
    private readonly Mock<IPlaytimeApi> playtime = new();
    private readonly Mock<IAuditLogger> auditLogger = new();
    private readonly PlaytimeIngestion sut;

    public PlaytimeIngestionTests()
    {
        repository.Setup(x => x.Players).Returns(versionedPlayers.Object);
        versionedPlayers.Setup(x => x.V1).Returns(players.Object);
        repository.Setup(x => x.Playtime).Returns(versionedPlaytime.Object);
        versionedPlaytime.Setup(x => x.V1).Returns(playtime.Object);
        playtime.Setup(x => x.BatchObservations(It.IsAny<PlaytimeObservationBatchDto>()))
            .ReturnsAsync(SuccessResult(new PlaytimeObservationBatchResultDto
            {
                Items = [new() { ObservationId = ObservationId, Outcome = PlaytimeObservationOutcome.Accepted }]
            }));
        sut = new PlaytimeIngestion(repository.Object, Mock.Of<ILogger<PlaytimeIngestion>>(), auditLogger.Object);
    }

    [Fact]
    public async Task MissingMetadata_IsNotAcknowledged()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProcessAsync(CreateMapEvent(null), CancellationToken.None));

        playtime.Verify(x => x.BatchObservations(It.IsAny<PlaytimeObservationBatchDto>()), Times.Never);
    }

    [Fact]
    public async Task ParseTimeWithoutLiveEvidence_IsNotProjected()
    {
        var metadata = Metadata(freshness: PlaytimeObservationFreshness.Unknown);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ProcessAsync(CreateMapEvent(metadata), CancellationToken.None));

        playtime.Verify(x => x.BatchObservations(It.IsAny<PlaytimeObservationBatchDto>()), Times.Never);
    }

    [Fact]
    public async Task JoinedPlayer_IsCreatedAndResolvedBeforeObservation()
    {
        players.SetupSequence(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "guid", PlayerEntityOptions.None))
            .ReturnsAsync(NotFoundResult<PlayerDto>())
            .ReturnsAsync(SuccessResult(CreatePlayerDto(PlayerId)));
        players.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>())).ReturnsAsync(SuccessResult());

        await sut.ProcessAsync(CreateJoinEvent(), CancellationToken.None);

        playtime.Verify(x => x.BatchObservations(It.Is<PlaytimeObservationBatchDto>(batch =>
            batch.Observations.Count == 1 &&
            batch.Observations[0].ObservationId == ObservationId &&
            batch.Observations[0].CanonicalPlayerId == PlayerId &&
            batch.Observations[0].Kind == RepositoryKind.PlayerJoined)), Times.Once);
        auditLogger.Verify(x => x.LogAudit(It.IsAny<AuditEvent>()), Times.Once);
    }

    [Fact]
    public async Task FailedPlayerCreation_DoesNotAuditOrAcknowledgeObservation()
    {
        players.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "guid", PlayerEntityOptions.None))
            .ReturnsAsync(NotFoundResult<PlayerDto>());
        players.Setup(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>())).ReturnsAsync(new ApiResult(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProcessAsync(CreateJoinEvent(), CancellationToken.None));

        auditLogger.Verify(x => x.LogAudit(It.IsAny<AuditEvent>()), Times.Never);
        playtime.Verify(x => x.BatchObservations(It.IsAny<PlaytimeObservationBatchDto>()), Times.Never);
    }

    [Fact]
    public async Task KnownBot_IsRecordedWithoutCanonicalAttribution()
    {
        await sut.ProcessAsync(CreateJoinEvent(isBot: true), CancellationToken.None);

        players.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
        playtime.Verify(x => x.BatchObservations(It.Is<PlaytimeObservationBatchDto>(batch =>
            batch.Observations[0].IsBot == true && batch.Observations[0].CanonicalPlayerId == null)), Times.Once);
    }

    [Fact]
    public async Task Duplicate_IsAcknowledgedWithOriginalObservationId()
    {
        playtime.Setup(x => x.BatchObservations(It.IsAny<PlaytimeObservationBatchDto>()))
            .ReturnsAsync(SuccessResult(new PlaytimeObservationBatchResultDto
            {
                Items = [new() { ObservationId = ObservationId, Outcome = PlaytimeObservationOutcome.Duplicate }]
            }));

        var evt = CreateMapEvent(Metadata());
        await sut.ProcessAsync(evt, CancellationToken.None);
        await sut.ProcessAsync(evt, CancellationToken.None);

        playtime.Verify(x => x.BatchObservations(It.Is<PlaytimeObservationBatchDto>(
            batch => batch.Observations[0].ObservationId == ObservationId &&
                     batch.Observations[0].EventUtc == evt.Playtime!.EventUtc)), Times.Exactly(2));
    }

    [Theory]
    [InlineData("Event is too late for the accepted correction window.")]
    [InlineData("Replay or identity resolution arrived after the accepted correction window.")]
    public async Task LateRejection_ThrowsSoMessageCanBeRetried(string reason)
    {
        playtime.Setup(x => x.BatchObservations(It.IsAny<PlaytimeObservationBatchDto>()))
            .ReturnsAsync(SuccessResult(new PlaytimeObservationBatchResultDto
            {
                Items = [new() { ObservationId = ObservationId, Outcome = PlaytimeObservationOutcome.Rejected, Reason = reason }]
            }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ProcessAsync(CreateMapEvent(Metadata()), CancellationToken.None));

        Assert.Contains(reason, exception.Message);
    }

    [Fact]
    public async Task IncompleteBatchResponse_ThrowsRatherThanAcknowledging()
    {
        playtime.Setup(x => x.BatchObservations(It.IsAny<PlaytimeObservationBatchDto>()))
            .ReturnsAsync(new ApiResult<PlaytimeObservationBatchResultDto>(HttpStatusCode.OK));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ProcessAsync(CreateMapEvent(Metadata()), CancellationToken.None));
    }

    [Fact]
    public async Task CompleteRoster_PreservesSourceCompletenessAndBotClassification()
    {
        players.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "human-or-bot", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(PlayerId)));
        var status = new PlaytimeObservationEvent
        {
            EventGeneratedUtc = DateTime.UtcNow,
            EventPublishedUtc = DateTime.UtcNow,
            ServerId = ServerId,
            GameType = "CallOfDuty4",
            SequenceId = 1,
            MapName = "mp_crash",
            Kind = EventKind.ServerRoster,
            Players =
            [
                new() { PlayerGuid = "known-bot", Username = "bot", SlotId = 1, IsBot = true },
                new() { PlayerGuid = "human-or-bot", Username = "unknown", SlotId = 2 }
            ],
            Playtime = Metadata(EventRosterCompleteness.Complete)
        };

        await sut.ProcessAsync(status, CancellationToken.None);

        playtime.Verify(x => x.BatchObservations(It.Is<PlaytimeObservationBatchDto>(batch =>
            batch.Observations[0].RosterCompleteness == RepositoryRosterCompleteness.Complete &&
            batch.Observations[0].Roster!.Count == 2 &&
            batch.Observations[0].Roster![0].IsBot == true &&
            batch.Observations[0].Roster![0].CanonicalPlayerId == null &&
            batch.Observations[0].Roster![1].IsBot == null &&
            batch.Observations[0].Roster![1].CanonicalPlayerId == PlayerId)), Times.Once);
    }

    [Fact]
    public async Task PartialRoster_PreservesKnownBotsWithoutAttributingThem()
    {
        players.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "human-or-bot", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(PlayerId)));
        var status = new PlaytimeObservationEvent
        {
            EventGeneratedUtc = DateTime.UtcNow,
            EventPublishedUtc = DateTime.UtcNow,
            ServerId = ServerId,
            GameType = "CallOfDuty4",
            SequenceId = 1,
            Kind = EventKind.ServerRoster,
            Players =
            [
                new() { PlayerGuid = "known-bot", Username = "bot", SlotId = 1, IsBot = true },
                new() { PlayerGuid = "human-or-bot", Username = "unknown", SlotId = 2 }
            ],
            Playtime = Metadata(EventRosterCompleteness.Partial)
        };

        await sut.ProcessAsync(status, CancellationToken.None);

        players.Verify(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "known-bot", PlayerEntityOptions.None), Times.Never);
        playtime.Verify(x => x.BatchObservations(It.Is<PlaytimeObservationBatchDto>(batch =>
            batch.Observations[0].RosterCompleteness == RepositoryRosterCompleteness.Partial &&
            batch.Observations[0].Roster != null &&
            batch.Observations[0].Roster!.Count == 2 &&
            batch.Observations[0].Roster![0].IsBot == true &&
            batch.Observations[0].Roster![0].CanonicalPlayerId == null &&
            batch.Observations[0].Roster![1].GamePlayerId == "human-or-bot")), Times.Once);
    }

    [Fact]
    public async Task FailedRoster_DoesNotResolvePlayersOrAttributeMap()
    {
        var status = new PlaytimeObservationEvent
        {
            EventGeneratedUtc = DateTime.UtcNow,
            EventPublishedUtc = DateTime.UtcNow,
            ServerId = ServerId,
            GameType = "CallOfDuty4",
            SequenceId = 1,
            MapName = "mp_crash",
            Kind = EventKind.ServerRoster,
            Players =
            [
                new() { PlayerGuid = "guid", Username = "player", SlotId = 1 }
            ],
            Playtime = Metadata(EventRosterCompleteness.Failed)
        };

        await sut.ProcessAsync(status, CancellationToken.None);

        players.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
        playtime.Verify(x => x.BatchObservations(It.Is<PlaytimeObservationBatchDto>(batch =>
            batch.Observations[0].RosterCompleteness == RepositoryRosterCompleteness.Failed &&
            batch.Observations[0].MapName == null &&
            batch.Observations[0].Roster!.Count == 0)), Times.Once);
    }

    [Fact]
    public async Task PartialRosterWithoutGamePlayerId_IsRetainedWithoutInvalidApiSubmission()
    {
        var status = new PlaytimeObservationEvent
        {
            EventGeneratedUtc = DateTime.UtcNow,
            EventPublishedUtc = DateTime.UtcNow,
            ServerId = ServerId,
            GameType = "CallOfDuty4",
            SequenceId = 1,
            Kind = EventKind.ServerRoster,
            Players = [new() { PlayerGuid = "", Username = "unknown", SlotId = 3 }],
            Playtime = Metadata(EventRosterCompleteness.Partial)
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProcessAsync(status, CancellationToken.None));
        Assert.Contains("slot 3", error.Message);

        playtime.Verify(x => x.BatchObservations(It.IsAny<PlaytimeObservationBatchDto>()), Times.Never);
    }

    [Theory]
    [InlineData(PlaytimeObservationOutcome.Accepted)]
    [InlineData(PlaytimeObservationOutcome.Duplicate)]
    public async Task PartialRoster_SubmitsResolvedPresenceButRetainsUnresolvedSource(PlaytimeObservationOutcome outcome)
    {
        playtime.Setup(x => x.BatchObservations(It.IsAny<PlaytimeObservationBatchDto>()))
            .ReturnsAsync(SuccessResult(new PlaytimeObservationBatchResultDto
            {
                Items = [new() { ObservationId = ObservationId, Outcome = outcome }]
            }));
        players.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "known", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(PlayerId)));
        players.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "unresolved", PlayerEntityOptions.None))
            .ReturnsAsync(NotFoundResult<PlayerDto>());
        var status = new PlaytimeObservationEvent
        {
            EventGeneratedUtc = DateTime.UtcNow,
            EventPublishedUtc = DateTime.UtcNow,
            ServerId = ServerId,
            GameType = "CallOfDuty4",
            SequenceId = 1,
            Kind = EventKind.ServerRoster,
            Players =
            [
                new() { PlayerGuid = "known", Username = "player", SlotId = 1 },
                new() { PlayerGuid = "unresolved", Username = "", SlotId = 2 }
            ],
            Playtime = Metadata(EventRosterCompleteness.Partial)
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProcessAsync(status, CancellationToken.None));

        Assert.Contains("unresolved", error.Message);
        playtime.Verify(x => x.BatchObservations(It.Is<PlaytimeObservationBatchDto>(batch =>
            batch.Observations[0].Roster!.Count == 2 &&
            batch.Observations[0].Roster![0].CanonicalPlayerId == PlayerId &&
            batch.Observations[0].Roster![1].CanonicalPlayerId == null)), Times.Once);
    }

    [Fact]
    public async Task PartialRoster_ReplayEnrichesIdentityWithoutChangingSource()
    {
        players.SetupSequence(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "unresolved", PlayerEntityOptions.None))
            .ReturnsAsync(NotFoundResult<PlayerDto>())
            .ReturnsAsync(SuccessResult(CreatePlayerDto(PlayerId)));
        var submissions = new List<PlaytimeObservationDto>();
        playtime.Setup(x => x.BatchObservations(It.IsAny<PlaytimeObservationBatchDto>()))
            .Callback<PlaytimeObservationBatchDto, CancellationToken>((batch, _) =>
                submissions.Add(Assert.Single(batch.Observations)))
            .ReturnsAsync(() => SuccessResult(new PlaytimeObservationBatchResultDto
            {
                Items = [new()
                {
                    ObservationId = ObservationId,
                    Outcome = submissions.Count == 1 ? PlaytimeObservationOutcome.Accepted : PlaytimeObservationOutcome.Duplicate
                }]
            }));
        var status = new PlaytimeObservationEvent
        {
            EventGeneratedUtc = DateTime.UtcNow,
            EventPublishedUtc = DateTime.UtcNow,
            ServerId = ServerId,
            GameType = "CallOfDuty4",
            SequenceId = 1,
            Kind = EventKind.ServerRoster,
            Players = [new() { PlayerGuid = "unresolved", Username = "", SlotId = 2 }],
            Playtime = Metadata(EventRosterCompleteness.Partial)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProcessAsync(status, CancellationToken.None));
        await sut.ProcessAsync(status, CancellationToken.None);

        Assert.Equal(2, submissions.Count);
        Assert.Equal(submissions[0] with { Roster = null }, submissions[1] with { Roster = null });
        var original = Assert.Single(submissions[0].Roster!);
        var replay = Assert.Single(submissions[1].Roster!);
        Assert.Null(original.CanonicalPlayerId);
        Assert.Equal(PlayerId, replay.CanonicalPlayerId);
        Assert.Equal(original, replay with { CanonicalPlayerId = null });
        players.Verify(x => x.CreatePlayer(It.IsAny<CreatePlayerDto>()), Times.Never);
    }

    [Fact]
    public async Task Coverage_IsForwardedWithTheProducerEpoch()
    {
        var publishedUtc = DateTime.UtcNow;
        var status = new PlaytimeObservationEvent
        {
            EventGeneratedUtc = publishedUtc,
            EventPublishedUtc = publishedUtc,
            ServerId = ServerId,
            GameType = "CallOfDuty4",
            SequenceId = 1,
            MapName = "mp_crash",
            Kind = EventKind.ServerRoster,
            Players = [],
            Playtime = new PlaytimeEventMetadata
            {
                ObservationId = ObservationId,
                EventUtc = publishedUtc.AddMinutes(-2),
                TimestampQuality = EventTimestampQuality.RosterSnapshotTimestamp,
                RosterCompleteness = EventRosterCompleteness.Partial,
                ProducerEpoch = Guid.NewGuid()
            },
            PlaytimeCoverage = new PlaytimeCoverage
            {
                ProducerStartedUtc = publishedUtc.AddMinutes(-1),
                DroppedCapacity = 1,
                DroppedAttempts = 0,
                DroppedAge = 0
            }
        };

        await sut.ProcessAsync(status, CancellationToken.None);
        await sut.ProcessAsync(status, CancellationToken.None);

        playtime.Verify(x => x.BatchObservations(It.Is<PlaytimeObservationBatchDto>(batch =>
            batch.Observations[0].Coverage != null &&
            batch.Observations[0].Coverage!.ProducerEpoch == status.Playtime.ProducerEpoch &&
            batch.Observations[0].Coverage!.ProducerStartedUtc == status.PlaytimeCoverage.ProducerStartedUtc &&
            batch.Observations[0].Coverage!.ReportedUtc == publishedUtc &&
            batch.Observations[0].Coverage!.DroppedCapacityMax == 1 &&
            batch.Observations[0].Coverage!.DroppedAttemptsMax == 0 &&
            batch.Observations[0].Coverage!.DroppedAgeMax == 0)), Times.Exactly(2));
    }

    [Theory]
    [InlineData("missing-epoch")]
    [InlineData("empty-epoch")]
    [InlineData("capacity")]
    [InlineData("attempts")]
    [InlineData("age")]
    [InlineData("missing-report-time")]
    [InlineData("non-utc-report-time")]
    [InlineData("report-before-start")]
    [InlineData("missing-start-time")]
    [InlineData("non-utc-start-time")]
    [InlineData("non-roster")]
    public async Task InvalidCoverage_IsNotSubmitted(string invalidField)
    {
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var status = new PlaytimeObservationEvent
        {
            EventGeneratedUtc = timestamp,
            EventPublishedUtc = invalidField switch
            {
                "missing-report-time" => default,
                "non-utc-report-time" => DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified),
                "report-before-start" => timestamp.AddMinutes(-1),
                _ => timestamp
            },
            ServerId = ServerId,
            GameType = "CallOfDuty4",
            SequenceId = 1,
            Kind = invalidField == "non-roster" ? EventKind.MapChanged : EventKind.ServerRoster,
            Players = [],
            Playtime = new PlaytimeEventMetadata
            {
                ObservationId = ObservationId,
                EventUtc = timestamp,
                TimestampQuality = EventTimestampQuality.RosterSnapshotTimestamp,
                RosterCompleteness = EventRosterCompleteness.Partial,
                ProducerEpoch = invalidField switch
                {
                    "missing-epoch" => null,
                    "empty-epoch" => Guid.Empty,
                    _ => Guid.NewGuid()
                }
            },
            PlaytimeCoverage = new PlaytimeCoverage
            {
                ProducerStartedUtc = invalidField switch
                {
                    "missing-start-time" => default,
                    "non-utc-start-time" => DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified),
                    _ => timestamp
                },
                DroppedCapacity = invalidField == "capacity" ? -1 : 0,
                DroppedAttempts = invalidField == "attempts" ? -1 : 0,
                DroppedAge = invalidField == "age" ? -1 : 0
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProcessAsync(status, CancellationToken.None));

        playtime.Verify(x => x.BatchObservations(It.IsAny<PlaytimeObservationBatchDto>()), Times.Never);
    }

    [Fact]
    public async Task ProducerRosterJson_MapsToRepositoryPayload()
    {
        players.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "guid", PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(PlayerId)));
        PlaytimeObservationDto? captured = null;
        playtime.Setup(x => x.BatchObservations(It.IsAny<PlaytimeObservationBatchDto>()))
            .Callback<PlaytimeObservationBatchDto, CancellationToken>((batch, _) =>
                captured = Assert.Single(batch.Observations))
            .ReturnsAsync(SuccessResult(new PlaytimeObservationBatchResultDto
            {
                Items = [new() { ObservationId = ObservationId, Outcome = PlaytimeObservationOutcome.Accepted }]
            }));
        var processor = new PlaytimeObservationProcessor(sut, Mock.Of<ILogger<PlaytimeObservationProcessor>>());
        var message = CreateMessage(/*lang=json,strict*/ """
            {"eventGeneratedUtc":"2026-01-01T00:01:00Z","eventPublishedUtc":"2026-01-01T00:01:00Z",
             "serverId":"11111111-1111-1111-1111-111111111111","gameType":"CallOfDuty4","sequenceId":7,
             "kind":4,"mapName":"mp_crash","players":[{"playerGuid":"guid","username":"player","slotId":2,"isBot":false}],
             "playtime":{"observationId":"22222222-2222-2222-2222-222222222222","eventUtc":"2026-01-01T00:01:00Z",
             "timestampQuality":3,"rosterCompleteness":2,"producerEpoch":"44444444-4444-4444-4444-444444444444",
             "producerSequence":7},
             "playtimeCoverage":{"producerStartedUtc":"2026-01-01T00:00:00Z","droppedCapacity":1,"droppedAttempts":2,"droppedAge":3}}
            """);

        await processor.ProcessPlaytimeObservation(message, Mock.Of<FunctionContext>());

        Assert.NotNull(captured);
        Assert.Equal(RepositoryTimestampQuality.RosterSnapshotTimestamp, captured.TimestampQuality);
        Assert.Equal(RepositoryRosterCompleteness.Partial, captured.RosterCompleteness);
        Assert.Equal(PlayerId, Assert.Single(captured.Roster!).CanonicalPlayerId);
        Assert.False(captured.Roster![0].IsBot);
        Assert.Equal(2, captured.Coverage!.DroppedAttemptsMax);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc), captured.Coverage.ReportedUtc);

        var json = JsonSerializer.Serialize(captured, JsonSerializerOptions.Web);
        var roundTrip = JsonSerializer.Deserialize<PlaytimeObservationDto>(json, JsonSerializerOptions.Web);
        Assert.NotNull(roundTrip);
        Assert.Equal(captured.ObservationId, roundTrip.ObservationId);
        Assert.Equal(PlayerId, Assert.Single(roundTrip.Roster!).CanonicalPlayerId);
    }

    [Theory]
    [InlineData(EventTimestampQuality.ParsedLogTimestamp, RepositoryTimestampQuality.ParsedLogTimestamp)]
    [InlineData(EventTimestampQuality.ProducerTimestamp, RepositoryTimestampQuality.ProducerTimestamp)]
    [InlineData(EventTimestampQuality.RosterSnapshotTimestamp, RepositoryTimestampQuality.RosterSnapshotTimestamp)]
    [InlineData(EventTimestampQuality.ParseTime, RepositoryTimestampQuality.ParseTime)]
    public async Task TimestampQuality_IsMappedByMeaning(EventTimestampQuality input, RepositoryTimestampQuality expected)
    {
        var metadata = new PlaytimeEventMetadata
        {
            ObservationId = ObservationId,
            EventUtc = DateTime.UtcNow,
            TimestampQuality = input,
            Freshness = PlaytimeObservationFreshness.Live
        };

        await sut.ProcessAsync(CreateMapEvent(metadata), CancellationToken.None);

        playtime.Verify(x => x.BatchObservations(It.Is<PlaytimeObservationBatchDto>(batch =>
            batch.Observations[0].TimestampQuality == expected)), Times.Once);
    }

    private static PlaytimeEventMetadata Metadata(
        EventRosterCompleteness completeness = EventRosterCompleteness.NotApplicable,
        PlaytimeObservationFreshness freshness = PlaytimeObservationFreshness.Live) => new()
        {
            ObservationId = ObservationId,
            EventUtc = DateTime.UtcNow,
            TimestampQuality = EventTimestampQuality.ParseTime,
            Freshness = freshness,
            RosterCompleteness = completeness
        };

    private static PlaytimeObservationEvent CreateMapEvent(PlaytimeEventMetadata? metadata) => new()
    {
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        ServerId = ServerId,
        GameType = "CallOfDuty4",
        SequenceId = 1,
        MapName = "mp_crash",
        Kind = EventKind.MapChanged,
        Playtime = metadata!
    };

    private static PlaytimeObservationEvent CreateJoinEvent(bool? isBot = null) => new()
    {
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        ServerId = ServerId,
        GameType = "CallOfDuty4",
        SequenceId = 1,
        PlayerGuid = "guid",
        Username = "player",
        SlotId = 1,
        IsBot = isBot,
        Kind = EventKind.PlayerJoined,
        Playtime = Metadata()
    };
}
