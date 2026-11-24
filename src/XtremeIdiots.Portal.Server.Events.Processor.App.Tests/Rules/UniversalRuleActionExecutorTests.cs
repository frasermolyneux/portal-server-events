using System.Net;

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

        var waiting = await fixture.Executor.AvailabilityAsync(fixture.Command, DateTimeOffset.UtcNow.AddMinutes(2), default);
        var ready = await fixture.Executor.AvailabilityAsync(fixture.Command, DateTimeOffset.UtcNow.AddMinutes(2), default);

        Assert.True(waiting.Deferred);
        Assert.False(ready.Deferred);
        Assert.Null(ready.Unavailable);
        fixture.Servers.VerifyNoOtherCalls();
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

        var result = await fixture.Executor.AvailabilityAsync(fixture.Command, DateTimeOffset.UtcNow.AddMinutes(2), default);

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

        var result = await fixture.Executor.AvailabilityAsync(fixture.Command, DateTimeOffset.UtcNow.AddMinutes(2), default);

        Assert.Equal(ActionExecutionOutcome.Stale, result.Unavailable?.Outcome);
    }

    [Fact]
    public async Task TombstoneOrMapNeverArrivingExpiresWithoutClaimingEffect()
    {
        var fixture = new Fixture();
        fixture.Bindings.Setup(item => item.GetCurrentBinding(It.IsAny<Guid>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RuleConnectionBindingDto>(HttpStatusCode.NotFound));

        var result = await fixture.Executor.AvailabilityAsync(fixture.Command, DateTimeOffset.UtcNow.AddSeconds(-1), default);

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
        public int Calls { get; private set; }
        public Action? OnPersist { get; set; }
        public Task<Guid> PersistAsync(
            RuleActionDefinition action, RuleActionCommand command, RuleInvocation invocation, CancellationToken ct)
        {
            Calls++;
            OnPersist?.Invoke();
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class Fixture
    {
        public Mock<IRuleConnectionBindingsApi> Bindings { get; } = new();
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
            Executor = new(repository.Object, Servers.Object, Records, NullLogger<UniversalRuleActionExecutor>.Instance, clock);
        }

        public void Setup(RuleConnectionBindingDto binding) =>
            Bindings.Setup(item => item.GetCurrentBinding(It.IsAny<Guid>(), It.IsAny<int>(),
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(binding));
    }
}
