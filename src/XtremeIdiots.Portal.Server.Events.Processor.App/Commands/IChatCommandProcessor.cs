namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

/// <summary>
/// Orchestrates command detection and execution across all registered IChatCommand implementations.
/// </summary>
public interface IChatCommandProcessor
{
    /// <summary>
    /// Process a chat message through all registered commands.
    /// Returns the result of the first command that handles it, or NotHandled if no command matches.
    /// </summary>
    Task<CommandResult> ProcessAsync(CommandContext context, CancellationToken ct = default);
}
