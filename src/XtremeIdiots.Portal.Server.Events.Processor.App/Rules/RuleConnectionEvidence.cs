using System.Net;

using Microsoft.Extensions.Logging;

using MX.Api.Client;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal static class RuleConnectionEvidence
{
    public static async Task ObserveAsync(
        IRepositoryApiClient repository, ServerStatusEvent source, ILogger logger, CancellationToken ct)
    {
        var snapshot = Create(source.ServerId, source.GameType, source.SnapshotId, source.Sequence,
            source.ObservedAtUtc, source.Runtime, source.Players.Select(player => new RuleConnectionStatusPlayerDto
            {
                SlotId = player.SlotId,
                PlayerGuid = player.PlayerGuid,
                ConnectionId = player.ConnectionId,
                ConnectionGeneration = player.ConnectionGeneration,
                State = player.State switch
                {
                    "Connected" => RuleConnectionState.Connected,
                    "Unknown" => RuleConnectionState.Unknown,
                    _ => RuleConnectionState.Unknown
                }
            }).ToArray(), logger);
        if (snapshot is null || !await ObserveAsync(repository, snapshot, logger, ct).ConfigureAwait(false))
        {
            return;
        }

        foreach (var player in source.Players.Where(player =>
            player.State == "Connected" && player.ConnectionId is { } id && id != Guid.Empty &&
            !string.IsNullOrWhiteSpace(player.PlayerGuid)))
        {
            // Status enrichment can precede the connection queue; freeze facts before it writes observations.
            FeatureExecutionOrchestrator.Require(await repository.RuleExecutions.V1.CreateFactSnapshot(source.ServerId,
                new RuleFactSnapshotRequestDto
                {
                    ObservationId = snapshot.SnapshotId,
                    ConnectionId = player.ConnectionId!.Value,
                    GameType = snapshot.GameType,
                    PlayerGuid = player.PlayerGuid,
                    PlayerIpAddress = IpAddressGuard.IsPersistable(player.IpAddress) ? player.IpAddress : null,
                    ObservedAtUtc = snapshot.ObservedAtUtc
                }, ct).ConfigureAwait(false), "capture status pre-observation rules facts");
        }
    }

    public static async Task ObserveAsync(
        IRepositoryApiClient repository, PlayerDisconnectedEvent source, ILogger logger, CancellationToken ct)
    {
        var snapshot = Create(source.ServerId, source.GameType, source.SnapshotId, source.Sequence,
            source.ObservedAtUtc, source.Runtime,
            [
                new RuleConnectionStatusPlayerDto
                {
                    SlotId = source.SlotId,
                    PlayerGuid = source.PlayerGuid,
                    ConnectionId = source.ConnectionId,
                    ConnectionGeneration = source.ConnectionGeneration,
                    State = RuleConnectionState.Disconnected
                }
            ], logger);
        if (snapshot is not null)
        {
            await ObserveAsync(repository, snapshot, logger, ct).ConfigureAwait(false);
        }
    }

    private static RuleConnectionStatusSnapshotDto? Create(
        Guid serverId, string game, Guid? snapshotId, ulong? sequence, DateTimeOffset? observedAtUtc,
        Abstractions.V1.Events.RuleRuntimeDescriptor? sourceRuntime, RuleConnectionStatusPlayerDto[] players, ILogger logger)
    {
        if (snapshotId is null && sequence is null && observedAtUtc is null && sourceRuntime is null)
        {
            return null;
        }
        var runtime = RuleRuntimeMapping.From(sourceRuntime);
        if (snapshotId is null || snapshotId == Guid.Empty || sequence is null or > long.MaxValue ||
            observedAtUtc is null || observedAtUtc.Value == default || runtime is null ||
            runtime.IdentityModel == RuleRuntimeIdentityModel.Unknown ||
            string.IsNullOrWhiteSpace(runtime.Version) ||
            runtime.InstanceId == Guid.Empty || runtime.StartedAtUtc == default || runtime.ProtocolVersion != 1 ||
            !Enum.TryParse<GameType>(game, true, out var gameType) || gameType == GameType.Unknown || !Enum.IsDefined(gameType))
        {
            logger.LogWarning("Rules connection snapshot lacks valid producer evidence for server {ServerId}", serverId);
            return null;
        }

        return new()
        {
            GameServerId = serverId,
            GameType = gameType,
            SnapshotId = snapshotId.Value,
            SequenceId = (long)sequence.Value,
            ObservedAtUtc = observedAtUtc.Value,
            Runtime = runtime,
            IntegrationCapabilities = ["player.connection", "player.ip"],
            Players = players
        };
    }

    private static async Task<bool> ObserveAsync(
        IRepositoryApiClient repository, RuleConnectionStatusSnapshotDto snapshot, ILogger logger, CancellationToken ct)
    {
        HttpStatusCode? rejectedStatusCode;
        try
        {
            var response = await repository.RuleConnectionBindings.V1.ObserveSnapshot(
                snapshot.GameServerId, snapshot, ct).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Conflict))
            {
                FeatureExecutionOrchestrator.Require(response, "observe rules connection evidence");
                return true;
            }
            rejectedStatusCode = response.StatusCode;
        }
        catch (ApiHttpRequestException exception) when (
            exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            rejectedStatusCode = exception.StatusCode;
        }

        logger.LogWarning("Repository rejected stale, conflicting or invalid rules snapshot {SnapshotId} for server {ServerId}: {StatusCode}",
            snapshot.SnapshotId, snapshot.GameServerId, rejectedStatusCode);
        return false;
    }
}
