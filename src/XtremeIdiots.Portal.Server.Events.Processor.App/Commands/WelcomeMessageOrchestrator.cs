using Microsoft.Extensions.Logging;

using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class WelcomeMessageOrchestrator : IWelcomeMessageOrchestrator
{
    private readonly IWelcomeMessageSettingsProvider _settingsProvider;
    private readonly IWelcomeMessageIdempotencyStore _idempotencyStore;
    private readonly WelcomeMessageTemplateRenderer _renderer;
    private readonly IServersApiClient _serversApiClient;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<WelcomeMessageOrchestrator> _logger;

    public WelcomeMessageOrchestrator(
        IWelcomeMessageSettingsProvider settingsProvider,
        IWelcomeMessageIdempotencyStore idempotencyStore,
        WelcomeMessageTemplateRenderer renderer,
        IServersApiClient serversApiClient,
        IAuditLogger auditLogger,
        ILogger<WelcomeMessageOrchestrator> logger)
    {
        _settingsProvider = settingsProvider;
        _idempotencyStore = idempotencyStore;
        _renderer = renderer;
        _serversApiClient = serversApiClient;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public async Task ProcessAsync(
        PlayerConnectedEvent playerEvent,
        GameType gameType,
        string[] playerTags,
        string? country,
        CancellationToken ct = default)
    {
        if (!IsSupportedGameType(gameType))
        {
            LogSkipped("UnsupportedGameType", playerEvent, gameType, null);
            return;
        }

        var settings = await _settingsProvider.GetEffectiveSettingsAsync(playerEvent.ServerId, ct).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            LogSkipped("SettingsDisabled", playerEvent, gameType, null);
            return;
        }

        if (settings.ValidationFailed)
        {
            LogSkipped("SettingsValidationFailed", playerEvent, gameType, null);
            return;
        }

        var age = DateTime.UtcNow - playerEvent.EventGeneratedUtc;
        if (age > TimeSpan.FromSeconds(settings.StaleThresholdSeconds))
        {
            LogSkipped("StaleEvent", playerEvent, gameType, null);
            return;
        }

        var winner = SelectWinner(settings.Rules, playerTags);
        if (winner is null)
        {
            LogSkipped("NoMatchingRule", playerEvent, gameType, null);
            return;
        }

        var idempotencyKey = string.Join(":",
            playerEvent.ServerId,
            playerEvent.PlayerGuid,
            playerEvent.EventGeneratedUtc.ToString("O"),
            playerEvent.SequenceId,
            winner.Id);

        var acquired = await _idempotencyStore.TryBeginAsync(idempotencyKey, DateTime.UtcNow, ct).ConfigureAwait(false);
        if (!acquired)
        {
            LogSkipped("DuplicateOrInProgress", playerEvent, gameType, winner.Id);
            return;
        }

        var delivered = false;
        try
        {
            if (winner.ConnectionDelaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(winner.ConnectionDelaySeconds), ct).ConfigureAwait(false);
            }

            var verification = await VerifyPlayerStillConnected(playerEvent, gameType, ct).ConfigureAwait(false);
            if (!verification.Success)
            {
                LogSkipped(verification.Reason ?? "PlayerVerificationFailed", playerEvent, gameType, winner.Id);
                return;
            }

            var messageCountry = string.IsNullOrWhiteSpace(country) ? settings.CountryFallback : country;
            var tokenValues = new WelcomeMessageTokenValues
            {
                Name = verification.PlayerName ?? playerEvent.Username,
                Country = messageCountry,
                IpAddress = playerEvent.IpAddress,
                Tags = string.Join(", ", playerTags.Where(static tag => !string.IsNullOrWhiteSpace(tag))),
                PlayerGuid = playerEvent.PlayerGuid,
                SteamId = playerEvent.SteamId ?? string.Empty,
                PlayerCount = verification.PlayerCount >= 0
                    ? verification.PlayerCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty
            };
            var renderedMessage = _renderer.Render(winner.MessageTemplate, tokenValues);

            var deliveryResult = winner.Visibility == WelcomeMessageVisibility.Public
                ? await SendPublicAsync(gameType, playerEvent.ServerId, renderedMessage, ct).ConfigureAwait(false)
                : await SendPrivateAsync(gameType, playerEvent.ServerId, verification.SlotId, renderedMessage, ct).ConfigureAwait(false);

            if (!deliveryResult.IsSuccess)
            {
                _logger.LogWarning(
                    "Welcome message send failed for server {ServerId}, player {PlayerGuid}, rule {RuleId}. Status {StatusCode}.",
                    playerEvent.ServerId,
                    playerEvent.PlayerGuid,
                    winner.Id,
                    deliveryResult.StatusCode);

                _auditLogger.LogAudit(AuditEvent.ServerAction("WelcomeMessageFailed", AuditAction.Execute)
                    .WithGameContext(gameType.ToString(), playerEvent.ServerId)
                    .WithPlayer(playerEvent.PlayerGuid, playerEvent.Username)
                    .WithSource(nameof(WelcomeMessageOrchestrator))
                    .WithProperty("RuleId", winner.Id)
                    .WithProperty("Reason", "RconFailure")
                    .Build());

                return;
            }

            delivered = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            if (delivered)
            {
                await _idempotencyStore.CompleteAsync(idempotencyKey, DateTime.UtcNow, ct).ConfigureAwait(false);
            }
        }
    }

    private static EffectiveWelcomeMessageRule? SelectWinner(
        IReadOnlyList<EffectiveWelcomeMessageRule> rules,
        IReadOnlyCollection<string> playerTags)
    {
        var normalizedTags = new HashSet<string>(playerTags.Where(static tag => !string.IsNullOrWhiteSpace(tag)), StringComparer.OrdinalIgnoreCase);

        return rules
            .Where(static rule => rule.Enabled)
            .Where(rule => rule.RequiredTags.All(normalizedTags.Contains))
            .OrderByDescending(static rule => rule.Priority)
            .ThenBy(static rule => rule.OrderIndex)
            .FirstOrDefault();
    }

    private async Task<VerificationResult> VerifyPlayerStillConnected(PlayerConnectedEvent playerEvent, GameType gameType, CancellationToken ct)
    {
        if (gameType == GameType.CallOfDuty2)
        {
            var statusResult = await _serversApiClient.Cod2Rcon.V1.Status(playerEvent.ServerId, ct).ConfigureAwait(false);
            if (!statusResult.IsSuccess || statusResult.Result?.Data is null)
            {
                return VerificationResult.FromFailure("StatusUnavailable");
            }

            var players = statusResult.Result.Data.Players;
            var player = players.FirstOrDefault(p =>
                string.Equals(p.Guid, playerEvent.PlayerGuid, StringComparison.OrdinalIgnoreCase));

            return player is null
                ? VerificationResult.FromFailure("PlayerNotConnected")
                : VerificationResult.FromSuccess(player.Num, player.Name, players.Count);
        }

        if (gameType == GameType.CallOfDuty4)
        {
            var statusResult = await _serversApiClient.Cod4Rcon.V1.Status(playerEvent.ServerId, ct).ConfigureAwait(false);
            if (!statusResult.IsSuccess || statusResult.Result?.Data is null)
            {
                return VerificationResult.FromFailure("StatusUnavailable");
            }

            var players = statusResult.Result.Data.Players;
            var player = players.FirstOrDefault(p =>
                string.Equals(p.Guid, playerEvent.PlayerGuid, StringComparison.OrdinalIgnoreCase));

            return player is null
                ? VerificationResult.FromFailure("PlayerNotConnected")
                : VerificationResult.FromSuccess(player.Num, player.Name, players.Count);
        }

        if (gameType == GameType.CallOfDuty5)
        {
            var statusResult = await _serversApiClient.Cod5Rcon.V1.Status(playerEvent.ServerId, ct).ConfigureAwait(false);
            if (!statusResult.IsSuccess || statusResult.Result?.Data is null)
            {
                return VerificationResult.FromFailure("StatusUnavailable");
            }

            var players = statusResult.Result.Data.Players;
            var player = players.FirstOrDefault(p =>
                string.Equals(p.Guid, playerEvent.PlayerGuid, StringComparison.OrdinalIgnoreCase));

            return player is null
                ? VerificationResult.FromFailure("PlayerNotConnected")
                : VerificationResult.FromSuccess(player.Num, player.Name, players.Count);
        }

        if (gameType == GameType.CallOfDuty4x)
        {
            var statusResult = await _serversApiClient.CoD4xRcon.V1.Status(playerEvent.ServerId, ct).ConfigureAwait(false);
            if (!statusResult.IsSuccess || statusResult.Result?.Data is null)
            {
                return VerificationResult.FromFailure("StatusUnavailable");
            }

            var player = statusResult.Result.Data.Players.FirstOrDefault(p =>
                string.Equals(p.PlayerIdentifier, playerEvent.PlayerGuid, StringComparison.OrdinalIgnoreCase));

            if (player is null)
            {
                return VerificationResult.FromFailure("PlayerNotConnected");
            }

            // Match RconResponseService: colour-coded CoD4x names live in RawName when Name is blank.
            var resolvedName = string.IsNullOrWhiteSpace(player.Name) ? player.RawName : player.Name;
            return VerificationResult.FromSuccess(player.Num, string.IsNullOrWhiteSpace(resolvedName) ? null : resolvedName, statusResult.Result.Data.Players.Count);
        }

        return VerificationResult.FromFailure("UnsupportedGameType");
    }

    private async Task<ApiResult> SendPublicAsync(GameType gameType, Guid serverId, string message, CancellationToken ct)
    {
        return gameType switch
        {
            GameType.CallOfDuty2 => await _serversApiClient.Cod2Rcon.V1
                .Say(serverId, new SayRequest { Message = message }, ct).ConfigureAwait(false),
            GameType.CallOfDuty4 => await _serversApiClient.Cod4Rcon.V1
                .Say(serverId, new SayRequest { Message = message }, ct).ConfigureAwait(false),
            GameType.CallOfDuty5 => await _serversApiClient.Cod5Rcon.V1
                .Say(serverId, new SayRequest { Message = message }, ct).ConfigureAwait(false),
            GameType.CallOfDuty4x => await _serversApiClient.CoD4xRcon.V1
                .Say(serverId, new CoD4xMessageRequestDto { Message = message }, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported game type: {gameType}")
        };
    }

    private async Task<ApiResult> SendPrivateAsync(GameType gameType, Guid serverId, int slotId, string message, CancellationToken ct)
    {
        var request = new CoD4xTargetMessageRequestDto
        {
            Target = slotId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Message = message
        };

        return gameType switch
        {
            GameType.CallOfDuty2 => await _serversApiClient.Cod2Rcon.V1.Tell(serverId, request, ct).ConfigureAwait(false),
            GameType.CallOfDuty4 => await _serversApiClient.Cod4Rcon.V1.Tell(serverId, request, ct).ConfigureAwait(false),
            GameType.CallOfDuty5 => await _serversApiClient.Cod5Rcon.V1.Tell(serverId, request, ct).ConfigureAwait(false),
            GameType.CallOfDuty4x => await _serversApiClient.CoD4xRcon.V1.Tell(serverId, request, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported game type: {gameType}")
        };
    }

    private static bool IsSupportedGameType(GameType gameType)
        => gameType is GameType.CallOfDuty2 or GameType.CallOfDuty4 or GameType.CallOfDuty5 or GameType.CallOfDuty4x;

    private void LogSkipped(string reason, PlayerConnectedEvent playerEvent, GameType gameType, string? ruleId)
    {
        _logger.LogDebug(
            "Welcome message skipped for server {ServerId}, player {PlayerGuid}, game {GameType}, reason {Reason}, rule {RuleId}",
            playerEvent.ServerId,
            playerEvent.PlayerGuid,
            gameType,
            reason,
            ruleId ?? string.Empty);
    }

    private sealed record VerificationResult(bool Success, int SlotId, string? PlayerName, int PlayerCount, string? Reason)
    {
        public static VerificationResult FromSuccess(int slotId, string? playerName, int playerCount) => new(true, slotId, playerName, playerCount, null);

        public static VerificationResult FromFailure(string reason) => new(false, -1, null, -1, reason);
    }
}
