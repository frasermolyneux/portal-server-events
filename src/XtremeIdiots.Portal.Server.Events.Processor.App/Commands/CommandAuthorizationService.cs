using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed class CommandAuthorizationService : ICommandAuthorizationService
{
    private readonly IOptionsMonitor<CommandAuthorizationOptions> _options;
    private readonly ILogger<CommandAuthorizationService> _logger;

    public CommandAuthorizationService(
        IOptionsMonitor<CommandAuthorizationOptions> options,
        ILogger<CommandAuthorizationService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task<CommandAuthorizationResult> AuthorizeAsync(CommandAuthorizationContext context, CancellationToken ct = default)
    {
        var hasInlineRequirements = context.RequiredTags.Length > 0 || context.RequiredClaims.Length > 0;

        CommandPolicyOptions? configuredPolicy = null;
        if (!string.IsNullOrWhiteSpace(context.RequiredPolicy))
        {
            var policies = _options.CurrentValue.Policies;
            if (!policies.TryGetValue(context.RequiredPolicy, out configuredPolicy))
            {
                _logger.LogWarning("Authorization policy {Policy} not configured for command {CommandPrefix}; denying by default.",
                    context.RequiredPolicy,
                    context.CommandPrefix);
                return Task.FromResult(CommandAuthorizationResult.Deny("Authorization policy is not configured for this command."));
            }
        }

        if (hasInlineRequirements)
        {
            var inlinePolicy = new CommandPolicyOptions
            {
                RequiredTags = context.RequiredTags,
                RequiredClaims = context.RequiredClaims,
                AllowedGameTypes = configuredPolicy?.AllowedGameTypes ?? [],
                AllowedServerIds = configuredPolicy?.AllowedServerIds ?? [],
                Privileged = context.Privileged
            };

            return Task.FromResult(EvaluatePolicy(inlinePolicy, context));
        }

        if (configuredPolicy is null)
        {
            return Task.FromResult(CommandAuthorizationResult.Allow());
        }

        return Task.FromResult(EvaluatePolicy(configuredPolicy, context));
    }

    private static CommandAuthorizationResult EvaluatePolicy(CommandPolicyOptions policy, CommandAuthorizationContext context)
    {
        if (!ScopeMatches(policy, context))
        {
            return CommandAuthorizationResult.Deny("You are not authorized to use this command in this scope.");
        }

        var snapshot = context.Snapshot;
        if (policy.Privileged)
        {
            if (snapshot is null)
            {
                return CommandAuthorizationResult.Deny("Authorization dependencies are unavailable.");
            }

            if ((!snapshot.TagsResolved && policy.RequiredTags.Length > 0) ||
                (!snapshot.ClaimsResolved && policy.RequiredClaims.Length > 0))
            {
                return CommandAuthorizationResult.Deny("Authorization dependencies are unavailable.");
            }
        }

        if (snapshot is null)
        {
            return CommandAuthorizationResult.Deny("Authorization context is unavailable.");
        }

        var tagMatch = MatchesAny(policy.RequiredTags, snapshot.Tags);
        var claimMatch = MatchesAny(policy.RequiredClaims, snapshot.Claims);

        if (policy.RequiredTags.Length > 0 && policy.RequiredClaims.Length > 0)
        {
            if (tagMatch && claimMatch)
            {
                return CommandAuthorizationResult.Allow();
            }

            if (policy.Privileged && tagMatch != claimMatch)
            {
                return CommandAuthorizationResult.Deny("Your authorization sources are inconsistent for this command.");
            }

            return CommandAuthorizationResult.Deny("You are not authorized to use this command.");
        }

        if (policy.RequiredTags.Length > 0 && !tagMatch)
        {
            return CommandAuthorizationResult.Deny("You are not authorized to use this command.");
        }

        if (policy.RequiredClaims.Length > 0 && !claimMatch)
        {
            return CommandAuthorizationResult.Deny("You are not authorized to use this command.");
        }

        return CommandAuthorizationResult.Allow();
    }

    private static bool ScopeMatches(CommandPolicyOptions policy, CommandAuthorizationContext context)
    {
        if (policy.AllowedGameTypes.Length > 0 &&
            !policy.AllowedGameTypes.Contains(context.GameType, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (policy.AllowedServerIds.Length > 0 && !policy.AllowedServerIds.Contains(context.ServerId))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesAny(string[] required, IReadOnlySet<string> actual)
    {
        if (required.Length == 0)
        {
            return true;
        }

        foreach (var item in required)
        {
            if (actual.Contains(item))
            {
                return true;
            }
        }

        return false;
    }
}
