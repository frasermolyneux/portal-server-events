using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class ChatCommandCatalog : IChatCommandCatalog
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IChatCommandSettingsProvider _settingsProvider;
    private readonly ICommandAuthorizationService _authorizationService;
    private readonly ILogger<ChatCommandCatalog> _logger;

    public ChatCommandCatalog(
        IServiceProvider serviceProvider,
        IChatCommandSettingsProvider settingsProvider,
        ICommandAuthorizationService authorizationService,
        ILogger<ChatCommandCatalog> logger)
    {
        _serviceProvider = serviceProvider;
        _settingsProvider = settingsProvider;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ChatCommandDefinition>> GetAvailableCommandsAsync(CommandContext context, CancellationToken ct = default)
    {
        var metadata = _serviceProvider
            .GetServices<IChatCommand>()
            .Select(c => c.Metadata)
            .Where(c => !c.Hidden)
            .GroupBy(c => c.Prefix, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Prefix, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var enabled = new List<ChatCommandDefinition>(metadata.Length);
        foreach (var command in metadata)
        {
            var commandSettings = await _settingsProvider
                .GetEffectiveSettingsAsync(context.ServerId, command.Name, command.IsMutating, ct)
                .ConfigureAwait(false);

            if (!commandSettings.Enabled)
            {
                continue;
            }

            if (string.Equals(command.Name, "fu", StringComparison.OrdinalIgnoreCase) &&
                FuCommand.ResolveMessagesFromSettings(commandSettings.Settings).Count == 0)
            {
                continue;
            }

            if (!IsFeatureEnabled(command.FeatureFlag))
            {
                continue;
            }

            var authorizationResult = await _authorizationService.AuthorizeAsync(new CommandAuthorizationContext
            {
                CommandPrefix = command.Prefix,
                RequiredPolicy = command.RequiredPolicy,
                RequiredTags = commandSettings.RequiredTags,
                Privileged = true,
                GameType = context.GameType,
                ServerId = context.ServerId,
                PlayerId = context.PlayerId,
                Snapshot = context.AuthorizationSnapshot
            }, ct).ConfigureAwait(false);

            if (!authorizationResult.Allowed)
            {
                continue;
            }

            enabled.Add(new ChatCommandDefinition
            {
                Prefix = command.Prefix,
                Name = command.Name,
                Usage = command.Usage,
                Description = command.Description
            });
        }

        return enabled;
    }

    private bool IsFeatureEnabled(string? featureFlag)
    {
        if (string.IsNullOrWhiteSpace(featureFlag))
        {
            return true;
        }

        _logger.LogWarning("Unknown chat command feature flag {FeatureFlag}; command will be hidden.", featureFlag);
        return false;
    }
}
