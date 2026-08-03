using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App;

/// <summary>
/// Single source of truth for the Repository API client DI registration used by the
/// Processor App. Program.cs and the startup-composition regression test both call
/// this helper so the exact option chain (BaseUrl, EntraId audience, cache partition,
/// L1 caching policy) can only ever diverge in one place.
/// </summary>
/// <remarks>
/// The chain re-enables consumer-side L1 caching that hotfix PR #53 removed. It is
/// safe again on Repository 4.2.22 / MX.Api.Client 2.3.77, which use the
/// reflection-free <c>SharedCacheConfiguration</c> to scope each cache policy to its
/// matching typed sub-API. On 4.2.21 the same call chain crashed startup with
/// <c>ArgumentException: The expression must invoke a method declared by ...IAdminActionsApi ...</c>.
/// </remarks>
internal static class RepositoryApiClientRegistration
{
    /// <summary>
    /// Stable, non-secret partition string used to isolate this consumer's cached
    /// responses inside the shared MX.Api L1 cache. Required by MX.Api.Client 2.3.77
    /// whenever caching is enabled.
    /// </summary>
    internal const string CachePartition = "portal-server-events";

    /// <summary>
    /// Registers the Repository API client with the same options the production host
    /// uses. Used by Program.cs at boot and by RepositoryApiClientRegistrationTests
    /// to prove the DI graph composes.
    /// </summary>
    internal static IServiceCollection AddPortalServerEventsRepositoryApiClient(
        this IServiceCollection services,
        string baseUrl,
        string audience)
    {
        return services.AddRepositoryApiClient(options => options
            .WithBaseUrl(baseUrl)
            .WithEntraIdAuthentication(audience)
            .WithCachePartition(CachePartition)
            .WithCaching(c => c.UseLibraryDefaults()));
    }

    /// <summary>
    /// Convenience overload that reads the same configuration keys Program.cs used
    /// previously, so the boot path is a one-liner and the required-key error
    /// messages stay identical to the pre-refactor behaviour.
    /// </summary>
    internal static IServiceCollection AddPortalServerEventsRepositoryApiClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var baseUrl = configuration["RepositoryApi:BaseUrl"]
            ?? throw new InvalidOperationException("RepositoryApi:BaseUrl is required");
        var audience = configuration["RepositoryApi:ApplicationAudience"]
            ?? throw new InvalidOperationException("RepositoryApi:ApplicationAudience is required");

        return services.AddPortalServerEventsRepositoryApiClient(baseUrl, audience);
    }
}
