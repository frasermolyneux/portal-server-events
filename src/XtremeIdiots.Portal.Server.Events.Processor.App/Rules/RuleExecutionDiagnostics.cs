using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed record RuleExecutionDiagnosticIdentity(
    RuleExecutionDiagnosticCode Code, string? RuleId, string? ActionId, string? TokenKey, string? Source)
{
    public static RuleExecutionDiagnosticIdentity From(RuleExecutionDiagnostic diagnostic) =>
        new(diagnostic.Code, diagnostic.RuleId, diagnostic.ActionId, diagnostic.TokenKey, diagnostic.Source);
}

internal sealed class RuleExecutionDiagnostics
{
    public List<RuleExecutionDiagnostic> Items { get; set; } = [];
    public HashSet<RuleExecutionDiagnosticIdentity> Omitted { get; set; } = [];
    public RuleExecutionDiagnostic? Waiting { get; set; }
    public bool OverflowReported { get; set; }

    public bool Add(RuleExecutionDiagnostic diagnostic)
    {
        var identity = RuleExecutionDiagnosticIdentity.From(diagnostic);
        if (Items.Any(item => RuleExecutionDiagnosticIdentity.From(item) == identity) || Omitted.Contains(identity))
        {
            return false;
        }

        if (Items.Count < RuleExecutionDiagnostic.MaximumCount)
        {
            Items.Add(diagnostic);
        }
        else
        {
            Omitted.Add(identity);
        }
        return true;
    }

    public bool ReplaceActionText(
        string ruleId, string actionId, IReadOnlyList<RuleTextDiagnostic> diagnostics, DateTimeOffset observedAtUtc, ILogger logger)
    {
        static bool IsTextDiagnostic(RuleExecutionDiagnosticCode code) =>
            code is RuleExecutionDiagnosticCode.SourceUnavailable or RuleExecutionDiagnosticCode.FallbackUsed;

        var replacement = diagnostics.Select(diagnostic => new RuleExecutionDiagnostic
        {
            Code = diagnostic.Code switch
            {
                "SourceUnavailable" => RuleExecutionDiagnosticCode.SourceUnavailable,
                "FallbackUsed" => RuleExecutionDiagnosticCode.FallbackUsed,
                _ => throw new InvalidOperationException($"Unknown rule text diagnostic '{diagnostic.Code}'.")
            },
            RuleId = ruleId,
            ActionId = actionId,
            TokenKey = diagnostic.TokenKey,
            Source = diagnostic.Source?.ToString(),
            Detail = SafeDetail(diagnostic.Message, logger),
            ObservedAtUtc = observedAtUtc
        }).DistinctBy(RuleExecutionDiagnosticIdentity.From).ToArray();
        var retained = Items.Where(item =>
            item.RuleId == ruleId && item.ActionId == actionId && IsTextDiagnostic(item.Code)).ToArray();
        var identities = retained.Select(RuleExecutionDiagnosticIdentity.From)
            .Concat(Omitted.Where(item => item.RuleId == ruleId && item.ActionId == actionId && IsTextDiagnostic(item.Code)))
            .ToHashSet();
        if (identities.SetEquals(replacement.Select(RuleExecutionDiagnosticIdentity.From)) &&
            retained.All(item => replacement.Any(next =>
                RuleExecutionDiagnosticIdentity.From(item) == RuleExecutionDiagnosticIdentity.From(next) && item.Detail == next.Detail)))
        {
            return false;
        }

        Items.RemoveAll(item => item.RuleId == ruleId && item.ActionId == actionId && IsTextDiagnostic(item.Code));
        Omitted.RemoveWhere(item => item.RuleId == ruleId && item.ActionId == actionId && IsTextDiagnostic(item.Code));
        foreach (var item in replacement)
        {
            Add(item);
        }
        return true;
    }

    public static string? SafeDetail(string? detail, ILogger logger)
    {
        if (detail is not { Length: > RuleExecutionDiagnostic.MaximumDetailLength })
        {
            return detail;
        }

        logger.LogWarning("Rule execution diagnostic detail exceeded the API limit: {Detail}", detail);
        return "Diagnostic detail exceeded the API limit; see processor logs.";
    }

    public RuleExecutionDiagnostic[] Project(DateTimeOffset observedAtUtc)
    {
        var projected = new List<RuleExecutionDiagnostic>();
        if (Waiting is not null)
        {
            projected.Add(Waiting);
        }
        projected.AddRange(Items);

        if (Omitted.Count == 0 && projected.Count <= RuleExecutionDiagnostic.MaximumCount)
        {
            return projected.ToArray();
        }

        var visible = projected.Take(RuleExecutionDiagnostic.MaximumCount - 1).ToList();
        var omittedCount = Omitted.Count + projected.Count - visible.Count;
        visible.Add(new()
        {
            Code = RuleExecutionDiagnosticCode.DiagnosticsOmitted,
            Detail = $"{omittedCount} additional execution diagnostics were omitted.",
            OmittedCount = omittedCount,
            ObservedAtUtc = observedAtUtc
        });
        return visible.ToArray();
    }
}
