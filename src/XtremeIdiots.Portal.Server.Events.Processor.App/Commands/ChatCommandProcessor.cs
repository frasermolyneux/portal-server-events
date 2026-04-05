using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class ChatCommandProcessor : IChatCommandProcessor
{
    private readonly IEnumerable<IChatCommand> _commands;
    private readonly ILogger<ChatCommandProcessor> _logger;

    public ChatCommandProcessor(IEnumerable<IChatCommand> commands, ILogger<ChatCommandProcessor> logger)
    {
        _commands = commands;
        _logger = logger;
    }

    public async Task<CommandResult> ProcessAsync(CommandContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(context.Message) || !context.Message.StartsWith('!'))
            return CommandResult.NotHandled;

        foreach (var command in _commands)
        {
            if (command.CanHandle(context.Message))
            {
                _logger.LogInformation("Command {CommandPrefix} matched for player {Username} on server {ServerId}",
                    command.Prefix, context.Username, context.ServerId);

                try
                {
                    return await command.ExecuteAsync(context, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Command {CommandPrefix} failed for player {Username}",
                        command.Prefix, context.Username);
                    return CommandResult.Failed($"Command failed: {ex.Message}");
                }
            }
        }

        return CommandResult.NotHandled;
    }
}
