using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Services;

public interface IAdminActionTopics
{
    Task<int> CreateTopicForAdminAction(AdminActionType type, GameType gameType, Guid playerId, string username, DateTime created, string text, string? adminId, CancellationToken ct = default);
}
