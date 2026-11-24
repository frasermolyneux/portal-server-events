using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal static class RulesServiceCollectionExtensions
{
    public static IServiceCollection AddUniversalRules(this IServiceCollection services)
    {
        services.AddTransient<ConfiguredFeatureRulesProvider>();
        services.AddTransient<VpnFeatureRulesProvider>();
        services.AddTransient<SiegeFeatureRulesProvider>();
        services.AddTransient<ISiegeActivationHistoryProvider, RepositorySiegeActivationHistoryProvider>();
        services.AddTransient<IRuleModerationRecordWriter, RepositoryRuleModerationRecordWriter>();
        services.AddTransient<FeatureExecutionOrchestrator>();
        services.AddTransient<IUniversalRuleActionExecutor, UniversalRuleActionExecutor>();
        services.AddTransient<IRegisteredRuleFeature>(provider => new RegisteredRuleFeature(
            provider.GetRequiredService<VpnFeatureRulesProvider>(), provider.GetRequiredService<FeatureExecutionOrchestrator>()));
        services.AddTransient<IRegisteredRuleFeature>(provider => new RegisteredRuleFeature(
            provider.GetRequiredService<SiegeFeatureRulesProvider>(), provider.GetRequiredService<FeatureExecutionOrchestrator>()));
        services.AddTransient<RegisteredFeatureHost>();
        services.AddTransient<RulesTriggerService>();
        services.AddTransient<IRulesTriggerService>(provider => provider.GetRequiredService<RulesTriggerService>());
        services.AddTransient<IRulesPluginTransport>(provider => provider.GetRequiredService<RulesTriggerService>());
        services.AddTransient<IRulesExecutionResumer>(provider => provider.GetRequiredService<RulesTriggerService>());
        services.AddSingleton<IRuleActionContinuationPublisher, RuleActionContinuationPublisher>();
        services.AddSingleton<IRuleExecutionContinuationPublisher, RuleExecutionContinuationPublisher>();
        return services;
    }
}
