using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.RulesEngine;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal sealed class RuleMessageBudgetProvider(IServersApiClient servers) : IRuleMessageBudgetProvider
{
    public async Task<RuleMessageBudgetResult> GetAsync(RuleInvocation invocation, string actionType, CancellationToken ct)
    {
        var privateMessage = actionType != "message.public";
        var runtime = invocation.Trigger.Runtime;
        if (runtime?.Id == "cod4x-plugin")
        {
            return runtime.Capabilities.Contains("cod4x.chat.fragments", StringComparer.Ordinal)
                ? new(new MessageChannelBudget(privateMessage ? "cod4x.chat.private" : "cod4x.chat.public", 149,
                    Provider: "cod4x-plugin", Evidence: "Native h chat handler: 149 UTF-8 bytes, no RCON prefix."))
                : new(null, "Plugin does not advertise bounded prepared-message support.");
        }
        var response = await servers.RulesNativeAction.V1.GetMessageChannelBudget(invocation.Trigger.GameServerId,
            privateMessage ? RuleMessageChannels.RconPrivateChat : RuleMessageChannels.RconPublicChat, ct).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result?.Data is not { } result)
        {
            throw new InvalidOperationException($"Unable to resolve message channel budget: {response.StatusCode}.");
        }
        return result.Status switch
        {
            RuleMessageBudgetStatus.Available when result.Budget is not null && result.Constraints is not null =>
                new(result.Budget, Constraints: result.Constraints),
            RuleMessageBudgetStatus.Unsupported => new(null, result.Detail),
            RuleMessageBudgetStatus.Unavailable => new(null, result.Detail, Unavailable: true),
            _ => throw new InvalidOperationException("Integration returned an invalid message channel budget.")
        };
    }
}
