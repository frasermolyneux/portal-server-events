using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Startup;

/// <summary>
/// Startup-time DI resolution tests for the Servers Integration API client registration
/// used by the Processor App. Servers 4.1.14 ships no default cache policies, so caching
/// is deliberately off here; this test still proves the container can build and resolve
/// every typed Rcon / Maps sub-API after the package bump.
/// </summary>
public class ServersApiClientRegistrationTests
{
    private const string BaseUrl = "https://servers-integration-api.test.local";
    private const string Audience = "api://servers-integration-api-test";

    [Fact]
    public void ProductionRegistration_BuildsAndResolvesServersClient()
    {
        var provider = BuildProductionServiceProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IServersApiClient>();
        Assert.NotNull(client);
    }

    [Fact]
    public void ProductionRegistration_ResolvesRepresentativeTypedSubClients()
    {
        var provider = BuildProductionServiceProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IServersApiClient>();

        // Touch every Rcon variant + Maps that the Processor App calls at runtime —
        // that materialises the underlying typed clients (where a hypothetical
        // caching-composition regression would surface after future bumps).
        Assert.NotNull(client.Cod2Rcon.V1);
        Assert.NotNull(client.Cod4Rcon.V1);
        Assert.NotNull(client.Cod5Rcon.V1);
        Assert.NotNull(client.CoD4xRcon.V1);
        Assert.NotNull(client.Maps.V1);
    }

    private static ServiceProvider BuildProductionServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddPortalServerEventsServersApiClient(BaseUrl, Audience);
        return services.BuildServiceProvider(validateScopes: true);
    }
}
