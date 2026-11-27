using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class FuCommandTests
{
    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Mock<IChatCommandSettingsProvider> _settingsProvider = new();
    private readonly FuMessageTemplateRenderer _templateRenderer = new();
    private readonly Mock<IServersApiClient> _serversClient = new();
    private readonly Mock<IVersionedCod2RconApi> _versionedCod2Rcon = new();
    private readonly Mock<ICod2RconApi> _cod2RconApi = new();
    private readonly Mock<IVersionedCod4RconApi> _versionedCod4Rcon = new();
    private readonly Mock<ICod4RconApi> _cod4RconApi = new();
    private readonly Mock<IVersionedCod5RconApi> _versionedCod5Rcon = new();
    private readonly Mock<ICod5RconApi> _cod5RconApi = new();
    private readonly Mock<IVersionedCoD4xRconApi> _versionedCoD4xRcon = new();
    private readonly Mock<ICoD4xRconApi> _coD4xRconApi = new();
    private readonly Mock<IRepositoryApiClient> _repositoryClient = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> _versionedGlobalConfigs = new();
    private readonly Mock<IGlobalConfigurationsApi> _globalConfigsApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _versionedServerConfigs = new();
    private readonly Mock<IGameServerConfigurationsApi> _serverConfigsApi = new();
    private readonly Mock<IVersionedRuleExecutionsApi> _versionedRuleExecutions = new();
    private readonly Mock<IRuleExecutionsApi> _ruleExecutions = new();
    private readonly Mock<IRconResponseService> _rconResponseService = new();
    private readonly Mock<ILogger<FuCommand>> _logger = new();

    private readonly FuCommand _sut;

    public FuCommandTests()
    {
        _versionedCod2Rcon.Setup(x => x.V1).Returns(_cod2RconApi.Object);
        _serversClient.Setup(x => x.Cod2Rcon).Returns(_versionedCod2Rcon.Object);

        _versionedCod4Rcon.Setup(x => x.V1).Returns(_cod4RconApi.Object);
        _serversClient.Setup(x => x.Cod4Rcon).Returns(_versionedCod4Rcon.Object);

        _versionedCod5Rcon.Setup(x => x.V1).Returns(_cod5RconApi.Object);
        _serversClient.Setup(x => x.Cod5Rcon).Returns(_versionedCod5Rcon.Object);

        _versionedCoD4xRcon.Setup(x => x.V1).Returns(_coD4xRconApi.Object);
        _serversClient.Setup(x => x.CoD4xRcon).Returns(_versionedCoD4xRcon.Object);

        _versionedGlobalConfigs.Setup(x => x.V1).Returns(_globalConfigsApi.Object);
        _repositoryClient.Setup(x => x.GlobalConfigurations).Returns(_versionedGlobalConfigs.Object);

        _versionedServerConfigs.Setup(x => x.V1).Returns(_serverConfigsApi.Object);
        _repositoryClient.Setup(x => x.GameServerConfigurations).Returns(_versionedServerConfigs.Object);

        _versionedRuleExecutions.Setup(x => x.V1).Returns(_ruleExecutions.Object);
        _repositoryClient.Setup(x => x.RuleExecutions).Returns(_versionedRuleExecutions.Object);
        _ruleExecutions.Setup(x => x.UpdateOccurrenceEffectOutcome(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UpdateRuleOccurrenceEffectOutcomeDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid serverId, Guid occurrenceId, string effectId, UpdateRuleOccurrenceEffectOutcomeDto update, CancellationToken _) =>
                new ApiResult<RuleOccurrenceEffectDto>(HttpStatusCode.OK, new ApiResponse<RuleOccurrenceEffectDto>(new RuleOccurrenceEffectDto
                {
                    GameServerId = serverId,
                    OccurrenceId = occurrenceId,
                    Claim = new ClaimRuleOccurrenceEffectDto { LogicalEffectId = effectId },
                    Outcome = update.Outcome
                })));

        _globalConfigsApi
            .Setup(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto("agent", /*lang=json,strict*/ "{\"agentName\":\"^5[GlobalBot]^7\"}")
            ])));

        _serverConfigsApi
            .Setup(x => x.GetConfigurations(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>(
            [
                CreateConfigurationDto("agent", /*lang=json,strict*/ "{\"agentName\":\"^2[ServerBot]^7\"}")
            ])));

        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(TestServerId, "fu", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEnabledFuSettings(["^1FU^7 {name}"]));

        _rconResponseService
            .Setup(x => x.TryTellAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _rconResponseService
            .Setup(x => x.SayWithOutcomeAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(RconSayOutcome.Accepted);

        _sut = new FuCommand(
            _settingsProvider.Object,
            _templateRenderer,
            _serversClient.Object,
            _repositoryClient.Object,
            _rconResponseService.Object,
            _logger.Object);
    }

    [Fact]
    public async Task ExecuteAsync_AcquiredOccurrenceClaimsOnePublicReplyAndRecordsDispatch()
    {
        var occurrenceId = Guid.NewGuid();
        SetupResolvedTarget();
        SetupClaim((serverId, occurrence, request) =>
            ClaimResult(HttpStatusCode.Created, RuleOccurrenceEffectDisposition.Acquired, true, serverId, occurrence, request));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target") with { OccurrenceId = occurrenceId });

        Assert.True(result.Success);
        _ruleExecutions.Verify(x => x.ClaimOccurrenceEffect(TestServerId, occurrenceId,
            It.Is<ClaimRuleOccurrenceEffectDto>(request =>
                request.FeatureId == "chat-commands" && request.RuleId == "fu" &&
                request.ActionId == "success" && request.ActionTypeId == "message.public" &&
                request.RuleIndex == 0 && request.ActionIndex == 0 &&
                request.RuleSetRevision == "command-unversioned-v1" && request.Mode == RuleExecutionMode.Enforce &&
                request.LogicalEffectId == RuleOccurrenceEffectIdentity.Create(TestServerId, occurrenceId, "chat-commands", "fu", "success")),
            It.IsAny<CancellationToken>()), Times.Once);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(TestServerId, "CallOfDuty4",
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _ruleExecutions.Verify(x => x.UpdateOccurrenceEffectOutcome(TestServerId, occurrenceId, It.IsAny<string>(),
            It.Is<UpdateRuleOccurrenceEffectOutcomeDto>(update => update.Outcome == RuleOccurrenceEffectOutcome.Dispatched),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, RuleOccurrenceEffectDisposition.AlreadyClaimed, false)]
    [InlineData(HttpStatusCode.OK, RuleOccurrenceEffectDisposition.Acquired, true)]
    [InlineData(HttpStatusCode.Created, RuleOccurrenceEffectDisposition.Acquired, false)]
    [InlineData(HttpStatusCode.Conflict, RuleOccurrenceEffectDisposition.Acquired, true)]
    public async Task ExecuteAsync_OnlyCreatedAcquiredAndDispatchableClaimCanSend(
        HttpStatusCode status, RuleOccurrenceEffectDisposition disposition, bool shouldDispatch)
    {
        var occurrenceId = Guid.NewGuid();
        SetupResolvedTarget();
        SetupClaim((serverId, occurrence, request) =>
            ClaimResult(status, disposition, shouldDispatch, serverId, occurrence, request));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target") with { OccurrenceId = occurrenceId });

        Assert.False(result.Success);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _ruleExecutions.Verify(x => x.UpdateOccurrenceEffectOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<UpdateRuleOccurrenceEffectOutcomeDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_MismatchedCreatedClaimDoesNotAuthorizePublicReply()
    {
        SetupResolvedTarget();
        SetupClaim((serverId, _, request) =>
            ClaimResult(HttpStatusCode.Created, RuleOccurrenceEffectDisposition.Acquired, true, serverId, Guid.NewGuid(), request));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target") with { OccurrenceId = Guid.NewGuid() });

        Assert.False(result.Success);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, RuleOccurrenceEffectDisposition.Acquired, true)]
    [InlineData(HttpStatusCode.OK, RuleOccurrenceEffectDisposition.AlreadyClaimed, false)]
    public async Task ExecuteAsync_MismatchedClaimMetadataDoesNotAuthorizeOrConfirmDuplicate(
        HttpStatusCode status, RuleOccurrenceEffectDisposition disposition, bool shouldDispatch)
    {
        SetupResolvedTarget();
        SetupClaim((serverId, occurrence, request) =>
            ClaimResult(status, disposition, shouldDispatch, serverId, occurrence, request with { RuleSetRevision = "other" }));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target") with { OccurrenceId = Guid.NewGuid() });

        Assert.True(result.Handled);
        Assert.False(result.Success);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_MismatchedDuplicateIsReportedAsFailureRatherThanConfirmedDuplicate()
    {
        SetupResolvedTarget();
        SetupClaim((serverId, _, request) =>
            ClaimResult(HttpStatusCode.OK, RuleOccurrenceEffectDisposition.AlreadyClaimed, false, serverId, Guid.NewGuid(), request));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target") with { OccurrenceId = Guid.NewGuid() });

        Assert.True(result.Handled);
        Assert.False(result.Success);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_MissingCreatedClaimBodyDoesNotAuthorizePublicReply()
    {
        SetupResolvedTarget();
        SetupClaim((_, _, _) => new ApiResult<ClaimRuleOccurrenceEffectResultDto>(HttpStatusCode.Created));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target") with { OccurrenceId = Guid.NewGuid() });

        Assert.False(result.Success);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_LostClaimResponseDoesNotSendOrFallbackOnRedelivery()
    {
        var occurrenceId = Guid.NewGuid();
        var claims = 0;
        SetupResolvedTarget();
        SetupClaim((serverId, occurrence, request) =>
        {
            if (++claims == 1)
            {
                throw new HttpRequestException("Claim response was lost");
            }
            return ClaimResult(HttpStatusCode.OK, RuleOccurrenceEffectDisposition.AlreadyClaimed, false, serverId, occurrence, request);
        });
        var context = CreateContext("!fu target") with { OccurrenceId = occurrenceId };

        var first = await _sut.ExecuteAsync(context);
        var duplicate = await _sut.ExecuteAsync(context);

        Assert.False(first.Success);
        Assert.False(duplicate.Handled);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _rconResponseService.Verify(x => x.TryTellAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(RconSayOutcome.NotAttempted)]
    [InlineData(RconSayOutcome.Rejected)]
    [InlineData(RconSayOutcome.InvalidMessage)]
    public async Task ExecuteAsync_DefiniteNonDispatchIsRecordedAsFailed(RconSayOutcome outcome)
    {
        var occurrenceId = Guid.NewGuid();
        SetupResolvedTarget();
        SetupClaim((serverId, occurrence, request) =>
            ClaimResult(HttpStatusCode.Created, RuleOccurrenceEffectDisposition.Acquired, true, serverId, occurrence, request));
        _rconResponseService.Setup(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(outcome);

        var result = await _sut.ExecuteAsync(CreateContext("!fu target") with { OccurrenceId = occurrenceId });

        Assert.False(result.Success);
        _ruleExecutions.Verify(x => x.UpdateOccurrenceEffectOutcome(TestServerId, occurrenceId, It.IsAny<string>(),
            It.Is<UpdateRuleOccurrenceEffectOutcomeDto>(update => update.Outcome == RuleOccurrenceEffectOutcome.Failed),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_FailedOutcomeWriteDoesNotResendAcceptedPublicReply()
    {
        var occurrenceId = Guid.NewGuid();
        var claims = 0;
        SetupResolvedTarget();
        SetupClaim((serverId, occurrence, request) => ++claims == 1
            ? ClaimResult(HttpStatusCode.Created, RuleOccurrenceEffectDisposition.Acquired, true, serverId, occurrence, request)
            : ClaimResult(HttpStatusCode.OK, RuleOccurrenceEffectDisposition.AlreadyClaimed, false, serverId, occurrence, request));
        _ruleExecutions.Setup(x => x.UpdateOccurrenceEffectOutcome(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<UpdateRuleOccurrenceEffectOutcomeDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Outcome response was lost"));
        var context = CreateContext("!fu target") with { OccurrenceId = occurrenceId };

        var first = await _sut.ExecuteAsync(context);
        var duplicate = await _sut.ExecuteAsync(context);

        Assert.True(first.Success);
        Assert.False(duplicate.Handled);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_StaleOccurrenceDoesNotClaimOrSend()
    {
        SetupResolvedTarget();
        var context = CreateContext("!fu target") with
        {
            OccurrenceId = Guid.NewGuid(),
            ResponseDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
        };

        var result = await _sut.ExecuteAsync(context);

        Assert.False(result.Success);
        _ruleExecutions.Verify(x => x.ClaimOccurrenceEffect(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<ClaimRuleOccurrenceEffectDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownPublicDispatchIsRecordedAndDuplicateDoesNotResend()
    {
        var occurrenceId = Guid.NewGuid();
        var claims = 0;
        SetupResolvedTarget();
        SetupClaim((serverId, occurrence, request) => ++claims == 1
            ? ClaimResult(HttpStatusCode.Created, RuleOccurrenceEffectDisposition.Acquired, true, serverId, occurrence, request)
            : ClaimResult(HttpStatusCode.OK, RuleOccurrenceEffectDisposition.AlreadyClaimed, false, serverId, occurrence, request));
        _rconResponseService.Setup(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(RconSayOutcome.Unknown);
        var context = CreateContext("!fu target") with { OccurrenceId = occurrenceId };

        var first = await _sut.ExecuteAsync(context);
        var duplicate = await _sut.ExecuteAsync(context);

        Assert.False(first.Success);
        Assert.False(duplicate.Handled);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _ruleExecutions.Verify(x => x.UpdateOccurrenceEffectOutcome(TestServerId, occurrenceId, It.IsAny<string>(),
            It.Is<UpdateRuleOccurrenceEffectOutcomeDto>(update => update.Outcome == RuleOccurrenceEffectOutcome.OutcomeUnknown),
            It.IsAny<CancellationToken>()), Times.Once);
        _rconResponseService.Verify(x => x.TryTellAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task ExecuteAsync_LegacyOccurrenceDoesNotClaim(string? rawOccurrenceId)
    {
        SetupResolvedTarget();
        var occurrenceId = rawOccurrenceId is null ? (Guid?)null : Guid.Parse(rawOccurrenceId);

        var result = await _sut.ExecuteAsync(CreateContext("!fu target") with { OccurrenceId = occurrenceId });

        Assert.True(result.Success);
        _ruleExecutions.Verify(x => x.ClaimOccurrenceEffect(It.IsAny<Guid>(), It.IsAny<Guid>(),
            It.IsAny<ClaimRuleOccurrenceEffectDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoArgument_ReturnsUsageAndSendsPrivateTell()
    {
        var result = await _sut.ExecuteAsync(CreateContext("!fu"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Usage: !fu <player name>", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
                It.IsAny<string>(),
            "abc123",
            3,
            "Usage: !fu <player name>",
            "Issuer",
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSettingsHaveNoUsableMessages_ReturnsNotHandled()
    {
        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(TestServerId, "fu", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveChatCommandSettings
            {
                CommandName = "fu",
                Enabled = true,
                FreshnessSeconds = 5,
                Settings = JsonSerializer.SerializeToElement(new { messages = Array.Empty<object>() }),
                EnabledSource = SettingsValueSource.ServerCommand,
                FreshnessSource = SettingsValueSource.ServerCommand,
                AuthorizationSource = SettingsValueSource.ServerCommand,
                PayloadSource = SettingsValueSource.ServerCommand
            });

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.False(result.Handled);

        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _rconResponseService.Verify(x => x.TryTellAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMessagesInvalidOrDisabled_ReturnsNotHandled()
    {
        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(TestServerId, "fu", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveChatCommandSettings
            {
                CommandName = "fu",
                Enabled = true,
                FreshnessSeconds = 5,
                Settings = JsonSerializer.SerializeToElement(new
                {
                    messages = new object[]
                    {
                        new { message = "", enabled = true },
                        new { message = "valid-but-disabled", enabled = false },
                        new { message = "invalid-enabled", enabled = "nope" }
                    }
                }),
                EnabledSource = SettingsValueSource.ServerCommand,
                FreshnessSource = SettingsValueSource.ServerCommand,
                AuthorizationSource = SettingsValueSource.ServerCommand,
                PayloadSource = SettingsValueSource.ServerCommand
            });

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.False(result.Handled);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _rconResponseService.Verify(x => x.TryTellAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CachesSuccessfulPrefixButNotFailedLookup()
    {
        _cod4RconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(HttpStatusCode.OK,
                new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto
                {
                    Players = [new RconStatusPlayerDto { Num = 4, Guid = "target-guid", Name = "Target" }]
                })));
        _globalConfigsApi.SetupSequence(x => x.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ConfigurationDto>>(HttpStatusCode.ServiceUnavailable,
                new ApiResponse<CollectionModel<ConfigurationDto>>(new ApiError("ERROR", "unavailable"))))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConfigurationDto>([])));
        using var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        var command = new FuCommand(_settingsProvider.Object, _templateRenderer, _serversClient.Object,
            _repositoryClient.Object, _rconResponseService.Object, _logger.Object, cache);

        await command.ExecuteAsync(CreateContext("!fu target"));
        await command.ExecuteAsync(CreateContext("!fu target"));
        await command.ExecuteAsync(CreateContext("!fu target"));

        _globalConfigsApi.Verify(x => x.GetConfigurations(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _serverConfigsApi.Verify(x => x.GetConfigurations(TestServerId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_WhenSendOutcomeUnknown_DoesNotSendRetryAdvice()
    {
        _cod4RconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(HttpStatusCode.OK,
                new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto
                {
                    Players = [new RconStatusPlayerDto { Num = 4, Guid = "target-guid", Name = "Target" }]
                })));
        _rconResponseService.Setup(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(RconSayOutcome.Unknown);

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.False(result.Success);
        Assert.Contains("may have been sent", result.ResponseMessage);
        _rconResponseService.Verify(x => x.TryTellAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenResolved_SendsPublicSayWithRenderedName()
    {
        _cod4RconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto
            {
                Players =
                [
                    new RconStatusPlayerDto
                    {
                        Num = 4,
                        Guid = "target-guid",
                        Name = "^3Target^7"
                    }
                ]
            })));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("^2[ServerBot]^7 ^1FU^7 ^3Target^7", result.ResponseMessage);

        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(
            TestServerId,
                It.IsAny<string>(),
            "^1FU^7 ^3Target^7",
            "^2[ServerBot]^7 ",
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconResponseService.Verify(x => x.TryTellAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_UsesConfiguredSuccessReplyAndPreservesLiteralPlayerName()
    {
        _cod4RconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto
            {
                Players = [new RconStatusPlayerDto { Num = 4, Guid = "target-guid", Name = "Target$1" }]
            })));
        var settings = new EffectiveChatCommandSettings
        {
            CommandName = "fu",
            Enabled = true,
            FreshnessSeconds = 5,
            Replies = new(StringComparer.OrdinalIgnoreCase) { ["success"] = ["Cheers {NAME}"] }
        };

        var result = await _sut.ExecuteAsync(CreateContext("!fu target") with { EffectiveSettings = settings });

        Assert.True(result.Success);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(TestServerId, "CallOfDuty4",
            "Cheers Target$1", It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _settingsProvider.Verify(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_UsesConfiguredAmbiguousReplyPrivately()
    {
        _cod4RconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto
            {
                Players =
                [
                    new RconStatusPlayerDto { Num = 3, Guid = "g1", Name = "Name1" },
                    new RconStatusPlayerDto { Num = 5, Guid = "g2", Name = "Name2" }
                ]
            })));
        var settings = CreateEnabledFuSettings(["legacy {name}"]) with
        {
            Replies = new(StringComparer.OrdinalIgnoreCase) { ["targetAmbiguous"] = ["Choose: {SUGGESTIONS}"] }
        };

        var result = await _sut.ExecuteAsync(CreateContext("!fu name") with { EffectiveSettings = settings });

        Assert.False(result.Success);
        Assert.Equal("Choose: Name1 (slot 3), Name2 (slot 5)", result.ResponseMessage);
        _rconResponseService.Verify(x => x.TryTellAsync(TestServerId, "CallOfDuty4", "abc123", 3,
            result.ResponseMessage!, "Issuer", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTemplateHasNoToken_LeavesTemplateUnchanged()
    {
        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(TestServerId, "fu", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEnabledFuSettings(["owned"]));

        _cod4RconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto
            {
                Players =
                [
                    new RconStatusPlayerDto
                    {
                        Num = 4,
                        Guid = "target-guid",
                        Name = "Target"
                    }
                ]
            })));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.True(result.Success);
        Assert.Equal("^2[ServerBot]^7 owned", result.ResponseMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WhenResolvedForCallOfDuty2_UsesCod2Status()
    {
        _cod2RconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto
            {
                Players =
                [
                    new RconStatusPlayerDto
                    {
                        Num = 4,
                        Guid = "target-guid",
                        Name = "^3Target^7"
                    }
                ]
            })));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target", "CallOfDuty2"));

        Assert.True(result.Handled);
        Assert.True(result.Success);
        _cod2RconApi.Verify(x => x.Status(TestServerId, It.IsAny<CancellationToken>()), Times.Once);
        _cod4RconApi.Verify(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _cod5RconApi.Verify(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _coD4xRconApi.Verify(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(
            TestServerId,
            "CallOfDuty2",
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenResolvedForCallOfDuty5_UsesCod5Status()
    {
        _cod5RconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto
            {
                Players =
                [
                    new RconStatusPlayerDto
                    {
                        Num = 4,
                        Guid = "target-guid",
                        Name = "^3Target^7"
                    }
                ]
            })));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target", "CallOfDuty5"));

        Assert.True(result.Handled);
        Assert.True(result.Success);
        _cod5RconApi.Verify(x => x.Status(TestServerId, It.IsAny<CancellationToken>()), Times.Once);
        _cod2RconApi.Verify(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _cod4RconApi.Verify(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _coD4xRconApi.Verify(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(
            TestServerId,
            "CallOfDuty5",
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenResolvedForCallOfDuty4x_UsesCod4xStatus()
    {
        _coD4xRconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
            {
                Players =
                [
                    new CoD4xStatusPlayerDto
                    {
                        Num = 4,
                        PlayerIdentifier = "target-guid",
                        Name = "^3Target^7"
                    }
                ]
            })));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target", "CallOfDuty4x"));

        Assert.True(result.Handled);
        Assert.True(result.Success);
        _coD4xRconApi.Verify(x => x.Status(TestServerId, It.IsAny<CancellationToken>()), Times.Once);
        _cod2RconApi.Verify(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _cod4RconApi.Verify(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _cod5RconApi.Verify(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(
            TestServerId,
            "CallOfDuty4x",
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNotFound_SendsPrivateTellOnly()
    {
        _cod4RconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto
            {
                Players = []
            })));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("No player found.", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
                It.IsAny<string>(),
            "abc123",
            3,
            "No player found.",
            "Issuer",
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAmbiguous_SendsPrivateTellWithSuggestionsOnly()
    {
        _cod4RconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(HttpStatusCode.OK, new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto
            {
                Players =
                [
                    new RconStatusPlayerDto { Num = 3, Guid = "g1", Name = "Name1" },
                    new RconStatusPlayerDto { Num = 5, Guid = "g2", Name = "Name2" }
                ]
            })));

        var result = await _sut.ExecuteAsync(CreateContext("!fu name"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("No exact player found. Did you mean: Name1 (slot 3), Name2 (slot 5)", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
                It.IsAny<string>(),
            "abc123",
            3,
            "No exact player found. Did you mean: Name1 (slot 3), Name2 (slot 5)",
            "Issuer",
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenResolvePlayerThrows_SendsPrivateTellOnly()
    {
        _cod4RconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("timeout"));

        var result = await _sut.ExecuteAsync(CreateContext("!fu target"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Unable to resolve player right now. Please try again.", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
                It.IsAny<string>(),
            "abc123",
            3,
            "Unable to resolve player right now. Please try again.",
            "Issuer",
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconResponseService.Verify(x => x.SayWithOutcomeAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void SetupResolvedTarget() =>
        _cod4RconApi.Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(HttpStatusCode.OK,
                new ApiResponse<RconStatusResponseDto>(new RconStatusResponseDto
                {
                    Players = [new RconStatusPlayerDto { Num = 4, Guid = "target-guid", Name = "Target" }]
                })));

    private void SetupClaim(Func<Guid, Guid, ClaimRuleOccurrenceEffectDto, ApiResult<ClaimRuleOccurrenceEffectResultDto>> response) =>
        _ruleExecutions.Setup(x => x.ClaimOccurrenceEffect(It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<ClaimRuleOccurrenceEffectDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid serverId, Guid occurrenceId, ClaimRuleOccurrenceEffectDto request, CancellationToken _) =>
                response(serverId, occurrenceId, request));

    private static ApiResult<ClaimRuleOccurrenceEffectResultDto> ClaimResult(
        HttpStatusCode status, RuleOccurrenceEffectDisposition disposition, bool shouldDispatch,
        Guid serverId, Guid occurrenceId, ClaimRuleOccurrenceEffectDto request) =>
        new(status, new ApiResponse<ClaimRuleOccurrenceEffectResultDto>(new ClaimRuleOccurrenceEffectResultDto
        {
            Disposition = disposition,
            ShouldDispatch = shouldDispatch,
            Effect = new RuleOccurrenceEffectDto
            {
                GameServerId = serverId,
                OccurrenceId = occurrenceId,
                Claim = JsonSerializer.Deserialize<ClaimRuleOccurrenceEffectDto>(JsonSerializer.Serialize(request))!,
                Outcome = RuleOccurrenceEffectOutcome.Claimed
            }
        }));

    private static CommandContext CreateContext(string message, string gameType = "CallOfDuty4") => new()
    {
        ServerId = TestServerId,
        GameType = gameType,
        PlayerGuid = "abc123",
        Username = "Issuer",
        SlotId = 3,
        Message = message,
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        SequenceId = 1,
        PlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    };

    private static ApiResult<CollectionModel<ConfigurationDto>> SuccessResult(CollectionModel<ConfigurationDto> data)
        => new(HttpStatusCode.OK, new ApiResponse<CollectionModel<ConfigurationDto>>(data));

    private static ConfigurationDto CreateConfigurationDto(string ns, string configurationJson)
    {
        var dto = new ConfigurationDto();
        SetConfigProperty(dto, nameof(ConfigurationDto.Namespace), ns);
        SetConfigProperty(dto, nameof(ConfigurationDto.Configuration), configurationJson);
        return dto;
    }

    private static void SetConfigProperty(ConfigurationDto dto, string propertyName, object? value) =>
        typeof(ConfigurationDto).GetProperty(propertyName)!.SetValue(dto, value);

    private static EffectiveChatCommandSettings CreateEnabledFuSettings(IReadOnlyList<string> messages)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            messages = messages.Select(m => new { message = m, enabled = true }).ToArray()
        });

        return new EffectiveChatCommandSettings
        {
            CommandName = "fu",
            Enabled = true,
            FreshnessSeconds = 5,
            Settings = payload,
            EnabledSource = SettingsValueSource.ServerCommand,
            FreshnessSource = SettingsValueSource.ServerCommand,
            AuthorizationSource = SettingsValueSource.ServerCommand,
            PayloadSource = SettingsValueSource.ServerCommand
        };
    }
}
