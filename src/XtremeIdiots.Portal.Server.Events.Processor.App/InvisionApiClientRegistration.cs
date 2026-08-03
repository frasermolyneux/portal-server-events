using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using MX.Api.Client.Configuration;
using MX.InvisionCommunity.Api.Client;

namespace XtremeIdiots.Portal.Server.Events.Processor.App;

/// <summary>
/// Single source of truth for the Invision Community API client DI registration used by
/// the Processor App. Program.cs and the startup-composition test both call this helper
/// so the exact option chain (BaseUrl, ApiKey, cache partition, L1 caching policy) can
/// only ever diverge in one place.
/// </summary>
/// <remarks>
/// Enables the read-only L1 in-memory cache defaults shipped with
/// <c>MX.InvisionCommunity.Api.Client</c> 1.0.63: <c>ICoreApi.GetCoreHello</c> (60s),
/// <c>ICoreApi.GetMember</c> (30s), <c>IDownloadsApi.GetDownloadFile</c> (30s).
/// <c>IForumsApi</c> caches nothing, so <c>PostTopic</c> — the only Invision call this
/// consumer makes today (see <c>AdminActionTopics</c>) — is unaffected. There is no
/// read-mutate-re-read pattern for members or download files in this codebase, so
/// enabling the library defaults as-is is safe without any <c>NotCached</c> exclusions.
/// </remarks>
internal static class InvisionApiClientRegistration
{
    /// <summary>
    /// Stable, non-secret partition string. Reuses the Processor App's shared partition
    /// namespace (<see cref="RepositoryApiClientRegistration.CachePartition"/>).
    /// </summary>
    internal const string CachePartition = RepositoryApiClientRegistration.CachePartition;

    /// <summary>
    /// Registers the Invision API client with the same options the production host uses.
    /// Used by Program.cs at boot and by <c>InvisionApiClientRegistrationTests</c> to
    /// prove the DI graph composes with caching enabled.
    /// </summary>
    internal static IServiceCollection AddPortalServerEventsInvisionApiClient(
        this IServiceCollection services,
        string baseUrl,
        string apiKey)
    {
        return services.AddInvisionApiClient(options => options
            .WithBaseUrl(baseUrl)
            .WithApiKeyAuthentication(apiKey, "key", ApiKeyLocation.QueryParameter)
            .WithCachePartition(CachePartition)
            .WithCaching(c => c.UseLibraryDefaults()));
    }

    /// <summary>
    /// Convenience overload that reads the same configuration keys Program.cs used
    /// previously, so the boot path is a one-liner and the required-key error messages
    /// stay identical to the pre-refactor behaviour.
    /// </summary>
    internal static IServiceCollection AddPortalServerEventsInvisionApiClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var baseUrl = configuration["XtremeIdiots:Forums:BaseUrl"]
            ?? throw new InvalidOperationException("XtremeIdiots:Forums:BaseUrl is required");
        var apiKey = configuration["XtremeIdiots:Forums:ApiKey"]
            ?? throw new InvalidOperationException("XtremeIdiots:Forums:ApiKey is required");

        return services.AddPortalServerEventsInvisionApiClient(baseUrl, apiKey);
    }
}
