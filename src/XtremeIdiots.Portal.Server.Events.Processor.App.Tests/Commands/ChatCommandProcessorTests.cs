using Microsoft.Extensions.Logging;

using Moq;

using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class ChatCommandProcessorTests
{
    private readonly Mock<ILogger<ChatCommandProcessor>> _logger = new();

    private static CommandContext CreateContext(string message = "!test") => new()
    {
        ServerId = Guid.NewGuid(),
        GameType = "CallOfDuty4",
        PlayerGuid = "abc123",
        Username = "TestPlayer",
        Message = message,
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        PlayerId = Guid.NewGuid()
    };

    [Theory]
    [InlineData("Hello world")]
    [InlineData("no prefix")]
    [InlineData("")]
    public async Task ProcessAsync_MessageWithNoPrefix_ReturnsNotHandled(string message)
    {
        var sut = new ChatCommandProcessor([], _logger.Object);

        var result = await sut.ProcessAsync(CreateContext(message));

        Assert.False(result.Handled);
    }

    [Fact]
    public async Task ProcessAsync_MessageWithUnknownCommand_ReturnsNotHandled()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!other");
        command.Setup(c => c.CanHandle("!unknown")).Returns(false);

        var sut = new ChatCommandProcessor([command.Object], _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!unknown"));

        Assert.False(result.Handled);
    }

    [Fact]
    public async Task ProcessAsync_MatchingCommand_ExecutesAndReturnsResult()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!test");
        command.Setup(c => c.CanHandle("!test")).Returns(true);
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("done"));

        var sut = new ChatCommandProcessor([command.Object], _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test"));

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("done", result.ResponseMessage);
    }

    [Fact]
    public async Task ProcessAsync_CommandThrows_ReturnsFailed()
    {
        var command = new Mock<IChatCommand>();
        command.Setup(c => c.Prefix).Returns("!boom");
        command.Setup(c => c.CanHandle("!boom")).Returns(true);
        command.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kaboom"));

        var sut = new ChatCommandProcessor([command.Object], _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!boom"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Contains("kaboom", result.ResponseMessage);
    }

    [Fact]
    public async Task ProcessAsync_MultipleCommands_FirstMatchWins()
    {
        var first = new Mock<IChatCommand>();
        first.Setup(c => c.Prefix).Returns("!test");
        first.Setup(c => c.CanHandle("!test")).Returns(true);
        first.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("first"));

        var second = new Mock<IChatCommand>();
        second.Setup(c => c.Prefix).Returns("!test");
        second.Setup(c => c.CanHandle("!test")).Returns(true);
        second.Setup(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("second"));

        var sut = new ChatCommandProcessor([first.Object, second.Object], _logger.Object);

        var result = await sut.ProcessAsync(CreateContext("!test"));

        Assert.Equal("first", result.ResponseMessage);
        second.Verify(c => c.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
