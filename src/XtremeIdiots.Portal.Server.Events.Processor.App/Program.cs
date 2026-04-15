using System.Reflection;

using Azure.AI.ContentSafety;
using Azure.Identity;
using Azure.Messaging.ServiceBus;

using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;

using MX.Api.Client.Configuration;
using MX.Observability.ApplicationInsights.Extensions;
using MX.GeoLocation.Api.Client.V1;
using MX.InvisionCommunity.Api.Client;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;
using XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

var host = new HostBuilder()
    .ConfigureAppConfiguration(builder =>
    {
        builder.AddEnvironmentVariables();
        builder.AddUserSecrets(Assembly.GetExecutingAssembly(), true);

        var builtConfig = builder.Build();
        var appConfigEndpoint = builtConfig["AzureAppConfiguration:Endpoint"];

        if (!string.IsNullOrWhiteSpace(appConfigEndpoint))
        {
            var managedIdentityClientId = builtConfig["AzureAppConfiguration:ManagedIdentityClientId"];
            var environmentLabel = builtConfig["AzureAppConfiguration:Environment"];

            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = managedIdentityClientId,
            });

            builder.AddAzureAppConfiguration(options =>
            {
                options.Connect(new Uri(appConfigEndpoint), credential)
                    .Select("RepositoryApi:*", environmentLabel)
                    .Select("ServersIntegrationApi:*", environmentLabel)
                    .Select("GeoLocationApi:*", environmentLabel)
                    .Select("XtremeIdiots.Portal.Server.Events.Processor.App:*", environmentLabel)
                    .TrimKeyPrefix("XtremeIdiots.Portal.Server.Events.Processor.App:")
                    .Select("ApplicationInsights:*", environmentLabel)
                    .Select("XtremeIdiots:*", environmentLabel)
                    .Select("ContentSafety:*", environmentLabel)
                    .UseFeatureFlags(ff => ff.Label = environmentLabel)
                    .ConfigureRefresh(refresh =>
                    {
                        refresh.Register("Sentinel", environmentLabel, refreshAll: true)
                            .SetRefreshInterval(TimeSpan.FromMinutes(5));
                    });

                options.ConfigureKeyVault(kv =>
                {
                    kv.SetCredential(credential);
                    kv.SetSecretRefreshInterval(TimeSpan.FromHours(1));
                });
            });
        }
    })
    .ConfigureFunctionsWorkerDefaults(builder =>
    {
        builder.Services.AddAzureAppConfiguration();
        builder.UseAzureAppConfiguration();
    })
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;

        services.AddSingleton<ITelemetryInitializer, TelemetryInitializer>();
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        services.AddObservability();

        services.AddRepositoryApiClient(options => options
            .WithBaseUrl(configuration["RepositoryApi:BaseUrl"] ?? throw new InvalidOperationException("RepositoryApi:BaseUrl is required"))
            .WithEntraIdAuthentication(configuration["RepositoryApi:ApplicationAudience"] ?? throw new InvalidOperationException("RepositoryApi:ApplicationAudience is required")));

        services.AddServersApiClient(options => options
            .WithBaseUrl(configuration["ServersIntegrationApi:BaseUrl"] ?? throw new InvalidOperationException("ServersIntegrationApi:BaseUrl is required"))
            .WithEntraIdAuthentication(configuration["ServersIntegrationApi:ApplicationAudience"] ?? throw new InvalidOperationException("ServersIntegrationApi:ApplicationAudience is required")));

        var geoBaseUrl = configuration["GeoLocationApi:BaseUrl"];
        var geoApiKey = configuration["GeoLocationApi:ApiKey"];
        var geoAudience = configuration["GeoLocationApi:ApplicationAudience"];

        if (!string.IsNullOrEmpty(geoBaseUrl) && !string.IsNullOrEmpty(geoApiKey) && !string.IsNullOrEmpty(geoAudience))
        {
            services.AddGeoLocationApiClient(options =>
            {
                options.WithBaseUrl(geoBaseUrl)
                    .WithApiKeyAuthentication(geoApiKey, "Ocp-Apim-Subscription-Key")
                    .WithEntraIdAuthentication(geoAudience);
            });
        }
        else
        {
            // GeoLocation API not configured — GeoIP enrichment will be skipped at runtime
        }

        // Forum integration
        services.AddInvisionApiClient(options => options
            .WithBaseUrl(configuration["XtremeIdiots:Forums:BaseUrl"] ?? throw new InvalidOperationException("XtremeIdiots:Forums:BaseUrl is required"))
            .WithApiKeyAuthentication(configuration["XtremeIdiots:Forums:ApiKey"] ?? throw new InvalidOperationException("XtremeIdiots:Forums:ApiKey is required"), "key", ApiKeyLocation.QueryParameter));
        services.AddTransient<IAdminActionTopics, AdminActionTopics>();

        // Command framework
        services.AddTransient<IChatCommandProcessor, ChatCommandProcessor>();
        services.AddTransient<IRconResponseService, RconResponseService>();

        // Protected name enforcement
        services.AddTransient<IProtectedNameService, ProtectedNameService>();

        // Chat commands — add new commands here
        services.AddTransient<IChatCommand, MapVoteLikeCommand>();
        services.AddTransient<IChatCommand, MapVoteDislikeCommand>();

        // Feature management
        services.AddFeatureManagement();

        // Chat moderation services
        var csEndpoint = configuration["ContentSafety:Endpoint"]
            ?? throw new InvalidOperationException("ContentSafety:Endpoint is required");

        services.AddSingleton(_ => new ContentSafetyClient(
            new Uri(csEndpoint),
            new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = configuration["AZURE_CLIENT_ID"]
            })));

        services.AddSingleton<IChatModerationService, ChatModerationService>();
        services.AddTransient<IChatModerationPipeline, ChatModerationPipeline>();

        // Service Bus client for manual DLQ access
        var serviceBusFqns = configuration["ServiceBusConnection:fullyQualifiedNamespace"];
        if (!string.IsNullOrEmpty(serviceBusFqns))
        {
            services.AddSingleton(_ => new ServiceBusClient(serviceBusFqns, new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = configuration["AZURE_CLIENT_ID"]
            })));
        }
        else
        {
            throw new InvalidOperationException("ServiceBusConnection:fullyQualifiedNamespace is required");
        }

        services.AddMemoryCache();
        services.AddHealthChecks();
    })
    .Build();

await host.RunAsync();
