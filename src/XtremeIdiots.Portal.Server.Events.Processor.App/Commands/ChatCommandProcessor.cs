using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class ChatCommandProcessor : IChatCommandProcessor
{
    private readonly IReadOnlyDictionary<string, IChatCommand> _commandsByPrefix;
    private readonly ICommandParser _parser;
    private readonly ILogger<ChatCommandProcessor> _logger;

    public ChatCommandProcessor(
        IEnumerable<IChatCommand> commands,
        ICommandParser parser,
        ILogger<ChatCommandProcessor> logger)
    {
        _parser = parser;
        _logger = logger;

        var registeredCommands = commands.ToArray();
        foreach (var command in registeredCommands)
        {
            if (!string.Equals(command.Prefix, command.Metadata.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Command {command.GetType().Name} has inconsistent prefixes. Prefix='{command.Prefix}', Metadata.Prefix='{command.Metadata.Prefix}'.");
            }
        }

        _commandsByPrefix = registeredCommands
            .GroupBy(c => c.Metadata.Prefix, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key.ToLowerInvariant(), g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var duplicate in registeredCommands
                     .GroupBy(c => c.Metadata.Prefix, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            _logger.LogWarning("Duplicate chat command prefix registration detected for {Prefix}; first registration will be used.", duplicate.Key);
        }
    }

    public async Task<CommandResult> ProcessAsync(CommandContext context, CancellationToken ct = default)
    {
        var parseResult = _parser.Parse(context.Message);
        if (!parseResult.IsCommand || parseResult.Command is null)
            return CommandResult.NotHandled;

        if (!_commandsByPrefix.TryGetValue(parseResult.Command.PrefixToken, out var command))
            return CommandResult.NotHandled;

        _logger.LogInformation("Command {CommandPrefix} matched for player {Username} on server {ServerId}",
            command.Prefix, context.Username, context.ServerId);

        try
        {
            return await command.ExecuteAsync(context with { ParsedCommand = parseResult.Command }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandPrefix} failed for player {Username}",
                command.Prefix, context.Username);
            return CommandResult.Failed($"Command failed: {ex.Message}");
        }
    }
}
