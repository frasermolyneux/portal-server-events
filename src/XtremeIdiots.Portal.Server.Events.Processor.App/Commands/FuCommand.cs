using System.Text.Json;

using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class FuCommand : IChatCommand
{
    private static readonly ChatCommandDescriptor Descriptor = ChatCommandDescriptorCatalog.Fu;

    private readonly IChatCommandSettingsProvider _settingsProvider;
    private readonly FuMessageTemplateRenderer _messageTemplateRenderer;
    private readonly IServersApiClient _serversClient;
    private readonly IRepositoryApiClient _repositoryClient;
    private readonly IRconResponseService _rconResponseService;
    private readonly ILogger<FuCommand> _logger;

    public FuCommand(
        IChatCommandSettingsProvider settingsProvider,
        FuMessageTemplateRenderer messageTemplateRenderer,
        IServersApiClient serversClient,
        IRepositoryApiClient repositoryClient,
        IRconResponseService rconResponseService,
        ILogger<FuCommand> logger)
    {
        _settingsProvider = settingsProvider;
        _messageTemplateRenderer = messageTemplateRenderer;
        _serversClient = serversClient;
        _repositoryClient = repositoryClient;
        _rconResponseService = rconResponseService;
        _logger = logger;
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
        string playerQuery;
        var parsed = context.ParsedCommand;
        if (parsed is not null)
        {
            if (!parsed.PrefixToken.Equals(Prefix, StringComparison.OrdinalIgnoreCase) ||
                parsed.Arguments.Count < 1)
            {
                return await FailAsync(context, $"Usage: {Descriptor.Usage}", ct).ConfigureAwait(false);
            }

            playerQuery = string.Join(' ', parsed.Arguments);
        }
        else
        {
            var messageParts = context.Message
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (messageParts.Length < 2 || !messageParts[0].Equals(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return await FailAsync(context, $"Usage: {Descriptor.Usage}", ct).ConfigureAwait(false);
            }

            playerQuery = string.Join(' ', messageParts.Skip(1));
        }

        var commandSettings = await _settingsProvider
            .GetEffectiveSettingsAsync(context.ServerId, Descriptor.Name, Descriptor.IsMutating, ct)
            .ConfigureAwait(false);

        var effectiveMessages = ResolveMessagesFromSettings(commandSettings.Settings);
        if (effectiveMessages.Count == 0)
        {
            return CommandResult.NotHandled;
        }

        MX.Api.Abstractions.ApiResult<ResolvePlayerResponseDto> resolveResult;
        try
        {
            resolveResult = await _serversClient.Rcon.V1
                .ResolvePlayer(context.ServerId, new ResolvePlayerRequestDto
                {
                    PlayerQuery = playerQuery,
                    MaxSuggestions = 3
                }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ResolvePlayer failed for server {ServerId} and query {PlayerQuery}", context.ServerId, playerQuery);
            return await FailAsync(context, "Unable to resolve player right now. Please try again.", ct).ConfigureAwait(false);
        }

        if (!resolveResult.IsSuccess || resolveResult.Result?.Data is null)
        {
            return await FailAsync(context, "Unable to resolve player right now. Please try again.", ct).ConfigureAwait(false);
        }

        var resolution = resolveResult.Result.Data;
        if (resolution.Status == ResolvePlayerStatus.NotFound)
        {
            return await FailAsync(context, "No player found.", ct).ConfigureAwait(false);
        }

        if (resolution.Status == ResolvePlayerStatus.Ambiguous)
        {
            return await FailAsync(context, BuildAmbiguousMessage(resolution.Suggestions), ct).ConfigureAwait(false);
        }

        var resolvedName = resolution.ResolvedPlayer?.Name;
        if (string.IsNullOrWhiteSpace(resolvedName))
        {
            return await FailAsync(context, "Unable to resolve player right now. Please try again.", ct).ConfigureAwait(false);
        }

        var template = effectiveMessages[Random.Shared.Next(effectiveMessages.Count)];
        var renderedMessage = _messageTemplateRenderer.Render(template, resolvedName);

        var prefix = await AgentNamePrefixResolver.ResolveAsync(_repositoryClient, _logger, context.ServerId, ct).ConfigureAwait(false);
        var response = BuildPrefixedMessage(prefix, renderedMessage);

        var saySent = await _rconResponseService
            .TrySayAsync(context.ServerId, response, context.EventGeneratedUtc, ct)
            .ConfigureAwait(false);

        if (!saySent)
        {
            return await FailAsync(context, "Unable to send !fu response right now. Please try again.", ct).ConfigureAwait(false);
        }

        return CommandResult.Ok(response);
    }

    private static string BuildAmbiguousMessage(IList<ResolvePlayerSuggestionDto>? suggestions)
    {
        if (suggestions is null || suggestions.Count == 0)
        {
            return "No exact player found. Please be more specific.";
        }

        var renderedSuggestions = suggestions
            .Select(x => $"{x.Name} (slot {x.Slot})");

        return $"No exact player found. Did you mean: {string.Join(", ", renderedSuggestions)}";
    }

    private async Task<CommandResult> FailAsync(CommandContext context, string reason, CancellationToken ct)
    {
        var sent = await _rconResponseService.TryTellAsync(
            context.ServerId,
            context.PlayerGuid,
            context.SlotId,
            reason,
            context.Username,
            context.EventGeneratedUtc,
            ct).ConfigureAwait(false);

        if (!sent)
        {
            _logger.LogWarning(
                "Private fu response not delivered for {Username} on server {ServerId} (player {PlayerGuid}, slot {SlotId})",
                context.Username,
                context.ServerId,
                context.PlayerGuid,
                context.SlotId);
        }

        return CommandResult.Failed(reason);
    }

    private static string BuildPrefixedMessage(string prefix, string message)
    {
        var trimmedPrefix = prefix?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(trimmedPrefix)
            ? message
            : $"{trimmedPrefix} {message}";
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
