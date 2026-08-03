using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App;

/// <summary>
/// Single source of truth for the Servers Integration API client DI registration used by
/// the Processor App. Program.cs and the startup-composition test both call this helper.
/// </summary>
/// <remarks>
/// Servers 4.1.14 ships no default cache policies, so this helper deliberately does not
/// call <c>WithCaching</c>: the bump is currency + crash-safety only. It is included in
/// the boot-composition tests to prove the DI graph still resolves every typed sub-API
/// after the package upgrade (Rcon variants, Maps, etc).
/// </remarks>
internal static class ServersApiClientRegistration
{
    /// <summary>
    /// Registers the Servers API client with the same options the production host uses.
    /// </summary>
    internal static IServiceCollection AddPortalServerEventsServersApiClient(
        this IServiceCollection services,
        string baseUrl,
        string audience)
    {
        return services.AddServersApiClient(options => options
            .WithBaseUrl(baseUrl)
            .WithEntraIdAuthentication(audience));
    }

    /// <summary>
    /// Convenience overload that reads the same configuration keys Program.cs used
    /// previously, keeping the boot path a one-liner and preserving the required-key
    /// error messages.
    /// </summary>
    internal static IServiceCollection AddPortalServerEventsServersApiClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var baseUrl = configuration["ServersIntegrationApi:BaseUrl"]
            ?? throw new InvalidOperationException("ServersIntegrationApi:BaseUrl is required");
        var audience = configuration["ServersIntegrationApi:ApplicationAudience"]
            ?? throw new InvalidOperationException("ServersIntegrationApi:ApplicationAudience is required");

        return services.AddPortalServerEventsServersApiClient(baseUrl, audience);
    }
}
