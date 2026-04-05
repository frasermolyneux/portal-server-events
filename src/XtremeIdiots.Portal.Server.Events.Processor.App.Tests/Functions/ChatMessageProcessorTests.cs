using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ChatMessages;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
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
    private readonly IMemoryCache _cache;
    private readonly TelemetryClient _telemetry;
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

        _commandProcessor.Setup(x => x.ProcessAsync(It.IsAny<CommandContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.NotHandled);

        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        _telemetry = new TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration
        {
            TelemetryChannel = new Mock<ITelemetryChannel>().Object
        });

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ContentSafety:ModerateChatTagName"] = "moderate-chat"
            })
            .Build();

        _sut = new ChatMessageProcessor(_logger.Object, _repoClient.Object, _cache, _telemetry, _commandProcessor.Object, _moderationPipeline.Object, _configuration);
    }

    private static ChatMessageEvent CreateValidEvent(
        string? gameType = null,
        string? playerGuid = null,
        string? username = null,
        string? chatMessage = null,
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
}
