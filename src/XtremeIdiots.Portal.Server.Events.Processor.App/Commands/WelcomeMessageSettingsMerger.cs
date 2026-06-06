namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class WelcomeMessageSettingsMerger
{
    public EffectiveWelcomeMessageSettings Merge(
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

        var mergedRules = new List<EffectiveWelcomeMessageRule>();
        var rulesById = new Dictionary<string, EffectiveWelcomeMessageRule>(StringComparer.OrdinalIgnoreCase);

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

        foreach (var overrideRule in serverDocument?.RuleOverrides ?? [])
        {
            if (string.IsNullOrWhiteSpace(overrideRule.Id))
            {
                continue;
            }

            if (!rulesById.TryGetValue(overrideRule.Id.Trim(), out var existing))
            {
                continue;
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

        foreach (var serverRule in serverDocument?.Rules ?? [])
        {
            var ruleId = serverRule.Id.Trim();
            if (rulesById.ContainsKey(ruleId))
            {
                continue;
            }

            var effective = ToEffectiveRule(serverRule, defaultConnectionDelaySeconds, mergedRules.Count);
            rulesById[effective.Id] = effective;
            mergedRules.Add(effective);
        }

        return new EffectiveWelcomeMessageSettings
        {
            Enabled = enabled,
            CountryFallback = countryFallback,
            StaleThresholdSeconds = staleThresholdSeconds,
            Rules = mergedRules
        };
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
