using System.Net;
using System.Text.Json;

using Moq;

using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using Newtonsoft.Json;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RepositoryRuleModerationRecordWriterTests
{
    [Fact]
    public async Task ObservationPersistsAndAuditsWithoutSchedulingUnrequestedForumWork()
    {
        var fixture = new Fixture();

        var id = await fixture.Writer.PersistAsync(fixture.Action, fixture.Command, fixture.Invocation, default);

        Assert.Equal(fixture.RecordId, id);
        Assert.Equal(AdminActionType.Observation, fixture.Request!.Type);
        Assert.Null(fixture.Request.Expires);
        Assert.Equal(fixture.Command.LogicalEffectId, fixture.Request.LogicalEffectId);
        fixture.Audit.Verify(item => item.LogAudit(It.IsAny<AuditEvent>()), Times.Once);
        Assert.Empty(fixture.Continuations.Messages);
    }

    [Fact]
    public async Task TemporaryBanExpiryIsAnchoredToDurableClaimNotRetryTime()
    {
        var fixture = new Fixture("moderation.temporary-ban", AdminActionType.TempBan);
        fixture.Action.Parameters["durationMinutes"] = System.Text.Json.JsonSerializer.SerializeToElement(30);

        await fixture.Writer.PersistAsync(fixture.Action, fixture.Command, fixture.Invocation, default);

        Assert.Equal(fixture.ClaimedAtUtc.AddMinutes(30).UtcDateTime, fixture.Request!.Expires);
    }

    [Fact]
    public async Task ExistingRecordCanScheduleForumRecoveryWithoutDuplicatingCreationAudit()
    {
        var fixture = new Fixture(created: false);
        fixture.Action.Parameters["postToForums"] = System.Text.Json.JsonSerializer.SerializeToElement(true);

        await fixture.Writer.PersistAsync(fixture.Action, fixture.Command, fixture.Invocation, default);

        var continuation = Assert.Single(fixture.Continuations.Messages);
        Assert.Equal(fixture.Command.ExecutionId, continuation.ExecutionId);
        Assert.Equal(fixture.Command.LogicalEffectId, continuation.LogicalEffectId);
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FailedIntentPublicationCannotCreateAnUnrecoverableModerationRecord()
    {
        var fixture = new Fixture();
        fixture.Action.Parameters["postToForums"] = System.Text.Json.JsonSerializer.SerializeToElement(true);
        fixture.Continuations.Failure = new InvalidOperationException("Queue unavailable.");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Writer.PersistAsync(fixture.Action, fixture.Command, fixture.Invocation, default));

        fixture.Api.Verify(item => item.EnsureRuleActionAdminAction(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<EnsureRuleActionAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FailedPersistenceCannotAuditOrPublishForumSuccess()
    {
        var fixture = new Fixture();
        fixture.Api.Setup(item => item.EnsureRuleActionAdminAction(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<EnsureRuleActionAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<EnsureAutomatedActionResultDto>(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Writer.PersistAsync(fixture.Action, fixture.Command, fixture.Invocation, default));

        fixture.Audit.VerifyNoOtherCalls();
        Assert.Empty(fixture.Continuations.Messages);
    }

    [Fact]
    public async Task AmbiguousRecordWriteRetainsItsDurablePublicationIntent()
    {
        var fixture = new Fixture();
        fixture.Action.Parameters["postToForums"] = System.Text.Json.JsonSerializer.SerializeToElement(true);
        fixture.Api.Setup(item => item.EnsureRuleActionAdminAction(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<EnsureRuleActionAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<EnsureAutomatedActionResultDto>(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Writer.PersistAsync(fixture.Action, fixture.Command, fixture.Invocation, default));

        Assert.Single(fixture.Continuations.Messages);
        fixture.Audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DryRunCannotReachPersistenceOrForumIntegrations()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Writer.PersistAsync(
            fixture.Action, fixture.Command with { Mode = RuleExecutionMode.DryRun }, fixture.Invocation, default));

        fixture.Api.VerifyNoOtherCalls();
        fixture.Audit.VerifyNoOtherCalls();
        Assert.Empty(fixture.Continuations.Messages);
    }

    private sealed class Continuations : IRuleActionContinuationPublisher
    {
        public List<RuleActionPublicationIntent> Messages { get; } = [];
        public Exception? Failure { get; set; }
        public Task PublishAsync(RuleActionPublicationIntent continuation, CancellationToken ct = default)
        {
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }
            Messages.Add(continuation);
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture
    {
        public Guid RecordId { get; } = Guid.NewGuid();
        public DateTimeOffset ClaimedAtUtc { get; } = DateTimeOffset.UtcNow.AddSeconds(-5);
        public Mock<IRuleExecutionsApi> Api { get; } = new();
        public Mock<IAuditLogger> Audit { get; } = new();
        public Continuations Continuations { get; } = new();
        public RuleActionDefinition Action { get; }
        public RuleActionCommand Command { get; }
        public RuleInvocation Invocation { get; }
        public EnsureRuleActionAdminActionDto? Request { get; private set; }
        public RepositoryRuleModerationRecordWriter Writer { get; }

        public Fixture(string actionType = "moderation.observation", AdminActionType type = AdminActionType.Observation, bool created = true)
        {
            Action = new()
            {
                Id = "action",
                TypeId = actionType,
                Parameters = new() { ["reason"] = System.Text.Json.JsonSerializer.SerializeToElement("Matched rule.") }
            };
            Command = new()
            {
                GameServerId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                ConnectionId = Guid.NewGuid(),
                PlayerGuid = "player",
                ActionId = Action.Id,
                LogicalEffectId = new string('a', 64),
                Mode = RuleExecutionMode.Enforce,
                Correlation = new() { ActionId = Action.Id, FeatureId = "vpn-protection", RuleId = "rule" },
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(30)
            };
            Invocation = new(new()
            {
                GameServerId = Command.GameServerId,
                GameType = GameType.CallOfDuty4x,
                PlayerGuid = Command.PlayerGuid,
                Username = "Player"
            }, new(), null);
            var execution = new RuleFeatureExecutionDto
            {
                ExecutionId = Command.ExecutionId,
                GameServerId = Command.GameServerId,
                ConnectionId = Command.ConnectionId,
                Version = 3,
                Actions =
                [
                    new()
                    {
                        Command = Command,
                        Correlation = Command.Correlation,
                        ActionTypeId = actionType,
                        LogicalEffectId = Command.LogicalEffectId,
                        Mode = RuleExecutionMode.Enforce,
                        Outcome = RuleActionExecutionOutcome.Claimed,
                        ClaimedAtUtc = ClaimedAtUtc
                    }
                ]
            };
            var repository = new Mock<IRepositoryApiClient>();
            var versioned = new Mock<IVersionedRuleExecutionsApi>();
            versioned.Setup(item => item.V1).Returns(Api.Object);
            repository.Setup(item => item.RuleExecutions).Returns(versioned.Object);
            Api.Setup(item => item.GetExecution(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApiResult<RuleFeatureExecutionDto>(HttpStatusCode.OK, new ApiResponse<RuleFeatureExecutionDto>(execution)));
            var persisted = false;
            Api.Setup(item => item.EnsureRuleActionAdminAction(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                    It.IsAny<EnsureRuleActionAdminActionDto>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid _, string _, EnsureRuleActionAdminActionDto request, CancellationToken _) =>
                {
                    if (Action.Parameters.TryGetValue("postToForums", out var forums) && forums.ValueKind == JsonValueKind.True)
                    {
                        Assert.Single(Continuations.Messages);
                    }
                    Request = request;
                    persisted = true;
                    var result = JsonConvert.DeserializeObject<EnsureAutomatedActionResultDto>(JsonConvert.SerializeObject(new
                    {
                        Created = created,
                        AdminAction = new
                        {
                            AdminActionId = RecordId,
                            PlayerId = Guid.NewGuid(),
                            Type = type,
                            Text = "Matched rule.",
                            Created = ClaimedAtUtc.UtcDateTime
                        }
                    }))!;
                    return Task.FromResult(new ApiResult<EnsureAutomatedActionResultDto>(
                        HttpStatusCode.OK, new ApiResponse<EnsureAutomatedActionResultDto>(result)));
                });
            Audit.Setup(item => item.LogAudit(It.IsAny<AuditEvent>())).Callback(() => Assert.True(persisted));
            Writer = new(repository.Object, Continuations, Audit.Object);
        }
    }
}
