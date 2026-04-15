using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class MapVoteDislikeCommand : MapVoteCommandBase
{
    public MapVoteDislikeCommand(
        IRepositoryApiClient repositoryClient,
        IServersApiClient serversClient,
        IRconResponseService rconService,
        IAuditLogger auditLogger,
        ILogger<MapVoteDislikeCommand> logger)
        : base(repositoryClient, serversClient, rconService, auditLogger, logger) { }

    public override string Prefix => "!dislike";
    protected override bool IsLike => false;
    protected override string FormatRconMessage(string username) =>
        $"^1{username} ^7voted to ^1DISLIKE ^7the current map";
}
