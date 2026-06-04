namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class ChatCommandSettingsValidator
{
    public ChatCommandSettingsValidationResult Validate(ChatCommandSettingsDocument? document)
    {
        var result = new ChatCommandSettingsValidationResult();
        if (document is null)
        {
            return result;
        }

        if (document.SchemaVersion != ChatCommandSettingsConstants.SupportedSchemaVersion)
        {
            result.Errors.Add($"Unsupported schemaVersion '{document.SchemaVersion}'.");
            return result;
        }

        ValidateDefaults(document.Defaults, result);

        foreach (var pair in document.Commands)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                result.Errors.Add("Command key cannot be empty.");
                continue;
            }

            if (!string.Equals(pair.Key, pair.Key.Trim(), StringComparison.Ordinal))
            {
                result.Errors.Add($"Command key '{pair.Key}' cannot contain leading or trailing whitespace.");
                continue;
            }

            if (pair.Value is null)
            {
                result.Errors.Add($"commands.{pair.Key} must be an object.");
                continue;
            }

            ValidateEntry($"commands.{pair.Key}", pair.Value, result);
        }

        return result;
    }

    private static void ValidateDefaults(ChatCommandSettingsDefaults? defaults, ChatCommandSettingsValidationResult result)
    {
        if (defaults is null)
        {
            return;
        }

        if (defaults.FreshnessSeconds is not null)
        {
            ValidateNonNegative(defaults.FreshnessSeconds.Default, "defaults.freshnessSeconds.default", result);
            ValidateNonNegative(defaults.FreshnessSeconds.ReadOnly, "defaults.freshnessSeconds.readOnly", result);
            ValidateNonNegative(defaults.FreshnessSeconds.Mutating, "defaults.freshnessSeconds.mutating", result);
        }

        ValidateStringArray(defaults.RequiredTags, "defaults.requiredTags", result);
    }

    private static void ValidateEntry(string path, ChatCommandSettingsEntry entry, ChatCommandSettingsValidationResult result)
    {
        ValidateNonNegative(entry.FreshnessSeconds, $"{path}.freshnessSeconds", result);
        ValidateStringArray(entry.RequiredTags, $"{path}.requiredTags", result);
    }

    private static void ValidateNonNegative(int? value, string path, ChatCommandSettingsValidationResult result)
    {
        if (value.HasValue && value.Value < 0)
        {
            result.Errors.Add($"{path} must be >= 0.");
        }
    }

    private static void ValidateStringArray(string[]? values, string path, ChatCommandSettingsValidationResult result)
    {
        if (values is null)
        {
            return;
        }

        for (var i = 0; i < values.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(values[i]))
            {
                result.Errors.Add($"{path}[{i}] cannot be empty.");
            }
        }
    }
}
