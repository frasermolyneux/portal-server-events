using MX.Observability.ApplicationInsights.Auditing;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using MX.Api.Abstractions;
using MX.GeoLocation.Abstractions.Models.V1_1;
using MX.GeoLocation.Api.Client.V1;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class PlayerIpResolvedProcessorTests
{
    private readonly Mock<ILogger<PlayerIpResolvedProcessor>> _logger = new();
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IVersionedPlayersApi> _versionedPlayers = new();
    private readonly Mock<IPlayersApi> _playersApi = new();
    private readonly Mock<IGeoLocationApiClient> _geoLocationApiClient = new();
    private readonly Mock<IVersionedGeoLookupApi> _versionedGeoLookupApi = new();
    private readonly Mock<MX.GeoLocation.Abstractions.Interfaces.V1_1.IGeoLookupApi> _geoLookupApi = new();
    private readonly Mock<IRulesTriggerService> _rulesTriggerService = new();
    private readonly PreparedRulesTrigger _prepared = new(null!, null!);
    private readonly Mock<IVpnDetectedTagService> _vpnDetectedTagService = new();
    private readonly IMemoryCache _cache;
    private readonly Mock<IAuditLogger> _auditLogger = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly PlayerIpResolvedProcessor _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public PlayerIpResolvedProcessorTests()
    {
        _versionedPlayers.Setup(x => x.V1).Returns(_playersApi.Object);
        _repoClient.Setup(x => x.Players).Returns(_versionedPlayers.Object);
        _playersApi
            .Setup(x => x.RecordPlayerIpObservation(
                It.IsAny<Guid>(),
                It.IsAny<RecordPlayerIpObservationDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        _versionedGeoLookupApi.Setup(x => x.V1_1).Returns(_geoLookupApi.Object);
        _geoLocationApiClient.Setup(x => x.GeoLookup).Returns(_versionedGeoLookupApi.Object);
        _geoLookupApi
            .Setup(x => x.GetIpIntelligence(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(System.Net.HttpStatusCode.NotFound));
        _rulesTriggerService
            .Setup(x => x.PrepareAsync(It.IsAny<PlayerIpResolvedEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_prepared);
        _rulesTriggerService
            .Setup(x => x.ProcessAsync(
                It.IsAny<PreparedRulesTrigger?>(),
                It.IsAny<IpIntelligenceDto?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RulesProcessingSummary());
        _vpnDetectedTagService
            .Setup(x => x.AddIfDetectedAsync(It.IsAny<Guid>(), It.IsAny<IpIntelligenceDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        _sut = new PlayerIpResolvedProcessor(
            _logger.Object,
            _repoClient.Object,
            _geoLocationApiClient.Object,
            _rulesTriggerService.Object,
            _vpnDetectedTagService.Object,
            _cache,
            _auditLogger.Object);
    }

    private static PlayerIpResolvedEvent CreateValidEvent(
        string? gameType = null,
        string? playerGuid = null,
        string? ipAddress = null,
        Guid? serverId = null,
        DateTime? eventGeneratedUtc = null,
        bool protectionEvaluatedByPlugin = false,
        int? slotId = null,
        Guid? connectionId = null) => new()
        {
            EventGeneratedUtc = eventGeneratedUtc ?? DateTime.UtcNow.AddSeconds(-10),
            EventPublishedUtc = DateTime.UtcNow.AddSeconds(-5),
            ServerId = serverId ?? TestServerId,
            GameType = gameType ?? "CallOfDuty4",
            SequenceId = 1,
            PlayerGuid = playerGuid ?? "abc123guid",
            IpAddress = ipAddress ?? "192.168.1.100",
            ProtectionEvaluatedByPlugin = protectionEvaluatedByPlugin,
            SlotId = slotId,
            ConnectionId = connectionId
        };

    [Fact]
    public async Task ValidEvent_PersistsIpAddress()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerIpObservation(
            TestPlayerId,
            It.Is<RecordPlayerIpObservationDto>(dto =>
                dto.MessageId == message.MessageId &&
                dto.IpAddress == "192.168.1.100" &&
                dto.ObservedAtUtc.HasValue &&
                dto.ObservedAtUtc.Value.UtcDateTime == DateTime.SpecifyKind(evt.EventGeneratedUtc, DateTimeKind.Utc)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PlayerNotFound_RetriesInsteadOfLosingIp()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(NotFoundResult<PlayerDto>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ProcessPlayerIpResolved(message, _functionContext.Object));

        _playersApi.Verify(x => x.RecordPlayerIpObservation(
            It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IpArrivesBeforeConnect_RedeliveryPersistsAfterPlayerCreation()
    {
        var evt = CreateValidEvent(protectionEvaluatedByPlugin: true);
        _playersApi.SetupSequence(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(NotFoundResult<PlayerDto>())
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        var message = CreateMessage(evt);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ProcessPlayerIpResolved(message, _functionContext.Object));
        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerIpObservation(
            TestPlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MalformedJson_LogsWarningAndReturns()
    {
        var message = CreateMessage("not valid json {{{");

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Fact]
    public async Task MissingIpAddress_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(ipAddress: "");
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public async Task PlaceholderIpAddress_SkipsPersistence(string ipAddress)
    {
        var evt = CreateValidEvent(ipAddress: ipAddress);
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
        _playersApi.Verify(x => x.RecordPlayerIpObservation(
            It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingPlayerGuid_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(playerGuid: "");
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Fact]
    public async Task InvalidGameType_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(gameType: "NotARealGame");
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Fact]
    public async Task EmptyServerId_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(serverId: Guid.Empty);
        var message = CreateMessage(evt);

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Fact]
    public async Task StaleEvent_PersistsHistoryWithoutEnforcement()
    {
        var evt = CreateValidEvent(eventGeneratedUtc: DateTime.UtcNow.AddMinutes(-45));
        var message = CreateMessage(evt);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerIpObservation(
            TestPlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApiFailure_RetriesInsteadOfLosingIp()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => _sut.ProcessPlayerIpResolved(message, _functionContext.Object));
    }

    [Fact]
    public async Task UnsuccessfulIpUpdate_RetriesInsteadOfClaimingPersistence()
    {
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NotFoundResult());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ProcessPlayerIpResolved(CreateMessage(CreateValidEvent()), _functionContext.Object));
    }

    [Fact]
    public async Task StalePluginEvent_StillPersistsHistoryWithoutEnforcement()
    {
        var evt = CreateValidEvent(eventGeneratedUtc: DateTime.UtcNow.AddHours(-1), protectionEvaluatedByPlugin: true);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerIpResolved(CreateMessage(evt), _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StaleReplayedEvent_StillPersistsHistoryWithoutEnforcement()
    {
        var original = CreateMessage(CreateValidEvent(eventGeneratedUtc: DateTime.UtcNow.AddHours(-1)));
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: original.Body,
            properties: new Dictionary<string, object> { ["DlqReplayCount"] = 1 });
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ValidEvent_WithIntelligence_PreparesIdentityAndProcessesSharedRules()
    {
        var evt = CreateValidEvent(slotId: 0, connectionId: Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var message = CreateMessage(evt);
        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));
        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());
        var intelligence = Newtonsoft.Json.JsonConvert.DeserializeObject<IpIntelligenceDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = true } }))!;
        _geoLookupApi.Setup(x => x.GetIpIntelligence("192.168.1.100", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<IpIntelligenceDto>(intelligence)));
        _rulesTriggerService.Setup(x => x.ProcessAsync(_prepared, intelligence, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                _playersApi.Verify(x => x.RecordPlayerIpObservation(
                    TestPlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Once);
                _vpnDetectedTagService.Verify(x => x.AddIfDetectedAsync(TestPlayerId, intelligence, It.IsAny<CancellationToken>()), Times.Once);
            })
            .ReturnsAsync(new RulesProcessingSummary());

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _rulesTriggerService.Verify(x => x.ProcessAsync(
            _prepared,
            intelligence,
            It.IsAny<CancellationToken>()), Times.Once);
        _vpnDetectedTagService.Verify(x => x.AddIfDetectedAsync(TestPlayerId, intelligence, It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.Verify(x => x.PrepareAsync(
            It.Is<PlayerIpResolvedEvent>(source =>
                source.ServerId == TestServerId &&
                source.PlayerGuid == evt.PlayerGuid &&
                source.SlotId == evt.SlotId &&
                source.ConnectionId == evt.ConnectionId &&
                source.EventGeneratedUtc == evt.EventGeneratedUtc &&
                source.IpAddress == evt.IpAddress),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidEvent_PreparesBeforeObservationAndProcessesWithoutIntelligence()
    {
        var evt = CreateValidEvent(slotId: 0, connectionId: Guid.NewGuid());
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _rulesTriggerService
            .Setup(x => x.PrepareAsync(It.IsAny<PlayerIpResolvedEvent>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                Assert.Empty(_playersApi.Invocations);
                Assert.Empty(_vpnDetectedTagService.Invocations);
            })
            .ReturnsAsync(_prepared);
        _rulesTriggerService
            .Setup(x => x.ProcessAsync(_prepared, null, It.IsAny<CancellationToken>()))
            .Callback(() => _playersApi.Verify(x => x.RecordPlayerIpObservation(
                TestPlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Once))
            .ReturnsAsync(new RulesProcessingSummary());

        await _sut.ProcessPlayerIpResolved(CreateMessage(evt), _functionContext.Object);

        _rulesTriggerService.Verify(x => x.PrepareAsync(It.IsAny<PlayerIpResolvedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.Verify(x => x.ProcessAsync(_prepared, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PluginEvent_PersistsAndEnrichesAndDelegatesProducerOwnership()
    {
        var evt = CreateValidEvent(protectionEvaluatedByPlugin: true);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());
        var intelligence = Newtonsoft.Json.JsonConvert.DeserializeObject<IpIntelligenceDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = true } }))!;
        _geoLookupApi.Setup(x => x.GetIpIntelligence("192.168.1.100", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(
                System.Net.HttpStatusCode.OK, new ApiResponse<IpIntelligenceDto>(intelligence)));

        await _sut.ProcessPlayerIpResolved(CreateMessage(evt), _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Once);
        _vpnDetectedTagService.Verify(x => x.AddIfDetectedAsync(TestPlayerId, intelligence, It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.Verify(x => x.PrepareAsync(It.IsAny<PlayerIpResolvedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.Verify(x => x.ProcessAsync(_prepared, intelligence, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GeoLookupThrows_FailsOpenAfterPersistingIp()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        _playersApi.Setup(x => x.RecordPlayerIpObservation(It.IsAny<Guid>(), It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());
        _geoLookupApi.Setup(x => x.GetIpIntelligence("192.168.1.100", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("GeoLocation unavailable"));

        await _sut.ProcessPlayerIpResolved(message, _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerIpObservation(
            TestPlayerId,
            It.Is<RecordPlayerIpObservationDto>(dto => dto.IpAddress == "192.168.1.100"),
            It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.Verify(x => x.ProcessAsync(_prepared, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RulesFailure_PropagatesWithCancellationToken(bool duringPreparation, bool cancellation)
    {
        var ct = new CancellationToken(canceled: cancellation);
        _functionContext.SetupGet(x => x.CancellationToken).Returns(ct);
        Exception failure = cancellation
            ? new OperationCanceledException(ct)
            : new HttpRequestException("Rules service unavailable");
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));
        if (duringPreparation)
        {
            _rulesTriggerService.Setup(x => x.PrepareAsync(It.IsAny<PlayerIpResolvedEvent>(), ct))
                .ThrowsAsync(failure);
        }
        else
        {
            _rulesTriggerService.Setup(x => x.ProcessAsync(_prepared, null, ct))
                .ThrowsAsync(failure);
        }

        var actual = await Assert.ThrowsAnyAsync<Exception>(
            () => _sut.ProcessPlayerIpResolved(CreateMessage(CreateValidEvent()), _functionContext.Object));

        Assert.Same(failure, actual);
        _playersApi.Verify(x => x.RecordPlayerIpObservation(
            TestPlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()),
            duringPreparation ? Times.Never : Times.Once);
    }

    [Fact]
    public async Task NoPreparedTrigger_StillPersistsAndDelegatesProcessing()
    {
        _rulesTriggerService.Setup(x => x.PrepareAsync(It.IsAny<PlayerIpResolvedEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PreparedRulesTrigger?)null);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(CreatePlayerDto(TestPlayerId)));

        await _sut.ProcessPlayerIpResolved(CreateMessage(CreateValidEvent()), _functionContext.Object);

        _playersApi.Verify(x => x.RecordPlayerIpObservation(
            TestPlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()), Times.Once);
        _rulesTriggerService.Verify(x => x.ProcessAsync(null, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
