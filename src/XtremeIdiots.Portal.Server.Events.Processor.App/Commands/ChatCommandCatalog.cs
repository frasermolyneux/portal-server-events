namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class ChatCommandCatalog : IChatCommandCatalog
{
    private static readonly ChatCommandDefinition FuCommand = new() { Prefix = "!fu" };

    private static readonly IReadOnlyList<ChatCommandDefinition> Definitions =
    [
        new() { Prefix = "!commands" },
        new() { Prefix = "!register" },
        new() { Prefix = "!like" },
        new() { Prefix = "!dislike" }
    ];

    private readonly IFuMessageSettingsProvider _fuMessageSettingsProvider;

    public ChatCommandCatalog(IFuMessageSettingsProvider fuMessageSettingsProvider)
    {
        _fuMessageSettingsProvider = fuMessageSettingsProvider;
    }

    public async Task<IReadOnlyList<ChatCommandDefinition>> GetAvailableCommandsAsync(CommandContext context, CancellationToken ct = default)
    {
        if (!await _fuMessageSettingsProvider.IsEnabledAsync(context.ServerId, ct).ConfigureAwait(false))
        {
            return Definitions;
        }

        var definitions = Definitions.ToList();
        definitions.Add(FuCommand);

        return definitions;
    }
}
