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
    private readonly RuleActionContinuation continuation = new(
        "effect-1", Guid.NewGuid(), "action-1", Guid.NewGuid(), Guid.NewGuid(),
        GameType.CallOfDuty4x, Guid.NewGuid(), "player", AdminActionType.Ban,
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "reason", "admin");
    private readonly RuleActionContinuationProcessor processor;

    public RuleActionContinuationProcessorTests()
    {
        versioned.Setup(x => x.V1).Returns(actions.Object);
        repository.Setup(x => x.AdminActions).Returns(versioned.Object);
        processor = new(Mock.Of<ILogger<RuleActionContinuationProcessor>>(), repository.Object, topics.Object, audit.Object,
            Mock.Of<IRulesExecutionResumer>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingTopicOrUnknownOutcomeDoesNotPost(bool completed)
    {
        SetupClaim(completed ? new { ForumTopicId = (int?)123, RequiresManualRecovery = false }
            : new { ForumTopicId = (int?)null, RequiresManualRecovery = true });

        await processor.ProcessRuleActionContinuation(CreateMessage(continuation), context.Object);
        await processor.ProcessRuleActionContinuation(CreateMessage(continuation), context.Object);

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

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessRuleActionContinuation(CreateMessage(continuation), context.Object));
        await processor.ProcessRuleActionContinuation(CreateMessage(continuation), context.Object);

        VerifyPost(Times.Once());
        audit.Verify(x => x.LogAudit(It.IsAny<AuditEvent>()), Times.Never);
    }

    [Fact]
    public async Task ClaimFailureSurfacesWithoutPosting()
    {
        actions.Setup(x => x.ClaimForumTopicPublication(continuation.AdminActionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ForumTopicPublicationClaimResultDto>(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessRuleActionContinuation(CreateMessage(continuation), context.Object));

        VerifyNoPost();
        audit.Verify(x => x.LogAudit(It.IsAny<AuditEvent>()), Times.Never);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutClaiming()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        context.Setup(x => x.CancellationToken).Returns(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessRuleActionContinuation(CreateMessage(continuation), context.Object));

        actions.Verify(x => x.ClaimForumTopicPublication(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SuccessLinksBeforeAuditing()
    {
        var claimId = Guid.NewGuid();
        SetupClaim(new { ClaimId = claimId, RequiresManualRecovery = false });
        SetupTopic();
        var linked = false;
        actions.Setup(x => x.CompleteForumTopicPublication(continuation.AdminActionId,
                It.Is<CompleteForumTopicPublicationDto>(c => c.ClaimId == claimId && c.ForumTopicId == 123), It.IsAny<CancellationToken>()))
            .Callback(() => linked = true).ReturnsAsync(SuccessResult());
        audit.Setup(x => x.LogAudit(It.IsAny<AuditEvent>())).Callback(() => Assert.True(linked));

        await processor.ProcessRuleActionContinuation(CreateMessage(continuation), context.Object);

        VerifyPost(Times.Once());
        audit.Verify(x => x.LogAudit(It.Is<AuditEvent>(a => a.EventName == "RuleActionForumTopicPublished")), Times.Once);
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

        await publisher.PublishAsync(continuation);

        Assert.NotNull(sent);
        Assert.Equal(continuation.LogicalEffectId, sent.MessageId);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Contains("\"logicalEffectId\":\"effect-1\"", sent.Body.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"actionId\":\"action-1\"", sent.Body.ToString(), StringComparison.Ordinal);
    }

    private void SetupClaim(object claim)
        => actions.Setup(x => x.ClaimForumTopicPublication(continuation.AdminActionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Claim(claim));

    private static ApiResult<ForumTopicPublicationClaimResultDto> Claim(object claim)
        => new(HttpStatusCode.OK, new ApiResponse<ForumTopicPublicationClaimResultDto>(
            JsonConvert.DeserializeObject<ForumTopicPublicationClaimResultDto>(JsonConvert.SerializeObject(claim))!));

    private void SetupTopic()
        => topics.Setup(x => x.CreateTopicForAdminAction(continuation.ActionType, continuation.GameType,
            continuation.PlayerId, continuation.Username, continuation.Created, continuation.Text, continuation.AdminId,
            It.IsAny<CancellationToken>())).ReturnsAsync(123);

    private void VerifyNoPost() => VerifyPost(Times.Never());

    private void VerifyPost(Times times)
        => topics.Verify(x => x.CreateTopicForAdminAction(It.IsAny<AdminActionType>(), It.IsAny<GameType>(),
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), times);
}
