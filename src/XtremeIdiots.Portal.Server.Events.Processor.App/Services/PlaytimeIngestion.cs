using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Playtime;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

using EventRosterCompleteness = XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events.PlaytimeRosterCompleteness;
using EventTimestampQuality = XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events.PlaytimeTimestampQuality;
using EventKind = XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events.PlaytimeObservationKind;
using RepositoryKind = XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Playtime.PlaytimeObservationKind;
using RepositoryRosterCompleteness = XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Playtime.PlaytimeRosterCompleteness;
using RepositoryTimestampQuality = XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Playtime.PlaytimeTimestampQuality;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Services;

public sealed class PlaytimeIngestion(
    IRepositoryApiClient repository,
    ILogger<PlaytimeIngestion> logger,
    IAuditLogger auditLogger) : IPlaytimeIngestion
{
    public async Task ProcessAsync(PlaytimeObservationEvent serverEvent, CancellationToken cancellationToken)
    {
        var metadata = serverEvent.Playtime;
        if (serverEvent.PlaytimeCoverage is { } coverage)
        {
            if (serverEvent.Kind != EventKind.ServerRoster || metadata?.ProducerEpoch is null || metadata.ProducerEpoch == Guid.Empty ||
                coverage.ProducerStartedUtc == default || coverage.ProducerStartedUtc.Kind != DateTimeKind.Utc ||
                serverEvent.EventPublishedUtc == default || serverEvent.EventPublishedUtc.Kind != DateTimeKind.Utc ||
                coverage.ProducerStartedUtc > serverEvent.EventPublishedUtc ||
                coverage.DroppedCapacity < 0 || coverage.DroppedAttempts < 0 || coverage.DroppedAge < 0)
            {
                throw new InvalidOperationException("Playtime coverage requires valid counters, UTC start time and producer epoch.");
            }

        }
        if (metadata is null)
        {
            throw new InvalidOperationException("Playtime observation metadata is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (metadata.ObservationId == Guid.Empty || serverEvent.ServerId == Guid.Empty ||
            metadata.EventUtc == default || metadata.EventUtc.Kind != DateTimeKind.Utc ||
            !Enum.IsDefined(metadata.TimestampQuality) || metadata.TimestampQuality == EventTimestampQuality.Unknown ||
            !Enum.IsDefined(metadata.Freshness) ||
            metadata.TimestampQuality == EventTimestampQuality.ParseTime && metadata.Freshness != PlaytimeObservationFreshness.Live ||
            !Enum.IsDefined(metadata.RosterCompleteness) ||
            metadata.ProducerSequence.HasValue && !metadata.ProducerEpoch.HasValue ||
            metadata.ProducerEpoch == Guid.Empty ||
            metadata.ProducerSequence < 0 ||
            metadata.ConnectionCorrelationId == Guid.Empty ||
            serverEvent.Kind is EventKind.ServerRoster or EventKind.MapChanged && metadata.ConnectionCorrelationId.HasValue ||
            serverEvent.Kind == EventKind.ServerRoster && metadata.RosterCompleteness == EventRosterCompleteness.NotApplicable ||
            serverEvent.Kind != EventKind.ServerRoster && metadata.RosterCompleteness != EventRosterCompleteness.NotApplicable ||
            !Enum.TryParse<GameType>(serverEvent.GameType, true, out var gameType) || !Enum.IsDefined(gameType))
        {
            throw new InvalidOperationException($"Invalid playtime metadata for observation {metadata.ObservationId}.");
        }

        var observation = new PlaytimeObservationDto
        {
            ObservationId = metadata.ObservationId,
            ServerId = serverEvent.ServerId,
            GameType = gameType,
            EventUtc = metadata.EventUtc,
            TimestampQuality = metadata.TimestampQuality switch
            {
                EventTimestampQuality.ParsedLogTimestamp => RepositoryTimestampQuality.ParsedLogTimestamp,
                EventTimestampQuality.ProducerTimestamp => RepositoryTimestampQuality.ProducerTimestamp,
                EventTimestampQuality.RosterSnapshotTimestamp => RepositoryTimestampQuality.RosterSnapshotTimestamp,
                EventTimestampQuality.ParseTime => RepositoryTimestampQuality.ParseTime,
                _ => throw new InvalidOperationException($"Unsupported playtime timestamp quality {metadata.TimestampQuality}.")
            },
            ProducerEpoch = metadata.ProducerEpoch,
            ProducerSequence = metadata.ProducerSequence,
            ConnectionCorrelationId = metadata.ConnectionCorrelationId,
            Coverage = serverEvent.PlaytimeCoverage is { } sourceCoverage
                ? new PlaytimeCoverageCountersDto
                {
                    ProducerEpoch = metadata.ProducerEpoch!.Value,
                    ProducerStartedUtc = sourceCoverage.ProducerStartedUtc,
                    ReportedUtc = serverEvent.EventPublishedUtc,
                    DroppedCapacityMax = sourceCoverage.DroppedCapacity,
                    DroppedAttemptsMax = sourceCoverage.DroppedAttempts,
                    DroppedAgeMax = sourceCoverage.DroppedAge
                }
                : null
        };
        var unresolvedIdentities = new List<string>();

        switch (serverEvent.Kind)
        {
            case EventKind.PlayerJoined:
            case EventKind.PlayerLeft:
                ValidatePlayer(serverEvent.PlayerGuid, serverEvent.Username, metadata);
                if (serverEvent.SlotId is null or < 0)
                {
                    throw new InvalidOperationException($"Slot is required for playtime observation {metadata.ObservationId}.");
                }
                observation = observation with
                {
                    Kind = serverEvent.Kind == EventKind.PlayerJoined ? RepositoryKind.PlayerJoined : RepositoryKind.PlayerLeft,
                    GamePlayerId = serverEvent.PlayerGuid,
                    SlotId = serverEvent.SlotId,
                    IsBot = serverEvent.IsBot,
                    CanonicalPlayerId = serverEvent.IsBot == true
                        ? null
                        : await ResolvePlayerAsync(
                            gameType, serverEvent.PlayerGuid!, serverEvent.Username!, serverEvent.ServerId,
                            serverEvent.Kind == EventKind.PlayerJoined, cancellationToken, serverEvent.SteamId).ConfigureAwait(false)
                };
                if (serverEvent.IsBot != true && observation.CanonicalPlayerId is null)
                {
                    unresolvedIdentities.Add($"player {serverEvent.PlayerGuid}");
                }
                break;

            case EventKind.MapChanged:
                if (string.IsNullOrWhiteSpace(serverEvent.MapName))
                {
                    throw new InvalidOperationException($"Map is required for playtime observation {metadata.ObservationId}.");
                }
                observation = observation with { Kind = RepositoryKind.MapChanged, MapName = serverEvent.MapName };
                break;

            case EventKind.ServerRoster:
                if (serverEvent.Players is null && metadata.RosterCompleteness != EventRosterCompleteness.Failed)
                {
                    throw new InvalidOperationException($"Roster is required for playtime observation {metadata.ObservationId}.");
                }
                var roster = new List<PlaytimeRosterPlayerDto>();
                foreach (var player in metadata.RosterCompleteness == EventRosterCompleteness.Failed ? [] : serverEvent.Players!)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (player.IsBot == true)
                    {
                        roster.Add(new PlaytimeRosterPlayerDto
                        {
                            GamePlayerId = player.PlayerGuid ?? string.Empty,
                            SlotId = player.SlotId,
                            IsBot = true
                        });
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(player.PlayerGuid))
                    {
                        logger.LogWarning("Playtime roster {ObservationId} contains an entry without a game-player identifier at slot {SlotId}",
                            metadata.ObservationId, player.SlotId);
                        throw new InvalidOperationException(
                            $"Playtime roster {metadata.ObservationId} has no game-player identifier for slot {player.SlotId}; it cannot be resolved by replay.");
                    }
                    var playerId = await ResolvePlayerAsync(
                        gameType, player.PlayerGuid, player.Username, serverEvent.ServerId,
                        !string.IsNullOrWhiteSpace(player.Username), cancellationToken).ConfigureAwait(false);
                    if (playerId is null)
                    {
                        unresolvedIdentities.Add($"player {player.PlayerGuid}");
                    }
                    roster.Add(new PlaytimeRosterPlayerDto
                    {
                        CanonicalPlayerId = playerId,
                        GamePlayerId = player.PlayerGuid,
                        SlotId = player.SlotId,
                        IsBot = player.IsBot
                    });
                }
                var completeness = metadata.RosterCompleteness switch
                {
                    EventRosterCompleteness.Complete => RepositoryRosterCompleteness.Complete,
                    EventRosterCompleteness.Partial => RepositoryRosterCompleteness.Partial,
                    EventRosterCompleteness.Failed => RepositoryRosterCompleteness.Failed,
                    _ => throw new InvalidOperationException($"Invalid roster completeness for observation {metadata.ObservationId}.")
                };
                if (completeness != RepositoryRosterCompleteness.Complete)
                {
                    logger.LogWarning("Playtime roster {ObservationId} has incomplete source coverage ({Completeness})",
                        metadata.ObservationId, completeness);
                }
                observation = observation with
                {
                    Kind = RepositoryKind.ServerRoster,
                    MapName = metadata.RosterCompleteness == EventRosterCompleteness.Failed ? null : serverEvent.MapName,
                    RosterCompleteness = completeness,
                    Roster = roster
                };
                break;

            default:
                throw new InvalidOperationException($"Unsupported playtime observation kind {serverEvent.Kind}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var response = await repository.Playtime.V1.BatchObservations(new PlaytimeObservationBatchDto
        {
            Observations = [observation]
        }, cancellationToken).ConfigureAwait(false);

        var results = response.Result?.Data?.Items;
        if (!response.IsSuccess || results is null || results.Count != 1 || results[0].ObservationId != metadata.ObservationId)
        {
            throw new InvalidOperationException($"Playtime observation {metadata.ObservationId} was not durably acknowledged (HTTP {response.StatusCode}).");
        }
        if (results[0].Outcome is not (PlaytimeObservationOutcome.Accepted or PlaytimeObservationOutcome.Duplicate))
        {
            throw new InvalidOperationException($"Playtime observation {metadata.ObservationId} was rejected: {results[0].Reason ?? "no reason supplied"}.");
        }
        if (unresolvedIdentities.Count > 0)
        {
            throw new InvalidOperationException(
                $"Playtime observation {metadata.ObservationId} has unresolved identities: {string.Join(", ", unresolvedIdentities)}.");
        }
    }

    private static void ValidatePlayer(string? playerGuid, string? username, PlaytimeEventMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(playerGuid) || string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException($"Player identity is required for playtime observation {metadata.ObservationId}.");
        }
    }

    private async Task<Guid?> ResolvePlayerAsync(
        GameType gameType,
        string playerGuid,
        string username,
        Guid serverId,
        bool create,
        CancellationToken cancellationToken,
        string? steamId = null)
    {
        var result = await repository.Players.V1.GetPlayerByGameType(gameType, playerGuid, PlayerEntityOptions.None).ConfigureAwait(false);
        if (result.IsSuccess && result.Result?.Data is { } player && player.PlayerId != Guid.Empty)
        {
            return player.PlayerId;
        }
        if (!result.IsNotFound)
        {
            throw new InvalidOperationException($"Player identity lookup failed for {gameType}/{playerGuid} (HTTP {result.StatusCode}).");
        }
        if (!create)
        {
            logger.LogWarning("Playtime player identity unresolved for {GameType}/{PlayerGuid}", gameType, playerGuid);
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var created = await repository.Players.V1.CreatePlayer(new CreatePlayerDto(username, playerGuid, gameType)
        {
            SteamId = steamId
        }).ConfigureAwait(false);
        if (!created.IsSuccess && !created.IsConflict)
        {
            throw new InvalidOperationException($"Player creation failed for {gameType}/{playerGuid} (HTTP {created.StatusCode}).");
        }
        if (created.IsSuccess)
        {
            auditLogger.LogAudit(AuditEvent.ServerAction("PlayerCreated", AuditAction.Create)
                .WithGameContext(gameType.ToString(), serverId)
                .WithPlayer(playerGuid, username)
                .Build());
        }
        var resolved = await repository.Players.V1.GetPlayerByGameType(gameType, playerGuid, PlayerEntityOptions.None).ConfigureAwait(false);
        if (!resolved.IsSuccess || resolved.Result?.Data is null || resolved.Result.Data.PlayerId == Guid.Empty)
        {
            throw new InvalidOperationException($"Created player identity could not be resolved for {gameType}/{playerGuid}.");
        }
        return resolved.Result.Data.PlayerId;
    }
}
