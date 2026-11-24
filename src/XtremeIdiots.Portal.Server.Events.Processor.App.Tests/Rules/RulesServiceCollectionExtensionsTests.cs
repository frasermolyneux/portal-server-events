using Azure.Messaging.ServiceBus;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Moq;

using MX.GeoLocation.Api.Client.V1;
using MX.Observability.ApplicationInsights.Auditing;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;
using XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Rules;

public sealed class RulesServiceCollectionExtensionsTests
{
    [Fact]
    public async Task ProductionRulesRegistrationsResolveEveryTransportAndBothConcreteAdapters()
    {
        var client = new Mock<ServiceBusClient>();
        client.Setup(item => item.CreateSender(It.IsAny<string>())).Returns(Mock.Of<ServiceBusSender>());
        var services = new ServiceCollection()
            .AddLogging()
            .AddMemoryCache()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton(Mock.Of<IRepositoryApiClient>())
            .AddSingleton(Mock.Of<IServersApiClient>())
            .AddSingleton(Mock.Of<IGeoLocationApiClient>())
            .AddSingleton(Mock.Of<IAuditLogger>())
            .AddSingleton(client.Object)
            .AddUniversalRules();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.IsType<RulesTriggerService>(provider.GetRequiredService<IRulesTriggerService>());
        Assert.IsType<RulesTriggerService>(provider.GetRequiredService<IRulesPluginTransport>());
        Assert.IsType<RulesTriggerService>(provider.GetRequiredService<IRulesExecutionResumer>());
        Assert.IsType<RepositoryRuleModerationRecordWriter>(provider.GetRequiredService<IRuleModerationRecordWriter>());
        Assert.IsType<RepositorySiegeActivationHistoryProvider>(provider.GetRequiredService<ISiegeActivationHistoryProvider>());
    }
}
