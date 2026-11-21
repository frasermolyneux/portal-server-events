using System.Text.Json;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using Moq;

using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

using static XtremeIdiots.Portal.Server.Events.Processor.App.Tests.ServiceBusTestHelpers;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public class PlaytimeObservationProcessorTests
{
    private readonly Mock<IPlaytimeIngestion> ingestion = new();
    private readonly Mock<FunctionContext> context = new();

    [Fact]
    public async Task ValidObservation_IsPassedToIngestion()
    {
        var processor = new PlaytimeObservationProcessor(ingestion.Object, Mock.Of<ILogger<PlaytimeObservationProcessor>>());
        var observation = new PlaytimeObservationEvent
        {
            EventGeneratedUtc = DateTime.UtcNow,
            EventPublishedUtc = DateTime.UtcNow,
            ServerId = Guid.NewGuid(),
            GameType = "CallOfDuty4",
            SequenceId = 1,
            Kind = PlaytimeObservationKind.MapChanged,
            MapName = "mp_crash",
            Playtime = new PlaytimeEventMetadata
            {
                ObservationId = Guid.NewGuid(),
                EventUtc = DateTime.UtcNow,
                TimestampQuality = PlaytimeTimestampQuality.ParseTime
            }
        };

        await processor.ProcessPlaytimeObservation(CreateMessage(observation), context.Object);

        ingestion.Verify(x => x.ProcessAsync(It.Is<PlaytimeObservationEvent>(value =>
            value.Playtime.ObservationId == observation.Playtime.ObservationId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvalidMessage_PropagatesForDeadLetterHandling()
    {
        var processor = new PlaytimeObservationProcessor(ingestion.Object, Mock.Of<ILogger<PlaytimeObservationProcessor>>());

        await Assert.ThrowsAsync<JsonException>(() => processor.ProcessPlaytimeObservation(CreateMessage("{invalid"), context.Object));
        ingestion.Verify(x => x.ProcessAsync(It.IsAny<PlaytimeObservationEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IngestionFailure_Propagates()
    {
        ingestion.Setup(x => x.ProcessAsync(It.IsAny<PlaytimeObservationEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Repository unavailable"));
        var processor = new PlaytimeObservationProcessor(ingestion.Object, Mock.Of<ILogger<PlaytimeObservationProcessor>>());
        var message = CreateMessage(/*lang=json,strict*/ """
            {"eventGeneratedUtc":"2026-01-01T00:00:00Z","eventPublishedUtc":"2026-01-01T00:00:00Z",
             "serverId":"11111111-1111-1111-1111-111111111111","gameType":"CallOfDuty4","sequenceId":1,
             "kind":3,"mapName":"mp_crash","playtime":{"observationId":"22222222-2222-2222-2222-222222222222",
             "eventUtc":"2026-01-01T00:00:00Z","timestampQuality":4}}
            """);

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessPlaytimeObservation(message, context.Object));
    }
}
