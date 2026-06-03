namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

/// <summary>
/// A chat command that can be triggered by a player message.
/// Implement this interface and register via DI to add new commands.
/// </summary>
public interface IChatCommand
{
    /// <summary>
    /// The command prefix (e.g. "!like", "!dislike", "!help").
    /// Used as the canonical routing token in command metadata.
    /// </summary>
    string Prefix { get; }

    /// <summary>
    /// Metadata used for command discovery/help output.
    /// </summary>
    ChatCommandMetadata Metadata => new()
    {
        Name = Prefix.TrimStart('!'),
        Prefix = Prefix,
        Usage = Prefix,
        Description = string.Empty
    };

    /// <summary>
    /// Execute the command. Called after the chat message has been persisted.
    /// </summary>
    Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default);
}
