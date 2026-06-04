using System.Net;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;
using MX.GeoLocation.Abstractions.Models.V1_1;
using MX.GeoLocation.Api.Client.V1;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class WhoAmICommandTests
{
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IVersionedPlayersApi> _versionedPlayers = new();
    private readonly Mock<IPlayersApi> _playersApi = new();
    private readonly Mock<IGeoLocationApiClient> _geoClient = new();
    private readonly Mock<IVersionedGeoLookupApi> _versionedGeoLookup = new();
    private readonly Mock<MX.GeoLocation.Abstractions.Interfaces.V1_1.IGeoLookupApi> _geoLookupApi = new();
    private readonly Mock<IRconResponseService> _rconResponseService = new();
    private readonly Mock<ILogger<WhoAmICommand>> _logger = new();

    private readonly WhoAmICommand _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public WhoAmICommandTests()
    {
        _versionedPlayers.Setup(x => x.V1).Returns(_playersApi.Object);
        _repoClient.Setup(x => x.Players).Returns(_versionedPlayers.Object);

        _versionedGeoLookup.Setup(x => x.V1_1).Returns(_geoLookupApi.Object);
        _geoClient.Setup(x => x.GeoLookup).Returns(_versionedGeoLookup.Object);

        _rconResponseService
            .Setup(x => x.TryTellAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var services = new ServiceCollection();
        services.AddSingleton(_geoClient.Object);

        _sut = new WhoAmICommand(
            _repoClient.Object,
            services.BuildServiceProvider(),
            _rconResponseService.Object,
            _logger.Object);
    }

    private static CommandContext CreateContext(string message = "!whoami") => new()
    {
        ServerId = TestServerId,
        GameType = "CallOfDuty4",
        PlayerGuid = "abc123",
        Username = "PlayerFromChat",
        SlotId = 7,
        Message = message,
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        SequenceId = 1,
        PlayerId = TestPlayerId,
        AuthorizationSnapshot = new CommandAuthorizationSnapshot
        {
            Tags = new HashSet<string>(["HeadAdmin", "GameAdmin"], StringComparer.OrdinalIgnoreCase),
            TagsResolved = true
        }
    };

    [Fact]
    public async Task ExecuteAsync_WhenValid_ReturnsSuccessAndSendsPrivateSummary()
    {
        var playerDto = CreatePlayerDto(TestPlayerId, "ProfileName", "203.0.113.50");
        _playersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        var geo = CreateIpIntelligence(cityName: "London", countryName: "United Kingdom", countryCode: "GB");
        _geoLookupApi
            .Setup(x => x.GetIpIntelligence("203.0.113.50", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(geo));

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("WhoAmI response delivered.", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            7,
            "Your name is ProfileName, ip is 203.0.113.50, location is London, United Kingdom, your roles are GameAdmin, HeadAdmin.",
            "PlayerFromChat",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenUsageInvalid_ReturnsFailedAndSendsUsage()
    {
        var result = await _sut.ExecuteAsync(CreateContext("!whoami extra"));

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Usage: !whoami", result.ResponseMessage);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            7,
            "Usage: !whoami",
            "PlayerFromChat",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenGeoLookupFails_UsesUnknownLocationAndNoRolesFallback()
    {
        var playerDto = CreatePlayerDto(TestPlayerId, "ProfileName", "198.51.100.10");
        _playersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _geoLookupApi
            .Setup(x => x.GetIpIntelligence("198.51.100.10", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<IpIntelligenceDto>(HttpStatusCode.BadRequest));

        var context = CreateContext() with
        {
            AuthorizationSnapshot = new CommandAuthorizationSnapshot
            {
                Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                TagsResolved = true
            }
        };

        var result = await _sut.ExecuteAsync(context);

        Assert.True(result.Handled);
        Assert.True(result.Success);
        Assert.Equal("WhoAmI response delivered.", result.ResponseMessage);

        _rconResponseService.Verify(x => x.TryTellAsync(
            TestServerId,
            "abc123",
            7,
            "Your name is ProfileName, ip is 198.51.100.10, location is unknown, your roles are none.",
            "PlayerFromChat",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenGameTypeInvalid_ReturnsFailedAndSkipsPlayerLookup()
    {
        var context = CreateContext() with { GameType = "not-a-game" };

        var result = await _sut.ExecuteAsync(context);

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Unable to resolve your game type.", result.ResponseMessage);

        _playersApi.Verify(x => x.GetPlayerByGameType(It.IsAny<GameType>(), It.IsAny<string>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPlayerLookupFails_ReturnsFailed()
    {
        _playersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123", PlayerEntityOptions.Tags))
            .ReturnsAsync(new ApiResult<PlayerDto>(HttpStatusCode.NotFound));

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Unable to load your profile right now.", result.ResponseMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPrivateTellFails_ReturnsFailed()
    {
        var playerDto = CreatePlayerDto(TestPlayerId, "ProfileName", "198.51.100.10");
        _playersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _geoLookupApi
            .Setup(x => x.GetIpIntelligence("198.51.100.10", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(CreateIpIntelligence(countryCode: "GB")));

        _rconResponseService
            .Setup(x => x.TryTellAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Handled);
        Assert.False(result.Success);
        Assert.Equal("Unable to send !whoami response right now. Please try again.", result.ResponseMessage);
    }

    private static PlayerDto CreatePlayerDto(Guid playerId, string username, string ipAddress)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            PlayerId = playerId,
            Username = username,
            IpAddress = ipAddress,
            Tags = new object[0]
        });

        return Newtonsoft.Json.JsonConvert.DeserializeObject<PlayerDto>(json)!;
    }

    private static ApiResult<T> SuccessResult<T>(T data)
    {
        return new ApiResult<T>(HttpStatusCode.OK, new ApiResponse<T>(data));
    }

    private static IpIntelligenceDto CreateIpIntelligence(string? cityName = null, string? countryName = null, string? countryCode = null)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            CityName = cityName,
            CountryName = countryName,
            CountryCode = countryCode
        });

        return Newtonsoft.Json.JsonConvert.DeserializeObject<IpIntelligenceDto>(json)!;
    }
}
