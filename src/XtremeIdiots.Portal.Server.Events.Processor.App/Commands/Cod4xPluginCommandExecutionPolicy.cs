using System.Text.RegularExpressions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xCommands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class Cod4xPluginCommandExecutionPolicy(
    IRepositoryApiClient repositoryApiClient,
    IServersApiClient serversApiClient,
    IMemoryCache cache,
    ICommandParser commandParser,
    ILogger<Cod4xPluginCommandExecutionPolicy> logger) : ICod4xPluginCommandExecutionPolicy
{
    private const string CacheKeyPrefix = "cod4x-enabled-chat-commands-";
    private static readonly TimeSpan CommandListCacheSlidingExpiration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CommandListCacheAbsoluteExpiration = TimeSpan.FromMinutes(2);
    private static readonly Regex ColorCodeRegex = new(@"\^[0-9A-Za-z]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CommandPowerLineRegex = new(
        @"^(?<command>[A-Za-z0-9_]+)\s+(?<power>\d{1,3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<bool> ShouldSkipBackendExecutionAsync(
        Guid serverId,
        string gameType,
        string message,
        CancellationToken ct = default)
    {
        if (!string.Equals(gameType, nameof(GameType.CallOfDuty4x), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pluginSourceEnabled = await Cod4xPluginSourceResolver
            .IsPluginSourceEnabledAsync(repositoryApiClient, cache, logger, serverId, ct)
            .ConfigureAwait(false);

        if (pluginSourceEnabled is false)
        {
            return false;
        }

        var parsed = commandParser.Parse(message);
        if (parsed.IsCommand is false || parsed.Command is null)
        {
            return false;
        }

        var normalizedCommand = ResolveCanonicalCommand(parsed.Command.Verb);
        if (string.IsNullOrWhiteSpace(normalizedCommand))
        {
            return false;
        }

        var enabledCommands = await GetEnabledCommandsAsync(serverId, ct).ConfigureAwait(false);
        if (enabledCommands is null)
        {
            // Fail open: if we cannot verify command ownership, let backend command processing run.
            return false;
        }

        return enabledCommands.Contains(normalizedCommand);
    }

    private async Task<HashSet<string>?> GetEnabledCommandsAsync(Guid serverId, CancellationToken ct)
    {
        var cacheKey = CacheKeyPrefix + serverId;
        if (cache.TryGetValue(cacheKey, out HashSet<string>? cached))
        {
            return cached;
        }

        MX.Api.Abstractions.ApiResult<string> commandListResult;
        try
        {
            commandListResult = await serversApiClient.CoD4xRcon.V1
                .AdminListCommands(serverId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Unable to resolve CoD4x AdminListCommands for server {ServerId}: request failed",
                serverId);
            return null;
        }

        if (commandListResult.IsSuccess is false || commandListResult.Result?.Data is null)
        {
            logger.LogWarning(
                "Unable to resolve CoD4x AdminListCommands for server {ServerId}: status {StatusCode}",
                serverId,
                commandListResult.StatusCode);
            return null;
        }

        var enabledCommands = ParseEnabledCommands(commandListResult.Result.Data);

        cache.Set(
            cacheKey,
            enabledCommands,
            new MemoryCacheEntryOptions()
                .SetSlidingExpiration(CommandListCacheSlidingExpiration)
                .SetAbsoluteExpiration(CommandListCacheAbsoluteExpiration));

        return enabledCommands;
    }

    private static HashSet<string> ParseEnabledCommands(string rawCommandList)
    {
        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var lines = rawCommandList.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var rawLine in lines)
        {
            var line = ColorCodeRegex.Replace(rawLine, string.Empty).Trim();
            if (line.Length is 0)
            {
                continue;
            }

            var match = CommandPowerLineRegex.Match(line);
            if (match.Success is false)
            {
                continue;
            }

            if (int.TryParse(match.Groups["power"].Value, out var power) is false)
            {
                continue;
            }

            if (power is <= 0 or >= Cod4xCommandSettingsConstants.MaxPower)
            {
                continue;
            }

            var command = ResolveCanonicalCommand(match.Groups["command"].Value);
            if (command.Length is 0)
            {
                continue;
            }

            commands.Add(command);
        }

        return commands;
    }

    private static string ResolveCanonicalCommand(string command)
    {
        var trimmed = command.Trim().TrimStart('!');
        if (trimmed.Length is 0)
        {
            return string.Empty;
        }

        foreach (var alias in Cod4xCommandSettingsConstants.BuiltInCommandAliases)
        {
            if (string.Equals(alias.Key, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return alias.Value;
            }
        }

        return trimmed;
    }
}
