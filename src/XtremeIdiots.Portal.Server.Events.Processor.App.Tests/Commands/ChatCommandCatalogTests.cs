using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

using Moq;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
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
        var settingsProvider = new Mock<IChatCommandSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string commandName, bool _, CancellationToken _) =>
            {
                if (string.Equals(commandName, "fu", StringComparison.OrdinalIgnoreCase))
                {
                    return new EffectiveChatCommandSettings
                    {
                        CommandName = commandName,
                        Enabled = true,
                        FreshnessSeconds = 5,
                        Settings = JsonSerializer.SerializeToElement(new
                        {
                            messages = new object[]
                            {
                                new { message = "fu-{name}", enabled = true }
                            }
                        }),
                        EnabledSource = SettingsValueSource.ServerCommand,
                        FreshnessSource = SettingsValueSource.ServerCommand,
                        AuthorizationSource = SettingsValueSource.ServerCommand,
                        PayloadSource = SettingsValueSource.ServerCommand
                    };
                }

                return new EffectiveChatCommandSettings
                {
                    CommandName = commandName,
                    Enabled = true,
                    FreshnessSeconds = 5,
                    EnabledSource = SettingsValueSource.Hardcoded,
                    FreshnessSource = SettingsValueSource.Hardcoded,
                    AuthorizationSource = SettingsValueSource.Hardcoded,
                    PayloadSource = SettingsValueSource.Hardcoded
                };
            });

        var sut = CreateSut(settingsProvider.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.Contains(result, x => x.Prefix == "!fu");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenFuDisabled_ExcludesFu()
    {
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

        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), "fu", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveChatCommandSettings
            {
                CommandName = "fu",
                Enabled = false,
                FreshnessSeconds = 5,
                EnabledSource = SettingsValueSource.ServerCommand,
                FreshnessSource = SettingsValueSource.ServerCommand,
                AuthorizationSource = SettingsValueSource.ServerCommand,
                PayloadSource = SettingsValueSource.ServerCommand
            });

        var sut = CreateSut(settingsProvider.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.DoesNotContain(result, x => x.Prefix == "!fu");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenUnknownFeatureFlag_HidesCommand()
    {
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
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("commands", "!commands", "!commands"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("beta", "!beta", "!beta", "unknown-flag"));

        var authorizationService = new Mock<ICommandAuthorizationService>();
        authorizationService
            .Setup(x => x.AuthorizeAsync(It.IsAny<CommandAuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandAuthorizationResult.Allow());

        var logger = new Mock<ILogger<ChatCommandCatalog>>();
        var sut = new ChatCommandCatalog(services.BuildServiceProvider(), settingsProvider.Object, authorizationService.Object, logger.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.Contains(result, x => x.Prefix == "!commands");
        Assert.DoesNotContain(result, x => x.Prefix == "!beta");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenUnauthorized_HidesCommand()
    {
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
        var sut = new ChatCommandCatalog(services.BuildServiceProvider(), settingsProvider.Object, authorizationService.Object, logger.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.Contains(result, x => x.Prefix == "!commands");
        Assert.DoesNotContain(result, x => x.Prefix == "!admin");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenCommandDisabledInSettings_HidesCommand()
    {
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

        var sut = CreateSut(settingsProvider.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.DoesNotContain(result, x => x.Prefix == "!register");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenFuHasNoUsableMessages_HidesFu()
    {
        var settingsProvider = new Mock<IChatCommandSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string commandName, bool _, CancellationToken _) =>
            {
                if (string.Equals(commandName, "fu", StringComparison.OrdinalIgnoreCase))
                {
                    return new EffectiveChatCommandSettings
                    {
                        CommandName = commandName,
                        Enabled = true,
                        FreshnessSeconds = 5,
                        Settings = JsonSerializer.SerializeToElement(new
                        {
                            messages = new object[]
                            {
                                new { message = "", enabled = true },
                                new { message = "disabled", enabled = false }
                            }
                        }),
                        EnabledSource = SettingsValueSource.ServerCommand,
                        FreshnessSource = SettingsValueSource.ServerCommand,
                        AuthorizationSource = SettingsValueSource.ServerCommand,
                        PayloadSource = SettingsValueSource.ServerCommand
                    };
                }

                return new EffectiveChatCommandSettings
                {
                    CommandName = commandName,
                    Enabled = true,
                    FreshnessSeconds = 5,
                    EnabledSource = SettingsValueSource.Hardcoded,
                    FreshnessSource = SettingsValueSource.Hardcoded,
                    AuthorizationSource = SettingsValueSource.Hardcoded,
                    PayloadSource = SettingsValueSource.Hardcoded
                };
            });

        var sut = CreateSut(settingsProvider.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext());

        Assert.DoesNotContain(result, x => x.Prefix == "!fu");
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_PassesSettingsRequirementsToAuthorizationContext()
    {
        var settingsProvider = new Mock<IChatCommandSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string commandName, bool _, CancellationToken _) => new EffectiveChatCommandSettings
            {
                CommandName = commandName,
                Enabled = true,
                FreshnessSeconds = 5,
                RequiredTags = string.Equals(commandName, "register", StringComparison.OrdinalIgnoreCase) ? ["tag-r"] : [],
                EnabledSource = SettingsValueSource.ServerCommand,
                FreshnessSource = SettingsValueSource.ServerCommand,
                AuthorizationSource = SettingsValueSource.ServerCommand,
                PayloadSource = SettingsValueSource.Hardcoded
            });

        var services = new ServiceCollection();
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("register", "!register", "!register CODE", requiredPolicy: "register-policy"));

        var authorizationService = new Mock<ICommandAuthorizationService>();
        authorizationService
            .Setup(x => x.AuthorizeAsync(It.IsAny<CommandAuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandAuthorizationResult.Allow());

        var logger = new Mock<ILogger<ChatCommandCatalog>>();
        var sut = new ChatCommandCatalog(services.BuildServiceProvider(), settingsProvider.Object, authorizationService.Object, logger.Object);

        await sut.GetAvailableCommandsAsync(CreateContext());

        authorizationService.Verify(x => x.AuthorizeAsync(
            It.Is<CommandAuthorizationContext>(c =>
                c.RequiredPolicy == "register-policy" &&
                c.RequiredTags.SequenceEqual(new[] { "tag-r" }) &&
                c.Privileged),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenCommandGameTypeIsUnsupported_HidesCommand()
    {
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
        services.AddTransient<IChatCommand>(_ => new TestChatCommand(
            "commands",
            "!commands",
            "!commands",
            supportedGameTypes: [GameType.CallOfDuty4x]));

        var authorizationService = new Mock<ICommandAuthorizationService>();
        authorizationService
            .Setup(x => x.AuthorizeAsync(It.IsAny<CommandAuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandAuthorizationResult.Allow());

        var logger = new Mock<ILogger<ChatCommandCatalog>>();
        var sut = new ChatCommandCatalog(services.BuildServiceProvider(), settingsProvider.Object, authorizationService.Object, logger.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext() with { GameType = nameof(GameType.CallOfDuty4) });

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAvailableCommandsAsync_WhenGameTypeIsUnparseableAndCommandHasGameTypeConstraints_HidesCommand()
    {
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
        services.AddTransient<IChatCommand>(_ => new TestChatCommand(
            "commands",
            "!commands",
            "!commands",
            supportedGameTypes: [GameType.CallOfDuty4x]));

        var authorizationService = new Mock<ICommandAuthorizationService>();
        authorizationService
            .Setup(x => x.AuthorizeAsync(It.IsAny<CommandAuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandAuthorizationResult.Allow());

        var logger = new Mock<ILogger<ChatCommandCatalog>>();
        var sut = new ChatCommandCatalog(services.BuildServiceProvider(), settingsProvider.Object, authorizationService.Object, logger.Object);

        var result = await sut.GetAvailableCommandsAsync(CreateContext() with { GameType = "CallOfDutyX" });

        Assert.Empty(result);
    }

    private static ChatCommandCatalog CreateSut(IChatCommandSettingsProvider settingsProvider)
    {
        var services = new ServiceCollection();
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("commands", "!commands", "!commands"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("register", "!register", "!register CODE"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("like", "!like", "!like"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("dislike", "!dislike", "!dislike"));
        services.AddTransient<IChatCommand>(_ => new TestChatCommand("fu", "!fu", "!fu <player name>"));

        var authorizationService = new Mock<ICommandAuthorizationService>();
        authorizationService
            .Setup(x => x.AuthorizeAsync(It.IsAny<CommandAuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandAuthorizationResult.Allow());

        var logger = new Mock<ILogger<ChatCommandCatalog>>();
        return new ChatCommandCatalog(services.BuildServiceProvider(), settingsProvider, authorizationService.Object, logger.Object);
    }

    private sealed class TestChatCommand(
        string name,
        string prefix,
        string usage,
        string? featureFlag = null,
        string? requiredPolicy = null,
        IReadOnlyList<GameType>? supportedGameTypes = null) : IChatCommand
    {
        public string Prefix => prefix;

        public ChatCommandMetadata Metadata => new()
        {
            Name = name,
            Prefix = prefix,
            Usage = usage,
            FeatureFlag = featureFlag,
            RequiredPolicy = requiredPolicy,
            SupportedGameTypes = supportedGameTypes
        };

        public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
            => Task.FromResult(CommandResult.Ok());
    }
}
