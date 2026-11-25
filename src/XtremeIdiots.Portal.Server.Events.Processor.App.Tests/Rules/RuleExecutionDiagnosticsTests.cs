using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RuleExecutionDiagnosticsTests
{
    [Fact]
    public void OverflowRemainsExactAndDeduplicatedAcrossRestartsAndProvisionalReplacement()
    {
        var now = DateTimeOffset.UtcNow;
        var progress = new RuleExecutionProgress { Diagnostics = new() };
        for (var index = 0; index < 260; index++)
        {
            progress.Diagnostics.Add(Diagnostic(index, now));
        }

        var projected = progress.Diagnostics.Project(now);
        Assert.Equal(256, projected.Length);
        Assert.Equal(5, projected[^1].OmittedCount);

        var persisted = JsonSerializer.Serialize(progress, RulesSerialization.Options);
        var resumed = JsonSerializer.Deserialize<RuleExecutionProgress>(persisted, RulesSerialization.Options)!;
        resumed.Diagnostics!.Add(Diagnostic(259, now));
        resumed.Diagnostics.Add(Diagnostic(0, now));
        Assert.Equal(5, resumed.Diagnostics.Project(now)[^1].OmittedCount);

        Assert.True(resumed.Diagnostics.ReplaceActionText("rule", "action-0", [], now,
            NullLogger.Instance));
        Assert.Equal(4, resumed.Diagnostics.Project(now)[^1].OmittedCount);
        resumed.Diagnostics.Waiting = new()
        {
            Code = RuleExecutionDiagnosticCode.WaitingForDelivery,
            RuleId = "rule",
            ObservedAtUtc = now
        };
        var waiting = resumed.Diagnostics.Project(now);
        Assert.Equal(RuleExecutionDiagnosticCode.WaitingForDelivery, waiting[0].Code);
        Assert.Equal(5, waiting[^1].OmittedCount);
    }

    [Fact]
    public void ReplacingActionTextRemovesOmittedIdentitiesWithoutLosingOtherEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var diagnostics = new RuleExecutionDiagnostics();
        for (var index = 0; index < 257; index++)
        {
            diagnostics.Add(Diagnostic(index, now));
        }

        Assert.True(diagnostics.ReplaceActionText("rule", "action-256", [], now, NullLogger.Instance));

        var projected = diagnostics.Project(now);
        Assert.Equal(256, projected.Length);
        Assert.DoesNotContain(projected, item => item.Code == RuleExecutionDiagnosticCode.DiagnosticsOmitted);
    }

    [Fact]
    public void RepeatedIdenticalTextDiagnosticsDoNotChangeDurableProgress()
    {
        var diagnostics = new RuleExecutionDiagnostics();
        var now = DateTimeOffset.UtcNow;
        RuleTextDiagnostic[] text = [new("FallbackUsed", "player.name", RuleTextTokenSource.Connection, "Used fallback.")];

        Assert.True(diagnostics.ReplaceActionText("rule", "action", text, now, NullLogger.Instance));
        Assert.False(diagnostics.ReplaceActionText("rule", "action", text, now.AddMinutes(1), NullLogger.Instance));
        Assert.Equal(now, Assert.Single(diagnostics.Project(now)).ObservedAtUtc);
    }

    private static RuleExecutionDiagnostic Diagnostic(int index, DateTimeOffset now) => new()
    {
        Code = RuleExecutionDiagnosticCode.FallbackUsed,
        RuleId = "rule",
        ActionId = $"action-{index}",
        TokenKey = "player.name",
        Source = "Connection",
        ObservedAtUtc = now
    };
}
