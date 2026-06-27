using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class WelcomeMessageOrchestrator : IWelcomeMessageOrchestrator
{
    private readonly IWelcomeMessageSettingsProvider _settingsProvider;
    private readonly IWelcomeMessageIdempotencyStore _idempotencyStore;
    private readonly WelcomeMessageTemplateRenderer _renderer;
    private readonly IRconApi _rconApi;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<WelcomeMessageOrchestrator> _logger;

    public WelcomeMessageOrchestrator(
        IWelcomeMessageSettingsProvider settingsProvider,
        IWelcomeMessageIdempotencyStore idempotencyStore,
        WelcomeMessageTemplateRenderer renderer,
        IRconApi rconApi,
        IAuditLogger auditLogger,
        ILogger<WelcomeMessageOrchestrator> logger)
    {
        _settingsProvider = settingsProvider;
        _idempotencyStore = idempotencyStore;
        _renderer = renderer;
        _rconApi = rconApi;
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

            var verification = await VerifyPlayerStillConnected(playerEvent, ct).ConfigureAwait(false);
            if (!verification.Success)
            {
                LogSkipped(verification.Reason ?? "PlayerVerificationFailed", playerEvent, gameType, winner.Id);
                return;
            }

            var messageCountry = string.IsNullOrWhiteSpace(country) ? settings.CountryFallback : country;
            var renderedMessage = _renderer.Render(winner.MessageTemplate, verification.PlayerName ?? playerEvent.Username, messageCountry);

            var deliveryResult = winner.Visibility == WelcomeMessageVisibility.Public
                ? await _rconApi.Say(playerEvent.ServerId, renderedMessage).ConfigureAwait(false)
                : await _rconApi.TellPlayerWithVerification(
                    playerEvent.ServerId,
                    verification.SlotId,
                    renderedMessage,
                    verification.PlayerName ?? playerEvent.Username).ConfigureAwait(false);

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

    private async Task<VerificationResult> VerifyPlayerStillConnected(PlayerConnectedEvent playerEvent, CancellationToken ct)
    {
        var statusResult = await _rconApi.GetServerStatus(playerEvent.ServerId).ConfigureAwait(false);
        if (!statusResult.IsSuccess || statusResult.Result?.Data is null)
        {
            return VerificationResult.FromFailure("StatusUnavailable");
        }

        var players = statusResult.Result.Data.Players;
        var byGuid = players.FirstOrDefault(p =>
            string.Equals(p.Guid, playerEvent.PlayerGuid, StringComparison.OrdinalIgnoreCase));

        if (byGuid is null)
        {
            return VerificationResult.FromFailure("PlayerNotConnected");
        }

        // Preferred path: the expected slot should still match.
        if (byGuid.Num == playerEvent.SlotId)
        {
            return VerificationResult.FromSuccess(byGuid.Num, byGuid.Name);
        }

        // Fallback path: resolve by guid to ensure we still target the same identity.
        var resolveResult = await _rconApi.ResolvePlayer(
            playerEvent.ServerId,
            new ResolvePlayerRequestDto
            {
                PlayerQuery = playerEvent.PlayerGuid,
                MaxSuggestions = 1
            },
            ct).ConfigureAwait(false);

        if (!resolveResult.IsSuccess || resolveResult.Result?.Data?.ResolvedPlayer is null)
        {
            return VerificationResult.FromFailure("ResolvePlayerFailed");
        }

        var resolvedPlayer = resolveResult.Result.Data.ResolvedPlayer;
        if (!string.Equals(resolvedPlayer.Guid, playerEvent.PlayerGuid, StringComparison.OrdinalIgnoreCase))
        {
            return VerificationResult.FromFailure("ResolvedGuidMismatch");
        }

        return VerificationResult.FromSuccess(resolvedPlayer.Slot, resolvedPlayer.Name);
    }

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

    private sealed record VerificationResult(bool Success, int SlotId, string? PlayerName, string? Reason)
    {
        public static VerificationResult FromSuccess(int slotId, string? playerName) => new(true, slotId, playerName, null);

        public static VerificationResult FromFailure(string reason) => new(false, -1, null, reason);
    }
}
