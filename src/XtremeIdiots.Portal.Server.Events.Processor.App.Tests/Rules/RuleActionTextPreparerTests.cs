using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RuleActionTextPreparerTests
{
    [Fact]
    public async Task ExistingLiteralFieldsNeverStartTokenLookupsOrInterpretBraces()
    {
        var context = new Mock<IRuleTextContextHydrator>(MockBehavior.Strict);
        var action = Action("Literal {context.player.Name}");

        var resolved = await Create(context).PrepareAsync(action, Invocation(), DateTimeOffset.UtcNow, "feature", "rule", default);

        Assert.Same(action, resolved.Action);
        Assert.Empty(resolved.Diagnostics);
        context.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolvesOnlyOptedInFieldsAndUsesOriginalExecutionContext()
    {
        var context = new Mock<IRuleTextContextHydrator>();
        context.Setup(item => item.HydrateAsync(It.IsAny<RuleInvocation>(),
                It.IsAny<IReadOnlySet<RuleTextTokenSource>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuleTextValues { Player = new() { Name = "Joining player" } });
        var action = Action("Hello {context.player.Name}: {context.rule.Id}");
        action.Parameters["reason"] = JsonSerializer.SerializeToElement("Literal {context.player.Name}");
        action.TemplateParameters = ["message"];

        var resolved = await Create(context).PrepareAsync(action, Invocation(), DateTimeOffset.UtcNow, "feature", "selected-rule", default);

        Assert.Equal("Hello Joining player: selected-rule", resolved.Action.Parameters["message"].GetString());
        Assert.Equal("Literal {context.player.Name}", resolved.Action.Parameters["reason"].GetString());
        Assert.Empty(resolved.Action.TemplateParameters);
        Assert.Empty(resolved.Diagnostics);
        Assert.Equal("Hello {context.player.Name}: {context.rule.Id}", action.Parameters["message"].GetString());
        context.Verify(item => item.HydrateAsync(It.IsAny<RuleInvocation>(),
            It.Is<IReadOnlySet<RuleTextTokenSource>>(sources =>
                sources.SetEquals(new[] { RuleTextTokenSource.Connection, RuleTextTokenSource.Execution })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MessageItemsRenderSeparatelyWithDeclaredFallbacks()
    {
        var context = new Mock<IRuleTextContextHydrator>();
        context.Setup(item => item.HydrateAsync(It.IsAny<RuleInvocation>(),
                It.IsAny<IReadOnlySet<RuleTextTokenSource>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuleTextValues());
        var action = new RuleActionDefinition
        {
            Id = "action",
            TypeId = "message.private",
            TemplateParameters = ["messages"],
            Parameters = new()
            {
                ["messages"] = JsonSerializer.SerializeToElement(new[]
                {
                    "Welcome {context.player.Name}",
                    "From {context.player.Geo.CountryName}"
                })
            }
        };

        var resolved = await Create(context).PrepareAsync(action, Invocation(), DateTimeOffset.UtcNow, "feature", "rule", default);

        Assert.Equal(["Welcome player", "From an unknown country"],
            resolved.Action.Parameters["messages"].EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task RepeatedItemsRetainOneDiagnosticPerSourceAndFallback()
    {
        var context = new Mock<IRuleTextContextHydrator>();
        context.Setup(item => item.HydrateAsync(It.IsAny<RuleInvocation>(),
                It.IsAny<IReadOnlySet<RuleTextTokenSource>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuleTextValues
            {
                SourceDiagnostics = new Dictionary<RuleTextTokenSource, string>
                {
                    [RuleTextTokenSource.Connection] = "Player name unavailable."
                }
            });
        var action = Action("Hello {context.player.Name}");
        action.Parameters["messages"] = JsonSerializer.SerializeToElement(new[] { "Again {context.player.Name}" });
        action.TemplateParameters = ["message", "messages"];

        var resolved = await Create(context).PrepareAsync(action, Invocation(), DateTimeOffset.UtcNow, "feature", "rule", default);

        Assert.Equal("Hello player", resolved.Action.Parameters["message"].GetString());
        Assert.Equal("Again player", resolved.Action.Parameters["messages"][0].GetString());
        Assert.Equal(2, resolved.Diagnostics.Count);
        Assert.Contains(resolved.Diagnostics, item =>
            item.Code == "SourceUnavailable" && item.Source == RuleTextTokenSource.Connection && item.TokenKey is null);
        Assert.Contains(resolved.Diagnostics, item =>
            item.Code == "FallbackUsed" && item.Source == RuleTextTokenSource.Connection && item.TokenKey == "context.player.Name");
    }

    private static RuleActionTextPreparer Create(Mock<IRuleTextContextHydrator> context) =>
        new(context.Object, NullLogger<RuleActionTextPreparer>.Instance);

    private static RuleActionDefinition Action(string message) => new()
    {
        Id = "action",
        TypeId = "message.public",
        Parameters = new() { ["message"] = JsonSerializer.SerializeToElement(message) }
    };

    private static RuleInvocation Invocation() => new(new(), new(), null);
}
