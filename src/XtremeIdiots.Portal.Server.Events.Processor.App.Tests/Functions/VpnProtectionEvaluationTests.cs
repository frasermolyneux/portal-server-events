using System.Net;
using System.Text;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using MX.Api.Abstractions;
using MX.GeoLocation.Abstractions.Models.V1_1;
using MX.GeoLocation.Api.Client.V1;

using Newtonsoft.Json;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;
using XtremeIdiots.Portal.Server.Events.Processor.App.Publishing;
using XtremeIdiots.Portal.Server.Events.Processor.App.VpnProtection;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public sealed class VpnProtectionEvaluationTests
{
    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly Mock<ICod4xVpnProtectionPolicyProvider> policyProvider = new();
    private readonly Mock<IVpnProtectionSettingsProvider> settingsProvider = new();
    private readonly Mock<IVpnProtectionEvaluator> evaluator = new();
    private readonly Mock<IGeoLocationApiClient> geoLocationApiClient = new();
    private readonly Mock<IVersionedGeoLookupApi> versionedGeoLookupApi = new();
    private readonly Mock<MX.GeoLocation.Abstractions.Interfaces.V1_1.IGeoLookupApi> geoLookupApi = new();
    private readonly Mock<IRepositoryApiClient> repositoryApiClient = new();
    private readonly Mock<IVersionedPlayersApi> versionedPlayers = new();
    private readonly Mock<IPlayersApi> playersApi = new();
    private readonly Mock<IBanAppliedPublisher> banAppliedPublisher = new();
    private readonly Mock<ILogger<VpnProtectionEvaluation>> logger = new();

    public VpnProtectionEvaluationTests()
    {
        policyProvider
            .Setup(x => x.IsEnabledAsync(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        settingsProvider
            .Setup(x => x.GetEffectiveSettingsAsync(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveVpnProtectionSettings { Enabled = true });
        versionedGeoLookupApi.Setup(x => x.V1_1).Returns(geoLookupApi.Object);
        geoLocationApiClient.Setup(x => x.GeoLookup).Returns(versionedGeoLookupApi.Object);

        versionedPlayers.Setup(x => x.V1).Returns(playersApi.Object);
        repositoryApiClient.Setup(x => x.Players).Returns(versionedPlayers.Object);
        // Default: player exists with no tags, so the exemption never triggers unless a test opts in.
        playersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "player-guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(new ApiResult<PlayerDto>(HttpStatusCode.OK, new ApiResponse<PlayerDto>(CreatePlayerDto())));
    }

    [Fact]
    public async Task EvaluateVpnProtection_InvalidRequest_ReturnsBadRequest()
    {
        var (request, context) = CreateRequest("{}");

        var response = await CreateSut().EvaluateVpnProtection(request, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EvaluateVpnProtection_PolicyDisabled_ReturnsNoMatchWithoutLookup()
    {
        policyProvider
            .Setup(x => x.IsEnabledAsync(ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var (request, context) = CreateRequest(CreateValidRequestJson());

        var response = await CreateSut().EvaluateVpnProtection(request, context);
        var body = await ReadBody(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"matched\":false", body, StringComparison.Ordinal);
        geoLookupApi.Verify(
            x => x.GetIpIntelligence(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateVpnProtection_MatchingDecision_ReturnsActionAndRuleIds()
    {
        var intelligence = JsonConvert.DeserializeObject<IpIntelligenceDto>(
            JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = true } }))!;
        geoLookupApi
            .Setup(x => x.GetIpIntelligence("198.51.100.10", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(
                HttpStatusCode.OK,
                new ApiResponse<IpIntelligenceDto>(intelligence)));
        evaluator
            .Setup(x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IReadOnlyCollection<string>>(), intelligence))
            .Returns(new VpnProtectionDecision
            {
                Action = VpnProtectionAction.Ban,
                Reason = "VPN Protection",
                MatchedRules =
                [
                    new VpnProtectionRuleMatch
                    {
                        RuleId = "vpn",
                        Signal = VpnProtectionSignal.ProxyCheckIsVpn,
                        ActualValue = "True",
                        ExpectedValue = "true",
                        Action = VpnProtectionAction.Ban,
                        Reason = "VPN Protection",
                        OrderIndex = 0
                    }
                ]
            });
        var (request, context) = CreateRequest(CreateValidRequestJson());

        var response = await CreateSut().EvaluateVpnProtection(request, context);
        var body = await ReadBody(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"matched\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"Ban\"", body, StringComparison.Ordinal);
        Assert.Contains("\"matchedRuleIds\":[\"vpn\"]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateVpnProtection_IntelligenceUnavailable_ReturnsServiceUnavailable()
    {
        geoLookupApi
            .Setup(x => x.GetIpIntelligence("198.51.100.10", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(HttpStatusCode.ServiceUnavailable));
        var (request, context) = CreateRequest(CreateValidRequestJson());

        var response = await CreateSut().EvaluateVpnProtection(request, context);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task EvaluateVpnProtection_ExcludedPlayer_ResolvesTagsAndReturnsNoMatch()
    {
        var intelligence = JsonConvert.DeserializeObject<IpIntelligenceDto>(
            JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = true } }))!;
        geoLookupApi
            .Setup(x => x.GetIpIntelligence("198.51.100.10", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(
                HttpStatusCode.OK,
                new ApiResponse<IpIntelligenceDto>(intelligence)));
        // The player carries an excluded tag; the endpoint must resolve it and hand it to the shared
        // evaluator, which short-circuits to an exclusion (surfaced to the plugin as no match).
        playersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "player-guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(new ApiResult<PlayerDto>(HttpStatusCode.OK, new ApiResponse<PlayerDto>(CreatePlayerDto("Trusted VPN"))));
        evaluator
            .Setup(x => x.Evaluate(
                It.IsAny<EffectiveVpnProtectionSettings>(),
                It.Is<IReadOnlyCollection<string>>(tags => tags.Contains("Trusted VPN")),
                intelligence))
            .Returns(VpnProtectionDecision.Excluded("Trusted VPN"));
        var (request, context) = CreateRequest(CreateValidRequestJson());

        var response = await CreateSut().EvaluateVpnProtection(request, context);
        var body = await ReadBody(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"matched\":false", body, StringComparison.Ordinal);
        evaluator.Verify(
            x => x.Evaluate(
                It.IsAny<EffectiveVpnProtectionSettings>(),
                It.Is<IReadOnlyCollection<string>>(tags => tags.Contains("Trusted VPN")),
                intelligence),
            Times.Once);
    }

    [Fact]
    public async Task EvaluateVpnProtection_PlayerTagLookupFails_EvaluatesWithNoTagsFailClosed()
    {
        var intelligence = JsonConvert.DeserializeObject<IpIntelligenceDto>(
            JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = true } }))!;
        geoLookupApi
            .Setup(x => x.GetIpIntelligence("198.51.100.10", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(
                HttpStatusCode.OK,
                new ApiResponse<IpIntelligenceDto>(intelligence)));
        // Tag lookup fails: the endpoint must fail closed (empty tag set), so no exemption can be
        // inferred and evaluation still proceeds against the IP intelligence rules.
        playersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "player-guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(new ApiResult<PlayerDto>(HttpStatusCode.NotFound));
        evaluator
            .Setup(x => x.Evaluate(
                It.IsAny<EffectiveVpnProtectionSettings>(),
                It.Is<IReadOnlyCollection<string>>(tags => tags.Count == 0),
                intelligence))
            .Returns(new VpnProtectionDecision
            {
                Action = VpnProtectionAction.Ban,
                Reason = "VPN Protection",
                MatchedRules =
                [
                    new VpnProtectionRuleMatch
                    {
                        RuleId = "vpn",
                        Signal = VpnProtectionSignal.ProxyCheckIsVpn,
                        ActualValue = "True",
                        ExpectedValue = "true",
                        Action = VpnProtectionAction.Ban,
                        Reason = "VPN Protection",
                        OrderIndex = 0
                    }
                ]
            });
        var (request, context) = CreateRequest(CreateValidRequestJson());

        var response = await CreateSut().EvaluateVpnProtection(request, context);
        var body = await ReadBody(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"matched\":true", body, StringComparison.Ordinal);
        evaluator.Verify(
            x => x.Evaluate(
                It.IsAny<EffectiveVpnProtectionSettings>(),
                It.Is<IReadOnlyCollection<string>>(tags => tags.Count == 0),
                intelligence),
            Times.Once);
    }

    [Fact]
    public async Task EvaluateVpnProtection_BanDecision_PublishesBanAppliedImport()
    {
        var intelligence = JsonConvert.DeserializeObject<IpIntelligenceDto>(
            JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = true } }))!;
        geoLookupApi
            .Setup(x => x.GetIpIntelligence("198.51.100.10", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(
                HttpStatusCode.OK,
                new ApiResponse<IpIntelligenceDto>(intelligence)));
        evaluator
            .Setup(x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IReadOnlyCollection<string>>(), intelligence))
            .Returns(Decision(VpnProtectionAction.Ban));
        var (request, context) = CreateRequest(CreateValidRequestJson());

        var response = await CreateSut().EvaluateVpnProtection(request, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        banAppliedPublisher.Verify(
            x => x.PublishAsync(
                It.Is<BanAppliedEvent>(e =>
                    e.ServerId == ServerId &&
                    e.GameType == nameof(GameType.CallOfDuty4x) &&
                    e.PlayerGuid == "player-guid" &&
                    e.PlayerName == "TestPlayer" &&
                    !e.IsTemporary &&
                    e.Source == "CoD4xVpnProtection" &&
                    e.Reason == "VPN Protection"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EvaluateVpnProtection_NoMatch_DoesNotPublishBanApplied()
    {
        var intelligence = JsonConvert.DeserializeObject<IpIntelligenceDto>(
            JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = false } }))!;
        geoLookupApi
            .Setup(x => x.GetIpIntelligence("198.51.100.10", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(
                HttpStatusCode.OK,
                new ApiResponse<IpIntelligenceDto>(intelligence)));
        evaluator
            .Setup(x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IReadOnlyCollection<string>>(), intelligence))
            .Returns(VpnProtectionDecision.NoMatch);
        var (request, context) = CreateRequest(CreateValidRequestJson());

        var response = await CreateSut().EvaluateVpnProtection(request, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        banAppliedPublisher.Verify(
            x => x.PublishAsync(It.IsAny<BanAppliedEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateVpnProtection_KickDecision_DoesNotPublishBanApplied()
    {
        var intelligence = JsonConvert.DeserializeObject<IpIntelligenceDto>(
            JsonConvert.SerializeObject(new { ProxyCheck = new { IsVpn = true } }))!;
        geoLookupApi
            .Setup(x => x.GetIpIntelligence("198.51.100.10", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(
                HttpStatusCode.OK,
                new ApiResponse<IpIntelligenceDto>(intelligence)));
        evaluator
            .Setup(x => x.Evaluate(It.IsAny<EffectiveVpnProtectionSettings>(), It.IsAny<IReadOnlyCollection<string>>(), intelligence))
            .Returns(Decision(VpnProtectionAction.Kick));
        var (request, context) = CreateRequest(CreateValidRequestJson());

        var response = await CreateSut().EvaluateVpnProtection(request, context);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        banAppliedPublisher.Verify(
            x => x.PublishAsync(It.IsAny<BanAppliedEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static VpnProtectionDecision Decision(VpnProtectionAction action) => new()
    {
        Action = action,
        Reason = "VPN Protection",
        MatchedRules =
        [
            new VpnProtectionRuleMatch
            {
                RuleId = "vpn",
                Signal = VpnProtectionSignal.ProxyCheckIsVpn,
                ActualValue = "True",
                ExpectedValue = "true",
                Action = action,
                Reason = "VPN Protection",
                OrderIndex = 0
            }
        ]
    };

    private VpnProtectionEvaluation CreateSut() => new(
        policyProvider.Object,
        settingsProvider.Object,
        evaluator.Object,
        geoLocationApiClient.Object,
        repositoryApiClient.Object,
        banAppliedPublisher.Object,
        logger.Object);

    private static (HttpRequestData Request, FunctionContext Context) CreateRequest(string json)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new WorkerOptions
        {
            Serializer = new Azure.Core.Serialization.JsonObjectSerializer()
        }));
        var serviceProvider = services.BuildServiceProvider();
        var context = new Mock<FunctionContext>();
        context.Setup(x => x.InstanceServices).Returns(serviceProvider);
        context.Setup(x => x.CancellationToken).Returns(CancellationToken.None);

        var request = new Mock<HttpRequestData>(context.Object);
        request.Setup(x => x.Body).Returns(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        request.Setup(x => x.Url).Returns(new Uri("https://localhost/api/vpn-protection/evaluate"));
        request.Setup(x => x.CreateResponse()).Returns(() =>
        {
            var response = new Mock<HttpResponseData>(context.Object);
            response.SetupProperty(x => x.StatusCode);
            response.SetupProperty(x => x.Headers, new HttpHeadersCollection());
            response.Setup(x => x.Body).Returns(new MemoryStream());
            return response.Object;
        });
        return (request.Object, context.Object);
    }

    private static async Task<string> ReadBody(HttpResponseData response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static PlayerDto CreatePlayerDto(params string[] tagNames)
    {
        var json = JsonConvert.SerializeObject(new
        {
            PlayerId = Guid.NewGuid(),
            Username = "TestPlayer",
            Tags = tagNames.Select(name => new { Tag = new { Name = name } }).ToArray()
        });
        return JsonConvert.DeserializeObject<PlayerDto>(json)!;
    }

    private static string CreateValidRequestJson() => $$"""
        {
            "serverId": "{{ServerId}}",
            "ipAddress": "198.51.100.10",
            "playerGuid": "player-guid",
            "username": "TestPlayer",
            "slotId": 4
        }
        """;
}