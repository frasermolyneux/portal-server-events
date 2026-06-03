using Microsoft.Extensions.DependencyInjection;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class ChatCommandCatalog : IChatCommandCatalog
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IFuMessageSettingsProvider _fuMessageSettingsProvider;

    public ChatCommandCatalog(
        IServiceProvider serviceProvider,
        IFuMessageSettingsProvider fuMessageSettingsProvider)
    {
        _serviceProvider = serviceProvider;
        _fuMessageSettingsProvider = fuMessageSettingsProvider;
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
            if (!await IsFeatureEnabledAsync(command.FeatureFlag, context.ServerId, ct).ConfigureAwait(false))
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

        return true;
    }
}
