using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class CommandAuthorizationServiceTests
{
    private readonly Mock<ILogger<CommandAuthorizationService>> _logger = new();

    [Fact]
    public async Task AuthorizeAsync_WhenPolicyNotRequired_Allows()
    {
        var sut = CreateSut(new CommandAuthorizationOptions());

        var result = await sut.AuthorizeAsync(CreateContext(requiredPolicy: null));

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPolicyMissing_Denies()
    {
        var sut = CreateSut(new CommandAuthorizationOptions());

        var result = await sut.AuthorizeAsync(CreateContext(requiredPolicy: "admin"));

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenTagPolicyMatches_Allows()
    {
        var sut = CreateSut(new CommandAuthorizationOptions
        {
            Policies =
            {
                ["admin"] = new CommandPolicyOptions
                {
                    RequiredTags = ["game-admin"]
                }
            }
        });

        var result = await sut.AuthorizeAsync(CreateContext(requiredPolicy: "admin", tags: ["game-admin"]));

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenClaimPolicyMatches_Allows()
    {
        var sut = CreateSut(new CommandAuthorizationOptions
        {
            Policies =
            {
                ["admin"] = new CommandPolicyOptions
                {
                    RequiredClaims = ["AdminActions.Create"]
                }
            }
        });

        var result = await sut.AuthorizeAsync(CreateContext(requiredPolicy: "admin", claims: ["AdminActions.Create"]));

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenHybridConflictsForPrivileged_Denies()
    {
        var sut = CreateSut(new CommandAuthorizationOptions
        {
            Policies =
            {
                ["admin"] = new CommandPolicyOptions
                {
                    RequiredTags = ["game-admin"],
                    RequiredClaims = ["AdminActions.Create"],
                    Privileged = true
                }
            }
        });

        var result = await sut.AuthorizeAsync(CreateContext(requiredPolicy: "admin", tags: ["game-admin"], claims: []));

        Assert.False(result.Allowed);
        Assert.Contains("inconsistent", result.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenGameScopeDoesNotMatch_Denies()
    {
        var sut = CreateSut(new CommandAuthorizationOptions
        {
            Policies =
            {
                ["admin"] = new CommandPolicyOptions
                {
                    RequiredTags = ["game-admin"],
                    AllowedGameTypes = ["CallOfDuty2"]
                }
            }
        });

        var result = await sut.AuthorizeAsync(CreateContext(requiredPolicy: "admin", gameType: "CallOfDuty4", tags: ["game-admin"]));

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenServerScopeDoesNotMatch_Denies()
    {
        var allowedServer = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var actualServer = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var sut = CreateSut(new CommandAuthorizationOptions
        {
            Policies =
            {
                ["admin"] = new CommandPolicyOptions
                {
                    RequiredTags = ["game-admin"],
                    AllowedServerIds = [allowedServer]
                }
            }
        });

        var result = await sut.AuthorizeAsync(CreateContext(requiredPolicy: "admin", serverId: actualServer, tags: ["game-admin"]));

        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task AuthorizeAsync_WhenPrivilegedClaimsUnavailable_DeniesFailClosed()
    {
        var sut = CreateSut(new CommandAuthorizationOptions
        {
            Policies =
            {
                ["admin"] = new CommandPolicyOptions
                {
                    RequiredClaims = ["AdminActions.Create"],
                    Privileged = true
                }
            }
        });

        var snapshot = new CommandAuthorizationSnapshot
        {
            ClaimsResolved = false,
            TagsResolved = true,
            Claims = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        var result = await sut.AuthorizeAsync(CreateContext(requiredPolicy: "admin", snapshot: snapshot));

        Assert.False(result.Allowed);
        Assert.Contains("dependencies", result.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private CommandAuthorizationService CreateSut(CommandAuthorizationOptions options)
    {
        var monitor = new Mock<IOptionsMonitor<CommandAuthorizationOptions>>();
        monitor.Setup(x => x.CurrentValue).Returns(options);

        return new CommandAuthorizationService(monitor.Object, _logger.Object);
    }

    private static CommandAuthorizationContext CreateContext(
        string? requiredPolicy,
        string gameType = "CallOfDuty4",
        Guid? serverId = null,
        string[]? tags = null,
        string[]? claims = null,
        CommandAuthorizationSnapshot? snapshot = null)
    {
        snapshot ??= new CommandAuthorizationSnapshot
        {
            TagsResolved = true,
            ClaimsResolved = true,
            Tags = (tags ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
            Claims = (claims ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase)
        };

        return new CommandAuthorizationContext
        {
            CommandPrefix = "!admin",
            RequiredPolicy = requiredPolicy,
            GameType = gameType,
            ServerId = serverId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PlayerId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Snapshot = snapshot
        };
    }
}
