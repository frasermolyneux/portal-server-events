using System.Net;

using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.SiegeMode;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.VpnProtection;

public sealed class SiegeModeServiceTests
{
    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ConnectionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ActionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid PlayerId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly Mock<IRepositoryApiClient> repositoryApiClient = new();
    private readonly Mock<IVersionedSiegeModeApi> versionedSiegeModeApi = new();
    private readonly Mock<ISiegeModeApi> siegeModeApi = new();
    private readonly Mock<IVersionedPlayersApi> versionedPlayersApi = new();
    private readonly Mock<IPlayersApi> playersApi = new();
    private readonly Mock<IVpnProtectionRconEnforcer> rconEnforcer = new();
    private readonly Mock<IAuditLogger> auditLogger = new();
    private readonly Mock<ILogger<SiegeModeService>> logger = new();

    public SiegeModeServiceTests()
    {
        versionedSiegeModeApi.Setup(x => x.V1).Returns(siegeModeApi.Object);
        repositoryApiClient.Setup(x => x.SiegeMode).Returns(versionedSiegeModeApi.Object);
        versionedPlayersApi.Setup(x => x.V1).Returns(playersApi.Object);
        repositoryApiClient.Setup(x => x.Players).Returns(versionedPlayersApi.Object);
    }

    [Fact]
    public async Task DryRun_NeverDispatchesEvenIfClaimRequestsDispatch()
    {
        SetupSession(DryRunRule, SiegeSessionMode.DryRun);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, BaselineRevision = 7 });
        SetupClaim(SiegeSessionMode.DryRun, SiegeActionOutcomes.WouldKick, shouldDispatch: true);

        var result = await CreateSut().EvaluateAsync(CreateContext() with { IsPluginPath = false });

        Assert.True(result.Matched);
        Assert.False(result.ShouldDispatch);
        Assert.Equal(SiegeActionOutcomes.WouldKick, result.Action?.Outcome);
        rconEnforcer.Verify(
            x => x.EnforceAsync(
                It.IsAny<VpnProtectionContext>(),
                It.IsAny<VpnProtectionAction>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OrRule_AllowsConfirmedNewGuidWhenVpnSignalIsUnknown()
    {
        SetupSession(NestedOrRule);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, NewIp = null, BaselineRevision = 7 });
        SetupClaim(SiegeSessionMode.Enforce, SiegeActionOutcomes.Pending, shouldDispatch: true);

        var result = await CreateSut().EvaluateAsync(CreateContext());

        Assert.True(result.Matched);
        Assert.True(result.ShouldDispatch);
        siegeModeApi.Verify(x => x.ClaimAction(
            ServerId,
            SessionId,
            It.Is<ClaimSiegeActionDto>(claim =>
                claim.ConnectionId == ConnectionId &&
                claim.PlayerGuid == "player-guid" &&
                claim.SlotId == 4 &&
                claim.RuleId == "or-rule" &&
                claim.BaselineRevision == 7),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LegacyNumericSnapshot_PreservesNestedRuleMeaning()
    {
        SetupSession(/*lang=json,strict*/ """
            {"schemaVersion":1,"durationMinutes":60,"rules":[{"id":"legacy","enabled":true,"condition":
              {"groupOperator":2,"conditions":[
                {"playerSignal":1,"operator":1,"expectedValue":"true"},
                {"playerSignal":2,"operator":1,"expectedValue":"true"}
              ]}}]}
            """);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, NewIp = null, BaselineRevision = 7 });
        SetupClaim(SiegeSessionMode.Enforce, SiegeActionOutcomes.Pending, shouldDispatch: true);

        var result = await CreateSut().EvaluateAsync(CreateContext());

        Assert.True(result.Matched);
        Assert.True(result.ShouldDispatch);
        siegeModeApi.Verify(x => x.ClaimAction(
            ServerId, SessionId, It.Is<ClaimSiegeActionDto>(claim => claim.RuleId == "legacy"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AndRule_WithUnknownVpnSignalDoesNotClaimAction()
    {
        SetupSession(NestedAndRule);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, NewIp = null, BaselineRevision = 7 });

        var result = await CreateSut().EvaluateAsync(CreateContext());

        Assert.False(result.Matched);
        siegeModeApi.Verify(x => x.ClaimAction(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<ClaimSiegeActionDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
        siegeModeApi.Verify(x => x.RecordCheck(
            ServerId,
            SessionId,
            It.Is<RecordSiegeCheckDto>(check =>
                check.Outcome == "RuleUnknown" &&
                check.Detail == "One or more Siege conditions are unknown."),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExistingClaim_IsNotRedispatched()
    {
        SetupSession(DryRunRule);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, BaselineRevision = 7 });
        SetupClaim(SiegeSessionMode.Enforce, SiegeActionOutcomes.Pending, shouldDispatch: false);

        var result = await CreateSut().EvaluateAsync(CreateContext());

        Assert.True(result.Matched);
        Assert.False(result.ShouldDispatch);
        rconEnforcer.Verify(x => x.EnforceAsync(
            It.IsAny<VpnProtectionContext>(),
            It.IsAny<VpnProtectionAction>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StaleBaselineClaimConflict_DoesNotDispatch()
    {
        SetupSession(DryRunRule);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, BaselineRevision = 7 });
        siegeModeApi.Setup(x => x.ClaimAction(
                ServerId, SessionId, It.IsAny<ClaimSiegeActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<SiegeActionClaimDto>(HttpStatusCode.Conflict));

        var result = await CreateSut().EvaluateAsync(CreateContext());

        Assert.False(result.ShouldDispatch);
        rconEnforcer.Verify(x => x.EnforceAsync(
            It.IsAny<VpnProtectionContext>(),
            It.IsAny<VpnProtectionAction>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        siegeModeApi.Verify(x => x.RecordCheck(
            ServerId, SessionId,
            It.Is<RecordSiegeCheckDto>(check => check.Outcome == "ClaimConflict"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EventPath_WithUnknownIpFreshness_DoesNotClaimOrEnforce()
    {
        SetupSession(DryRunRule);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, BaselineRevision = 7 });

        var result = await CreateSut().EvaluateAsync(CreateContext() with { IsPluginPath = false, IpAddress = "8.8.8.8" });

        Assert.True(result.Matched);
        Assert.False(result.ShouldDispatch);
        siegeModeApi.Verify(x => x.ClaimAction(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ClaimSiegeActionDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
        rconEnforcer.Verify(x => x.EnforceAsync(
            It.IsAny<VpnProtectionContext>(), It.IsAny<VpnProtectionAction>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        siegeModeApi.Verify(x => x.RecordCheck(
            ServerId, SessionId,
            It.Is<RecordSiegeCheckDto>(check => check.Outcome == "FreshnessUnknown"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(VpnProtectionRconOutcome.Succeeded, SiegeActionOutcomes.Applied, true)]
    [InlineData(VpnProtectionRconOutcome.PlayerNotConnected, SiegeActionOutcomes.Stale, false)]
    [InlineData(VpnProtectionRconOutcome.Failed, SiegeActionOutcomes.Failed, false)]
    public async Task EventPath_WithoutIp_ClaimsAndRecordsRconOutcome(
        VpnProtectionRconOutcome rconOutcome, string expectedOutcome, bool wasEnforced)
    {
        SetupSession(DryRunRule);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, BaselineRevision = 7 });
        SetupClaim(SiegeSessionMode.Enforce, SiegeActionOutcomes.Pending, shouldDispatch: true);
        rconEnforcer.Setup(x => x.EnforceAsync(
                It.IsAny<VpnProtectionContext>(), VpnProtectionAction.Kick, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rconOutcome);

        var result = await CreateSut().EvaluateAsync(CreateContext() with { IsPluginPath = false });

        Assert.True(result.Matched);
        Assert.False(result.ShouldDispatch);
        Assert.Equal(wasEnforced, result.WasEnforced);
        rconEnforcer.Verify(x => x.EnforceAsync(
            It.Is<VpnProtectionContext>(c => c.ServerId == ServerId && c.PlayerGuid == "player-guid" && c.SlotId == 4),
            VpnProtectionAction.Kick, It.Is<string>(reason => reason.Contains("SIEGE:new-guid")), It.IsAny<CancellationToken>()), Times.Once);
        siegeModeApi.Verify(x => x.UpdateActionOutcome(
            ServerId, SessionId, ActionId,
            It.Is<UpdateSiegeActionOutcomeDto>(update => update.ConnectionId == ConnectionId && update.Outcome == expectedOutcome),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(PlayerIpObservationStatuses.NotCurrent)]
    [InlineData(PlayerIpObservationStatuses.Unknown)]
    public async Task EventPath_NonCurrentObservation_DoesNotClaim(string status)
    {
        SetupSession(DryRunRule);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, BaselineRevision = 7 });
        playersApi.Setup(x => x.RecordPlayerIpObservationWithStatus(PlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FreshnessResult(status));

        await CreateSut().EvaluateAsync(CreateIpContext());

        siegeModeApi.Verify(x => x.ClaimAction(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ClaimSiegeActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
        rconEnforcer.Verify(x => x.EnforceAsync(It.IsAny<VpnProtectionContext>(), It.IsAny<VpnProtectionAction>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EventPath_CurrentObservation_RechecksSameIdentityBeforeKick()
    {
        SetupSession(DryRunRule);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, BaselineRevision = 7 });
        SetupClaim(SiegeSessionMode.Enforce, SiegeActionOutcomes.Pending, shouldDispatch: true);
        playersApi.Setup(x => x.RecordPlayerIpObservationWithStatus(PlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FreshnessResult(PlayerIpObservationStatuses.Current));
        rconEnforcer.Setup(x => x.EnforceAsync(It.IsAny<VpnProtectionContext>(), VpnProtectionAction.Kick, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(VpnProtectionRconOutcome.Succeeded);
        var context = CreateIpContext();

        var result = await CreateSut().EvaluateAsync(context);

        Assert.True(result.WasEnforced);
        playersApi.Verify(x => x.RecordPlayerIpObservationWithStatus(PlayerId,
            It.Is<RecordPlayerIpObservationDto>(dto => dto.MessageId == "message-1" && dto.IpAddress == context.IpAddress &&
                dto.ObservedAtUtc == context.IpObservation!.ObservedAtUtc), It.IsAny<CancellationToken>()), Times.Exactly(2));
        rconEnforcer.Verify(x => x.EnforceAsync(It.IsAny<VpnProtectionContext>(), VpnProtectionAction.Kick, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(PlayerIpObservationStatuses.NotCurrent, SiegeActionOutcomes.Stale)]
    [InlineData(PlayerIpObservationStatuses.Unknown, SiegeActionOutcomes.Failed)]
    public async Task EventPath_ObservationChangesAfterClaim_DoesNotKick(string status, string outcome)
    {
        SetupSession(DryRunRule);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, BaselineRevision = 7 });
        SetupClaim(SiegeSessionMode.Enforce, SiegeActionOutcomes.Pending, shouldDispatch: true);
        playersApi.SetupSequence(x => x.RecordPlayerIpObservationWithStatus(PlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FreshnessResult(PlayerIpObservationStatuses.Current))
            .ReturnsAsync(FreshnessResult(status));

        var result = await CreateSut().EvaluateAsync(CreateIpContext());

        Assert.False(result.WasEnforced);
        rconEnforcer.Verify(x => x.EnforceAsync(It.IsAny<VpnProtectionContext>(), It.IsAny<VpnProtectionAction>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        siegeModeApi.Verify(x => x.UpdateActionOutcome(ServerId, SessionId, ActionId,
            It.Is<UpdateSiegeActionOutcomeDto>(update => update.Outcome == outcome), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EventPath_FreshnessApiFailure_FailsOpen()
    {
        SetupSession(DryRunRule);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, BaselineRevision = 7 });
        playersApi.Setup(x => x.RecordPlayerIpObservationWithStatus(PlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unavailable"));

        await CreateSut().EvaluateAsync(CreateIpContext());

        siegeModeApi.Verify(x => x.ClaimAction(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ClaimSiegeActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task EventPath_FreshnessHttpError_FailsOpen(HttpStatusCode status)
    {
        SetupSession(DryRunRule);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, BaselineRevision = 7 });
        playersApi.Setup(x => x.RecordPlayerIpObservationWithStatus(PlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<PlayerIpObservationResultDto>(status));

        await CreateSut().EvaluateAsync(CreateIpContext());

        siegeModeApi.Verify(x => x.ClaimAction(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ClaimSiegeActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EventPath_ExistingClaim_DoesNotKickOnReplay()
    {
        SetupSession(DryRunRule);
        SetupBaseline(new SiegeBaselineDto { NewGuid = true, BaselineRevision = 7 });
        SetupClaim(SiegeSessionMode.Enforce, SiegeActionOutcomes.Pending, shouldDispatch: false);
        playersApi.Setup(x => x.RecordPlayerIpObservationWithStatus(PlayerId, It.IsAny<RecordPlayerIpObservationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FreshnessResult(PlayerIpObservationStatuses.Current));

        await CreateSut().EvaluateAsync(CreateIpContext());

        rconEnforcer.Verify(x => x.EnforceAsync(It.IsAny<VpnProtectionContext>(), It.IsAny<VpnProtectionAction>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ApiResult<PlayerIpObservationResultDto> FreshnessResult(string status) =>
        new(HttpStatusCode.OK, new ApiResponse<PlayerIpObservationResultDto>(new PlayerIpObservationResultDto { Status = status }));

    private static SiegeModeEvaluationContext CreateIpContext()
    {
        var context = CreateContext();
        return context with
        {
            IsPluginPath = false,
            IpAddress = "8.8.8.8",
            ObservationPlayerId = PlayerId,
            IpObservation = new RecordPlayerIpObservationDto
            {
                MessageId = "message-1",
                IpAddress = "8.8.8.8",
                ObservedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(context.EventGeneratedUtc, DateTimeKind.Utc))
            }
        };
    }

    private SiegeModeService CreateSut() => new(
        repositoryApiClient.Object,
        rconEnforcer.Object,
        auditLogger.Object,
        logger.Object);

    private void SetupSession(string settingsJson, SiegeSessionMode mode = SiegeSessionMode.Enforce)
    {
        var session = new SiegeSessionDto
        {
            SessionId = SessionId,
            GameServerId = ServerId,
            GameType = (int)GameType.CallOfDuty4x,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
            Mode = mode,
            SettingsJson = settingsJson
        };
        siegeModeApi.Setup(x => x.GetCurrentSession(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<SiegeSessionDto>(
                HttpStatusCode.OK,
                new ApiResponse<SiegeSessionDto>(session)));
    }

    private void SetupBaseline(SiegeBaselineDto baseline)
    {
        siegeModeApi.Setup(x => x.GetBaseline(
                ServerId,
                SessionId,
                It.IsAny<SiegeBaselineRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<SiegeBaselineDto>(
                HttpStatusCode.OK,
                new ApiResponse<SiegeBaselineDto>(baseline)));
    }

    private void SetupClaim(SiegeSessionMode mode, string outcome, bool shouldDispatch)
    {
        var action = new SiegeActionDto
        {
            ActionId = ActionId,
            SessionId = SessionId,
            GameServerId = ServerId,
            GameType = (int)GameType.CallOfDuty4x,
            Mode = mode,
            ConnectionId = ConnectionId,
            PlayerGuid = "player-guid",
            SlotId = 4,
            RuleId = "new-guid",
            ClaimedAtUtc = DateTime.UtcNow,
            Outcome = outcome
        };
        siegeModeApi.Setup(x => x.ClaimAction(
                ServerId,
                SessionId,
                It.IsAny<ClaimSiegeActionDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<SiegeActionClaimDto>(
                HttpStatusCode.OK,
                new ApiResponse<SiegeActionClaimDto>(new SiegeActionClaimDto
                {
                    Action = action,
                    ShouldDispatch = shouldDispatch
                })));
    }

    private static SiegeModeEvaluationContext CreateContext() => new()
    {
        ServerId = ServerId,
        GameType = GameType.CallOfDuty4x,
        PlayerGuid = "player-guid",
        Username = "Player",
        SlotId = 4,
        ConnectionId = ConnectionId,
        IpAddress = null,
        EventGeneratedUtc = DateTime.UtcNow,
        PlayerTags = [],
        Intelligence = null,
        IsPluginPath = true,
        SuppressEnforcement = false
    };

    private const string DryRunRule = /*lang=json,strict*/ """
        {"schemaVersion":1,"durationMinutes":60,"rules":[{"id":"new-guid","enabled":true,"condition":{"playerSignal":"NewGuid","operator":"Equal","expectedValue":"true"}}]}
        """;

    private const string NestedOrRule = /*lang=json,strict*/ """
        {"schemaVersion":1,"durationMinutes":60,"rules":[{"id":"or-rule","enabled":true,"condition":{"groupOperator":"Or","conditions":[{"playerSignal":"NewGuid","operator":"Equal","expectedValue":"true"},{"signal":"ProxyCheckIsVpn","operator":"Equal","expectedValue":"true"}]}}]}
        """;

    private const string NestedAndRule = /*lang=json,strict*/ """
        {"schemaVersion":1,"durationMinutes":60,"rules":[{"id":"and-rule","enabled":true,"condition":{"groupOperator":"And","conditions":[{"playerSignal":"NewGuid","operator":"Equal","expectedValue":"true"},{"signal":"ProxyCheckIsVpn","operator":"Equal","expectedValue":"true"}]}}]}
        """;
}
