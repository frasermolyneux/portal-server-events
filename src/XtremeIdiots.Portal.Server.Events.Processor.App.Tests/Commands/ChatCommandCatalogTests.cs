using Moq;

using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class ChatCommandCatalogTests
{
    private static CommandContext CreateContext() => new()
    {
        ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        GameType = "CallOfDuty4",
        PlayerGuid = "abc123",
        Username = "TestPlayer",
        SlotId = 3,
        Message = "!commands",
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        PlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    };

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenFuEnabled_IncludesFu()
    {
        var provider = new Mock<IFuMessageSettingsProvider>();
        provider.Setup(x => x.IsEnabledAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var sut = new ChatCommandCatalog(provider.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.Contains(result, x => x.Prefix == "!fu");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenFuDisabled_ExcludesFu()
    {
        var provider = new Mock<IFuMessageSettingsProvider>();
        provider.Setup(x => x.IsEnabledAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var sut = new ChatCommandCatalog(provider.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.DoesNotContain(result, x => x.Prefix == "!fu");
    }
}
