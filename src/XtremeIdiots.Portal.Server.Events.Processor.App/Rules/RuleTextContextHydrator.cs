using System.Text.Json;

using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal interface IRuleTextContextHydrator
{
    Task<RuleTextValues> HydrateAsync(
        RuleInvocation invocation, IReadOnlySet<RuleTextTokenSource> sources, CancellationToken ct);
}

internal sealed class RuleTextContextHydrator(
    IRepositoryApiClient repository,
    IServersApiClient servers,
    ILogger<RuleTextContextHydrator> logger,
    TimeProvider? timeProvider = null) : IRuleTextContextHydrator
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<RuleTextValues> HydrateAsync(
        RuleInvocation invocation, IReadOnlySet<RuleTextTokenSource> sources, CancellationToken ct)
    {
        var tasks = sources.Select(source =>
        {
            var lookupSource = source == RuleTextTokenSource.PlayerTags ? RuleTextTokenSource.PlayerProfile : source;
            return invocation.TextLookups.GetOrAdd(lookupSource,
                key => new(() => LookupAsync(key, invocation, ct))).Value;
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var values = tasks.Select(task => task.Result).Distinct().ToArray();
        var connection = values.FirstOrDefault(value => value.Source == RuleTextTokenSource.Connection)?.Values.Player;
        var profile = values.FirstOrDefault(value => value.Source == RuleTextTokenSource.PlayerProfile)?.Values.Player;
        var intelligence = values.FirstOrDefault(value => value.Source == RuleTextTokenSource.IpIntelligence)?.Values.Player;
        var metadata = values.FirstOrDefault(value => value.Source == RuleTextTokenSource.ServerMetadata)?.Values.Server;
        var status = values.FirstOrDefault(value => value.Source == RuleTextTokenSource.ServerStatus)?.Values.Server;
        return new()
        {
            Player = new()
            {
                Name = connection?.Name,
                Guid = connection?.Guid,
                IpAddress = connection?.IpAddress,
                SteamId = profile?.SteamId,
                FirstSeenUtc = profile?.FirstSeenUtc,
                ProfileAgeDays = profile?.ProfileAgeDays,
                Tags = profile?.Tags,
                Geo = intelligence?.Geo,
                Network = intelligence?.Network
            },
            Server = new()
            {
                Name = metadata?.Name,
                GameType = metadata?.GameType,
                CurrentMap = status?.CurrentMap,
                CurrentPlayerCount = status?.CurrentPlayerCount,
                MaxPlayerCount = status?.MaxPlayerCount
            },
            SourceDiagnostics = values.SelectMany(value => value.Values.SourceDiagnostics)
                .ToDictionary(item => item.Key, item => item.Value)
        };
    }

    private async Task<RuleTextSourceValues> LookupAsync(RuleTextTokenSource source, RuleInvocation invocation, CancellationToken ct)
    {
        try
        {
            return await LookupCoreAsync(source, invocation, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return Unavailable(source, invocation, ex);
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            return Unavailable(source, invocation, ex);
        }
        catch (TimeoutException ex) when (!ct.IsCancellationRequested)
        {
            return Unavailable(source, invocation, ex);
        }
    }

    private RuleTextSourceValues Unavailable(RuleTextTokenSource source, RuleInvocation invocation, Exception exception)
    {
        logger.LogWarning(exception, "Rule token source {Source} lookup failed for connection {ConnectionId}",
            source, invocation.Trigger.ConnectionId);
        var diagnostics = new Dictionary<RuleTextTokenSource, string> { [source] = "The source lookup failed." };
        if (source == RuleTextTokenSource.PlayerProfile)
        {
            diagnostics[RuleTextTokenSource.PlayerTags] = diagnostics[source];
        }
        return new(source, new() { SourceDiagnostics = diagnostics });
    }

    private async Task<RuleTextSourceValues> LookupCoreAsync(RuleTextTokenSource source, RuleInvocation invocation, CancellationToken ct)
    {
        var trigger = invocation.Trigger;
        var diagnostics = new Dictionary<RuleTextTokenSource, string>();
        RuleTextPlayerValues? player = null;
        RuleTextServerValues? server = null;
        switch (source)
        {
            case RuleTextTokenSource.Connection:
                player = new()
                {
                    Name = await CurrentNameAsync(invocation, ct).ConfigureAwait(false),
                    Guid = trigger.PlayerGuid,
                    IpAddress = trigger.PlayerIpAddress
                };
                if (player.Name is null)
                {
                    diagnostics[source] = "A current matching player name is unavailable.";
                }
                break;
            case RuleTextTokenSource.PlayerProfile:
                var profile = await repository.Players.V1.GetPlayerByGameType(
                    trigger.GameType, trigger.PlayerGuid!, PlayerEntityOptions.Tags).WaitAsync(ct).ConfigureAwait(false);
                if (profile.IsSuccess && profile.Result?.Data is { } data &&
                    data.GameType == trigger.GameType &&
                    string.Equals(data.Guid, trigger.PlayerGuid, StringComparison.OrdinalIgnoreCase))
                {
                    var firstSeen = data.FirstSeen == default ? (DateTimeOffset?)null :
                        new DateTimeOffset(DateTime.SpecifyKind(data.FirstSeen, DateTimeKind.Utc));
                    player = new()
                    {
                        SteamId = data.SteamId,
                        FirstSeenUtc = firstSeen,
                        ProfileAgeDays = firstSeen is { } first && first <= clock.GetUtcNow()
                            ? (int)(clock.GetUtcNow() - first).TotalDays : null,
                        Tags = data.Tags.Any(item => string.IsNullOrWhiteSpace(item.Tag?.Name)) ? null :
                            data.Tags.Select(item => item.Tag!.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    };
                    if (player.Tags is null)
                    {
                        diagnostics[RuleTextTokenSource.PlayerTags] = "One or more authoritative tag labels are unavailable.";
                    }
                }
                else
                {
                    diagnostics[source] = $"Player display facts unavailable: {profile.StatusCode}.";
                    diagnostics[RuleTextTokenSource.PlayerTags] = diagnostics[source];
                }
                break;
            case RuleTextTokenSource.IpIntelligence:
                var intelligence = invocation.Intelligence ?? (invocation.IntelligenceLookup is { } lookup
                    ? await lookup.Value.ConfigureAwait(false) : null);
                if (intelligence is null)
                {
                    diagnostics[source] = "Intelligence for this connection's IP is unavailable.";
                }
                else
                {
                    player = new()
                    {
                        Geo = new()
                        {
                            CountryName = intelligence.CountryName,
                            CountryCode = intelligence.CountryCode,
                            CityName = intelligence.CityName
                        },
                        Network = new()
                        {
                            ProxyCheckRiskScore = intelligence.ProxyCheck?.RiskScore,
                            ProxyCheckIsVpn = intelligence.ProxyCheck?.IsVpn,
                            ProxyCheckIsProxy = intelligence.ProxyCheck?.IsProxy,
                            ProxyCheckAsNumber = intelligence.ProxyCheck?.AsNumber,
                            ProxyCheckAsOrganization = intelligence.ProxyCheck?.AsOrganization,
                            MaxMindIsAnonymousVpn = intelligence.Anonymizer?.IsAnonymousVpn,
                            MaxMindProviderName = intelligence.Anonymizer?.ProviderName
                        }
                    };
                }
                break;
            case RuleTextTokenSource.ServerMetadata:
                var metadata = await repository.GameServers.V1.GetGameServer(trigger.GameServerId).WaitAsync(ct).ConfigureAwait(false);
                if (metadata.IsSuccess && metadata.Result?.Data is { } gameServer &&
                    gameServer.GameServerId == trigger.GameServerId)
                {
                    server = new() { Name = gameServer.Title, GameType = gameServer.GameType.ToString() };
                }
                else
                {
                    diagnostics[source] = $"Server display facts unavailable: {metadata.StatusCode}.";
                }
                break;
            case RuleTextTokenSource.ServerStatus:
                var status = await servers.Query.V1.GetServerStatus(trigger.GameServerId).WaitAsync(ct).ConfigureAwait(false);
                if (status.IsSuccess && status.Result?.Data is { } observed)
                {
                    server = new()
                    {
                        CurrentMap = observed.Map,
                        CurrentPlayerCount = observed.PlayerCount >= 0 ? observed.PlayerCount : null,
                        MaxPlayerCount = observed.MaxPlayers > 0 ? observed.MaxPlayers : null
                    };
                }
                else
                {
                    diagnostics[source] = $"Fresh server display facts unavailable: {status.StatusCode}.";
                }
                break;
        }
        foreach (var diagnostic in diagnostics)
        {
            logger.LogWarning("Rule token source {Source} unavailable for connection {ConnectionId}: {Detail}",
                diagnostic.Key, trigger.ConnectionId, diagnostic.Value);
        }
        return new(source, new() { Player = player, Server = server, SourceDiagnostics = diagnostics });
    }

    private async Task<string?> CurrentNameAsync(RuleInvocation invocation, CancellationToken ct)
    {
        var trigger = invocation.Trigger;
        if (trigger.GameType == GameType.CallOfDuty4x)
        {
            var response = await servers.CoD4xRcon.V1.Status(trigger.GameServerId, ct).ConfigureAwait(false);
            var player = response.IsSuccess ? response.Result?.Data?.Players.FirstOrDefault(
                item => item.Num == trigger.SlotId &&
                    string.Equals(item.PlayerIdentifier, trigger.PlayerGuid, StringComparison.OrdinalIgnoreCase)) : null;
            return string.IsNullOrWhiteSpace(player?.Name) ? player?.RawName : player.Name;
        }
        var status = trigger.GameType switch
        {
            GameType.CallOfDuty2 => await servers.Cod2Rcon.V1.Status(trigger.GameServerId, ct).ConfigureAwait(false),
            GameType.CallOfDuty4 => await servers.Cod4Rcon.V1.Status(trigger.GameServerId, ct).ConfigureAwait(false),
            GameType.CallOfDuty5 => await servers.Cod5Rcon.V1.Status(trigger.GameServerId, ct).ConfigureAwait(false),
            _ => null
        };
        return status?.IsSuccess == true ? status.Result?.Data?.Players.FirstOrDefault(
            item => item.Num == trigger.SlotId &&
                string.Equals(item.Guid, trigger.PlayerGuid, StringComparison.OrdinalIgnoreCase))?.Name : null;
    }
}

internal sealed record RuleTextSourceValues(RuleTextTokenSource Source, RuleTextValues Values);

internal interface IRuleActionTextPreparer
{
    Task<RuleActionDefinition> PrepareAsync(
        RuleActionDefinition action, RuleInvocation invocation, DateTimeOffset occurredAtUtc,
        string featureId, string ruleId, CancellationToken ct);
}

internal sealed class RuleActionTextPreparer(
    IRuleTextContextHydrator context, ILogger<RuleActionTextPreparer> logger) : IRuleActionTextPreparer
{
    public async Task<RuleActionDefinition> PrepareAsync(
        RuleActionDefinition action, RuleInvocation invocation, DateTimeOffset occurredAtUtc,
        string featureId, string ruleId, CancellationToken ct)
    {
        if (action.TemplateParameters.Count == 0)
        {
            return action;
        }
        var templates = action.TemplateParameters.SelectMany(parameter =>
            action.Parameters[parameter].ValueKind == JsonValueKind.Array
                ? action.Parameters[parameter].EnumerateArray().Select(value => value.GetString()!)
                : [action.Parameters[parameter].GetString()!]).ToArray();
        var parsed = templates.Select(RuleTextRenderer.Parse).ToArray();
        if (parsed.Any(item => !item.IsValid))
        {
            throw new InvalidOperationException("Invalid action template: " +
                string.Join("; ", parsed.SelectMany(item => item.Errors).Select(error => error.Message)));
        }
        var values = await context.HydrateAsync(invocation,
            parsed.SelectMany(item => item.RequiredSources).ToHashSet(), ct).ConfigureAwait(false);
        values = values with { TriggerOccurredAtUtc = occurredAtUtc, FeatureId = featureId, RuleId = ruleId };
        var parameters = new Dictionary<string, JsonElement>(action.Parameters, StringComparer.Ordinal);
        foreach (var parameter in action.TemplateParameters)
        {
            var value = parameters[parameter];
            parameters[parameter] = value.ValueKind == JsonValueKind.Array
                ? JsonSerializer.SerializeToElement(value.EnumerateArray().Select(item => Render(item.GetString()!)).ToArray())
                : JsonSerializer.SerializeToElement(Render(value.GetString()!));
        }
        return new()
        {
            Id = action.Id,
            TypeId = action.TypeId,
            DependsOnActionIds = action.DependsOnActionIds,
            Parameters = parameters
        };

        string Render(string template)
        {
            var result = RuleTextRenderer.Render(template, values);
            if (!result.IsValid || result.Text is null)
            {
                throw new InvalidOperationException("Action rendering failed: " +
                    string.Join("; ", result.Errors.Select(error => error.Message)));
            }
            foreach (var diagnostic in result.Diagnostics)
            {
                logger.LogWarning("Rule token {Token} used fallback for connection {ConnectionId}: {Detail}",
                    diagnostic.TokenKey, invocation.Trigger.ConnectionId, diagnostic.Message);
            }
            return result.Text;
        }
    }
}
