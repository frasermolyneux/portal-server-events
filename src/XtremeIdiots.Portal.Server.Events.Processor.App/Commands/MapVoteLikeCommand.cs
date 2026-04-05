using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class MapVoteLikeCommand : MapVoteCommandBase
{
    public MapVoteLikeCommand(
        IRepositoryApiClient repositoryClient,
        IServersApiClient serversClient,
        IRconResponseService rconService,
        ILogger<MapVoteLikeCommand> logger)
        : base(repositoryClient, serversClient, rconService, logger) { }

    public override string Prefix => "!like";
    protected override bool IsLike => true;
    protected override string FormatRconMessage(string username) =>
        $"^2{username} ^7voted to ^2LIKE ^7the current map";
}
