namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

/// <summary>
/// A chat command that can be triggered by a player message.
/// Implement this interface and register via DI to add new commands.
/// </summary>
public interface IChatCommand
{
    /// <summary>
    /// The command prefix (e.g. "!like", "!dislike", "!help").
    /// Matched case-insensitively against the start of the message.
    /// </summary>
    string Prefix { get; }

    /// <summary>
    /// Whether this command should be executed for the given message.
    /// Default implementation checks if message starts with Prefix.
    /// Override for more complex matching (e.g. "!ban PlayerName").
    /// </summary>
    bool CanHandle(string message) => message.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Execute the command. Called after the chat message has been persisted.
    /// </summary>
    Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default);
}
