using System.Net;

using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Commands;

public class RegisterCommandTests
{
    private readonly Mock<IConnectedPlayersApi> _api = new();
    private readonly Mock<IRconResponseService> _responses = new();
    private readonly Mock<IRegisterCommandRateLimiter> _limiter = new();
    private readonly Mock<IAuditLogger> _audit = new();
    private readonly RegisterCommand _sut;

    public RegisterCommandTests()
    {
        var versioned = new Mock<IVersionedConnectedPlayersApi>();
        versioned.Setup(x => x.V1).Returns(_api.Object);
        var repository = new Mock<IRepositoryApiClient>();
        repository.Setup(x => x.ConnectedPlayers).Returns(versioned.Object);
        _limiter.Setup(x => x.TryAcquire(It.IsAny<Guid>(), It.IsAny<DateTime>(), out It.Ref<TimeSpan>.IsAny))
            .Returns(true);
        _responses.Setup(x => x.TryTellAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _sut = new RegisterCommand(repository.Object, _responses.Object, _limiter.Object, _audit.Object,
            Mock.Of<ILogger<RegisterCommand>>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Submission_UsesTrustedIdentityAndRequiresWebsiteApproval(HttpStatusCode status)
    {
        _api.Setup(x => x.SubmitConnectedPlayerClaim(It.IsAny<SubmitConnectedPlayerClaimDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConnectedPlayerClaimDto>(status));
        var context = CreateContext();

        var result = await _sut.ExecuteAsync(context);
        await _sut.ExecuteAsync(context with { EventPublishedUtc = context.EventPublishedUtc.AddMinutes(1) });

        Assert.True(result.Success);
        Assert.Contains("Approve it on the website", result.ResponseMessage);
        Assert.Contains("not linked", result.ResponseMessage);
        _api.Verify(x => x.SubmitConnectedPlayerClaim(It.Is<SubmitConnectedPlayerClaimDto>(dto =>
            dto.RequestId == ConnectedPlayerClaimRequestId.FromEvent(context) &&
            dto.PlayerId == context.PlayerId && dto.GameServerId == context.ServerId && dto.Code == "AB12CD"),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        _api.Verify(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<ConsumeConnectedPlayerActivationCodeDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _audit.Verify(x => x.LogAudit(It.Is<AuditEvent>(audit => audit.EventName == "ConnectedPlayerClaimSubmitted")),
            status == HttpStatusCode.Created ? Times.Exactly(2) : Times.Never());
    }

    [Theory]
    [InlineData("!register")]
    [InlineData("!register AB12CD EXTRA")]
    [InlineData("!register abc!23")]
    [InlineData("!register 123")]
    public async Task InvalidFormat_DoesNotSubmit(string message)
    {
        Assert.False((await _sut.ExecuteAsync(CreateContext() with { Message = message })).Success);
        _api.Verify(x => x.SubmitConnectedPlayerClaim(It.IsAny<SubmitConnectedPlayerClaimDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingPlayer_DoesNotSubmit()
    {
        Assert.False((await _sut.ExecuteAsync(CreateContext() with { PlayerId = null })).Success);
        _api.Verify(x => x.SubmitConnectedPlayerClaim(It.IsAny<SubmitConnectedPlayerClaimDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid, expired")]
    [InlineData(HttpStatusCode.Conflict, "already linked")]
    public async Task Rejection_DoesNotClaimSuccess(HttpStatusCode status, string expected)
    {
        _api.Setup(x => x.SubmitConnectedPlayerClaim(It.IsAny<SubmitConnectedPlayerClaimDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConnectedPlayerClaimDto>(status));
        var result = await _sut.ExecuteAsync(CreateContext());
        Assert.False(result.Success);
        Assert.Contains(expected, result.ResponseMessage);
        _audit.Verify(x => x.LogAudit(It.Is<AuditEvent>(audit => audit.EventName == "ConnectedPlayerRegisterFailed" &&
            audit.Outcome == AuditOutcome.Failure)), Times.Once);
    }

    [Fact]
    public async Task RateLimit_IsPreserved()
    {
        _limiter.Setup(x => x.TryAcquire(It.IsAny<Guid>(), It.IsAny<DateTime>(), out It.Ref<TimeSpan>.IsAny))
            .Returns((Guid _, DateTime _, out TimeSpan retryAfter) =>
            {
                retryAfter = TimeSpan.FromSeconds(42);
                return false;
            });
        var result = await _sut.ExecuteAsync(CreateContext());
        Assert.Contains("42 seconds", result.ResponseMessage);
        _api.Verify(x => x.SubmitConnectedPlayerClaim(It.IsAny<SubmitConnectedPlayerClaimDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TemporaryFailure_PropagatesForServiceBusRetryWithoutSuccessAudit()
    {
        _api.Setup(x => x.SubmitConnectedPlayerClaim(It.IsAny<SubmitConnectedPlayerClaimDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("temporary"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.ExecuteAsync(CreateContext()));
        _audit.Verify(x => x.LogAudit(It.IsAny<AuditEvent>()), Times.Never);
    }

    [Fact]
    public async Task ServerFailure_PropagatesForServiceBusRetry()
    {
        _api.Setup(x => x.SubmitConnectedPlayerClaim(It.IsAny<SubmitConnectedPlayerClaimDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConnectedPlayerClaimDto>(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => _sut.ExecuteAsync(CreateContext()));
        _api.Verify(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<ConsumeConnectedPlayerActivationCodeDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LegacyCode_RemainsUsableOnlyAfterKnownCodeRejection()
    {
        SetupCodeRejection();
        _api.Setup(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<ConsumeConnectedPlayerActivationCodeDto>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(new ApiResult<ConnectedPlayerDto>(HttpStatusCode.Created));

        var result = await _sut.ExecuteAsync(CreateContext());

        Assert.True(result.Success);
        Assert.Contains("Legacy registration completed", result.ResponseMessage);
        _audit.Verify(x => x.LogAudit(It.Is<AuditEvent>(audit =>
            audit.EventName == "ConnectedPlayerLegacyRegistrationSucceeded")), Times.Once);
        _api.Verify(x => x.ConsumeConnectedPlayerActivationCode(It.Is<ConsumeConnectedPlayerActivationCodeDto>(
            dto => dto.Code == "AB12CD" && dto.PlayerId == CreateContext().PlayerId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApprovalCodeRejectedByLegacyEndpoint_CannotBypassApproval()
    {
        SetupCodeRejection();
        _api.Setup(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<ConsumeConnectedPlayerActivationCodeDto>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(new ApiResult<ConnectedPlayerDto>(HttpStatusCode.Conflict,
                new ApiResponse<ConnectedPlayerDto>(new ApiError(ApiErrorCodes.EntityConflict, "This code requires member approval."))));
        var result = await _sut.ExecuteAsync(CreateContext());
        Assert.False(result.Success);
        Assert.Contains("requires approval", result.ResponseMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task UnknownOrConflictResponse_NeverFallsBack(HttpStatusCode status)
    {
        _api.Setup(x => x.SubmitConnectedPlayerClaim(It.IsAny<SubmitConnectedPlayerClaimDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConnectedPlayerClaimDto>(status));
        Assert.False((await _sut.ExecuteAsync(CreateContext())).Success);
        _api.Verify(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<ConsumeConnectedPlayerActivationCodeDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LegacyTemporaryFailure_Propagates()
    {
        SetupCodeRejection();
        _api.Setup(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<ConsumeConnectedPlayerActivationCodeDto>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(new ApiResult<ConnectedPlayerDto>(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => _sut.ExecuteAsync(CreateContext()));
    }

    [Fact]
    public async Task ConcurrentDuplicateDeliveries_SubmitTheSameDurableRequestId()
    {
        var requests = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        _api.Setup(x => x.SubmitConnectedPlayerClaim(It.IsAny<SubmitConnectedPlayerClaimDto>(), It.IsAny<CancellationToken>()))
            .Callback<SubmitConnectedPlayerClaimDto, CancellationToken>((dto, _) => requests.Add(dto.RequestId))
            .ReturnsAsync(new ApiResult<ConnectedPlayerClaimDto>(HttpStatusCode.OK));
        var context = CreateContext();
        await Task.WhenAll(_sut.ExecuteAsync(context), _sut.ExecuteAsync(context));
        Assert.Equal(2, requests.Count);
        Assert.All(requests, id => Assert.Equal(ConnectedPlayerClaimRequestId.FromEvent(context), id));
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Declined")]
    [InlineData("Expired")]
    public async Task ResolvedClaimReplay_DoesNotAskForApprovalAgain(string status)
    {
        _api.Setup(x => x.SubmitConnectedPlayerClaim(It.IsAny<SubmitConnectedPlayerClaimDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConnectedPlayerClaimDto>(HttpStatusCode.OK,
                new ApiResponse<ConnectedPlayerClaimDto>(new ConnectedPlayerClaimDto { Status = status })));
        var result = await _sut.ExecuteAsync(CreateContext());
        Assert.True(result.Success);
        Assert.Contains("already processed", result.ResponseMessage);
        _api.Verify(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<ConsumeConnectedPlayerActivationCodeDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutLegacyFallback()
    {
        _api.Setup(x => x.SubmitConnectedPlayerClaim(It.IsAny<SubmitConnectedPlayerClaimDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => _sut.ExecuteAsync(CreateContext()));
        _api.Verify(x => x.ConsumeConnectedPlayerActivationCode(It.IsAny<ConsumeConnectedPlayerActivationCodeDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private void SetupCodeRejection()
    {
        _api.Setup(x => x.SubmitConnectedPlayerClaim(It.IsAny<SubmitConnectedPlayerClaimDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConnectedPlayerClaimDto>(HttpStatusCode.BadRequest,
                new ApiResponse<ConnectedPlayerClaimDto>(new ApiError(ApiErrorCodes.EntityConflict,
                    "The registration code is invalid or expired."))));
    }

    private static CommandContext CreateContext() => new()
    {
        ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        PlayerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        GameType = "CallOfDuty4",
        PlayerGuid = "abc123",
        Username = "TestPlayer",
        SlotId = 3,
        Message = "!register\tab12cd",
        EventGeneratedUtc = DateTime.UtcNow,
        EventPublishedUtc = DateTime.UtcNow,
        SequenceId = 1,
        SourceMessageId = "message"
    };
}
