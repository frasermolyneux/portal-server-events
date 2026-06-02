namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface IChatCommandCatalog
{
    Task<IReadOnlyList<ChatCommandDefinition>> GetAvailableCommandsAsync(CommandContext context, CancellationToken ct = default);
}
