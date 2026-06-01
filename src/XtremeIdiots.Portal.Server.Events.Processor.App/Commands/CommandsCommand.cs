using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class CommandsCommand : IChatCommand
{
    private readonly IChatCommandCatalog _catalog;
    private readonly IRconResponseService _rconResponseService;
    private readonly ILogger<CommandsCommand> _logger;

    public CommandsCommand(
        IChatCommandCatalog catalog,
        IRconResponseService rconResponseService,
        ILogger<CommandsCommand> logger)
    {
        _catalog = catalog;
        _rconResponseService = rconResponseService;
        _logger = logger;
    }

    public string Prefix => "!commands";

    public bool CanHandle(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        if (!message.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return message.Length == Prefix.Length || char.IsWhiteSpace(message[Prefix.Length]);
    }

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var parts = context.Message
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 1 || !parts[0].Equals(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return await FailAsync(context, "Usage: !commands", ct).ConfigureAwait(false);
        }

        var commands = _catalog.GetAvailableCommands(context)
            .Select(x => x.Prefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var response = commands.Length == 0
            ? "No commands are currently available."
            : $"Available commands: {string.Join(", ", commands)}";

        var sent = await TryTellAsync(context, response, ct).ConfigureAwait(false);
        if (!sent)
        {
            _logger.LogWarning(
                "Private commands response not delivered for {Username} on server {ServerId} (player {PlayerGuid}, slot {SlotId})",
                context.Username,
                context.ServerId,
                context.PlayerGuid,
                context.SlotId);
        }

        return CommandResult.Ok(response);
    }

    private async Task<CommandResult> FailAsync(CommandContext context, string reason, CancellationToken ct)
    {
        var sent = await TryTellAsync(context, reason, ct).ConfigureAwait(false);
        if (!sent)
        {
            _logger.LogWarning(
                "Private commands failure response not delivered for {Username} on server {ServerId} (player {PlayerGuid}, slot {SlotId})",
                context.Username,
                context.ServerId,
                context.PlayerGuid,
                context.SlotId);
        }

        return CommandResult.Failed(reason);
    }

    private async Task<bool> TryTellAsync(CommandContext context, string message, CancellationToken ct)
    {
        return await _rconResponseService.TryTellAsync(
            context.ServerId,
            context.PlayerGuid,
            context.SlotId,
            message,
            context.Username,
            context.EventGeneratedUtc,
            ct).ConfigureAwait(false);
    }
}
