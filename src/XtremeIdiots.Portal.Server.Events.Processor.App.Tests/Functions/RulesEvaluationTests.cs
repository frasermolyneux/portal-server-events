using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

using Moq;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Functions;

public sealed class RulesEvaluationTests
{
    [Theory]
    [InlineData("evaluate")]
    [InlineData("outcomes")]
    [InlineData("poll")]
    public async Task ValidRequest_DelegatesAndReturnsBareResponse(string route)
    {
        var transport = new Mock<IRulesPluginTransport>();
        var result = new RuleEvaluationResponse { Commands = [], ExecutionIds = [] };
        transport.Setup(t => t.EvaluateAsync(It.IsAny<RuleTriggerEnvelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        transport.Setup(t => t.OutcomeAsync(It.IsAny<RuleActionResult>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        transport.Setup(t => t.PollAsync(It.IsAny<RulePollRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        var context = new Mock<FunctionContext>();
        var request = CreateRequest("{}", context.Object);
        var handler = new RulesEvaluation(transport.Object);

        var response = await InvokeAsync(handler, route, request, context.Object);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(response.Body);
        Assert.Equal(1, json.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("commands").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("executionIds").ValueKind);
        if (route == "evaluate")
        {
            transport.Verify(t => t.EvaluateAsync(It.IsAny<RuleTriggerEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        else if (route == "outcomes")
        {
            transport.Verify(t => t.OutcomeAsync(It.IsAny<RuleActionResult>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            transport.Verify(t => t.PollAsync(It.IsAny<RulePollRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData(/*lang=json,strict*/ "{\"unknown\":true}")]
    [InlineData(/*lang=json,strict*/ "{\"gameType\":1}")]
    public async Task InvalidPayload_ReturnsBadRequestWithoutCallingEngine(string body)
    {
        var transport = new Mock<IRulesPluginTransport>(MockBehavior.Strict);
        var context = new Mock<FunctionContext>();
        var response = await new RulesEvaluation(transport.Object).Evaluate(CreateRequest(body, context.Object), context.Object);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        transport.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Enums_UseCanonicalStrings()
    {
        var transport = new Mock<IRulesPluginTransport>();
        transport.Setup(t => t.EvaluateAsync(It.Is<RuleTriggerEnvelope>(r => r.GameType == GameType.CallOfDuty4x), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuleEvaluationResponse
            {
                Commands = [new RuleActionCommand { Effect = new RuleNativeEffect { Type = RuleNativeEffectType.DropPlayer } }]
            });
        var context = new Mock<FunctionContext>();

        var response = await new RulesEvaluation(transport.Object).Evaluate(
            CreateRequest(/*lang=json,strict*/ "{\"gameType\":\"CallOfDuty4x\"}", context.Object), context.Object);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(response.Body);
        Assert.Equal("DropPlayer", json.RootElement.GetProperty("commands")[0].GetProperty("effect").GetProperty("type").GetString());
    }

    [Fact]
    public async Task InvalidEngineArgument_ReturnsBadRequest()
    {
        var transport = new Mock<IRulesPluginTransport>();
        transport.Setup(t => t.PollAsync(It.IsAny<RulePollRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("invalid identity"));
        var context = new Mock<FunctionContext>();

        var response = await new RulesEvaluation(transport.Object).Poll(CreateRequest("{}", context.Object), context.Object);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EngineFailure_Propagates()
    {
        var transport = new Mock<IRulesPluginTransport>();
        transport.Setup(t => t.OutcomeAsync(It.IsAny<RuleActionResult>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("backend unavailable"));
        var context = new Mock<FunctionContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RulesEvaluation(transport.Object).Outcomes(CreateRequest("{}", context.Object), context.Object));
    }

    private static Task<HttpResponseData> InvokeAsync(
        RulesEvaluation handler, string route, HttpRequestData request, FunctionContext context) => route switch
        {
            "evaluate" => handler.Evaluate(request, context),
            "outcomes" => handler.Outcomes(request, context),
            _ => handler.Poll(request, context)
        };

    private static HttpRequestData CreateRequest(string body, FunctionContext context)
    {
        var request = new Mock<HttpRequestData>(context);
        request.Setup(r => r.Body).Returns(new MemoryStream(Encoding.UTF8.GetBytes(body)));
        request.Setup(r => r.CreateResponse()).Returns(() =>
        {
            var response = new Mock<HttpResponseData>(context);
            response.SetupProperty(r => r.StatusCode);
            response.SetupProperty(r => r.Headers, new HttpHeadersCollection());
            response.Setup(r => r.Body).Returns(new MemoryStream());
            return response.Object;
        });
        return request.Object;
    }
}
