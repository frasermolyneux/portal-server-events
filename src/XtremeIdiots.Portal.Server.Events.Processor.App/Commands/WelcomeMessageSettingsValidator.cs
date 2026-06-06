namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class WelcomeMessageSettingsValidator
{
    public WelcomeMessageSettingsValidationResult Validate(WelcomeMessageSettingsDocument? document)
    {
        var result = new WelcomeMessageSettingsValidationResult();
        if (document is null)
        {
            return result;
        }

        if (document.SchemaVersion != WelcomeMessageSettingsConstants.SupportedSchemaVersion)
        {
            result.Errors.Add($"Unsupported schemaVersion '{document.SchemaVersion}'.");
            return result;
        }

        ValidateDefaults(document.Defaults, result);
        ValidateRules(document.Rules, result, "rules");
        ValidateRuleOverrides(document.RuleOverrides, result);

        return result;
    }

    private static void ValidateDefaults(WelcomeMessageDefaults? defaults, WelcomeMessageSettingsValidationResult result)
    {
        if (defaults is null)
        {
            return;
        }

        if (defaults.ConnectionDelaySeconds is int delay)
        {
            ValidateRange(delay,
                WelcomeMessageSettingsConstants.MinConnectionDelaySeconds,
                WelcomeMessageSettingsConstants.MaxConnectionDelaySeconds,
                "defaults.connectionDelaySeconds",
                result);
        }

        if (defaults.StaleThresholdSeconds is int staleThreshold)
        {
            ValidateRange(staleThreshold,
                WelcomeMessageSettingsConstants.MinStaleThresholdSeconds,
                WelcomeMessageSettingsConstants.MaxStaleThresholdSeconds,
                "defaults.staleThresholdSeconds",
                result);
        }
    }

    private static void ValidateRules(
        IReadOnlyList<WelcomeMessageRule>? rules,
        WelcomeMessageSettingsValidationResult result,
        string pathPrefix)
    {
        if (rules is null)
        {
            return;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var path = $"{pathPrefix}[{i}]";

            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                result.Errors.Add($"{path}.id is required.");
            }
            else if (!seenIds.Add(rule.Id.Trim()))
            {
                result.Errors.Add($"{path}.id '{rule.Id}' must be unique.");
            }

            if (rule.Priority is int priority)
            {
                ValidateRange(priority,
                    WelcomeMessageSettingsConstants.MinPriority,
                    WelcomeMessageSettingsConstants.MaxPriority,
                    $"{path}.priority",
                    result);
            }

            if (string.IsNullOrWhiteSpace(rule.MessageTemplate))
            {
                result.Errors.Add($"{path}.messageTemplate is required.");
            }
            else if (rule.MessageTemplate.Trim().Length > WelcomeMessageSettingsConstants.MaxMessageTemplateLength)
            {
                result.Errors.Add($"{path}.messageTemplate must be <= {WelcomeMessageSettingsConstants.MaxMessageTemplateLength} characters.");
            }

            if (rule.RequiredTags.Length > WelcomeMessageSettingsConstants.MaxRequiredTags)
            {
                result.Errors.Add($"{path}.requiredTags supports at most {WelcomeMessageSettingsConstants.MaxRequiredTags} tags.");
            }

            for (var tagIndex = 0; tagIndex < rule.RequiredTags.Length; tagIndex++)
            {
                if (string.IsNullOrWhiteSpace(rule.RequiredTags[tagIndex]))
                {
                    result.Errors.Add($"{path}.requiredTags[{tagIndex}] cannot be empty.");
                }
            }

            if (rule.ConnectionDelaySeconds is int delay)
            {
                ValidateRange(delay,
                    WelcomeMessageSettingsConstants.MinConnectionDelaySeconds,
                    WelcomeMessageSettingsConstants.MaxConnectionDelaySeconds,
                    $"{path}.connectionDelaySeconds",
                    result);
            }
        }
    }

    private static void ValidateRuleOverrides(
        IReadOnlyList<WelcomeMessageRuleOverride>? overrides,
        WelcomeMessageSettingsValidationResult result)
    {
        if (overrides is null)
        {
            return;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < overrides.Count; i++)
        {
            var overrideRule = overrides[i];
            var path = $"ruleOverrides[{i}]";

            if (string.IsNullOrWhiteSpace(overrideRule.Id))
            {
                result.Errors.Add($"{path}.id is required.");
                continue;
            }

            if (!seenIds.Add(overrideRule.Id.Trim()))
            {
                result.Errors.Add($"{path}.id '{overrideRule.Id}' must be unique within ruleOverrides.");
            }

            if (overrideRule.Priority is int priority)
            {
                ValidateRange(priority,
                    WelcomeMessageSettingsConstants.MinPriority,
                    WelcomeMessageSettingsConstants.MaxPriority,
                    $"{path}.priority",
                    result);
            }

            if (overrideRule.MessageTemplate is not null &&
                overrideRule.MessageTemplate.Trim().Length > WelcomeMessageSettingsConstants.MaxMessageTemplateLength)
            {
                result.Errors.Add($"{path}.messageTemplate must be <= {WelcomeMessageSettingsConstants.MaxMessageTemplateLength} characters.");
            }

            if (overrideRule.RequiredTags is not null)
            {
                if (overrideRule.RequiredTags.Length > WelcomeMessageSettingsConstants.MaxRequiredTags)
                {
                    result.Errors.Add($"{path}.requiredTags supports at most {WelcomeMessageSettingsConstants.MaxRequiredTags} tags.");
                }

                for (var tagIndex = 0; tagIndex < overrideRule.RequiredTags.Length; tagIndex++)
                {
                    if (string.IsNullOrWhiteSpace(overrideRule.RequiredTags[tagIndex]))
                    {
                        result.Errors.Add($"{path}.requiredTags[{tagIndex}] cannot be empty.");
                    }
                }
            }

            if (overrideRule.ConnectionDelaySeconds is int delay)
            {
                ValidateRange(delay,
                    WelcomeMessageSettingsConstants.MinConnectionDelaySeconds,
                    WelcomeMessageSettingsConstants.MaxConnectionDelaySeconds,
                    $"{path}.connectionDelaySeconds",
                    result);
            }
        }
    }

    private static void ValidateRange(
        int value,
        int min,
        int max,
        string path,
        WelcomeMessageSettingsValidationResult result)
    {
        if (value < min || value > max)
        {
            result.Errors.Add($"{path} must be between {min} and {max}.");
        }
    }
}
