using Microsoft.Extensions.Logging;

using Moq;

using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class CommandsCommandTests
{
    private readonly Mock<IChatCommandCatalog> _catalog = new();
    private readonly Mock<IRconResponseService> _rconResponseService = new();
    private readonly Mock<ILogger<CommandsCommand>> _logger = new();

    private readonly CommandsCommand _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public CommandsCommandTests()
    {
        _catalog.Setup(x => x.GetAvailableCommandsAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ChatCommandDefinition { Prefix = "!register" },
            new ChatCommandDefinition { Prefix = "!commands" },
            new ChatCommandDefinition { Prefix = "!like" },
            new ChatCommandDefinition { Prefix = "!dislike" }
        ]);

        _rconResponseService
            .Setup(x => x.TryTellAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _sut = new CommandsCommand(_catalog.Object, _rconResponseService.Object, _logger.Object);
    }

    private static CommandContext CreateContext(string message = "!commands") => new()
    {
        ServerId = TestServerId,
        GameType = "CallOfDuty4",
        PlayerGuid = "abc123",
        Username = "TestPlayer",
        SlotId = 3,
        Message = message,
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        SequenceId = 1,
        PlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    };

    [Fact]
    public async Task ExecuteAsync_WhenValid_ReturnsSuccessAndSendsPrivateCommandsList()
    {
        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("Available commands: !commands, !dislike, !like, !register", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
                It.IsAny<string>(),
            "abc123",
            3,
            "Available commands: !commands, !dislike, !like, !register",
            "TestPlayer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFuEnabled_IncludesFuInCommandsList()
    {
        _catalog.Setup(x => x.GetAvailableCommandsAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ChatCommandDefinition { Prefix = "!register" },
            new ChatCommandDefinition { Prefix = "!commands" },
            new ChatCommandDefinition { Prefix = "!like" },
            new ChatCommandDefinition { Prefix = "!dislike" },
            new ChatCommandDefinition { Prefix = "!fu" }
        ]);

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("Available commands: !commands, !dislike, !fu, !like, !register", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
                It.IsAny<string>(),
            "abc123",
            3,
            "Available commands: !commands, !dislike, !fu, !like, !register",
            "TestPlayer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFuDisabled_DoesNotIncludeFuInCommandsList()
    {
        _catalog.Setup(x => x.GetAvailableCommandsAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ChatCommandDefinition { Prefix = "!register" },
            new ChatCommandDefinition { Prefix = "!commands" },
            new ChatCommandDefinition { Prefix = "!like" },
            new ChatCommandDefinition { Prefix = "!dislike" }
        ]);

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("Available commands: !commands, !dislike, !like, !register", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
                It.IsAny<string>(),
            "abc123",
            3,
            "Available commands: !commands, !dislike, !like, !register",
            "TestPlayer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenUsageInvalid_ReturnsFailedAndSendsUsage()
    {
        var result = await _sut.ExecuteAsync(CreateContext("!commands extra"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Usage: !commands", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
                It.IsAny<string>(),
            "abc123",
            3,
            "Usage: !commands",
            "TestPlayer",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

}
