using MX.Observability.ApplicationInsights.Auditing;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ChatMessages;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.UserProfiles;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;
using XtremeIdiots.Portal.Server.Events.Processor.App.Moderation;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class ChatMessageProcessorTests
{
    private readonly Mock<ILogger<ChatMessageProcessor>> _logger = new();
    private readonly Mock<IRepositoryApiClient> _repoClient = new();
    private readonly Mock<IVersionedPlayersApi> _versionedPlayers = new();
    private readonly Mock<IPlayersApi> _playersApi = new();
    private readonly Mock<IVersionedChatMessagesApi> _versionedChat = new();
    private readonly Mock<IChatMessagesApi> _chatApi = new();
    private readonly Mock<IVersionedGameServersEventsApi> _versionedEvents = new();
    private readonly Mock<IGameServersEventsApi> _eventsApi = new();
    private readonly Mock<IVersionedConnectedPlayersApi> _versionedConnectedPlayers = new();
    private readonly Mock<IConnectedPlayersApi> _connectedPlayersApi = new();
    private readonly Mock<IVersionedUserProfileApi> _versionedUserProfiles = new();
    private readonly Mock<IUserProfileApi> _userProfilesApi = new();
    private readonly IMemoryCache _cache;
    private readonly Mock<IAuditLogger> _auditLogger = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly Mock<IChatCommandProcessor> _commandProcessor = new();
    private readonly Mock<IChatModerationPipeline> _moderationPipeline = new();
    private readonly IConfiguration _configuration;
    private readonly ChatMessageProcessor _sut;

    private static readonly Guid TestServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestPlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public ChatMessageProcessorTests()
    {
        _versionedPlayers.Setup(x => x.V1).Returns(_playersApi.Object);
        _repoClient.Setup(x => x.Players).Returns(_versionedPlayers.Object);

        _versionedChat.Setup(x => x.V1).Returns(_chatApi.Object);
        _repoClient.Setup(x => x.ChatMessages).Returns(_versionedChat.Object);

        _versionedEvents.Setup(x => x.V1).Returns(_eventsApi.Object);
        _repoClient.Setup(x => x.GameServersEvents).Returns(_versionedEvents.Object);

        _versionedConnectedPlayers.Setup(x => x.V1).Returns(_connectedPlayersApi.Object);
        _repoClient.Setup(x => x.ConnectedPlayers).Returns(_versionedConnectedPlayers.Object);

        _versionedUserProfiles.Setup(x => x.V1).Returns(_userProfilesApi.Object);
        _repoClient.Setup(x => x.UserProfiles).Returns(_versionedUserProfiles.Object);

        _connectedPlayersApi
            .Setup(x => x.GetConnectedPlayers(
                It.IsAny<Guid?>(),
                It.IsAny<Guid?>(),
                It.IsAny<GameType?>(),
                It.IsAny<bool?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(new CollectionModel<ConnectedPlayerDto>([])));

        _commandProcessor.Setup(x => x.ProcessAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.NotHandled);

        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ContentSafety:ModerateChatTagName"] = "moderate-chat"
            })
            .Build();

        _sut = new ChatMessageProcessor(_logger.Object, _repoClient.Object, _cache, _auditLogger.Object, _commandProcessor.Object, _moderationPipeline.Object, _configuration);
    }

    private static ChatMessageEvent CreateValidEvent(
        string? gameType = null,
        string? playerGuid = null,
        string? username = null,
        string? chatMessage = null,
        int slotId = 3,
        ChatMessageType? type = null,
        Guid? serverId = null) => new()
        {
            EventGeneratedUtc = DateTime.UtcNow.AddSeconds(-10),
            EventPublishedUtc = DateTime.UtcNow.AddSeconds(-5),
            ServerId = serverId ?? TestServerId,
            GameType = gameType ?? "CallOfDuty4",
            SequenceId = 1,
            PlayerGuid = playerGuid ?? "abc123guid",
            Username = username ?? "TestPlayer",
            SlotId = slotId,
            Message = chatMessage ?? "Hello world",
            Type = type ?? ChatMessageType.All
        };

    [Fact]
    public async Task ValidMessage_PersistsChatMessage()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _chatApi.Setup(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _chatApi.Verify(x => x.CreateChatMessage(It.Is<CreateChatMessageDto>(dto =>
            dto.GameServerId == TestServerId &&
            dto.PlayerId == TestPlayerId &&
            dto.ChatType == ChatType.All &&
            dto.Username == "TestPlayer" &&
            dto.Message == "Hello world"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TeamMessage_CorrectChatType()
    {
        var evt = CreateValidEvent(type: ChatMessageType.Team);
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _chatApi.Setup(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _chatApi.Verify(x => x.CreateChatMessage(It.Is<CreateChatMessageDto>(dto =>
            dto.ChatType == ChatType.Team), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidMessage_PassesSlotToCommandContext()
    {
        var evt = CreateValidEvent(slotId: 9);
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _chatApi.Setup(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _commandProcessor.Verify(x => x.ProcessAsync(
            It.Is<CommandContext>(ctx => ctx.SlotId == 9),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MessageWithoutSlotId_DoesNotProcess()
    {
        var legacyPayload = """
            {
              "eventGeneratedUtc": "2025-01-15T12:00:00Z",
              "eventPublishedUtc": "2025-01-15T12:00:01Z",
              "serverId": "11111111-1111-1111-1111-111111111111",
              "gameType": "CallOfDuty4",
              "sequenceId": 1,
              "playerGuid": "abc123guid",
              "username": "TestPlayer",
              "message": "Hello world",
              "type": "All"
            }
            """;

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _chatApi.Setup(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessChatMessage(CreateMessage(legacyPayload), _functionContext.Object);

        _chatApi.Verify(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _commandProcessor.Verify(x => x.ProcessAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MessageWithNullSlotId_DoesNotProcess()
    {
        var legacyPayload = """
            {
              "eventGeneratedUtc": "2025-01-15T12:00:00Z",
              "eventPublishedUtc": "2025-01-15T12:00:01Z",
              "serverId": "11111111-1111-1111-1111-111111111111",
              "gameType": "CallOfDuty4",
              "sequenceId": 1,
              "playerGuid": "abc123guid",
              "username": "TestPlayer",
              "slotId": null,
              "message": "Hello world",
              "type": "All"
            }
            """;

        _chatApi.Setup(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessChatMessage(CreateMessage(legacyPayload), _functionContext.Object);

        _chatApi.Verify(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _commandProcessor.Verify(x => x.ProcessAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NegativeSlotId_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(slotId: -1);
        var message = CreateMessage(evt);

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _chatApi.Verify(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _commandProcessor.Verify(x => x.ProcessAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlayerNotFound_ThrowsForRetry()
    {
        var evt = CreateValidEvent();
        var message = CreateMessage(evt);

        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(NotFoundResult<PlayerDto>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ProcessChatMessage(message, _functionContext.Object));
    }

    [Fact]
    public async Task MissingMessage_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(chatMessage: "");
        var message = CreateMessage(evt);

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _chatApi.Verify(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvalidGameType_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(gameType: "InvalidGame");
        var message = CreateMessage(evt);

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _chatApi.Verify(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmptyServerId_LogsWarningAndReturns()
    {
        var evt = CreateValidEvent(serverId: Guid.Empty);
        var message = CreateMessage(evt);

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _chatApi.Verify(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MalformedJson_LogsWarningAndReturns()
    {
        var message = CreateMessage("not valid json");

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _chatApi.Verify(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CachedPlayer_DoesNotCallApiAgain()
    {
        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _chatApi.Setup(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        // Process twice
        await _sut.ProcessChatMessage(CreateMessage(CreateValidEvent()), _functionContext.Object);
        await _sut.ProcessChatMessage(CreateMessage(CreateValidEvent()), _functionContext.Object);

        // GetPlayerByGameType should only be called once (second call uses cache)
        _playersApi.Verify(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags), Times.Once);
        _chatApi.Verify(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task HandledCommand_PersistsChatCommandExecutionEvent()
    {
        var evt = CreateValidEvent(chatMessage: "!commands");
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _chatApi.Setup(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        _commandProcessor.Setup(x => x.ProcessAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("ok"));

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(
            It.Is<CreateGameServerEventDto>(dto =>
                dto.GameServerId == TestServerId &&
                dto.EventType == "ChatCommandExecution" &&
                dto.EventData != null &&
                dto.EventData.Contains("\"commandPrefix\":\"!commands\"", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeniedCommand_PersistsChatCommandDeniedEvent()
    {
        var evt = CreateValidEvent(chatMessage: "!admin");
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _chatApi.Setup(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        _eventsApi.Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        _commandProcessor.Setup(x => x.ProcessAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.DeniedByPolicy("You are not authorized to use this command."));

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(
            It.Is<CreateGameServerEventDto>(dto =>
                dto.GameServerId == TestServerId &&
                dto.EventType == "ChatCommandDenied"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidMessage_EnrichesAuthorizationSnapshotInCommandContext()
    {
        var evt = CreateValidEvent(chatMessage: "!commands");
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _chatApi.Setup(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _commandProcessor.Verify(x => x.ProcessAsync(
            It.Is<CommandContext>(ctx =>
                ctx.AuthorizationSnapshot != null &&
                ctx.AuthorizationSnapshot.TagsResolved == true &&
                ctx.AuthorizationSnapshot.ClaimsResolved == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotHandledCommand_DoesNotPersistChatCommandExecutionEvent()
    {
        var evt = CreateValidEvent(chatMessage: "!unknown");
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _chatApi.Setup(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        _commandProcessor.Setup(x => x.ProcessAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.NotHandled);

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _eventsApi.Verify(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandledCommand_WhenEventWriteFails_ContinuesPipeline()
    {
        var evt = CreateValidEvent(chatMessage: "!commands");
        var message = CreateMessage(evt);

        var playerDto = CreatePlayerDto(TestPlayerId);
        _playersApi.Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4, "abc123guid", PlayerEntityOptions.Tags))
            .ReturnsAsync(SuccessResult(playerDto));

        _chatApi.Setup(x => x.CreateChatMessage(It.IsAny<CreateChatMessageDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        _eventsApi
            .Setup(x => x.CreateGameServerEvent(It.IsAny<CreateGameServerEventDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("event write failed"));

        _commandProcessor.Setup(x => x.ProcessAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.Ok("ok"));

        await _sut.ProcessChatMessage(message, _functionContext.Object);

        _moderationPipeline.Verify(x => x.RunAsync(It.IsAny<ModerationContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
