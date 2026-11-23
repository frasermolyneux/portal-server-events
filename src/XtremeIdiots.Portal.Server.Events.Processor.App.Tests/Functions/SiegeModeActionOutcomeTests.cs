using System.Net;
using System.Text;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.SiegeMode;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public sealed class SiegeModeActionOutcomeTests
{
    private readonly Mock<IRepositoryApiClient> repositoryApiClient = new();
    private readonly Mock<IVersionedSiegeModeApi> versionedSiegeModeApi = new();
    private readonly Mock<ISiegeModeApi> siegeModeApi = new();
    private readonly Mock<ILogger<SiegeModeActionOutcome>> logger = new();

    public SiegeModeActionOutcomeTests()
    {
        versionedSiegeModeApi.Setup(x => x.V1).Returns(siegeModeApi.Object);
        repositoryApiClient.Setup(x => x.SiegeMode).Returns(versionedSiegeModeApi.Object);
    }

    [Fact]
    public async Task RecordOutcome_RejectsPlayerIdentifiersInDetail()
    {
        var (request, context) = CreateRequest(/*lang=json,strict*/ """
            {
                "serverId": "11111111-1111-1111-1111-111111111111",
                "sessionId": "22222222-2222-2222-2222-222222222222",
                "actionId": "33333333-3333-3333-3333-333333333333",
                "connectionId": "44444444-4444-4444-4444-444444444444",
                "outcome": "Failed",
                "detail": "192.0.2.1"
            }
            """);

        var response = await CreateSut().RecordSiegeModeActionOutcome(request, context);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        siegeModeApi.Verify(x => x.UpdateActionOutcome(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<UpdateSiegeActionOutcomeDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.Conflict, HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task RecordOutcome_PropagatesRepositoryResult(HttpStatusCode repositoryStatus, HttpStatusCode expectedStatus)
    {
        siegeModeApi.Setup(x => x.UpdateActionOutcome(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<UpdateSiegeActionOutcomeDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<SiegeActionDto>(
                repositoryStatus, new ApiResponse<SiegeActionDto>(new SiegeActionDto())));
        var (request, context) = CreateRequest(/*lang=json,strict*/ """
            {
                "serverId": "11111111-1111-1111-1111-111111111111",
                "sessionId": "22222222-2222-2222-2222-222222222222",
                "actionId": "33333333-3333-3333-3333-333333333333",
                "connectionId": "44444444-4444-4444-4444-444444444444",
                "outcome": "Stale",
                "detail": "Connection no longer matches"
            }
            """);

        var response = await CreateSut().RecordSiegeModeActionOutcome(request, context);

        Assert.Equal(expectedStatus, response.StatusCode);
        siegeModeApi.Verify(x => x.UpdateActionOutcome(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            It.Is<UpdateSiegeActionOutcomeDto>(dto =>
                dto.ConnectionId == Guid.Parse("44444444-4444-4444-4444-444444444444") &&
                dto.Outcome == "Stale" && dto.Detail == "Connection no longer matches"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private SiegeModeActionOutcome CreateSut() => new(repositoryApiClient.Object, logger.Object);

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
}
