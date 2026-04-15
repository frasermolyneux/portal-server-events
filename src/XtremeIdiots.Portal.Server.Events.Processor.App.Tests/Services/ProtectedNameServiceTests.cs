using MX.Observability.ApplicationInsights.Auditing;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Services;

public class ProtectedNameServiceTests
{
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IVersionedPlayersApi> _versionedPlayers = new();
    private readonly Mock<IPlayersApi> _playersApi = new();
    private readonly Mock<IVersionedAdminActionsApi> _versionedAdminActions = new();
    private readonly Mock<IAdminActionsApi> _adminActionsApi = new();
    private readonly Mock<IRconApi> _rconApi = new();
    private readonly Mock<ILogger<ProtectedNameService>> _logger = new();
    private readonly Mock<IAuditLogger> _auditLogger = new();
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ProtectedNameService _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OwnerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string BotAdminId = "44444444-4444-4444-4444-444444444444";

    public ProtectedNameServiceTests()
    {
        _versionedPlayers.Setup(x => x.V1).Returns(_playersApi.Object);
        _repoClient.Setup(x => x.Players).Returns(_versionedPlayers.Object);

        _versionedAdminActions.Setup(x => x.V1).Returns(_adminActionsApi.Object);
        _repoClient.Setup(x => x.AdminActions).Returns(_versionedAdminActions.Object);

        _adminActionsApi
            .Setup(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        _rconApi
            .Setup(x => x.BanPlayerWithVerification(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync(SuccessResult());

        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ContentSafety:BotAdminId"] = BotAdminId
            })
            .Build();

        _sut = new ProtectedNameService(
            _repoClient.Object,
            _rconApi.Object,
            _cache,
            _auditLogger.Object,
            _configuration,
            _logger.Object);
    }

    private static ProtectedNameContext CreateContext(
        string? username = null,
        Guid? playerId = null,
        int slotId = 3) => new()
    {
        ServerId = TestServerId,
        GameType = "CallOfDuty4",
        Username = username ?? "TestPlayer",
        PlayerId = playerId ?? TestPlayerId,
        SlotId = slotId
    };

    private void SetupProtectedNames(params (string name, Guid ownerId)[] entries)
    {
        var dtos = entries.Select(e => CreateProtectedNameDto(e.name, e.ownerId)).ToList();
        var collection = new CollectionModel<ProtectedNameDto>(dtos);

        _playersApi
            .Setup(x => x.GetProtectedNames(0, 1000))
            .ReturnsAsync(new ApiResult<CollectionModel<ProtectedNameDto>>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<ProtectedNameDto>>(collection)));
    }

    private void SetupOwnerLookup(Guid ownerId, string ownerUsername)
    {
        var ownerDto = CreatePlayerDtoWithUsername(ownerId, ownerUsername);
        _playersApi
            .Setup(x => x.GetPlayer(ownerId, PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(ownerDto));
    }

    private static ProtectedNameDto CreateProtectedNameDto(string name, Guid ownerId)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            ProtectedNameId = Guid.NewGuid(),
            PlayerId = ownerId,
            Name = name,
            CreatedOn = DateTime.UtcNow.AddDays(-30),
            CreatedByUserProfileId = Guid.NewGuid()
        });
        return Newtonsoft.Json.JsonConvert.DeserializeObject<ProtectedNameDto>(json)!;
    }

    private static PlayerDto CreatePlayerDtoWithUsername(Guid playerId, string username)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { PlayerId = playerId, Username = username });
        return Newtonsoft.Json.JsonConvert.DeserializeObject<PlayerDto>(json)!;
    }

    [Fact]
    public async Task CheckAsync_WhenNameMatchesProtectedName_KicksAndBans()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));
        SetupOwnerLookup(OwnerId, "OwnerGuy");

        await _sut.CheckAsync(CreateContext(username: "TestPlayer"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto =>
                dto.PlayerId == TestPlayerId &&
                dto.Type == AdminActionType.Ban &&
                dto.Text.Contains("Protected Name Violation") &&
                dto.Text.Contains("TestPlayer") &&
                dto.Text.Contains("OwnerGuy") &&
                dto.AdminId == BotAdminId),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconApi.Verify(x => x.BanPlayerWithVerification(TestServerId, 3, "TestPlayer"), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_WhenNameIsOwner_NoAction()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer", playerId: OwnerId));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);

        _rconApi.Verify(x => x.BanPlayerWithVerification(
            It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_WhenNoMatch_NoAction()
    {
        SetupProtectedNames(("AdminUser", OwnerId));

        await _sut.CheckAsync(CreateContext(username: "SomeRandomPlayer"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);

        _rconApi.Verify(x => x.BanPlayerWithVerification(
            It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_WhenSubstringMatch_DetectsViolation()
    {
        SetupProtectedNames(("Admin", OwnerId));
        SetupOwnerLookup(OwnerId, "RealAdmin");

        // Player name contains the protected name as a substring
        await _sut.CheckAsync(CreateContext(username: "FakeAdminHere"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto =>
                dto.PlayerId == TestPlayerId &&
                dto.Type == AdminActionType.Ban),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconApi.Verify(x => x.BanPlayerWithVerification(TestServerId, 3, "FakeAdminHere"), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_WhenProtectedNameContainsPlayerName_DetectsViolation()
    {
        // Protected name "SuperAdmin" contains player name "Admin"
        SetupProtectedNames(("SuperAdmin", OwnerId));
        SetupOwnerLookup(OwnerId, "RealSuperAdmin");

        await _sut.CheckAsync(CreateContext(username: "Admin"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto => dto.Type == AdminActionType.Ban),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_WhenApiFails_DoesNotThrow()
    {
        _playersApi
            .Setup(x => x.GetProtectedNames(0, 1000))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        // Should not throw
        await _sut.CheckAsync(CreateContext());
    }

    [Fact]
    public async Task CheckAsync_CachesProtectedNames()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));
        SetupOwnerLookup(OwnerId, "OwnerGuy");

        // First call — fetches from API
        await _sut.CheckAsync(CreateContext(username: "TestPlayer"));

        // Second call — should use cache, not call API again
        await _sut.CheckAsync(CreateContext(username: "TestPlayer"));

        _playersApi.Verify(x => x.GetProtectedNames(0, 1000), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_CaseInsensitiveMatch()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));
        SetupOwnerLookup(OwnerId, "OwnerGuy");

        await _sut.CheckAsync(CreateContext(username: "TESTPLAYER"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto => dto.Type == AdminActionType.Ban),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_SlotIdZero_SkipsCheck()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer", slotId: 0));

        _playersApi.Verify(x => x.GetProtectedNames(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_OwnerLookupFails_StillEnforces()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));

        _playersApi
            .Setup(x => x.GetPlayer(OwnerId, PlayerEntityOptions.None))
            .ThrowsAsync(new HttpRequestException("API error"));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer"));

        // Should still create admin action — with owner ID as fallback in reason text
        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto =>
                dto.PlayerId == TestPlayerId &&
                dto.Type == AdminActionType.Ban &&
                dto.Text.Contains(OwnerId.ToString())),
            It.IsAny<CancellationToken>()), Times.Once);

        _rconApi.Verify(x => x.BanPlayerWithVerification(TestServerId, 3, "TestPlayer"), Times.Once);
    }
}
