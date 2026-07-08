using System.Net;

using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class WelcomeMessageOrchestratorTests
{
    private readonly Mock<IWelcomeMessageSettingsProvider> _settingsProvider = new();
    private readonly Mock<IWelcomeMessageIdempotencyStore> _idempotencyStore = new();
    private readonly Mock<IServersApiClient> _serversApiClient = new();
    private readonly Mock<IVersionedCod2RconApi> _versionedCod2RconApi = new();
    private readonly Mock<ICod2RconApi> _cod2RconApi = new();
    private readonly Mock<IVersionedCod4RconApi> _versionedCod4RconApi = new();
    private readonly Mock<ICod4RconApi> _cod4RconApi = new();
    private readonly Mock<IVersionedCod5RconApi> _versionedCod5RconApi = new();
    private readonly Mock<ICod5RconApi> _cod5RconApi = new();
    private readonly Mock<IVersionedCoD4xRconApi> _versionedCoD4xRconApi = new();
    private readonly Mock<ICoD4xRconApi> _coD4xRconApi = new();
    private readonly Mock<IAuditLogger> _auditLogger = new();
    private readonly WelcomeMessageOrchestrator _sut;

    private static readonly Guid TestServerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public WelcomeMessageOrchestratorTests()
    {
        _versionedCod2RconApi.Setup(x => x.V1).Returns(_cod2RconApi.Object);
        _serversApiClient.Setup(x => x.Cod2Rcon).Returns(_versionedCod2RconApi.Object);
        _versionedCod4RconApi.Setup(x => x.V1).Returns(_cod4RconApi.Object);
        _serversApiClient.Setup(x => x.Cod4Rcon).Returns(_versionedCod4RconApi.Object);
        _versionedCod5RconApi.Setup(x => x.V1).Returns(_cod5RconApi.Object);
        _serversApiClient.Setup(x => x.Cod5Rcon).Returns(_versionedCod5RconApi.Object);
        _versionedCoD4xRconApi.Setup(x => x.V1).Returns(_coD4xRconApi.Object);
        _serversApiClient.Setup(x => x.CoD4xRcon).Returns(_versionedCoD4xRconApi.Object);

        _idempotencyStore
            .Setup(x => x.TryBeginAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _sut = new WelcomeMessageOrchestrator(
            _settingsProvider.Object,
            _idempotencyStore.Object,
            new WelcomeMessageTemplateRenderer(),
            _serversApiClient.Object,
            _auditLogger.Object,
            new Mock<ILogger<WelcomeMessageOrchestrator>>().Object);
    }

    private void SetupSettings(WelcomeMessageVisibility visibility, string messageTemplate)
    {
        var settings = new EffectiveWelcomeMessageSettings
        {
            Enabled = true,
            CountryFallback = "Unknown",
            StaleThresholdSeconds = 3600,
            Rules =
            [
                new EffectiveWelcomeMessageRule
                {
                    Id = "rule-1",
                    Enabled = true,
                    Priority = 1,
                    Visibility = visibility,
                    MessageTemplate = messageTemplate,
                    RequiredTags = [],
                    ConnectionDelaySeconds = 0,
                    OrderIndex = 0
                }
            ]
        };

        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);
    }

    private static PlayerConnectedEvent CreateEvent(string gameType, string playerGuid, int slotId) => new()
    {
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        ServerId = TestServerId,
        GameType = gameType,
        SequenceId = 1,
        PlayerGuid = playerGuid,
        Username = "OriginalName",
        IpAddress = "1.2.3.4",
        SlotId = slotId
    };

    private static ApiResult<RconStatusResponseDto> StatusWith(int num, string guid, string name) =>
        new(HttpStatusCode.OK, new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto
        {
            Players = [new RconStatusPlayerDto { Num = num, Guid = guid, Name = name }]
        }));

    [Fact]
    public async Task ProcessAsync_CoD4PublicRule_UsesCod4StatusAndSay()
    {
        SetupSettings(WelcomeMessageVisibility.Public, "Welcome {name}");
        _cod4RconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(StatusWith(2, "guid-1", "ResolvedName"));
        _cod4RconApi.Setup(x => x.Say(TestServerId, It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        await _sut.ProcessAsync(CreateEvent("CallOfDuty4", "guid-1", 2), GameType.CallOfDuty4, [], "GB");

        _cod4RconApi.Verify(x => x.Status(TestServerId, It.IsAny<CancellationToken>()), Times.Once);
        _cod4RconApi.Verify(x => x.Say(
            TestServerId,
            It.Is<SayRequest>(r => r.Message == "Welcome ResolvedName"),
            It.IsAny<CancellationToken>()), Times.Once);
        _coD4xRconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<CoD4xMessageRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_CoD2PrivateRule_UsesCod2StatusAndTell()
    {
        SetupSettings(WelcomeMessageVisibility.Private, "Hi {name}");
        _cod2RconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(StatusWith(3, "guid-2", "P2"));
        _cod2RconApi.Setup(x => x.Tell(TestServerId, It.IsAny<CoD4xTargetMessageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(HttpStatusCode.OK, new ApiResponse<string>("ok")));

        await _sut.ProcessAsync(CreateEvent("CallOfDuty2", "guid-2", 3), GameType.CallOfDuty2, [], "GB");

        _cod2RconApi.Verify(x => x.Status(TestServerId, It.IsAny<CancellationToken>()), Times.Once);
        _cod2RconApi.Verify(x => x.Tell(
            TestServerId,
            It.Is<CoD4xTargetMessageRequestDto>(r => r.Target == "3" && r.Message == "Hi P2"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_CoD4xPublicRule_UsesCoD4xConSayEquivalentSay()
    {
        SetupSettings(WelcomeMessageVisibility.Public, "Welcome {name}");
        _coD4xRconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players = [new CoD4xStatusPlayerDto { Num = 4, PlayerIdentifier = "guid-4x", Name = "X" }]
                })));
        _coD4xRconApi.Setup(x => x.Say(TestServerId, It.IsAny<CoD4xMessageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(HttpStatusCode.OK, new ApiResponse<string>("ok")));

        await _sut.ProcessAsync(CreateEvent("CallOfDuty4x", "guid-4x", 4), GameType.CallOfDuty4x, [], "GB");

        _coD4xRconApi.Verify(x => x.Say(
            TestServerId,
            It.Is<CoD4xMessageRequestDto>(r => r.Message == "Welcome X"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_UnsupportedGameType_DoesNotResolveSettingsOrSend()
    {
        await _sut.ProcessAsync(CreateEvent("Insurgency", "guid-9", 1), GameType.Insurgency, [], "GB");

        _settingsProvider.Verify(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _cod2RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _cod4RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _cod5RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _coD4xRconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<CoD4xMessageRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_CoD5PublicRule_UsesCod5StatusAndSay()
    {
        SetupSettings(WelcomeMessageVisibility.Public, "Welcome {name}");
        _cod5RconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(StatusWith(5, "guid-5", "P5"));
        _cod5RconApi.Setup(x => x.Say(TestServerId, It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        await _sut.ProcessAsync(CreateEvent("CallOfDuty5", "guid-5", 5), GameType.CallOfDuty5, [], "GB");

        _cod5RconApi.Verify(x => x.Say(
            TestServerId,
            It.Is<SayRequest>(r => r.Message == "Welcome P5"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_PlayerNotConnected_DoesNotSend()
    {
        SetupSettings(WelcomeMessageVisibility.Public, "Welcome {name}");
        _cod4RconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(StatusWith(2, "someone-else", "Other"));

        await _sut.ProcessAsync(CreateEvent("CallOfDuty4", "guid-1", 2), GameType.CallOfDuty4, [], "GB");

        _cod4RconApi.Verify(x => x.Status(TestServerId, It.IsAny<CancellationToken>()), Times.Once);
        _cod4RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_PublicRule_RendersAllTokens()
    {
        SetupSettings(WelcomeMessageVisibility.Public,
            "^1{name}^7 {country} {ipaddress} [{tags}] {guid} {steamid} {playercount}");
        _cod4RconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(StatusWith(2, "guid-1", "ResolvedName"));
        _cod4RconApi.Setup(x => x.Say(TestServerId, It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        await _sut.ProcessAsync(CreateEvent("CallOfDuty4", "guid-1", 2), GameType.CallOfDuty4, ["Veteran", "Donator"], "GB");

        // SteamId is unset on the test event, so {steamid} renders empty (two spaces before player count).
        _cod4RconApi.Verify(x => x.Say(
            TestServerId,
            It.Is<SayRequest>(r => r.Message == "^1ResolvedName^7 GB 1.2.3.4 [Veteran, Donator] guid-1  1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
