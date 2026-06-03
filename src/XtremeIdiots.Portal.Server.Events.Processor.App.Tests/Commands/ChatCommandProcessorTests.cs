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
        var sut = new ChatCommandProcessor([], _parser, _authorizationService.Object, _idempotencyStore.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

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

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

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

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

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

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

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

        var sut = new ChatCommandProcessor([first.Object, second.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

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

        var action = () => new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

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

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

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

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

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

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

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

        var sut = new ChatCommandProcessor([command.Object], _parser, _authorizationService.Object, _idempotencyStore.Object, _rconResponseService.Object, _auditLogger.Object, _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test"));

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("replayed", result.ResponseMessage);
        command.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
