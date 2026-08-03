using Microsoft.Extensions.DependencyInjection;

using MX.Caching.Abstractions;
using MX.GeoLocation.Abstractions.Interfaces.V1;
using MX.GeoLocation.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Startup;

/// <summary>
/// Startup-time DI resolution tests for the GeoLocation API client registration used by
/// the Processor App. These replicate the production registration in
/// <see cref="GeoLocationApiClientRegistration"/> and prove the container can build and
/// resolve every typed sub-client the app depends on (v1 and v1.1 GeoLookup) with the
/// library-default L1 read-only caching enabled.
/// </summary>
/// <remarks>
/// This is the guard against the failure mode that took down consumer boot on Repository
/// 4.2.21 / MX.Api.Client 2.3.76: <c>ArgumentException: The expression must invoke a
/// method declared by ...</c> when a single captured cache delegate was walked through a
/// reflection expression across sibling sub-APIs. MX.Api.Client 2.3.77's
/// <c>SharedCacheConfiguration</c> makes this composition safe; if a future package bump
/// regresses that, this test fails at <c>BuildServiceProvider(validateScopes: true)</c>
/// or on the first typed sub-API resolution.
/// </remarks>
public class GeoLocationApiClientRegistrationTests
{
    private const string BaseUrl = "https://geolocation-api.test.local";
    private const string ApiKey = "test-subscription-key";
    private const string Audience = "api://geolocation-api-test";

    [Fact]
    public void ProductionRegistration_BuildsAndResolvesGeoLocationClient()
    {
        var provider = BuildProductionServiceProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IGeoLocationApiClient>();

        Assert.NotNull(client);
    }

    [Fact]
    public void ProductionRegistration_ResolvesVersionedGeoLookupSubClients()
    {
        var provider = BuildProductionServiceProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IGeoLocationApiClient>();

        // Touch each versioned facade so the DI graph for the underlying typed client is
        // materialised — that is where a shared-caching composition regression would
        // surface (the same code path that crashed portal-sync / portal-repository-func
        // pre-2.3.77 with "The expression must invoke a method declared by ...").
        IGeoLookupApi v1 = client.GeoLookup.V1;
        MX.GeoLocation.Abstractions.Interfaces.V1_1.IGeoLookupApi v11 = client.GeoLookup.V1_1;

        Assert.NotNull(v1);
        Assert.NotNull(v11);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IVersionedGeoLookupApi>());
    }

    [Fact]
    public void ProductionRegistration_RegistersL1CacheInfrastructure()
    {
        var provider = BuildProductionServiceProvider();
        using var scope = provider.CreateScope();

        // MX.Api.Client's WithCaching(UseLibraryDefaults()) requires an IMxCache to be
        // wired in. AddGeoLocationApiClient (via AddTypedApiClient) auto-registers the
        // in-memory L1 backing. If a future dependency change breaks that auto-wiring,
        // the boot host would fail with a missing-cache-service error at first cached
        // lookup; catching it here as a scoped resolution failure keeps that failure
        // out of production.
        var cache = scope.ServiceProvider.GetRequiredService<IMxCache>();
        Assert.NotNull(cache);
    }

    private static ServiceProvider BuildProductionServiceProvider()
    {
        var services = new ServiceCollection();

        // Invoke the exact same helper Program.cs uses so this test is a real regression
        // guard against composition drift. Enabling WithCaching is what regressed on
        // 2.3.76-era clients; on MX.Api.Client 2.3.77 the SharedCacheConfiguration path
        // scopes each captured policy to its matching typed sub-API and composition
        // succeeds.
        services.AddPortalServerEventsGeoLocationApiClient(BaseUrl, ApiKey, Audience);

        return services.BuildServiceProvider(validateScopes: true);
    }
}
