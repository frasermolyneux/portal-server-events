using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Startup;

/// <summary>
/// Startup-time DI resolution tests for the Repository API client registration used by the
/// Processor App. These tests replicate the production registration in
/// <c>Program.cs</c> and prove the container can build and resolve every typed sub-client
/// the app depends on (e.g. <see cref="IAdminActionsApi"/>). They exercise real DI
/// resolution rather than inspecting option flags so that any regression in the
/// <c>services.AddRepositoryApiClient(...)</c> composition — such as the
/// <c>WithCaching(c =&gt; c.UseLibraryDefaults())</c> policy expression rejection that
/// crashed portal-sync / portal-repository-func on Repository 4.2.21 — is caught here
/// before it reaches production.
/// </summary>
public class RepositoryApiClientRegistrationTests
{
    private const string BaseUrl = "https://repository-api.test.local";
    private const string Audience = "api://repository-api-test";

    [Fact]
    public void ProductionRegistration_BuildsAndResolvesRepositoryClient()
    {
        var provider = BuildProductionServiceProvider();
        using var scope = provider.CreateScope();

        var repositoryClient = scope.ServiceProvider.GetRequiredService<IRepositoryApiClient>();

        Assert.NotNull(repositoryClient);
    }

    [Fact]
    public void ProductionRegistration_ResolvesAdminActionsSubClient()
    {
        var provider = BuildProductionServiceProvider();
        using var scope = provider.CreateScope();

        var repositoryClient = scope.ServiceProvider.GetRequiredService<IRepositoryApiClient>();
        IAdminActionsApi adminActions = repositoryClient.AdminActions.V1;

        Assert.NotNull(adminActions);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IVersionedAdminActionsApi>());
    }

    [Fact]
    public void ProductionRegistration_ResolvesRepresentativeSubClients()
    {
        var provider = BuildProductionServiceProvider();
        using var scope = provider.CreateScope();

        var repositoryClient = scope.ServiceProvider.GetRequiredService<IRepositoryApiClient>();

        // Every sub-client the processor actually calls at runtime must be resolvable.
        // Touching .V1 on each versioned facade forces the DI graph for the underlying
        // typed client to be materialised — which is where the client-side caching
        // policy composition failure surfaces on start-up.
        Assert.NotNull(repositoryClient.AdminActions.V1);
        Assert.NotNull(repositoryClient.Players.V1);
        Assert.NotNull(repositoryClient.GameServers.V1);
        Assert.NotNull(repositoryClient.GameServersEvents.V1);
        Assert.NotNull(repositoryClient.GameServersStats.V1);
        Assert.NotNull(repositoryClient.ChatMessages.V1);
        Assert.NotNull(repositoryClient.Maps.V1);
        Assert.NotNull(repositoryClient.ConnectedPlayers.V1);
        Assert.NotNull(repositoryClient.RecentPlayers.V1);
        Assert.NotNull(repositoryClient.GlobalConfigurations.V1);
        Assert.NotNull(repositoryClient.GameServerConfigurations.V1);
        Assert.NotNull(repositoryClient.LiveStatus.V1);
    }

    private static ServiceProvider BuildProductionServiceProvider()
    {
        var services = new ServiceCollection();

        // Invoke the exact same helper Program.cs uses so this test is a real regression
        // guard against composition drift. The helper's option chain is what crashed under
        // Repository 4.2.21 / MX.Api 2.3.76 with "The expression must invoke a method
        // declared by ...IAdminActionsApi..."; Repository 4.2.22 consumes MX.Api 2.3.77's
        // reflection-free SharedCacheConfiguration which scopes each policy to its matching
        // typed sub-API, so composition now succeeds. If someone changes the production
        // registration, this test picks up the new chain automatically.
        services.AddPortalServerEventsRepositoryApiClient(BaseUrl, Audience);

        return services.BuildServiceProvider(validateScopes: true);
    }
}
