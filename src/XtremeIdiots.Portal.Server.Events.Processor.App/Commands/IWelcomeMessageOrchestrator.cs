using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public interface IWelcomeMessageOrchestrator
{
    Task ProcessAsync(
        PlayerConnectedEvent playerEvent,
        GameType gameType,
        string[] playerTags,
        string? country,
        CancellationToken ct = default);
}
