namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface IChatCommandCatalog
{
    IReadOnlyList<ChatCommandDefinition> GetAvailableCommands(CommandContext context);
}
