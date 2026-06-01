namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class ChatCommandCatalog : IChatCommandCatalog
{
    private static readonly IReadOnlyList<ChatCommandDefinition> Definitions =
    [
        new() { Prefix = "!commands" },
        new() { Prefix = "!register" },
        new() { Prefix = "!like" },
        new() { Prefix = "!dislike" }
    ];

    public IReadOnlyList<ChatCommandDefinition> GetAvailableCommands(CommandContext context)
    {
        // Role-aware filtering can be added here when role resolution is available in command context.
        return Definitions;
    }
}
