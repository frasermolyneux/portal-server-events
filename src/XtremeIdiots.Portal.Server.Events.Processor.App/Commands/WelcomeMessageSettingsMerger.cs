namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class WelcomeMessageSettingsMerger
{
    public EffectiveWelcomeMessageSettings Merge(
        WelcomeMessageSettingsDocument? globalDocument,
        WelcomeMessageSettingsDocument? serverDocument)
    {
        var enabled = ResolveEnabled(globalDocument, serverDocument);
        var (countryFallback, staleThresholdSeconds, defaultConnectionDelaySeconds) =
            ResolveDefaults(globalDocument, serverDocument);
        var mergedRules = MergeRules(globalDocument, serverDocument, defaultConnectionDelaySeconds);

        return new EffectiveWelcomeMessageSettings
        {
            Enabled = enabled,
            CountryFallback = countryFallback,
            StaleThresholdSeconds = staleThresholdSeconds,
            Rules = mergedRules
        };
    }

    private static bool ResolveEnabled(
        WelcomeMessageSettingsDocument? globalDocument,
        WelcomeMessageSettingsDocument? serverDocument)
    {
        var enabled = true;
        if (globalDocument?.Enabled is bool globalEnabled)
        {
            enabled = globalEnabled;
        }

        if (serverDocument?.Enabled is bool serverEnabled)
        {
            enabled = serverEnabled;
        }

        return enabled;
    }

    private static (string CountryFallback, int StaleThresholdSeconds, int DefaultConnectionDelaySeconds) ResolveDefaults(
        WelcomeMessageSettingsDocument? globalDocument,
        WelcomeMessageSettingsDocument? serverDocument)
    {
        var countryFallback = WelcomeMessageSettingsConstants.DefaultCountryFallback;
        if (!string.IsNullOrWhiteSpace(globalDocument?.Defaults?.CountryFallback))
        {
            countryFallback = globalDocument.Defaults.CountryFallback.Trim();
        }

        if (!string.IsNullOrWhiteSpace(serverDocument?.Defaults?.CountryFallback))
        {
            countryFallback = serverDocument.Defaults.CountryFallback.Trim();
        }

        var staleThresholdSeconds = WelcomeMessageSettingsConstants.DefaultStaleThresholdSeconds;
        if (globalDocument?.Defaults?.StaleThresholdSeconds is int globalStaleThreshold)
        {
            staleThresholdSeconds = globalStaleThreshold;
        }

        if (serverDocument?.Defaults?.StaleThresholdSeconds is int serverStaleThreshold)
        {
            staleThresholdSeconds = serverStaleThreshold;
        }

        var defaultConnectionDelaySeconds = WelcomeMessageSettingsConstants.DefaultConnectionDelaySeconds;
        if (globalDocument?.Defaults?.ConnectionDelaySeconds is int globalDelay)
        {
            defaultConnectionDelaySeconds = globalDelay;
        }

        if (serverDocument?.Defaults?.ConnectionDelaySeconds is int serverDelay)
        {
            defaultConnectionDelaySeconds = serverDelay;
        }

        return (countryFallback, staleThresholdSeconds, defaultConnectionDelaySeconds);
    }

    private static List<EffectiveWelcomeMessageRule> MergeRules(
        WelcomeMessageSettingsDocument? globalDocument,
        WelcomeMessageSettingsDocument? serverDocument,
        int defaultConnectionDelaySeconds)
    {
        var mergedRules = new List<EffectiveWelcomeMessageRule>();
        var rulesById = new Dictionary<string, EffectiveWelcomeMessageRule>(StringComparer.OrdinalIgnoreCase);

        AddGlobalRules(globalDocument, serverDocument, defaultConnectionDelaySeconds, mergedRules, rulesById);
        ApplyRuleOverrides(serverDocument, mergedRules, rulesById);
        AddServerRules(serverDocument, defaultConnectionDelaySeconds, mergedRules, rulesById);

        return mergedRules;
    }

    private static void AddGlobalRules(
        WelcomeMessageSettingsDocument? globalDocument,
        WelcomeMessageSettingsDocument? serverDocument,
        int defaultConnectionDelaySeconds,
        List<EffectiveWelcomeMessageRule> mergedRules,
        Dictionary<string, EffectiveWelcomeMessageRule> rulesById)
    {
        var inheritGlobalRules = serverDocument?.InheritGlobalRules ?? true;
        if (inheritGlobalRules)
        {
            foreach (var globalRule in globalDocument?.Rules ?? [])
            {
                var effective = ToEffectiveRule(globalRule, defaultConnectionDelaySeconds, mergedRules.Count);
                rulesById[effective.Id] = effective;
                mergedRules.Add(effective);
            }
        }
    }

    private static void ApplyRuleOverrides(
        WelcomeMessageSettingsDocument? serverDocument,
        List<EffectiveWelcomeMessageRule> mergedRules,
        Dictionary<string, EffectiveWelcomeMessageRule> rulesById)
    {
        foreach (var overrideRule in serverDocument?.RuleOverrides ?? [])
        {
            ApplyRuleOverride(overrideRule, mergedRules, rulesById);
        }
    }

    private static void ApplyRuleOverride(
        WelcomeMessageRuleOverride overrideRule,
        List<EffectiveWelcomeMessageRule> mergedRules,
        Dictionary<string, EffectiveWelcomeMessageRule> rulesById)
    {
        if (string.IsNullOrWhiteSpace(overrideRule.Id))
        {
            return;
        }

        if (!rulesById.TryGetValue(overrideRule.Id.Trim(), out var existing))
        {
            return;
        }

        var updatedRule = existing with
        {
            Enabled = overrideRule.Enabled ?? existing.Enabled,
            Priority = overrideRule.Priority ?? existing.Priority,
            Visibility = overrideRule.Visibility ?? existing.Visibility,
            MessageTemplate = string.IsNullOrWhiteSpace(overrideRule.MessageTemplate)
                ? existing.MessageTemplate
                : overrideRule.MessageTemplate.Trim(),
            RequiredTags = overrideRule.RequiredTags is null
                ? existing.RequiredTags
                : NormalizeTags(overrideRule.RequiredTags),
            ConnectionDelaySeconds = overrideRule.ConnectionDelaySeconds ?? existing.ConnectionDelaySeconds
        };

        rulesById[updatedRule.Id] = updatedRule;

        var existingIndex = mergedRules.FindIndex(r => string.Equals(r.Id, updatedRule.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            mergedRules[existingIndex] = updatedRule;
        }
    }

    private static void AddServerRules(
        WelcomeMessageSettingsDocument? serverDocument,
        int defaultConnectionDelaySeconds,
        List<EffectiveWelcomeMessageRule> mergedRules,
        Dictionary<string, EffectiveWelcomeMessageRule> rulesById)
    {
        foreach (var serverRule in serverDocument?.Rules ?? [])
        {
            AddServerRule(serverRule, defaultConnectionDelaySeconds, mergedRules, rulesById);
        }
    }

    private static void AddServerRule(
        WelcomeMessageRule serverRule,
        int defaultConnectionDelaySeconds,
        List<EffectiveWelcomeMessageRule> mergedRules,
        Dictionary<string, EffectiveWelcomeMessageRule> rulesById)
    {
        var ruleId = serverRule.Id.Trim();
        if (rulesById.ContainsKey(ruleId))
        {
            return;
        }

        var effective = ToEffectiveRule(serverRule, defaultConnectionDelaySeconds, mergedRules.Count);
        rulesById[effective.Id] = effective;
        mergedRules.Add(effective);
    }

    private static EffectiveWelcomeMessageRule ToEffectiveRule(
        WelcomeMessageRule rule,
        int defaultConnectionDelaySeconds,
        int orderIndex)
    {
        return new EffectiveWelcomeMessageRule
        {
            Id = rule.Id.Trim(),
            Enabled = rule.Enabled ?? true,
            Priority = rule.Priority ?? 0,
            Visibility = rule.Visibility ?? WelcomeMessageVisibility.Private,
            MessageTemplate = rule.MessageTemplate.Trim(),
            RequiredTags = NormalizeTags(rule.RequiredTags),
            ConnectionDelaySeconds = rule.ConnectionDelaySeconds ?? defaultConnectionDelaySeconds,
            OrderIndex = orderIndex
        };
    }

    private static string[] NormalizeTags(IEnumerable<string>? tags)
    {
        return tags is null
            ? []
            : tags
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }
}
