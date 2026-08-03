using Microsoft.Extensions.DependencyInjection;

using MX.InvisionCommunity.Api.Abstractions;
using MX.InvisionCommunity.Api.Abstractions.Interfaces;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Startup;

/// <summary>
/// Startup-time DI resolution tests for the Invision Community API client registration
/// used by the Processor App. These replicate the production registration in
/// <see cref="InvisionApiClientRegistration"/> and prove the container can build and
/// resolve every typed sub-client (Core / Downloads / Forums) with the library-default
/// L1 read-only caching enabled.
/// </summary>
/// <remarks>
/// The only Invision call this consumer makes today is
/// <c>Forums.PostTopic</c> — which is intentionally uncached — so enabling library
/// defaults is safe. This test locks in that the DI graph still composes; if a future
/// package bump reintroduces the cross-sub-API expression walk that crashed 4.2.21-era
/// consumers, boot fails here rather than in production.
/// </remarks>
public class InvisionApiClientRegistrationTests
{
    private const string BaseUrl = "https://forums.test.local";
    private const string ApiKey = "test-forums-api-key";

    [Fact]
    public void ProductionRegistration_BuildsAndResolvesInvisionClient()
    {
        var provider = BuildProductionServiceProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IInvisionApiClient>();

        Assert.NotNull(client);
    }

    [Fact]
    public void ProductionRegistration_ResolvesTypedSubClients()
    {
        var provider = BuildProductionServiceProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IInvisionApiClient>();

        ICoreApi core = client.Core;
        IDownloadsApi downloads = client.Downloads;
        IForumsApi forums = client.Forums;

        Assert.NotNull(core);
        Assert.NotNull(downloads);
        Assert.NotNull(forums);
    }

    private static ServiceProvider BuildProductionServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddPortalServerEventsInvisionApiClient(BaseUrl, ApiKey);
        return services.BuildServiceProvider(validateScopes: true);
    }
}
