using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class MapVoteDislikeCommand : MapVoteCommandBase
{
    private static readonly ChatCommandDescriptor Descriptor = ChatCommandDescriptorCatalog.Dislike;

    public MapVoteDislikeCommand(
        IRepositoryApiClient repositoryClient,
        IServersApiClient serversClient,
        ICommandSafetyService commandSafetyService,
        IRconResponseService rconService,
        IAuditLogger auditLogger,
        ILogger<MapVoteDislikeCommand> logger)
        : base(repositoryClient, serversClient, commandSafetyService, rconService, auditLogger, logger) { }

    public override string Prefix => Descriptor.Prefix;
    public ChatCommandMetadata Metadata => new()
    {
        Name = Descriptor.Name,
        Prefix = Prefix,
        Usage = Descriptor.Usage,
        Description = Descriptor.Description,
        IsMutating = Descriptor.IsMutating,
        Aliases = Descriptor.Aliases
    };

    protected override bool IsLike => false;
    protected override string FormatRconMessage(string username) =>
        $"^1{username} ^7voted to ^1DISLIKE ^7the current map";
}
