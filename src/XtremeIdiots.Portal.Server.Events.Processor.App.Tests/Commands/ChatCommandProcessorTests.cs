using Microsoft.Extensions.Logging;

using Moq;

using MX.Observability.ApplicationInsights.Auditing;

using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class ChatCommandProcessorTests
{
    private readonly Mock<ILogger<ChatCommandProcessor>> _logger = new();
    private readonly Mock<ICommandAuthorizationService> _authorizationService = new();
    private readonly Mock<ICommandIdempotencyStore> _idempotencyStore = new();
    private readonly Mock<ISystemClock> _clock = new();
    private readonly Mock<IChatCommandSettingsProvider> _settingsProvider = new();
    private readonly Mock<IRconResponseService> _rconResponseService = new();
    private readonly Mock<IAuditLogger> _auditLogger = new();
    private readonly ICommandParser _parser = new ChatCommandParser();

    public ChatCommandProcessorTests()
    {
        _authorizationService
            .Setup(x => x.AuthorizeAsync(It.IsAny<CommandAuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandAuthorizationResult.Allow());

        _rconResponseService
            .Setup(x => x.TryTellAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _idempotencyStore
            .Setup(x => x.TryBeginAsync(It.IsAny<CommandIdempotencyKey>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandIdempotencyDecision.Acquired());

        _idempotencyStore
            .Setup(x => x.CompleteAsync(It.IsAny<CommandIdempotencyKey>(), It.IsAny<CommandResult>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string commandName, bool isMutating, CancellationToken _) => new EffectiveChatCommandSettings
            {
                CommandName = commandName,
                Enabled = true,
                FreshnessSeconds = isMutating ? 3 : 5,
                EnabledSource = SettingsValueSource.Hardcoded,
                FreshnessSource = SettingsValueSource.Hardcoded,
                AuthorizationSource = SettingsValueSource.Hardcoded,
                PayloadSource = SettingsValueSource.Hardcoded
            });

        _clock.SetupGet(x => x.UtcNow).Returns(DateTime.UtcNow);
    }

    private static CommandContext CreateContext(string message = "!test") => new()
    {
        ServerId = Guid.NewGuid(),
        GameType = "CallOfDuty4",
        PlayerGuid = "abc123",
        Username = "TestPlayer",
        SlotId = 3,
        Message = message,
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        SequenceId = 1,
        PlayerId = Guid.NewGuid()
    };

    [Theory]
    [InlineData("Hello world")]
    [InlineData("no prefix")]
    [InlineData("")]
    public async Task ProcessAsync_MessageWithNoPrefix_ReturnsNotHandled(string message)
    {
        var sut = new ChatCommandProcessor([], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext(message));

        Assert.False(result.Handled);
    }

    [Fact]
    public async Task ProcessAsync_MessageWithUnknownCommand_ReturnsNotHandled()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!other");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "other",
            Prefix = "!other",
            Usage = "!other"
        });

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!unknown"));

        Assert.False(result.Handled);
    }

    [Fact]
    public async Task ProcessAsync_MatchingCommand_ExecutesAndReturnsResult()
    {
        var command = new Mock<IChatCommand>();
        CommandContext? capturedContext = null;

        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!test",
            Usage = "!test"
        });
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .Callback<CommandContext, CancellationToken>((ctx, _) => capturedContext = ctx)
            .ReturnsAsync(CommandResult.Ok("done"));

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test"));

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("done", result.ResponseMessage);
        Assert.NotNull(capturedContext?.ParsedCommand);
        Assert.Equal("!test", capturedContext?.ParsedCommand?.PrefixToken);
    }

    [Fact]
    public async Task ProcessAsync_CommandThrows_ReturnsFailed()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!boom");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "boom",
            Prefix = "!boom",
            Usage = "!boom"
        });
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kaboom"));

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!boom"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Contains("kaboom", result.ResponseMessage);
    }

    [Fact]
    public async Task ProcessAsync_MultipleCommands_FirstRegistrationWins()
    {
        var first = new Mock<IChatCommand>();
        first.Setup(c => c.Prefix).Returns("!test");
        first.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!test",
            Usage = "!test"
        });
        first.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("first"));

        var second = new Mock<IChatCommand>();
        second.Setup(c => c.Prefix).Returns("!test");
        second.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test-duplicate",
            Prefix = "!test",
            Usage = "!test"
        });
        second.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("second"));

        var sut = new ChatCommandProcessor([first.Object, second.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test"));

        Assert.Equal("first", result.ResponseMessage);
        second.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Ctor_WhenMetadataPrefixDiffersFromCommandPrefix_Throws()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!different",
            Usage = "!test"
        });

        var action = () => new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        Assert.Throws<InvalidOperationException>(action);
    }

    [Fact]
    public async Task ProcessAsync_WhenUnauthorized_ReturnsDeniedAndDoesNotExecuteCommand()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!test",
            Usage = "!test",
            RequiredPolicy = "admin"
        });

        _authorizationService
            .Setup(x => x.AuthorizeAsync(It.IsAny<CommandAuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandAuthorizationResult.Deny("not allowed"));

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.True(result.Denied);
        command.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _rconResponseService.Verify(x => x.TryTellAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            "You are not authorized to use this command.",
            It.IsAny<string?>(),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WhenMutatingAndSequenceMissing_ReturnsFailed()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!test",
            Usage = "!test",
            IsMutating = true
        });

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test") with { SequenceId = 0 });

        Assert.True(result.Handled);
        Assert.False(result.Success);
        command.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_WhenMutatingAndAlreadyInProgress_ReturnsFailed()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!test",
            Usage = "!test",
            IsMutating = true
        });

        _idempotencyStore
            .Setup(x => x.TryBeginAsync(It.IsAny<CommandIdempotencyKey>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandIdempotencyDecision.InProgress());

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        command.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_WhenMutatingAndAlreadyCompleted_ReplaysStoredResult()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!test",
            Usage = "!test",
            IsMutating = true
        });

        _idempotencyStore
            .Setup(x => x.TryBeginAsync(It.IsAny<CommandIdempotencyKey>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandIdempotencyDecision.Completed(CommandResult.Ok("replayed")));

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test"));

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("replayed", result.ResponseMessage);
        command.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_WhenReadOnlyCommandIsStale_ReturnsExpired()
    {
        var now = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        _clock.SetupGet(x => x.UtcNow).Returns(now);

        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!test",
            Usage = "!test",
            IsMutating = false
        });

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test") with { EventGeneratedUtc = now.AddSeconds(-6) });

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Command expired. Please run it again.", result.ResponseMessage);
        command.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_WhenMutatingCommandIsStale_ReturnsExpired()
    {
        var now = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        _clock.SetupGet(x => x.UtcNow).Returns(now);

        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!test",
            Usage = "!test",
            IsMutating = true
        });

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test") with { EventGeneratedUtc = now.AddSeconds(-4) });

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Command expired. Please run it again.", result.ResponseMessage);
        command.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _idempotencyStore.Verify(x => x.TryBeginAsync(It.IsAny<CommandIdempotencyKey>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_WhenCommandAgeEqualsThreshold_Executes()
    {
        var now = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        _clock.SetupGet(x => x.UtcNow).Returns(now);

        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!test",
            Usage = "!test"
        });
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("ok"));

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test") with { EventGeneratedUtc = now.AddSeconds(-5) });

        Assert.True(result.Success);
        Assert.Equal("ok", result.ResponseMessage);
        command.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WhenCommandSpecificOverrideConfigured_UsesOverrideThreshold()
    {
        var now = new DateTime(2026, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        _clock.SetupGet(x => x.UtcNow).Returns(now);

        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), "test", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveChatCommandSettings
            {
                CommandName = "test",
                Enabled = true,
                FreshnessSeconds = 10,
                EnabledSource = SettingsValueSource.ServerCommand,
                FreshnessSource = SettingsValueSource.ServerCommand,
                AuthorizationSource = SettingsValueSource.Hardcoded,
                PayloadSource = SettingsValueSource.Hardcoded
            });

        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!test",
            Usage = "!test"
        });
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("ok"));

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test") with { EventGeneratedUtc = now.AddSeconds(-8) });

        Assert.True(result.Success);
        command.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WhenCommandDisabledInSettings_ReturnsNotHandled()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!test",
            Usage = "!test"
        });

        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), "test", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveChatCommandSettings
            {
                CommandName = "test",
                Enabled = false,
                FreshnessSeconds = 5,
                EnabledSource = SettingsValueSource.ServerCommand,
                FreshnessSource = SettingsValueSource.Hardcoded,
                AuthorizationSource = SettingsValueSource.Hardcoded,
                PayloadSource = SettingsValueSource.Hardcoded
            });

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test"));

        Assert.False(result.Handled);
        command.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_WithAliasToken_ExecutesTargetCommand()
    {
        var command = new Mock<IChatCommand>();
        CommandContext? capturedContext = null;

        command.Setup(c => c.Prefix).Returns("!commands");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "commands",
            Prefix = "!commands",
            Usage = "!commands",
            Aliases = ["!help"]
        });
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .Callback<CommandContext, CancellationToken>((ctx, _) => capturedContext = ctx)
            .ReturnsAsync(CommandResult.Ok("help text"));

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!help"));

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("help text", result.ResponseMessage);
        Assert.NotNull(capturedContext?.ParsedCommand);
        Assert.Equal("!commands", capturedContext?.ParsedCommand?.PrefixToken);
    }

    [Fact]
    public async Task ProcessAsync_WithCaseInsensitiveAlias_ExecutesTargetCommand()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!commands");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "commands",
            Prefix = "!commands",
            Usage = "!commands",
            Aliases = ["!HELP"]
        });
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("help text"));

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!help"));

        Assert.True(result.Handled);
        Assert.Equal("help text", result.ResponseMessage);
    }

    [Fact]
    public async Task ProcessAsync_WithAliasToken_ExecutesConcreteCommandsCommand()
    {
        var catalog = new Mock<IChatCommandCatalog>();
        catalog
            .Setup(x => x.GetAvailableCommandsAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ChatCommandDefinition { Prefix = "!register" },
                new ChatCommandDefinition { Prefix = "!commands" }
            ]);

        var command = new CommandsCommand(catalog.Object, _rconResponseService.Object, Mock.Of<ILogger<CommandsCommand>>());
        var sut = new ChatCommandProcessor([command], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!help"));

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("Available commands: !commands, !register", result.ResponseMessage);
        _rconResponseService.Verify(x => x.TryTellAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            "Available commands: !commands, !register",
            It.IsAny<string?>(),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithMutatingAlias_UsesCanonicalIdempotencyKey()
    {
        var command = new Mock<IChatCommand>();
        var capturedKeys = new List<CommandIdempotencyKey>();

        command.Setup(c => c.Prefix).Returns("!register");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "register",
            Prefix = "!register",
            Usage = "!register CODE",
            IsMutating = true,
            Aliases = ["!link"]
        });
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok());

        _idempotencyStore
            .Setup(x => x.TryBeginAsync(It.IsAny<CommandIdempotencyKey>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<CommandIdempotencyKey, DateTime, CancellationToken>((key, _, _) => capturedKeys.Add(key))
            .ReturnsAsync(CommandIdempotencyDecision.Acquired());

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);
        var baseContext = CreateContext("!register ABC123");

        var canonicalResult = await sut.ProcessAsync(baseContext);
        var aliasResult = await sut.ProcessAsync(baseContext with { Message = "!link ABC123" });

        Assert.True(canonicalResult.Success);
        Assert.True(aliasResult.Success);
        Assert.Equal(2, capturedKeys.Count);
        Assert.Equal(capturedKeys[0], capturedKeys[1]);
    }

    [Fact]
    public async Task ProcessAsync_AliasCollidesWithPrimaryPrefix_FirstRegistrationWins()
    {
        var commandOne = new Mock<IChatCommand>();
        commandOne.Setup(c => c.Prefix).Returns("!help");
        commandOne.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "help-standalone",
            Prefix = "!help",
            Usage = "!help"
        });
        commandOne.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("standalone help"));

        var commandTwo = new Mock<IChatCommand>();
        commandTwo.Setup(c => c.Prefix).Returns("!commands");
        commandTwo.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "commands",
            Prefix = "!commands",
            Usage = "!commands",
            Aliases = ["!help"]
        });
        commandTwo.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("commands list"));

        var sut = new ChatCommandProcessor([commandOne.Object, commandTwo.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!help"));

        Assert.Equal("standalone help", result.ResponseMessage);
        commandOne.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Once);
        commandTwo.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_DuplicateAliasAcrossCommands_FirstRegistrationWins()
    {
        var commandOne = new Mock<IChatCommand>();
        commandOne.Setup(c => c.Prefix).Returns("!first");
        commandOne.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "first",
            Prefix = "!first",
            Usage = "!first",
            Aliases = ["!alias"]
        });
        commandOne.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("first command"));

        var commandTwo = new Mock<IChatCommand>();
        commandTwo.Setup(c => c.Prefix).Returns("!second");
        commandTwo.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "second",
            Prefix = "!second",
            Usage = "!second",
            Aliases = ["!alias"]
        });
        commandTwo.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("second command"));

        var sut = new ChatCommandProcessor([commandOne.Object, commandTwo.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!alias"));

        Assert.Equal("first command", result.ResponseMessage);
        commandOne.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Once);
        commandTwo.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }



    [Fact]
    public async Task ProcessAsync_PassesSettingsRequirementsToAuthorizationContext()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.Metadata).Returns(new ChatCommandMetadata
        {
            Name = "test",
            Prefix = "!test",
            Usage = "!test",
            RequiredPolicy = "admin"
        });
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("ok"));

        _settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), "test", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveChatCommandSettings
            {
                CommandName = "test",
                Enabled = true,
                FreshnessSeconds = 5,
                RequiredTags = ["tag-a"],
                EnabledSource = SettingsValueSource.ServerCommand,
                FreshnessSource = SettingsValueSource.ServerCommand,
                AuthorizationSource = SettingsValueSource.ServerCommand,
                PayloadSource = SettingsValueSource.Hardcoded
            });

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _clock.Object, _settingsProvider.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        await sut.ProcessAsync(CreateContext("!test"));

        _authorizationService.Verify(x => x.AuthorizeAsync(
            It.Is<CommandAuthorizationContext>(c =>
                c.RequiredPolicy == "admin" &&
                c.RequiredTags.SequenceEqual(new[] { "tag-a" }) &&
                c.Privileged),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

