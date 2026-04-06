using System.Reflection;

using Azure.AI.ContentSafety;
using Azure.Identity;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;

using MX.GeoLocation.Api.Client.V1;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
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

        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services.AddRepositoryApiClient(options => options
            .WithBaseUrl(configuration["RepositoryApi:BaseUrl"] ?? throw new InvalidOperationException("RepositoryApi:BaseUrl is required"))
            .WithEntraIdAuthentication(configuration["RepositoryApi:ApplicationAudience"] ?? throw new InvalidOperationException("RepositoryApi:ApplicationAudience is required")));

        services.AddServersApiClient(options => options
            .WithBaseUrl(configuration["ServersIntegrationApi:BaseUrl"] ?? throw new InvalidOperationException("ServersIntegrationApi:BaseUrl is required"))
            .WithEntraIdAuthentication(configuration["ServersIntegrationApi:ApplicationAudience"] ?? throw new InvalidOperationException("ServersIntegrationApi:ApplicationAudience is required")));

        services.AddGeoLocationApiClient(options =>
        {
            options.WithBaseUrl(configuration["GeoLocationApi:BaseUrl"] ?? throw new InvalidOperationException("GeoLocationApi:BaseUrl is required"))
                .WithApiKeyAuthentication(configuration["GeoLocationApi:ApiKey"] ?? throw new InvalidOperationException("GeoLocationApi:ApiKey is required"), "Ocp-Apim-Subscription-Key")
                .WithEntraIdAuthentication(configuration["GeoLocationApi:ApplicationAudience"] ?? throw new InvalidOperationException("GeoLocationApi:ApplicationAudience is required"));
        });

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
        var csEndpoint = configuration["ContentSafety:Endpoint"];
        if (!string.IsNullOrEmpty(csEndpoint))
        {
            services.AddSingleton(_ => new ContentSafetyClient(
                new Uri(csEndpoint),
                new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ManagedIdentityClientId = configuration["AZURE_CLIENT_ID"]
                })));
        }
        else
        {
            services.AddSingleton(_ => new ContentSafetyClient(
                new Uri("https://not-configured.cognitiveservices.azure.com/"),
                new DefaultAzureCredential()));
        }

        services.AddSingleton<IChatModerationService, ChatModerationService>();
        services.AddTransient<IChatModerationPipeline, ChatModerationPipeline>();

        services.AddMemoryCache();
        services.AddHealthChecks();
    })
    .Build();

await host.RunAsync();
