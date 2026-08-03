using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using MX.GeoLocation.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App;

/// <summary>
/// Single source of truth for the GeoLocation API client DI registration used by the
/// Processor App. Program.cs and the startup-composition test both call this helper so
/// the exact option chain (BaseUrl, ApiKey, EntraId audience, cache partition, L1 caching
/// policy) can only ever diverge in one place.
/// </summary>
/// <remarks>
/// Enables the read-only L1 in-memory cache defaults shipped with
/// <c>MX.GeoLocation.Api.Client.V1</c> 1.2.98: v1 <c>GetGeoLocation</c> (60m); v1.1
/// <c>GetCityGeoLocation</c> (60m), <c>GetInsightsGeoLocation</c> (30m),
/// <c>GetProxyCheck</c> (15m), <c>GetIpIntelligence</c> (15m). Batch POST and DELETE
/// operations remain uncached. Safe on MX.Api.Client 2.3.77 which scopes each captured
/// policy via <c>SharedCacheConfiguration</c> rather than the reflection expression
/// walk that crashed startup on 4.2.21-era clients.
/// </remarks>
internal static class GeoLocationApiClientRegistration
{
    /// <summary>
    /// Stable, non-secret partition string used to isolate this consumer's cached
    /// GeoLocation responses inside the shared MX.Api L1 cache. Kept identical to
    /// <see cref="RepositoryApiClientRegistration.CachePartition"/> so all typed
    /// clients hosted by the Processor App share one partition namespace.
    /// </summary>
    internal const string CachePartition = RepositoryApiClientRegistration.CachePartition;

    /// <summary>
    /// Registers the GeoLocation API client with the same options the production host
    /// uses. Used by Program.cs at boot and by
    /// <c>GeoLocationApiClientRegistrationTests</c> to prove the DI graph composes with
    /// caching enabled.
    /// </summary>
    internal static IServiceCollection AddPortalServerEventsGeoLocationApiClient(
        this IServiceCollection services,
        string baseUrl,
        string apiKey,
        string audience)
    {
        return services.AddGeoLocationApiClient(options => options
            .WithBaseUrl(baseUrl)
            .WithApiKeyAuthentication(apiKey, "Ocp-Apim-Subscription-Key")
            .WithEntraIdAuthentication(audience)
            .WithCachePartition(CachePartition)
            .WithCaching(c => c.UseLibraryDefaults()));
    }

    /// <summary>
    /// Convenience overload that reads the same configuration keys Program.cs used
    /// previously. Returns <see langword="false"/> without registering when any of the
    /// three keys is missing (matches the pre-refactor "GeoIP enrichment skipped"
    /// behaviour) so a partial configuration cannot break boot.
    /// </summary>
    internal static bool TryAddPortalServerEventsGeoLocationApiClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var baseUrl = configuration["GeoLocationApi:BaseUrl"];
        var apiKey = configuration["GeoLocationApi:ApiKey"];
        var audience = configuration["GeoLocationApi:ApplicationAudience"];

        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(audience))
        {
            return false;
        }

        services.AddPortalServerEventsGeoLocationApiClient(baseUrl, apiKey, audience);
        return true;
    }
}
