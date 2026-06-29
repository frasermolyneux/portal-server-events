using MX.Observability.ApplicationInsights.Auditing;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
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
    private readonly Mock<IServersApiClient> _serversApiClient = new();
    private readonly Mock<IVersionedCoD4xRconApi> _versionedCoD4xRconApi = new();
    private readonly Mock<ICoD4xRconApi> _coD4xRconApi = new();
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

        _versionedCoD4xRconApi.Setup(x => x.V1).Returns(_coD4xRconApi.Object);
        _serversApiClient.Setup(x => x.CoD4xRcon).Returns(_versionedCoD4xRconApi.Object);

        _adminActionsApi
            .Setup(x => x.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        SetupActiveBans();

        _coD4xRconApi
            .Setup(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players =
                    [
                        new CoD4xStatusPlayerDto
                        {
                            Num = 3,
                            PlayerIdentifier = "test-guid",
                            Name = "TestPlayer"
                        }
                    ]
                })));

        _coD4xRconApi
            .Setup(x => x.BanClient(It.IsAny<Guid>(), It.IsAny<CoD4xClientReasonRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(System.Net.HttpStatusCode.OK, new ApiResponse<string>("ok")));

        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ContentSafety:BotAdminId"] = BotAdminId
            })
            .Build();

        _sut = new ProtectedNameService(
            _repoClient.Object,
            _serversApiClient.Object,
            _cache,
            _auditLogger.Object,
            _configuration,
            _logger.Object);
    }

    private static ProtectedNameContext CreateContext(
        string? username = null,
        Guid? playerId = null,
        int slotId = 3,
        string gameType = "CallOfDuty4") => new()
        {
            ServerId = TestServerId,
            GameType = gameType,
            Username = username ?? "TestPlayer",
            PlayerId = playerId ?? TestPlayerId,
            SlotId = slotId
        };

    private void SetupProtectedNames(params (string name, Guid ownerId)[] entries)
    {
        var dtos = entries.Select(e => CreateProtectedNameDto(e.name, e.ownerId)).ToList();
        var collection = new CollectionModel<ProtectedNameDto>(dtos);

        _playersApi
            .Setup(x => x.GetProtectedNames(0, 500, It.IsAny<GameType?>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ProtectedNameDto>>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<ProtectedNameDto>>(collection)));
    }

    private void SetupOwnerLookup(Guid ownerId, string ownerUsername)
    {
        var ownerDto = CreatePlayerDtoWithUsername(ownerId, ownerUsername, GameType.CallOfDuty4);
        _playersApi
            .Setup(x => x.GetPlayer(ownerId, PlayerEntityOptions.None))
            .ReturnsAsync(SuccessResult(ownerDto));
    }

    private void SetupActiveBans(params AdminActionDto[] adminActions)
    {
        var collection = new CollectionModel<AdminActionDto>(adminActions.ToList());

        _adminActionsApi
            .Setup(x => x.GetAdminActions(
                It.IsAny<GameType?>(),
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<AdminActionFilter?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<AdminActionOrder?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<AdminActionDto>>(collection)));
    }

    private static ProtectedNameDto CreateProtectedNameDto(string name, Guid ownerId)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            ProtectedNameId = Guid.NewGuid(),
            PlayerId = ownerId,
            Name = name,
            OwnerGameType = GameType.CallOfDuty4,
            CreatedOn = DateTime.UtcNow.AddDays(-30),
            CreatedByUserProfileId = Guid.NewGuid()
        });
        return Newtonsoft.Json.JsonConvert.DeserializeObject<ProtectedNameDto>(json)!;
    }

    private static PlayerDto CreatePlayerDtoWithUsername(Guid playerId, string username, GameType gameType)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { PlayerId = playerId, Username = username, GameType = gameType });
        return Newtonsoft.Json.JsonConvert.DeserializeObject<PlayerDto>(json)!;
    }

    private static AdminActionDto CreateAdminActionDto(AdminActionType type, string text)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            AdminActionId = Guid.NewGuid(),
            PlayerId = TestPlayerId,
            UserProfileId = (Guid?)null,
            ForumTopicId = (int?)null,
            Type = type,
            Text = text,
            Created = DateTime.UtcNow,
            Expires = (DateTime?)null,
            Player = new
            {
                PlayerId = TestPlayerId,
                Username = "TestPlayer",
                GameType = GameType.CallOfDuty4
            }
        });

        return Newtonsoft.Json.JsonConvert.DeserializeObject<AdminActionDto>(json)!;
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

        _adminActionsApi.Verify(x => x.GetAdminActions(
            GameType.CallOfDuty4,
            TestPlayerId,
            null,
            AdminActionFilter.ActiveBans,
            0,
            50,
            AdminActionOrder.CreatedDesc,
            It.IsAny<CancellationToken>()), Times.Once);

        _coD4xRconApi.Verify(x => x.BanClient(
            TestServerId,
            It.Is<CoD4xClientReasonRequestDto>(r => r.ClientId == 3),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_WhenNameIsOwner_NoAction()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer", playerId: OwnerId));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);

        _coD4xRconApi.Verify(x => x.BanClient(
            It.IsAny<Guid>(), It.IsAny<CoD4xClientReasonRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_RequestsProtectedNamesForContextGame()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer", gameType: "CallOfDuty4x"));

        _playersApi.Verify(x => x.GetProtectedNames(0, 500, GameType.CallOfDuty4x), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_WhenReturnedProtectedNameIsCrossGame_NoAction()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer", gameType: "CallOfDuty4x"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);

        _coD4xRconApi.Verify(x => x.BanClient(
            It.IsAny<Guid>(), It.IsAny<CoD4xClientReasonRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);

        _playersApi.Verify(x => x.GetPlayer(It.IsAny<Guid>(), It.IsAny<PlayerEntityOptions>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_WhenNoMatch_NoAction()
    {
        SetupProtectedNames(("AdminUser", OwnerId));

        await _sut.CheckAsync(CreateContext(username: "SomeRandomPlayer"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);

        _coD4xRconApi.Verify(x => x.BanClient(
            It.IsAny<Guid>(), It.IsAny<CoD4xClientReasonRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_WhenSubstringMatch_DetectsViolation()
    {
        SetupProtectedNames(("Admin", OwnerId));
        SetupOwnerLookup(OwnerId, "RealAdmin");

        _coD4xRconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players =
                    [
                        new CoD4xStatusPlayerDto
                        {
                            Num = 3,
                            PlayerIdentifier = "test-guid",
                            Name = "FakeAdminHere"
                        }
                    ]
                })));

        // Player name contains the protected name as a substring
        await _sut.CheckAsync(CreateContext(username: "FakeAdminHere"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto =>
                dto.PlayerId == TestPlayerId &&
                dto.Type == AdminActionType.Ban),
            It.IsAny<CancellationToken>()), Times.Once);

        _coD4xRconApi.Verify(x => x.BanClient(
            TestServerId,
            It.Is<CoD4xClientReasonRequestDto>(r => r.ClientId == 3),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_WhenSlotPlayerNameMismatches_DoesNotBan()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));
        SetupOwnerLookup(OwnerId, "OwnerGuy");

        _coD4xRconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players =
                    [
                        new CoD4xStatusPlayerDto
                        {
                            Num = 3,
                            PlayerIdentifier = "different-player",
                            Name = "CompletelyDifferent"
                        }
                    ]
                })));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _coD4xRconApi.Verify(x => x.BanClient(
            It.IsAny<Guid>(),
            It.IsAny<CoD4xClientReasonRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_WhenProtectedNameContainsPlayerName_DetectsViolation()
    {
        // Protected name "SuperAdmin" contains player name "Admin"
        SetupProtectedNames(("SuperAdmin", OwnerId));
        SetupOwnerLookup(OwnerId, "RealSuperAdmin");

        _coD4xRconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players =
                    [
                        new CoD4xStatusPlayerDto
                        {
                            Num = 3,
                            PlayerIdentifier = "admin-player",
                            Name = "Admin"
                        }
                    ]
                })));

        await _sut.CheckAsync(CreateContext(username: "Admin"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto => dto.Type == AdminActionType.Ban),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_WhenApiFails_DoesNotThrow()
    {
        _playersApi
            .Setup(x => x.GetProtectedNames(0, 500, It.IsAny<GameType?>()))
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

        _playersApi.Verify(x => x.GetProtectedNames(0, 500, GameType.CallOfDuty4), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_CachesProtectedNamesPerGameType()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));
        SetupOwnerLookup(OwnerId, "OwnerGuy");

        // First game type cache entry
        await _sut.CheckAsync(CreateContext(username: "TestPlayer", gameType: "CallOfDuty4"));

        // Different game type uses a different cache key and triggers a separate filtered API call.
        await _sut.CheckAsync(CreateContext(username: "TestPlayer", gameType: "CallOfDuty4x"));

        _playersApi.Verify(x => x.GetProtectedNames(0, 500, GameType.CallOfDuty4), Times.Once);
        _playersApi.Verify(x => x.GetProtectedNames(0, 500, GameType.CallOfDuty4x), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_WhenMatchIsOnSecondPage_StillEnforcesViolation()
    {
        var firstPage = Enumerable.Range(0, 500)
            .Select(i => CreateProtectedNameDto($"NoMatch{i}", Guid.NewGuid()))
            .ToList();

        var secondPage = new List<ProtectedNameDto>
        {
            CreateProtectedNameDto("TargetPlayer", OwnerId)
        };

        _playersApi
            .Setup(x => x.GetProtectedNames(0, 500, GameType.CallOfDuty4))
            .ReturnsAsync(new ApiResult<CollectionModel<ProtectedNameDto>>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<ProtectedNameDto>>(new CollectionModel<ProtectedNameDto>(firstPage))));

        _playersApi
            .Setup(x => x.GetProtectedNames(500, 500, GameType.CallOfDuty4))
            .ReturnsAsync(new ApiResult<CollectionModel<ProtectedNameDto>>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<ProtectedNameDto>>(new CollectionModel<ProtectedNameDto>(secondPage))));

        SetupOwnerLookup(OwnerId, "OwnerGuy");

        _coD4xRconApi
            .Setup(x => x.Status(TestServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto
                {
                    Players =
                    [
                        new CoD4xStatusPlayerDto
                        {
                            Num = 3,
                            PlayerIdentifier = "target-player",
                            Name = "TargetPlayer"
                        }
                    ]
                })));

        await _sut.CheckAsync(CreateContext(username: "TargetPlayer", gameType: "CallOfDuty4"));

        _playersApi.Verify(x => x.GetProtectedNames(0, 500, GameType.CallOfDuty4), Times.Once);
        _playersApi.Verify(x => x.GetProtectedNames(500, 500, GameType.CallOfDuty4), Times.Once);

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto => dto.Type == AdminActionType.Ban),
            It.IsAny<CancellationToken>()), Times.Once);
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

        _playersApi.Verify(x => x.GetProtectedNames(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<GameType?>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_InvalidGameType_SkipsCheck()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer", gameType: "NotARealGameType"));

        _playersApi.Verify(x => x.GetProtectedNames(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<GameType?>()), Times.Never);
        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _coD4xRconApi.Verify(x => x.BanClient(
            It.IsAny<Guid>(), It.IsAny<CoD4xClientReasonRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_OwnerLookupFails_SkipsEnforcement()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));

        _playersApi
            .Setup(x => x.GetPlayer(OwnerId, PlayerEntityOptions.None))
            .ThrowsAsync(new HttpRequestException("API error"));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);

        _coD4xRconApi.Verify(x => x.BanClient(
            It.IsAny<Guid>(), It.IsAny<CoD4xClientReasonRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_OwnerLookupReturnsNonSuccess_SkipsEnforcement()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));

        _playersApi
            .Setup(x => x.GetPlayer(OwnerId, PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(System.Net.HttpStatusCode.NotFound));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);

        _coD4xRconApi.Verify(x => x.BanClient(
            It.IsAny<Guid>(), It.IsAny<CoD4xClientReasonRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_WhenActiveProtectedNameBanExists_SkipsCreateAndStillRunsRcon()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));
        SetupOwnerLookup(OwnerId, "OwnerGuy");
        SetupActiveBans(CreateAdminActionDto(AdminActionType.Ban, "Protected Name Violation - existing enforcement"));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.IsAny<CreateAdminActionDto>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _coD4xRconApi.Verify(x => x.BanClient(
            TestServerId,
            It.Is<CoD4xClientReasonRequestDto>(r => r.ClientId == 3),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_WhenOnlyNonProtectedNameBanExists_CreatesProtectedNameBan()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));
        SetupOwnerLookup(OwnerId, "OwnerGuy");
        SetupActiveBans(CreateAdminActionDto(AdminActionType.Ban, "Manual ban from moderator"));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto => dto.Type == AdminActionType.Ban),
            It.IsAny<CancellationToken>()), Times.Once);

        _coD4xRconApi.Verify(x => x.BanClient(
            TestServerId,
            It.Is<CoD4xClientReasonRequestDto>(r => r.ClientId == 3),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_WhenActiveBanPrecheckFails_FailsOpenAndCreatesBan()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));
        SetupOwnerLookup(OwnerId, "OwnerGuy");

        _adminActionsApi
            .Setup(x => x.GetAdminActions(
                It.IsAny<GameType?>(),
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<AdminActionFilter?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<AdminActionOrder?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("admin actions unavailable"));

        await _sut.CheckAsync(CreateContext(username: "TestPlayer"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto => dto.Type == AdminActionType.Ban),
            It.IsAny<CancellationToken>()), Times.Once);

        _coD4xRconApi.Verify(x => x.BanClient(
            TestServerId,
            It.Is<CoD4xClientReasonRequestDto>(r => r.ClientId == 3),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_WhenCalledTwice_CreatesOnceThenSkipsDuplicateCreate()
    {
        SetupProtectedNames(("TestPlayer", OwnerId));
        SetupOwnerLookup(OwnerId, "OwnerGuy");

        var noBans = new ApiResult<CollectionModel<AdminActionDto>>(
            System.Net.HttpStatusCode.OK,
            new ApiResponse<CollectionModel<AdminActionDto>>(new CollectionModel<AdminActionDto>([])));

        var existingProtectedNameBan = new ApiResult<CollectionModel<AdminActionDto>>(
            System.Net.HttpStatusCode.OK,
            new ApiResponse<CollectionModel<AdminActionDto>>(
                new CollectionModel<AdminActionDto>(
                [
                    CreateAdminActionDto(AdminActionType.Ban, "Protected Name Violation - existing enforcement")
                ])));

        _adminActionsApi
            .SetupSequence(x => x.GetAdminActions(
                It.IsAny<GameType?>(),
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<AdminActionFilter?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<AdminActionOrder?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(noBans)
            .ReturnsAsync(existingProtectedNameBan);

        await _sut.CheckAsync(CreateContext(username: "TestPlayer"));
        await _sut.CheckAsync(CreateContext(username: "TestPlayer"));

        _adminActionsApi.Verify(x => x.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto => dto.Type == AdminActionType.Ban),
            It.IsAny<CancellationToken>()), Times.Once);

        _coD4xRconApi.Verify(x => x.BanClient(
            TestServerId,
            It.Is<CoD4xClientReasonRequestDto>(r => r.ClientId == 3),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
