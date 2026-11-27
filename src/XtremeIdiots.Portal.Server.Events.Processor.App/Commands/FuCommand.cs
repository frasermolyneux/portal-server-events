using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class FuCommand : IChatCommand
{
    private static readonly ChatCommandDescriptor Descriptor = ChatCommandDescriptorCatalog.Fu;
    private const string FeatureId = "chat-commands";
    private const string RuleId = "fu";
    private const string ActionId = "success";
    private const string UnversionedRevision = "command-unversioned-v1";

    private readonly IChatCommandSettingsProvider _settingsProvider;
    private readonly FuMessageTemplateRenderer _messageTemplateRenderer;
    private readonly IServersApiClient _serversClient;
    private readonly IRepositoryApiClient _repositoryClient;
    private readonly IRconResponseService _rconResponseService;
    private readonly ILogger<FuCommand> _logger;
    private readonly IMemoryCache? _cache;

    public FuCommand(
        IChatCommandSettingsProvider settingsProvider,
        FuMessageTemplateRenderer messageTemplateRenderer,
        IServersApiClient serversClient,
        IRepositoryApiClient repositoryClient,
        IRconResponseService rconResponseService,
        ILogger<FuCommand> logger,
        IMemoryCache? cache = null)
    {
        _settingsProvider = settingsProvider;
        _messageTemplateRenderer = messageTemplateRenderer;
        _serversClient = serversClient;
        _repositoryClient = repositoryClient;
        _rconResponseService = rconResponseService;
        _logger = logger;
        _cache = cache;
    }

    public string Prefix => Descriptor.Prefix;
    public ChatCommandMetadata Metadata => new()
    {
        Name = Descriptor.Name,
        Prefix = Prefix,
        Usage = Descriptor.Usage,
        Description = Descriptor.Description,
        IsMutating = Descriptor.IsMutating,
        Aliases = Descriptor.Aliases
    };

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var commandSettings = context.EffectiveSettings ?? await _settingsProvider
            .GetEffectiveSettingsAsync(context.ServerId, Descriptor.Name, Descriptor.IsMutating, ct)
            .ConfigureAwait(false);

        string playerQuery;
        var parsed = context.ParsedCommand;
        if (parsed is not null)
        {
            if (!parsed.PrefixToken.Equals(Prefix, StringComparison.OrdinalIgnoreCase) ||
                parsed.Arguments.Count < 1)
            {
                return await FailAsync(context, commandSettings, "usage", $"Usage: {Descriptor.Usage}", ct).ConfigureAwait(false);
            }

            playerQuery = string.Join(' ', parsed.Arguments);
        }
        else
        {
            var messageParts = context.Message
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (messageParts.Length < 2 || !messageParts[0].Equals(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return await FailAsync(context, commandSettings, "usage", $"Usage: {Descriptor.Usage}", ct).ConfigureAwait(false);
            }

            playerQuery = string.Join(' ', messageParts.Skip(1));
        }

        var effectiveMessages = ResolveSuccessMessages(commandSettings);
        if (effectiveMessages.Count == 0)
        {
            return CommandResult.NotHandled;
        }

        ResolvePlayerResponseDto resolution;
        try
        {
            var players = await GetStatusPlayersAsync(context.ServerId, context.GameType, ct).ConfigureAwait(false);
            if (players is null)
            {
                return await FailAsync(context, commandSettings, "targetUnavailable", "Unable to resolve player right now. Please try again.", ct).ConfigureAwait(false);
            }

            resolution = PlayerResolutionMatcher.ResolvePlayer(players, playerQuery, maxSuggestions: 3);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ResolvePlayer failed for server {ServerId} and query {PlayerQuery}", context.ServerId, playerQuery);
            return await FailAsync(context, commandSettings, "targetUnavailable", "Unable to resolve player right now. Please try again.", ct).ConfigureAwait(false);
        }

        if (resolution.Status == ResolvePlayerStatus.NotFound)
        {
            return await FailAsync(context, commandSettings, "targetNotFound", "No player found.", ct).ConfigureAwait(false);
        }

        if (resolution.Status == ResolvePlayerStatus.Ambiguous)
        {
            var suggestions = RenderSuggestions(resolution.Suggestions);
            return await FailAsync(context, commandSettings, "targetAmbiguous",
                BuildAmbiguousMessage(suggestions), ct, suggestions).ConfigureAwait(false);
        }

        var resolvedName = resolution.ResolvedPlayer?.Name;
        if (string.IsNullOrWhiteSpace(resolvedName))
        {
            return await FailAsync(context, commandSettings, "targetUnavailable", "Unable to resolve player right now. Please try again.", ct).ConfigureAwait(false);
        }

        var template = effectiveMessages[Random.Shared.Next(effectiveMessages.Count)];
        var renderedMessage = _messageTemplateRenderer.Render(template, resolvedName);

        var prefix = await AgentNamePrefixResolver.ResolveAsync(_repositoryClient, _logger, context.ServerId, ct, _cache).ConfigureAwait(false);
        var header = BotMessageHeader.Create(prefix, privateMessage: false, useCodColorCodes: true);
        var response = header + renderedMessage;

        string? claimedEffectId = null;
        var occurrenceId = context.OccurrenceId.GetValueOrDefault();
        if (occurrenceId != Guid.Empty)
        {
            if (DateTimeOffset.UtcNow > context.ResponseDeadline)
            {
                _logger.LogInformation("Skipping stale fu reply for server {ServerId}", context.ServerId);
                return CommandResult.Failed("Command expired. Please run it again.");
            }

            var claim = await ClaimPublicReplyAsync(context, occurrenceId, ct).ConfigureAwait(false);
            if (claim.Status == PublicReplyClaimStatus.Duplicate)
            {
                return CommandResult.NotHandled;
            }
            if (claim.Status != PublicReplyClaimStatus.Acquired || claim.LogicalEffectId is null)
            {
                return CommandResult.Failed("Public response could not be authorized.");
            }
            claimedEffectId = claim.LogicalEffectId;
        }

        var outcome = await _rconResponseService
            .SayWithOutcomeAsync(context.ServerId, context.GameType, renderedMessage, header, context.ResponseDeadline, ct)
            .ConfigureAwait(false);

        if (claimedEffectId is not null)
        {
            await RecordPublicReplyOutcomeAsync(context.ServerId, occurrenceId, claimedEffectId, outcome, ct).ConfigureAwait(false);
        }

        if (outcome == RconSayOutcome.Unknown)
        {
            return CommandResult.Failed("Public response dispatch is unconfirmed; it may have been sent.");
        }
        if (outcome == RconSayOutcome.InvalidMessage)
        {
            return await FailAsync(context, commandSettings, "invalidMessage", "Response contains characters unsupported by the server.", ct).ConfigureAwait(false);
        }
        if (outcome != RconSayOutcome.Accepted)
        {
            return await FailAsync(context, commandSettings, "sendFailed", "Unable to send !fu response right now.", ct).ConfigureAwait(false);
        }

        return CommandResult.Ok(response);
    }

    private async Task<PublicReplyClaim> ClaimPublicReplyAsync(CommandContext context, Guid occurrenceId, CancellationToken ct)
    {
        if (context.PlayerId is not { } playerId || playerId == Guid.Empty ||
            !RuleOccurrenceEffectIdentity.TryCreate(context.ServerId, occurrenceId, FeatureId, RuleId, ActionId, out var effectId))
        {
            _logger.LogWarning("Cannot claim fu reply for server {ServerId}, occurrence {OccurrenceId}: player or identity is unavailable",
                context.ServerId, occurrenceId);
            return new PublicReplyClaim(PublicReplyClaimStatus.Failed, null);
        }

        var request = new ClaimRuleOccurrenceEffectDto
        {
            LogicalEffectId = effectId,
            PlayerId = playerId,
            FeatureId = FeatureId,
            RuleId = RuleId,
            RuleIndex = 0,
            ActionId = ActionId,
            ActionIndex = 0,
            ActionTypeId = "message.public",
            RuleSetRevision = UnversionedRevision,
            Mode = RuleExecutionMode.Enforce
        };

        try
        {
            var result = await _repositoryClient.RuleExecutions.V1
                .ClaimOccurrenceEffect(context.ServerId, occurrenceId, request, ct).ConfigureAwait(false);
            var data = result.Result?.Data;
            var effect = data?.Effect;
            var correlated = effect is not null && effect.GameServerId == context.ServerId &&
                effect.OccurrenceId == occurrenceId && effect.Claim == request;
            if (result.IsSuccess && result.StatusCode == HttpStatusCode.OK &&
                correlated && data is { Disposition: RuleOccurrenceEffectDisposition.AlreadyClaimed, ShouldDispatch: false })
            {
                _logger.LogInformation("Skipping already claimed fu reply for server {ServerId}, occurrence {OccurrenceId}",
                    context.ServerId, occurrenceId);
                return new PublicReplyClaim(PublicReplyClaimStatus.Duplicate, null);
            }

            if (result.IsSuccess && result.StatusCode == HttpStatusCode.Created &&
                correlated && data is { Disposition: RuleOccurrenceEffectDisposition.Acquired, ShouldDispatch: true } &&
                effect?.Outcome == RuleOccurrenceEffectOutcome.Claimed)
            {
                return new PublicReplyClaim(PublicReplyClaimStatus.Acquired, effectId);
            }

            _logger.LogWarning("Fu reply claim not acquired for server {ServerId}, occurrence {OccurrenceId}; repository status {StatusCode}",
                context.ServerId, occurrenceId, result.StatusCode);
            return new PublicReplyClaim(PublicReplyClaimStatus.Failed, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Fu reply claim outcome is unknown for server {ServerId}, occurrence {OccurrenceId}",
                context.ServerId, occurrenceId);
            return new PublicReplyClaim(PublicReplyClaimStatus.Failed, null);
        }
    }

    private async Task RecordPublicReplyOutcomeAsync(
        Guid serverId, Guid occurrenceId, string effectId, RconSayOutcome outcome, CancellationToken ct)
    {
        var recordedOutcome = outcome switch
        {
            RconSayOutcome.Accepted => RuleOccurrenceEffectOutcome.Dispatched,
            RconSayOutcome.Unknown => RuleOccurrenceEffectOutcome.OutcomeUnknown,
            _ => RuleOccurrenceEffectOutcome.Failed
        };
        try
        {
            var result = await _repositoryClient.RuleExecutions.V1.UpdateOccurrenceEffectOutcome(
                serverId, occurrenceId, effectId,
                new UpdateRuleOccurrenceEffectOutcomeDto { Outcome = recordedOutcome }, ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Result?.Data is not { } effect ||
                effect.GameServerId != serverId || effect.OccurrenceId != occurrenceId ||
                effect.Claim?.LogicalEffectId != effectId || effect.Outcome != recordedOutcome)
            {
                _logger.LogWarning("Failed to record fu reply outcome for server {ServerId}, occurrence {OccurrenceId}; repository status {StatusCode}",
                    serverId, occurrenceId, result.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Fu reply outcome could not be recorded for server {ServerId}, occurrence {OccurrenceId}",
                serverId, occurrenceId);
        }
    }

    private enum PublicReplyClaimStatus { Failed, Duplicate, Acquired }

    private sealed record PublicReplyClaim(PublicReplyClaimStatus Status, string? LogicalEffectId);

    private static string RenderSuggestions(IList<ResolvePlayerSuggestionDto>? suggestions) =>
        suggestions is null ? string.Empty : string.Join(", ", suggestions.Select(x => $"{x.Name} (slot {x.Slot})"));

    private static string BuildAmbiguousMessage(string suggestions)
    {
        if (suggestions.Length == 0)
        {
            return "No exact player found. Please be more specific.";
        }

        return $"No exact player found. Did you mean: {suggestions}";
    }

    private async Task<CommandResult> FailAsync(
        CommandContext context, EffectiveChatCommandSettings settings, string resultKey,
        string fallback, CancellationToken ct, string? suggestions = null)
    {
        var reason = SelectRequiredReply(settings, resultKey, fallback);
        if (suggestions is not null)
        {
            reason = reason.Replace("{suggestions}", suggestions, StringComparison.OrdinalIgnoreCase);
        }

        var sent = await _rconResponseService.TryTellAsync(
            context.ServerId,
            context.GameType,
            context.PlayerGuid,
            context.SlotId,
            reason,
            context.Username,
            context.ResponseDeadline,
            ct).ConfigureAwait(false);

        if (!sent)
        {
            _logger.LogWarning(
                "Private fu response dispatch not confirmed for {Username} on server {ServerId} (player {PlayerGuid}, slot {SlotId})",
                context.Username,
                context.ServerId,
                context.PlayerGuid,
                context.SlotId);
        }

        return CommandResult.Failed(reason);
    }

    private async Task<IReadOnlyList<CoD4xStatusPlayerDto>?> GetStatusPlayersAsync(Guid serverId, string gameType, CancellationToken ct)
    {
        if (!Enum.TryParse<GameType>(gameType, true, out var parsedGameType))
        {
            return null;
        }

        if (parsedGameType == GameType.CallOfDuty2)
        {
            var statusResult = await _serversClient.Cod2Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!statusResult.IsSuccess || statusResult.Result?.Data?.Players is null)
            {
                return null;
            }

            return statusResult.Result.Data.Players
                .Select(static p => new CoD4xStatusPlayerDto
                {
                    Num = p.Num,
                    PlayerIdentifier = p.Guid,
                    Name = p.Name,
                    RawName = p.Name
                })
                .ToList();
        }

        if (parsedGameType == GameType.CallOfDuty4)
        {
            var statusResult = await _serversClient.Cod4Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!statusResult.IsSuccess || statusResult.Result?.Data?.Players is null)
            {
                return null;
            }

            return statusResult.Result.Data.Players
                .Select(static p => new CoD4xStatusPlayerDto
                {
                    Num = p.Num,
                    PlayerIdentifier = p.Guid,
                    Name = p.Name,
                    RawName = p.Name
                })
                .ToList();
        }

        if (parsedGameType == GameType.CallOfDuty5)
        {
            var statusResult = await _serversClient.Cod5Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!statusResult.IsSuccess || statusResult.Result?.Data?.Players is null)
            {
                return null;
            }

            return statusResult.Result.Data.Players
                .Select(static p => new CoD4xStatusPlayerDto
                {
                    Num = p.Num,
                    PlayerIdentifier = p.Guid,
                    Name = p.Name,
                    RawName = p.Name
                })
                .ToList();
        }

        if (parsedGameType == GameType.CallOfDuty4x)
        {
            var statusResult = await _serversClient.CoD4xRcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!statusResult.IsSuccess || statusResult.Result?.Data?.Players is null)
            {
                return null;
            }

            return statusResult.Result.Data.Players.ToList();
        }

        return null;
    }

    internal static IReadOnlyList<string> ResolveSuccessMessages(EffectiveChatCommandSettings settings)
    {
        if (settings.Replies?.TryGetValue("success", out var messages) == true && messages is not null)
        {
            return messages;
        }

        return ResolveMessagesFromSettings(settings.Settings);
    }

    internal static string SelectRequiredReply(EffectiveChatCommandSettings settings, string resultKey, string fallback)
    {
        if (settings.Replies?.TryGetValue(resultKey, out var templates) == true && templates is { Length: > 0 })
        {
            return templates[Random.Shared.Next(templates.Length)];
        }

        return fallback;
    }

    internal static IReadOnlyList<string> ResolveMessagesFromSettings(JsonElement? settings)
    {
        if (!settings.HasValue || settings.Value.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (!settings.Value.TryGetProperty("messages", out var messagesElement) || messagesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var messages = new List<string>();
        foreach (var item in messagesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!item.TryGetProperty("message", out var messageElement) || messageElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var isEnabled = true;
            if (item.TryGetProperty("enabled", out var enabledElement))
            {
                if (enabledElement.ValueKind == JsonValueKind.True)
                {
                    isEnabled = true;
                }
                else if (enabledElement.ValueKind == JsonValueKind.False)
                {
                    isEnabled = false;
                }
                else
                {
                    continue;
                }
            }

            if (!isEnabled)
            {
                continue;
            }

            var template = messageElement.GetString();
            if (!string.IsNullOrWhiteSpace(template))
            {
                messages.Add(template);
            }
        }

        return messages;
    }
}
