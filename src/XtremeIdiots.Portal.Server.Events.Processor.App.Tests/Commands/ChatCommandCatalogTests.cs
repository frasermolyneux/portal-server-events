using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        SequenceId = 1,
        PlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    };

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenFuEnabled_IncludesFu()
    {
        var provider = new Mock<IFuMessageSettingsProvider>();
        provider.Setup(x => x.IsEnabledAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var settingsProvider = new Mock<IChatCommandSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string commandName, bool _, CancellationToken _) => new EffectiveChatCommandSettings
            {
                CommandName = commandName,
                Enabled = true,
                FreshnessSeconds = 5,
                EnabledSource = SettingsValueSource.Hardcoded,
                FreshnessSource = SettingsValueSource.Hardcoded,
                AuthorizationSource = SettingsValueSource.Hardcoded,
                PayloadSource = SettingsValueSource.Hardcoded
            });

        var sut = CreateSut(provider.Object, settingsProvider.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.Contains(result, x => x.Prefix == "!fu");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenFuDisabled_ExcludesFu()
    {
        var provider = new Mock<IFuMessageSettingsProvider>();
        provider.Setup(x => x.IsEnabledAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var settingsProvider = new Mock<IChatCommandSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string commandName, bool _, CancellationToken _) => new EffectiveChatCommandSettings
            {
                CommandName = commandName,
                Enabled = true,
                FreshnessSeconds = 5,
                EnabledSource = SettingsValueSource.Hardcoded,
                FreshnessSource = SettingsValueSource.Hardcoded,
                AuthorizationSource = SettingsValueSource.Hardcoded,
                PayloadSource = SettingsValueSource.Hardcoded
            });

        var sut = CreateSut(provider.Object, settingsProvider.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.DoesNotContain(result, x => x.Prefix == "!fu");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenUnknownFeatureFlag_HidesCommand()
    {
        var provider = new Mock<IFuMessageSettingsProvider>();
        provider.Setup(x => x.IsEnabledAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var settingsProvider = new Mock<IChatCommandSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string commandName, bool _, CancellationToken _) => new EffectiveChatCommandSettings
            {
                CommandName = commandName,
                Enabled = true,
                FreshnessSeconds = 5,
                EnabledSource = SettingsValueSource.Hardcoded,
                FreshnessSource = SettingsValueSource.Hardcoded,
                AuthorizationSource = SettingsValueSource.Hardcoded,
                PayloadSource = SettingsValueSource.Hardcoded
            });

        var services = new ServiceCollection();
        services.AddSingleton(provider.Object);
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("commands", "!commands", "!commands"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("beta", "!beta", "!beta", "unknown-flag"));

        var authorizationService = new Mock<ICommandAuthorizationService>();
        authorizationService
            .Setup(x => x.AuthorizeAsync(It.IsAny<CommandAuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandAuthorizationResult.Allow());

        var logger = new Mock<ILogger<ChatCommandCatalog>>();
        var sut = new ChatCommandCatalog(services.BuildServiceProvider(), provider.Object, settingsProvider.Object, authorizationService.Object, logger.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.Contains(result, x => x.Prefix == "!commands");
        Assert.DoesNotContain(result, x => x.Prefix == "!beta");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenUnauthorized_HidesCommand()
    {
        var provider = new Mock<IFuMessageSettingsProvider>();
        provider.Setup(x => x.IsEnabledAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var settingsProvider = new Mock<IChatCommandSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string commandName, bool _, CancellationToken _) => new EffectiveChatCommandSettings
            {
                CommandName = commandName,
                Enabled = true,
                FreshnessSeconds = 5,
                EnabledSource = SettingsValueSource.Hardcoded,
                FreshnessSource = SettingsValueSource.Hardcoded,
                AuthorizationSource = SettingsValueSource.Hardcoded,
                PayloadSource = SettingsValueSource.Hardcoded
            });

        var services = new ServiceCollection();
        services.AddSingleton(provider.Object);
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("commands", "!commands", "!commands"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("admin", "!admin", "!admin", requiredPolicy: "admin"));

        var authorizationService = new Mock<ICommandAuthorizationService>();
        authorizationService
            .Setup(x => x.AuthorizeAsync(It.Is<CommandAuthorizationContext>(c => c.CommandPrefix == "!commands"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandAuthorizationResult.Allow());
        authorizationService
            .Setup(x => x.AuthorizeAsync(It.Is<CommandAuthorizationContext>(c => c.CommandPrefix == "!admin"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandAuthorizationResult.Deny("denied"));

        var logger = new Mock<ILogger<ChatCommandCatalog>>();
        var sut = new ChatCommandCatalog(services.BuildServiceProvider(), provider.Object, settingsProvider.Object, authorizationService.Object, logger.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.Contains(result, x => x.Prefix == "!commands");
        Assert.DoesNotContain(result, x => x.Prefix == "!admin");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenCommandDisabledInSettings_HidesCommand()
    {
        var provider = new Mock<IFuMessageSettingsProvider>();
        provider.Setup(x => x.IsEnabledAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var settingsProvider = new Mock<IChatCommandSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string commandName, bool _, CancellationToken _) => new EffectiveChatCommandSettings
            {
                CommandName = commandName,
                Enabled = !string.Equals(commandName, "register", StringComparison.OrdinalIgnoreCase),
                FreshnessSeconds = 5,
                EnabledSource = SettingsValueSource.Hardcoded,
                FreshnessSource = SettingsValueSource.Hardcoded,
                AuthorizationSource = SettingsValueSource.Hardcoded,
                PayloadSource = SettingsValueSource.Hardcoded
            });

        var sut = CreateSut(provider.Object, settingsProvider.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.DoesNotContain(result, x => x.Prefix == "!register");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_PassesSettingsRequirementsToAuthorizationContext()
    {
        var provider = new Mock<IFuMessageSettingsProvider>();
        provider.Setup(x => x.IsEnabledAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var settingsProvider = new Mock<IChatCommandSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string commandName, bool _, CancellationToken _) => new EffectiveChatCommandSettings
            {
                CommandName = commandName,
                Enabled = true,
                FreshnessSeconds = 5,
                RequiredTags = string.Equals(commandName, "register", StringComparison.OrdinalIgnoreCase) ? ["tag-r"] : [],
                RequiredClaims = string.Equals(commandName, "register", StringComparison.OrdinalIgnoreCase) ? ["claim-r"] : [],
                EnabledSource = SettingsValueSource.ServerCommand,
                FreshnessSource = SettingsValueSource.ServerCommand,
                AuthorizationSource = SettingsValueSource.ServerCommand,
                PayloadSource = SettingsValueSource.Hardcoded
            });

        var services = new ServiceCollection();
        services.AddSingleton(provider.Object);
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("register", "!register", "!register CODE", requiredPolicy: "register-policy"));

        var authorizationService = new Mock<ICommandAuthorizationService>();
        authorizationService
            .Setup(x => x.AuthorizeAsync(It.IsAny<CommandAuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandAuthorizationResult.Allow());

        var logger = new Mock<ILogger<ChatCommandCatalog>>();
        var sut = new ChatCommandCatalog(services.BuildServiceProvider(), provider.Object, settingsProvider.Object, authorizationService.Object, logger.Object);

        await sut.GetAvailableCommandsAsync(CreateContext());

        authorizationService.Verify(x => x.AuthorizeAsync(
            It.Is<CommandAuthorizationContext>(c =>
                c.RequiredPolicy == "register-policy" &&
                c.RequiredTags.SequenceEqual(new[] { "tag-r" }) &&
                c.RequiredClaims.SequenceEqual(new[] { "claim-r" }) &&
                c.Privileged),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ChatCommandCatalog CreateSut(IFuMessageSettingsProvider provider, IChatCommandSettingsProvider settingsProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(provider);
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("commands", "!commands", "!commands"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("register", "!register", "!register CODE"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("like", "!like", "!like"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("dislike", "!dislike", "!dislike"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("fu", "!fu", "!fu <player name>", "fu"));

        var authorizationService = new Mock<ICommandAuthorizationService>();
        authorizationService
            .Setup(x => x.AuthorizeAsync(It.IsAny<CommandAuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandAuthorizationResult.Allow());

        var logger = new Mock<ILogger<ChatCommandCatalog>>();
        return new ChatCommandCatalog(services.BuildServiceProvider(), provider, settingsProvider, authorizationService.Object, logger.Object);
    }

    private sealed class TestChatCommand(string name, string prefix, string usage, string? featureFlag = null, string? requiredPolicy = null) : IChatCommand
    {
        public string Prefix => prefix;

        public ChatCommandMetadata Metadata => new()
        {
            Name = name,
            Prefix = prefix,
            Usage = usage,
            FeatureFlag = featureFlag,
            RequiredPolicy = requiredPolicy
        };

        public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
            => Task.FromResult(CommandResult.Ok());
    }
}
