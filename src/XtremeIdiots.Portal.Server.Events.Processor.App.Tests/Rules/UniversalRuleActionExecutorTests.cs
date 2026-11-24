using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class UniversalRuleActionExecutorTests
{
    [Fact]
    public async Task TriggerBeforeBindingWaitsAndResumesWhenExactObservedIdentityArrives()
    {
        var fixture = new Fixture();
        fixture.Bindings.SetupSequence(item => item.GetCurrentBinding(
                fixture.Command.GameServerId, fixture.Command.SlotId, fixture.Command.PlayerGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RuleConnectionBindingDto>(HttpStatusCode.NotFound))
            .ReturnsAsync(Ok(fixture.Binding));

        var waiting = await fixture.AvailabilityAsync(DateTimeOffset.UtcNow.AddMinutes(2));
        var ready = await fixture.AvailabilityAsync(DateTimeOffset.UtcNow.AddMinutes(2));

        Assert.True(waiting.Deferred);
        Assert.False(ready.Deferred);
        Assert.Null(ready.Unavailable);
        fixture.Servers.Verify(item => item.RulesNativeAction.V1.Execute(It.IsAny<Guid>(),
            It.Is<RuleActionCommand>(command => command.Mode == RuleExecutionMode.DryRun),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Servers.Verify(item => item.RulesNativeAction.V1.Execute(It.IsAny<Guid>(),
            It.Is<RuleActionCommand>(command => command.Mode == RuleExecutionMode.Enforce),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, fixture.Records.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconnectOrSourceSwitchWithSameSlotAndGuidIsStale(bool sourceSwitch)
    {
        var fixture = new Fixture();
        var binding = sourceSwitch
            ? fixture.Binding with { Runtime = fixture.Binding.Runtime with { InstanceId = Guid.NewGuid() } }
            : fixture.Binding with { ConnectionId = Guid.NewGuid() };
        fixture.Setup(binding);

        var result = await fixture.AvailabilityAsync(DateTimeOffset.UtcNow.AddMinutes(2));

        Assert.False(result.Deferred);
        Assert.Equal(ActionExecutionOutcome.Stale, result.Unavailable?.Outcome);
    }

    [Fact]
    public async Task ReceiptTimeCannotExtendDelayedStatusFreshness()
    {
        var fixture = new Fixture();
        fixture.Setup(fixture.Binding with
        {
            ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2)
        });

        var result = await fixture.AvailabilityAsync(DateTimeOffset.UtcNow.AddMinutes(2));

        Assert.Equal(ActionExecutionOutcome.Stale, result.Unavailable?.Outcome);
    }

    [Fact]
    public async Task TombstoneOrMapNeverArrivingExpiresWithoutClaimingEffect()
    {
        var fixture = new Fixture();
        fixture.Bindings.Setup(item => item.GetCurrentBinding(It.IsAny<Guid>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RuleConnectionBindingDto>(HttpStatusCode.NotFound));

        var result = await fixture.AvailabilityAsync(DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.False(result.Deferred);
        Assert.Equal(ActionExecutionOutcome.Stale, result.Unavailable?.Outcome);
        fixture.Servers.VerifyNoOtherCalls();
        Assert.Equal(0, fixture.Records.Calls);
    }

    [Fact]
    public async Task PluginOwnedEffectNeverFallsBackToRcon()
    {
        var fixture = new Fixture();
        var command = fixture.Command with
        {
            Runtime = fixture.Command.Runtime with
            {
                Id = "cod4x-plugin",
                IdentityModel = RuleRuntimeIdentityModel.PluginRuntimeGeneration
            },
            ConnectionGeneration = 4
        };
        var invocation = new RuleInvocation(new RuleTriggerEnvelope(), new RuleFactSnapshotDto(), null);

        var result = await fixture.Executor.ExecuteAsync(
            new RuleActionDefinition { Id = "message", TypeId = "message.public" }, command, invocation, default);

        Assert.Equal(ActionExecutionOutcome.Dispatched, result.Outcome);
        Assert.Same(command, result.NativeCommand);
        fixture.Servers.VerifyNoOtherCalls();
        fixture.Bindings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LegacyKickDisplayReasonIsExplicitlyUnsupported()
    {
        var fixture = new Fixture();
        var invocation = new RuleInvocation(new RuleTriggerEnvelope
        {
            GameType = GameType.CallOfDuty4,
            Runtime = fixture.Command.Runtime
        }, new RuleFactSnapshotDto(), null);

        var reason = await fixture.Executor.UnsupportedReasonAsync(
            new RuleActionDefinition { Id = "kick", TypeId = "player.kick" }, invocation, default);

        Assert.Contains("display reason", reason);
    }

    [Theory]
    [InlineData(RuleActionExecutionOutcome.SkippedUnsupported)]
    [InlineData(RuleActionExecutionOutcome.Stale)]
    public async Task AgentChecksRealNativeEligibilityBeforeClaimOrModerationRecord(
        RuleActionExecutionOutcome nativeOutcome)
    {
        var fixture = new Fixture();
        fixture.Setup(fixture.Binding);
        fixture.SetupEligibility(nativeOutcome);
        fixture.Players.Setup(item => item.HeadPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        var result = await fixture.Executor.AvailabilityAsync(
            new() { Id = "ban", TypeId = "moderation.ban" },
            fixture.Command with { Effect = new() { Type = RuleNativeEffectType.DropPlayer } },
            new(new() { GameType = GameType.CallOfDuty4 }, new(), null),
            DateTimeOffset.UtcNow.AddMinutes(2), default);

        Assert.Equal(nativeOutcome.ToString(), result.Unavailable?.Outcome.ToString());
        Assert.Equal(0, fixture.Records.Calls);
        fixture.Servers.Verify(item => item.RulesNativeAction.V1.Execute(It.IsAny<Guid>(),
            It.Is<RuleActionCommand>(command => command.Mode == RuleExecutionMode.DryRun),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Servers.Verify(item => item.RulesNativeAction.V1.Execute(It.IsAny<Guid>(),
            It.Is<RuleActionCommand>(command => command.Mode == RuleExecutionMode.Enforce),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DryRunUsesReadOnlyNativeEligibilityWithoutRequiringAModerationPlayer()
    {
        var fixture = new Fixture();
        fixture.Setup(fixture.Binding);

        var result = await fixture.Executor.AvailabilityAsync(
            new() { Id = "ban", TypeId = "moderation.ban" },
            fixture.Command with { Mode = RuleExecutionMode.DryRun },
            new(new() { GameType = GameType.CallOfDuty4 }, new(), null),
            DateTimeOffset.UtcNow.AddMinutes(2), default);

        Assert.Null(result.Unavailable);
        Assert.False(result.Deferred);
        Assert.Equal(0, fixture.Records.Calls);
        fixture.Players.VerifyNoOtherCalls();
        fixture.Servers.Verify(item => item.RulesNativeAction.V1.Execute(It.IsAny<Guid>(),
            It.Is<RuleActionCommand>(command => command.Mode == RuleExecutionMode.DryRun),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AmbiguousEligibilityCannotAuthorizeAnEnforcementAttempt()
    {
        var fixture = new Fixture();
        fixture.Setup(fixture.Binding);
        fixture.SetupEligibility(RuleActionExecutionOutcome.Dispatched);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.AvailabilityAsync(DateTimeOffset.UtcNow.AddMinutes(2)));

        Assert.Equal(0, fixture.Records.Calls);
        fixture.Servers.Verify(item => item.RulesNativeAction.V1.Execute(It.IsAny<Guid>(),
            It.Is<RuleActionCommand>(command => command.Mode == RuleExecutionMode.Enforce),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(128, false, false)]
    [InlineData(129, false, true)]
    [InlineData(1, true, true)]
    public async Task PluginModerationReasonRespectsNativeUtf8AndControlCharacterLimits(
        int characters, bool controlCharacter, bool unsupported)
    {
        var fixture = new Fixture();
        var text = controlCharacter ? "reason\ntext" : new string('\u00e9', characters);
        var action = new RuleActionDefinition
        {
            Id = "ban",
            TypeId = "moderation.ban",
            Parameters = new Dictionary<string, JsonElement> { ["reason"] = JsonSerializer.SerializeToElement(text) }
        };
        var invocation = new RuleInvocation(new()
        {
            GameType = GameType.CallOfDuty4x,
            ConnectionGeneration = 1,
            Runtime = fixture.Command.Runtime with
            {
                Id = "cod4x-plugin",
                IdentityModel = RuleRuntimeIdentityModel.PluginRuntimeGeneration,
                Capabilities = [RulesEngineCatalogueV1.Cod4xPlayerDropCapability]
            }
        }, new(), null);

        var reason = await fixture.Executor.UnsupportedReasonAsync(action, invocation, default);

        Assert.Equal(unsupported, reason is not null);
        Assert.Equal(0, fixture.Records.Calls);
        fixture.Servers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NewPlayerModerationWaitsBeforeClaimWithoutChangingFrozenFacts()
    {
        var fixture = new Fixture();
        fixture.Players.SetupSequence(item => item.HeadPlayerByGameType(
                GameType.CallOfDuty4x, "player"))
            .ReturnsAsync(new ApiResult(HttpStatusCode.NotFound))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));
        var action = new RuleActionDefinition { Id = "observation", TypeId = "moderation.observation" };
        var command = fixture.Command with { Effect = new() };
        var invocation = new RuleInvocation(new() { GameType = GameType.CallOfDuty4x }, new(), null);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        var waiting = await fixture.Executor.AvailabilityAsync(action, command, invocation, deadline, default);
        var ready = await fixture.Executor.AvailabilityAsync(action, command, invocation, deadline, default);

        Assert.True(waiting.Deferred);
        Assert.False(ready.Deferred);
        Assert.Null(ready.Unavailable);
        Assert.Equal(0, fixture.Records.Calls);
        fixture.Servers.VerifyNoOtherCalls();
        fixture.Bindings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingModerationPlayerExpiresWithoutCreatingARecord()
    {
        var fixture = new Fixture();
        fixture.Players.Setup(item => item.HeadPlayerByGameType(
                It.IsAny<GameType>(), It.IsAny<string>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.NotFound));

        var result = await fixture.Executor.AvailabilityAsync(
            new() { Id = "observation", TypeId = "moderation.observation" }, fixture.Command,
            new(new() { GameType = GameType.CallOfDuty4x }, new(), null),
            DateTimeOffset.UtcNow.AddSeconds(-1), default);

        Assert.Equal(ActionExecutionOutcome.Stale, result.Unavailable?.Outcome);
        Assert.Equal(0, fixture.Records.Calls);
        fixture.Servers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ObservationCompletesWithLinkedRecordAndNoNativeDispatch()
    {
        var fixture = new Fixture();

        var result = await fixture.Executor.ExecuteAsync(
            new() { Id = "observation", TypeId = "moderation.observation" },
            fixture.Command with { Effect = new() },
            new(new(), new(), null), default);

        Assert.Equal(ActionExecutionOutcome.Applied, result.Outcome);
        Assert.Equal(fixture.Records.Id, result.AdminActionId);
        Assert.Null(result.NativeCommand);
        Assert.Equal(1, fixture.Records.Calls);
        fixture.Servers.VerifyNoOtherCalls();
        fixture.Bindings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExpiryDuringRecordPersistenceCannotExposeNativeCommand()
    {
        var clock = new Clock();
        var fixture = new Fixture(clock);
        fixture.Records.OnPersist = () => clock.Advance(TimeSpan.FromMinutes(1));
        var command = fixture.Command with
        {
            ExpiresAtUtc = clock.GetUtcNow().AddSeconds(30),
            Runtime = fixture.Command.Runtime with { IdentityModel = RuleRuntimeIdentityModel.PluginRuntimeGeneration }
        };

        var result = await fixture.Executor.ExecuteAsync(
            new() { Id = "ban", TypeId = "moderation.ban" }, command,
            new(new(), new(), null), default);

        Assert.Equal(1, fixture.Records.Calls);
        Assert.Equal(ActionExecutionOutcome.Stale, result.Outcome);
        Assert.Null(result.NativeCommand);
        fixture.Servers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AlreadyExpiredDecisionCannotCreateModerationRecord()
    {
        var fixture = new Fixture();

        var result = await fixture.Executor.ExecuteAsync(
            new() { Id = "ban", TypeId = "moderation.ban" },
            fixture.Command with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) },
            new(new(), new(), null), default);

        Assert.Equal(0, fixture.Records.Calls);
        Assert.Equal(ActionExecutionOutcome.Stale, result.Outcome);
        fixture.Servers.VerifyNoOtherCalls();
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private static ApiResult<T> Ok<T>(T value) => new(HttpStatusCode.OK, new ApiResponse<T>(value));

    private sealed class Records : IRuleModerationRecordWriter
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Calls { get; private set; }
        public Action? OnPersist { get; set; }
        public Task<Guid> PersistAsync(
            RuleActionDefinition action, RuleActionCommand command, RuleInvocation invocation, CancellationToken ct)
        {
            Calls++;
            OnPersist?.Invoke();
            return Task.FromResult(Id);
        }
    }

    private sealed class Fixture
    {
        public Mock<IRuleConnectionBindingsApi> Bindings { get; } = new();
        public Mock<IPlayersApi> Players { get; } = new();
        public Mock<IServersApiClient> Servers { get; } = new();
        public Records Records { get; } = new();
        public UniversalRuleActionExecutor Executor { get; }
        public RuleActionCommand Command { get; } = new()
        {
            GameServerId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            ConnectionId = Guid.NewGuid(),
            PlayerGuid = "player",
            SlotId = 4,
            Runtime = new()
            {
                Id = "server-agent",
                IdentityModel = RuleRuntimeIdentityModel.AgentObservedConnection,
                InstanceId = Guid.NewGuid(),
                StartedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
                Version = "1.0",
                ProtocolVersion = 1
            },
            Mode = RuleExecutionMode.Enforce,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(30),
            Effect = new() { Type = RuleNativeEffectType.PublicMessage, Message = "Hello" }
        };
        public RuleConnectionBindingDto Binding { get; }

        public Fixture(TimeProvider? clock = null)
        {
            var repository = new Mock<IRepositoryApiClient>();
            var versioned = new Mock<IVersionedRuleConnectionBindingsApi>();
            versioned.Setup(item => item.V1).Returns(Bindings.Object);
            repository.Setup(item => item.RuleConnectionBindings).Returns(versioned.Object);
            var players = new Mock<IVersionedPlayersApi>();
            players.Setup(item => item.V1).Returns(Players.Object);
            repository.Setup(item => item.Players).Returns(players.Object);
            var observed = DateTimeOffset.UtcNow;
            Binding = new()
            {
                GameServerId = Command.GameServerId,
                GameType = GameType.CallOfDuty4,
                SlotId = Command.SlotId,
                PlayerGuid = Command.PlayerGuid,
                ConnectionId = Command.ConnectionId,
                Runtime = Command.Runtime,
                ObservedAtUtc = observed,
                ExpiresAtUtc = observed.AddMinutes(2)
            };
            SetupEligibility(RuleActionExecutionOutcome.WouldExecute);
            Executor = new(repository.Object, Servers.Object, Records, NullLogger<UniversalRuleActionExecutor>.Instance, clock);
        }

        public void SetupEligibility(RuleActionExecutionOutcome outcome) =>
            Servers.Setup(item => item.RulesNativeAction.V1.Execute(It.IsAny<Guid>(),
                    It.Is<RuleActionCommand>(command => command.Mode == RuleExecutionMode.DryRun), It.IsAny<CancellationToken>()))
                .Returns((Guid _, RuleActionCommand command, CancellationToken _) => Task.FromResult(Ok(new RuleActionResult
                {
                    GameServerId = command.GameServerId,
                    ExecutionId = command.ExecutionId,
                    LogicalEffectId = command.LogicalEffectId,
                    ActionId = command.ActionId,
                    Correlation = command.Correlation,
                    Outcome = outcome,
                    OccurredAtUtc = DateTimeOffset.UtcNow
                })));

        public void Setup(RuleConnectionBindingDto binding) =>
            Bindings.Setup(item => item.GetCurrentBinding(It.IsAny<Guid>(), It.IsAny<int>(),
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(binding));

        public Task<ActionAvailability> AvailabilityAsync(DateTimeOffset deadline) => Executor.AvailabilityAsync(
            new() { Id = "message", TypeId = "message.public" }, Command,
            new(new() { GameType = GameType.CallOfDuty4 }, new(), null), deadline, default);
    }
}
