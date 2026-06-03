using Microsoft.Extensions.DependencyInjection;

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

        var sut = CreateSut(provider.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.Contains(result, x => x.Prefix == "!fu");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenFuDisabled_ExcludesFu()
    {
        var provider = new Mock<IFuMessageSettingsProvider>();
        provider.Setup(x => x.IsEnabledAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var sut = CreateSut(provider.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.DoesNotContain(result, x => x.Prefix == "!fu");
    }

    private static ChatCommandCatalog CreateSut(IFuMessageSettingsProvider provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(provider);
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("commands", "!commands", "!commands"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("register", "!register", "!register CODE"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("like", "!like", "!like"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("dislike", "!dislike", "!dislike"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("fu", "!fu", "!fu <player name>", "fu"));

        return new ChatCommandCatalog(services.BuildServiceProvider(), provider);
    }

    private sealed class TestChatCommand(string name, string prefix, string usage, string? featureFlag = null) : IChatCommand
    {
        public string Prefix => prefix;

        public ChatCommandMetadata Metadata => new()
        {
            Name = name,
            Prefix = prefix,
            Usage = usage,
            FeatureFlag = featureFlag
        };

        public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
            => Task.FromResult(CommandResult.Ok());
    }
}
