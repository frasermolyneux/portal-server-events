using System.Net;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Services;

public sealed class ProtectedNameServiceTests
{
    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OwnerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string BotAdminId = "44444444-4444-4444-4444-444444444444";

    private readonly Mock<IRepositoryApiClient> repositoryApiClient = new();
    private readonly Mock<IVersionedPlayersApi> versionedPlayersApi = new();
    private readonly Mock<IPlayersApi> playersApi = new();
    private readonly Mock<IVersionedAdminActionsApi> versionedAdminActionsApi = new();
    private readonly Mock<IAdminActionsApi> adminActionsApi = new();
    private readonly Mock<IServersApiClient> serversApiClient = new();
    private readonly Mock<IVersionedCoD4xRconApi> versionedCoD4xRconApi = new();
    private readonly Mock<ICoD4xRconApi> coD4xRconApi = new();
    private readonly Mock<IAdminActionTopics> adminActionTopics = new();
    private readonly Mock<IAuditLogger> auditLogger = new();
    private readonly Mock<ILogger<ProtectedNameService>> logger = new();
    private readonly IMemoryCache memoryCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

    public ProtectedNameServiceTests()
    {
        versionedPlayersApi.Setup(x => x.V1).Returns(playersApi.Object);
        repositoryApiClient.Setup(x => x.Players).Returns(versionedPlayersApi.Object);
        versionedAdminActionsApi.Setup(x => x.V1).Returns(adminActionsApi.Object);
        repositoryApiClient.Setup(x => x.AdminActions).Returns(versionedAdminActionsApi.Object);
        versionedCoD4xRconApi.Setup(x => x.V1).Returns(coD4xRconApi.Object);
        serversApiClient.Setup(x => x.CoD4xRcon).Returns(versionedCoD4xRconApi.Object);

        adminActionsApi.Setup(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(CreateEnsureResult(true));
        adminActionsApi.Setup(x => x.UpdateAdminAction(It.IsAny<EditAdminActionDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ApiResult(HttpStatusCode.OK));
        adminActionTopics.Setup(x => x.CreateTopicForAdminAction(It.IsAny<AdminActionType>(), It.IsAny<GameType>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(1234);
        coD4xRconApi.Setup(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(SuccessResult(CreateStatus("TestPlayer")));
        coD4xRconApi.Setup(x => x.BanClient(It.IsAny<Guid>(), It.IsAny<CoD4xClientReasonRequestDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ApiResult<string>(HttpStatusCode.OK, new ApiResponse<string>("ok")));
    }

    [Fact]
    public async Task CheckAsync_NewViolation_EnsuresActionCreatesTopicAndMarksRconBan()
    {
        SetupProtectedName("TestPlayer", OwnerId);
        SetupOwner(OwnerId, "OwnerGuy");

        await CreateSut().CheckAsync(CreateContext());

        adminActionsApi.Verify(x => x.EnsureAutomatedAction(It.Is<EnsureAutomatedActionDto>(dto => dto.PlayerId == PlayerId && dto.Type == AdminActionType.Ban && dto.AutomationFeature == AutomationFeature.ProtectedName && dto.AutomationRuleId == $"{OwnerId:N}:testplayer" && dto.AdminId == BotAdminId), It.IsAny<CancellationToken>()), Times.Once);
        adminActionTopics.Verify(x => x.CreateTopicForAdminAction(AdminActionType.Ban, GameType.CallOfDuty4, PlayerId, "TestPlayer", It.IsAny<DateTime>(), It.Is<string>(text => text.Contains("Protected Name Violation", StringComparison.Ordinal)), BotAdminId, It.IsAny<CancellationToken>()), Times.Once);
        adminActionsApi.Verify(x => x.UpdateAdminAction(It.Is<EditAdminActionDto>(dto => dto.ForumTopicId == 1234), It.IsAny<CancellationToken>()), Times.Once);
        coD4xRconApi.Verify(x => x.BanClient(ServerId, It.Is<CoD4xClientReasonRequestDto>(request => (request.Reason ?? string.Empty).Contains("[PORTAL-AUTOMATION] ProtectedName", StringComparison.Ordinal)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_ExistingViolation_DoesNotCreateTopicButStillBans()
    {
        SetupProtectedName("TestPlayer", OwnerId);
        SetupOwner(OwnerId, "OwnerGuy");
        adminActionsApi.Setup(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(CreateEnsureResult(false));

        await CreateSut().CheckAsync(CreateContext());

        adminActionTopics.Verify(x => x.CreateTopicForAdminAction(It.IsAny<AdminActionType>(), It.IsAny<GameType>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        adminActionsApi.Verify(x => x.UpdateAdminAction(It.IsAny<EditAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
        coD4xRconApi.Verify(x => x.BanClient(ServerId, It.IsAny<CoD4xClientReasonRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAsync_NameOwner_DoesNotEnforce()
    {
        SetupProtectedName("TestPlayer", OwnerId);

        await CreateSut().CheckAsync(CreateContext(OwnerId));

        adminActionsApi.Verify(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
        coD4xRconApi.Verify(x => x.BanClient(It.IsAny<Guid>(), It.IsAny<CoD4xClientReasonRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_ChangedSlotPlayer_DoesNotEnforce()
    {
        SetupProtectedName("TestPlayer", OwnerId);
        SetupOwner(OwnerId, "OwnerGuy");
        coD4xRconApi.Setup(x => x.Status(ServerId, It.IsAny<CancellationToken>())).ReturnsAsync(SuccessResult(CreateStatus("DifferentPlayer")));

        await CreateSut().CheckAsync(CreateContext());

        adminActionsApi.Verify(x => x.EnsureAutomatedAction(It.IsAny<EnsureAutomatedActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
        coD4xRconApi.Verify(x => x.BanClient(It.IsAny<Guid>(), It.IsAny<CoD4xClientReasonRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ProtectedNameService CreateSut()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ContentSafety:BotAdminId"] = BotAdminId }).Build();
        return new ProtectedNameService(repositoryApiClient.Object, adminActionTopics.Object, serversApiClient.Object, memoryCache, auditLogger.Object, configuration, logger.Object);
    }

    private static ProtectedNameContext CreateContext(Guid? playerId = null) => new()
    {
        ServerId = ServerId,
        GameType = nameof(GameType.CallOfDuty4),
        Username = "TestPlayer",
        PlayerId = playerId ?? PlayerId,
        SlotId = 3
    };

    private void SetupProtectedName(string name, Guid ownerId)
    {
        var data = Newtonsoft.Json.JsonConvert.DeserializeObject<ProtectedNameDto>($$"""{"protectedNameId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","playerId":"{{ownerId}}","name":"{{name}}","ownerGameType":"CallOfDuty4","createdOn":"2026-01-01T00:00:00Z"}""")!;
        playersApi.Setup(x => x.GetProtectedNames(0, 500, GameType.CallOfDuty4)).ReturnsAsync(SuccessResult(new CollectionModel<ProtectedNameDto>([data])));
    }

    private void SetupOwner(Guid ownerId, string username)
    {
        var data = Newtonsoft.Json.JsonConvert.DeserializeObject<PlayerDto>($$"""{"playerId":"{{ownerId}}","username":"{{username}}","gameType":"CallOfDuty4"}""")!;
        playersApi.Setup(x => x.GetPlayer(ownerId, PlayerEntityOptions.None)).ReturnsAsync(SuccessResult(data));
    }

    private static CoD4xStatusResponseDto CreateStatus(string name) => new()
    {
        Players = [new CoD4xStatusPlayerDto { Num = 3, PlayerIdentifier = "test-guid", Name = name }]
    };

    private static ApiResult<T> SuccessResult<T>(T data) => new(HttpStatusCode.OK, new ApiResponse<T>(data));

    private static ApiResult<EnsureAutomatedActionResultDto> CreateEnsureResult(bool created)
    {
        var forumTopic = created ? "null" : "1234";
        var json = "{\"created\":" + created.ToString().ToLowerInvariant() + ",\"adminAction\":{\"adminActionId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"playerId\":\"" + PlayerId + "\",\"forumTopicId\":" + forumTopic + ",\"type\":\"Ban\",\"text\":\"Protected Name Violation\",\"created\":\"2026-01-01T00:00:00Z\"}}";
        var data = Newtonsoft.Json.JsonConvert.DeserializeObject<EnsureAutomatedActionResultDto>(json)!;
        return new ApiResult<EnsureAutomatedActionResultDto>(created ? HttpStatusCode.Created : HttpStatusCode.OK, new ApiResponse<EnsureAutomatedActionResultDto>(data));
    }
}
