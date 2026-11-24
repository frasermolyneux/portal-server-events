using System.Net;

using Azure.Messaging.ServiceBus;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using Newtonsoft.Json;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public sealed class RuleActionContinuationProcessorTests
{
    private readonly Mock<IRepositoryApiClient> repository = new();
    private readonly Mock<IVersionedAdminActionsApi> versioned = new();
    private readonly Mock<IAdminActionsApi> actions = new();
    private readonly Mock<IAdminActionTopics> topics = new();
    private readonly Mock<IAuditLogger> audit = new();
    private readonly Mock<FunctionContext> context = new();
    private readonly Mock<IRuleExecutionsApi> executions = new();
    private readonly Mock<ServiceBusSender> sender = new();
    private readonly List<ServiceBusMessage> scheduled = [];
    private readonly RuleActionContinuation continuation = new(
        "effect-1", Guid.NewGuid(), "action-1", Guid.NewGuid(), Guid.NewGuid(),
        GameType.CallOfDuty4x, Guid.NewGuid(), "player", AdminActionType.Ban,
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "reason", null);
    private readonly RuleActionContinuationProcessor processor;
    private RuleFeatureExecutionDto execution = new();
    private RuleActionPublicationIntent Intent => new(
        continuation.LogicalEffectId, continuation.ExecutionId, continuation.ActionId, continuation.GameServerId);

    public RuleActionContinuationProcessorTests()
    {
        versioned.Setup(x => x.V1).Returns(actions.Object);
        repository.Setup(x => x.AdminActions).Returns(versioned.Object);
        var versionedExecutions = new Mock<IVersionedRuleExecutionsApi>();
        versionedExecutions.Setup(item => item.V1).Returns(executions.Object);
        repository.Setup(item => item.RuleExecutions).Returns(versionedExecutions.Object);
        executions.Setup(item => item.GetExecution(continuation.GameServerId, continuation.ExecutionId, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new ApiResult<RuleFeatureExecutionDto>(
                HttpStatusCode.OK, new ApiResponse<RuleFeatureExecutionDto>(execution))));
        SetupRecord(continuation);
        var client = new Mock<ServiceBusClient>();
        client.Setup(item => item.CreateSender("rule-action-continuations")).Returns(sender.Object);
        sender.Setup(item => item.ScheduleMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, DateTimeOffset, CancellationToken>((message, _, _) => scheduled.Add(message))
            .ReturnsAsync(1L);
        processor = new(Mock.Of<ILogger<RuleActionContinuationProcessor>>(), repository.Object, topics.Object, audit.Object,
            Mock.Of<IRulesExecutionResumer>(), client.Object);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingTopicOrUnknownOutcomeDoesNotPost(bool completed)
    {
        SetupClaim(completed ? new { ForumTopicId = (int?)123, RequiresManualRecovery = false }
            : new { ForumTopicId = (int?)null, RequiresManualRecovery = true });

        await processor.ProcessRuleActionContinuation(CreateMessage(Intent), context.Object);
        await processor.ProcessRuleActionContinuation(CreateMessage(Intent), context.Object);

        VerifyNoPost();
        audit.Verify(x => x.LogAudit(It.IsAny<AuditEvent>()), Times.Never);
    }

    [Fact]
    public async Task LinkingFailureSurfacesAndRetryDoesNotRepost()
    {
        var claimId = Guid.NewGuid();
        actions.SetupSequence(x => x.ClaimForumTopicPublication(continuation.AdminActionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Claim(new { ClaimId = (Guid?)claimId, RequiresManualRecovery = false }))
            .ReturnsAsync(Claim(new { ClaimId = (Guid?)null, RequiresManualRecovery = true }));
        SetupTopic();
        actions.Setup(x => x.CompleteForumTopicPublication(continuation.AdminActionId, It.IsAny<CompleteForumTopicPublicationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessRuleActionContinuation(CreateMessage(Intent), context.Object));
        await processor.ProcessRuleActionContinuation(CreateMessage(Intent), context.Object);

        VerifyPost(Times.Once());
        audit.Verify(x => x.LogAudit(It.IsAny<AuditEvent>()), Times.Never);
    }

    [Fact]
    public async Task ClaimFailureSurfacesWithoutPosting()
    {
        actions.Setup(x => x.ClaimForumTopicPublication(continuation.AdminActionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ForumTopicPublicationClaimResultDto>(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessRuleActionContinuation(CreateMessage(Intent), context.Object));

        VerifyNoPost();
        audit.Verify(x => x.LogAudit(It.IsAny<AuditEvent>()), Times.Never);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutClaiming()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        context.Setup(x => x.CancellationToken).Returns(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessRuleActionContinuation(CreateMessage(Intent), context.Object));

        actions.Verify(x => x.ClaimForumTopicPublication(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(RuleActionExecutionOutcome.Claimed)]
    [InlineData(RuleActionExecutionOutcome.OutcomeUnknown)]
    public async Task LinkedRecordPublishesEvenAfterAmbiguousExecutionAndLinksBeforeAuditing(
        RuleActionExecutionOutcome outcome)
    {
        execution = execution with { Actions = [execution.Actions.Single() with { Outcome = outcome }] };
        var claimId = Guid.NewGuid();
        SetupClaim(new { ClaimId = claimId, RequiresManualRecovery = false });
        SetupTopic();
        var linked = false;
        actions.Setup(x => x.CompleteForumTopicPublication(continuation.AdminActionId,
                It.Is<CompleteForumTopicPublicationDto>(c => c.ClaimId == claimId && c.ForumTopicId == 123), It.IsAny<CancellationToken>()))
            .Callback(() => linked = true).ReturnsAsync(SuccessResult());
        audit.Setup(x => x.LogAudit(It.IsAny<AuditEvent>())).Callback(() => Assert.True(linked));

        await processor.ProcessRuleActionContinuation(CreateMessage(Intent), context.Object);

        VerifyPost(Times.Once());
        audit.Verify(x => x.LogAudit(It.Is<AuditEvent>(a => a.EventName == "RuleActionForumTopicPublished")), Times.Once);
    }

    [Fact]
    public async Task MissingUsernameIsResolvedBeforeTakingPublicationClaim()
    {
        SetupRecord(continuation with { Username = null });
        var players = new Mock<IPlayersApi>();
        var versionedPlayers = new Mock<IVersionedPlayersApi>();
        versionedPlayers.Setup(item => item.V1).Returns(players.Object);
        repository.Setup(item => item.Players).Returns(versionedPlayers.Object);
        var loaded = false;
        var player = JsonConvert.DeserializeObject<PlayerDto>(JsonConvert.SerializeObject(new
        {
            continuation.PlayerId,
            Username = "resolved-player"
        }))!;
        players.Setup(item => item.GetPlayer(continuation.PlayerId, PlayerEntityOptions.None))
            .Callback(() => loaded = true)
            .ReturnsAsync(new ApiResult<PlayerDto>(HttpStatusCode.OK, new ApiResponse<PlayerDto>(player)));
        var claimId = Guid.NewGuid();
        actions.Setup(item => item.ClaimForumTopicPublication(continuation.AdminActionId, It.IsAny<CancellationToken>()))
            .Callback(() => Assert.True(loaded))
            .ReturnsAsync(Claim(new { ClaimId = claimId, RequiresManualRecovery = false }));
        topics.Setup(item => item.CreateTopicForAdminAction(continuation.ActionType, continuation.GameType,
            continuation.PlayerId, "resolved-player", continuation.Created, continuation.Text, continuation.AdminId,
            It.IsAny<CancellationToken>())).ReturnsAsync(123);
        actions.Setup(item => item.CompleteForumTopicPublication(continuation.AdminActionId,
                It.IsAny<CompleteForumTopicPublicationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await processor.ProcessRuleActionContinuation(CreateMessage(Intent), context.Object);

        VerifyPost(Times.Once());
        audit.Verify(item => item.LogAudit(It.IsAny<AuditEvent>()), Times.Once);
    }

    [Fact]
    public async Task FailedUsernameLookupCannotConsumePublicationClaim()
    {
        SetupRecord(continuation with { Username = null });
        var players = new Mock<IPlayersApi>();
        var versionedPlayers = new Mock<IVersionedPlayersApi>();
        versionedPlayers.Setup(item => item.V1).Returns(players.Object);
        repository.Setup(item => item.Players).Returns(versionedPlayers.Object);
        players.Setup(item => item.GetPlayer(continuation.PlayerId, PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessRuleActionContinuation(
            CreateMessage(Intent), context.Object));

        actions.Verify(item => item.ClaimForumTopicPublication(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyNoPost();
        audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PublisherUsesStableMessageIdAndCamelCase()
    {
        var client = new Mock<ServiceBusClient>();
        var sender = new Mock<ServiceBusSender>();
        client.Setup(x => x.CreateSender("rule-action-continuations")).Returns(sender.Object);
        ServiceBusMessage? sent = null;
        sender.Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((message, _) => sent = message).Returns(Task.CompletedTask);
        var publisher = new RuleActionContinuationPublisher(client.Object);

        await publisher.PublishAsync(Intent);

        Assert.NotNull(sent);
        Assert.Equal(continuation.LogicalEffectId, sent.MessageId);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Contains("\"logicalEffectId\":\"effect-1\"", sent.Body.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"actionId\":\"action-1\"", sent.Body.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("adminActionId", sent.Body.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingRecordIsRescheduledWithoutTakingPublicationClaim()
    {
        execution = execution with
        {
            Actions = [execution.Actions.Single() with { AdminActionId = null, Outcome = RuleActionExecutionOutcome.Claimed }]
        };

        await processor.ProcessRuleActionContinuation(CreateMessage(Intent), context.Object);

        var message = Assert.Single(scheduled);
        Assert.Equal(Intent.LogicalEffectId, message.MessageId);
        actions.VerifyNoOtherCalls();
        VerifyNoPost();
        audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FailedRescheduleSurfacesSoOriginalIntentIsRetried()
    {
        execution = execution with
        {
            Actions = [execution.Actions.Single() with { AdminActionId = null, Outcome = RuleActionExecutionOutcome.Claimed }]
        };
        sender.Setup(item => item.ScheduleMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Queue unavailable."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessRuleActionContinuation(CreateMessage(Intent), context.Object));

        actions.VerifyNoOtherCalls();
        VerifyNoPost();
        audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FailedActionWithoutRecordCannotPost()
    {
        execution = execution with
        {
            Actions = [execution.Actions.Single() with { AdminActionId = null, Outcome = RuleActionExecutionOutcome.OutcomeUnknown }]
        };

        await processor.ProcessRuleActionContinuation(CreateMessage(Intent), context.Object);

        Assert.Empty(scheduled);
        actions.VerifyNoOtherCalls();
        VerifyNoPost();
        audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExpiredUnlinkedClaimSurfacesForQueueRecoveryInsteadOfLosingIntent()
    {
        var action = execution.Actions.Single();
        execution = execution with
        {
            Actions =
            [
                action with
                {
                    AdminActionId = null,
                    Outcome = RuleActionExecutionOutcome.Claimed,
                    Command = action.Command with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }
                }
            ]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessRuleActionContinuation(CreateMessage(Intent), context.Object));

        Assert.Empty(scheduled);
        actions.VerifyNoOtherCalls();
        VerifyNoPost();
        audit.VerifyNoOtherCalls();
    }

    private void SetupRecord(RuleActionContinuation data)
    {
        execution = new()
        {
            GameServerId = data.GameServerId,
            ExecutionId = data.ExecutionId,
            Trigger = new() { GameType = data.GameType, Username = data.Username },
            Actions =
            [
                new()
                {
                    ActionId = data.ActionId,
                    LogicalEffectId = data.LogicalEffectId,
                    Mode = RuleExecutionMode.Enforce,
                    AdminActionId = data.AdminActionId,
                    Command = new() { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2) }
                }
            ]
        };
        var record = JsonConvert.DeserializeObject<AdminActionDto>(JsonConvert.SerializeObject(new
        {
            data.AdminActionId,
            data.PlayerId,
            Type = data.ActionType,
            data.Created,
            data.Text,
            data.AdminId
        }))!;
        actions.Setup(item => item.GetAdminAction(data.AdminActionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<AdminActionDto>(HttpStatusCode.OK, new ApiResponse<AdminActionDto>(record)));
    }

    private void SetupClaim(object claim)
        => actions.Setup(x => x.ClaimForumTopicPublication(continuation.AdminActionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Claim(claim));

    private static ApiResult<ForumTopicPublicationClaimResultDto> Claim(object claim)
        => new(HttpStatusCode.OK, new ApiResponse<ForumTopicPublicationClaimResultDto>(
            JsonConvert.DeserializeObject<ForumTopicPublicationClaimResultDto>(JsonConvert.SerializeObject(claim))!));

    private void SetupTopic()
        => topics.Setup(x => x.CreateTopicForAdminAction(continuation.ActionType, continuation.GameType,
            continuation.PlayerId, continuation.Username!, continuation.Created, continuation.Text, continuation.AdminId,
            It.IsAny<CancellationToken>())).ReturnsAsync(123);

    private void VerifyNoPost() => VerifyPost(Times.Never());

    private void VerifyPost(Times times)
        => topics.Verify(x => x.CreateTopicForAdminAction(It.IsAny<AdminActionType>(), It.IsAny<GameType>(),
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), times);
}
