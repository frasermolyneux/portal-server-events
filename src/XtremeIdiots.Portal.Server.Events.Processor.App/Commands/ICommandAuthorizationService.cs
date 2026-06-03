namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface ICommandAuthorizationService
{
    Task<CommandAuthorizationResult> AuthorizeAsync(CommandAuthorizationContext context, CancellationToken ct = default);
}
