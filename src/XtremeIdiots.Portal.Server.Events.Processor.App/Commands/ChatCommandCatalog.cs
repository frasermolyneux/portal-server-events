using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class ChatCommandCatalog : IChatCommandCatalog
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IFuMessageSettingsProvider _fuMessageSettingsProvider;
    private readonly IChatCommandSettingsProvider _settingsProvider;
    private readonly ICommandAuthorizationService _authorizationService;
    private readonly ILogger<ChatCommandCatalog> _logger;

    public ChatCommandCatalog(
        IServiceProvider serviceProvider,
        IFuMessageSettingsProvider fuMessageSettingsProvider,
        IChatCommandSettingsProvider settingsProvider,
        ICommandAuthorizationService authorizationService,
        ILogger<ChatCommandCatalog> logger)
    {
        _serviceProvider = serviceProvider;
        _fuMessageSettingsProvider = fuMessageSettingsProvider;
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

            if (!await IsFeatureEnabledAsync(command.FeatureFlag, context.ServerId, ct).ConfigureAwait(false))
            {
                continue;
            }

            var authorizationResult = await _authorizationService.AuthorizeAsync(new CommandAuthorizationContext
            {
                CommandPrefix = command.Prefix,
                RequiredPolicy = command.RequiredPolicy,
                RequiredTags = commandSettings.RequiredTags,
                RequiredClaims = commandSettings.RequiredClaims,
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

    private async Task<bool> IsFeatureEnabledAsync(string? featureFlag, Guid serverId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(featureFlag))
        {
            return true;
        }

        if (string.Equals(featureFlag, "fu", StringComparison.OrdinalIgnoreCase))
        {
            return await _fuMessageSettingsProvider.IsEnabledAsync(serverId, ct).ConfigureAwait(false);
        }

        _logger.LogWarning("Unknown chat command feature flag {FeatureFlag} for server {ServerId}; command will be hidden.", featureFlag, serverId);
        return false;
    }
}
