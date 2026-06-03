using System.Text.Json;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class ChatCommandSettingsMerger
{
    public EffectiveChatCommandSettings Merge(
        string commandName,
        bool isMutating,
        ChatCommandSettingsDocument? globalDocument,
        ChatCommandSettingsDocument? serverDocument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

        var normalizedCommand = commandName.Trim();

        var hardcodedEnabled = true;
        var hardcodedFreshness = isMutating
            ? ChatCommandSettingsConstants.HardcodedMutatingFreshnessSeconds
            : ChatCommandSettingsConstants.HardcodedReadOnlyFreshnessSeconds;

        var globalDefaults = globalDocument?.Defaults;
        var globalCommand = TryGetCommand(globalDocument, normalizedCommand);
        var serverCommand = TryGetCommand(serverDocument, normalizedCommand);

        var enabled = hardcodedEnabled;
        var enabledSource = SettingsValueSource.Hardcoded;

        if (globalDefaults?.Enabled is bool globalDefaultEnabled)
        {
            enabled = globalDefaultEnabled;
            enabledSource = SettingsValueSource.GlobalDefaults;
        }

        if (globalCommand?.Enabled is bool globalCommandEnabled)
        {
            enabled = globalCommandEnabled;
            enabledSource = SettingsValueSource.GlobalCommand;
        }

        if (serverCommand?.Enabled is bool serverCommandEnabled)
        {
            enabled = serverCommandEnabled;
            enabledSource = SettingsValueSource.ServerCommand;
        }

        var freshness = hardcodedFreshness;
        var freshnessSource = SettingsValueSource.Hardcoded;

        var globalDefaultFreshness = ResolveGlobalDefaultFreshness(globalDefaults, isMutating);
        if (globalDefaultFreshness.HasValue)
        {
            freshness = globalDefaultFreshness.Value;
            freshnessSource = SettingsValueSource.GlobalDefaults;
        }

        if (globalCommand?.FreshnessSeconds is int globalCommandFreshness)
        {
            freshness = globalCommandFreshness;
            freshnessSource = SettingsValueSource.GlobalCommand;
        }

        if (serverCommand?.FreshnessSeconds is int serverCommandFreshness)
        {
            freshness = serverCommandFreshness;
            freshnessSource = SettingsValueSource.ServerCommand;
        }

        var requiredTags = Array.Empty<string>();
        var requiredClaims = Array.Empty<string>();
        var authorizationSource = SettingsValueSource.Hardcoded;

        if (globalDefaults?.RequiredTags is not null || globalDefaults?.RequiredClaims is not null)
        {
            requiredTags = Normalize(globalDefaults?.RequiredTags);
            requiredClaims = Normalize(globalDefaults?.RequiredClaims);
            authorizationSource = SettingsValueSource.GlobalDefaults;
        }

        if (globalCommand?.RequiredTags is not null || globalCommand?.RequiredClaims is not null)
        {
            requiredTags = Normalize(globalCommand.RequiredTags);
            requiredClaims = Normalize(globalCommand.RequiredClaims);
            authorizationSource = SettingsValueSource.GlobalCommand;
        }

        if (serverCommand?.RequiredTags is not null || serverCommand?.RequiredClaims is not null)
        {
            requiredTags = Normalize(serverCommand.RequiredTags);
            requiredClaims = Normalize(serverCommand.RequiredClaims);
            authorizationSource = SettingsValueSource.ServerCommand;
        }

        JsonElement? payload = null;
        var payloadSource = SettingsValueSource.Hardcoded;

        if (globalCommand?.Settings is JsonElement globalPayload)
        {
            payload = globalPayload;
            payloadSource = SettingsValueSource.GlobalCommand;
        }

        if (serverCommand?.Settings is JsonElement serverPayload)
        {
            payload = serverPayload;
            payloadSource = SettingsValueSource.ServerCommand;
        }

        return new EffectiveChatCommandSettings
        {
            CommandName = normalizedCommand,
            Enabled = enabled,
            FreshnessSeconds = freshness,
            RequiredTags = requiredTags,
            RequiredClaims = requiredClaims,
            Settings = payload,
            EnabledSource = enabledSource,
            FreshnessSource = freshnessSource,
            AuthorizationSource = authorizationSource,
            PayloadSource = payloadSource
        };
    }

    private static ChatCommandSettingsEntry? TryGetCommand(ChatCommandSettingsDocument? document, string commandName)
    {
        if (document?.Commands is null)
        {
            return null;
        }

        return document.Commands.TryGetValue(commandName, out var entry) ? entry : null;
    }

    private static int? ResolveGlobalDefaultFreshness(ChatCommandSettingsDefaults? defaults, bool isMutating)
    {
        if (defaults?.FreshnessSeconds is null)
        {
            return null;
        }

        if (isMutating)
        {
            return defaults.FreshnessSeconds.Mutating
                ?? defaults.FreshnessSeconds.Default;
        }

        return defaults.FreshnessSeconds.ReadOnly
            ?? defaults.FreshnessSeconds.Default;
    }

    private static string[] Normalize(string[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return [];
        }

        return values
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
