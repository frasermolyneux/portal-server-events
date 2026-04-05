using System.Net;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

public class HealthCheck(HealthCheckService healthCheck)
{
    [Function(nameof(HealthCheck))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req,
        FunctionContext context)
    {
        var result = await healthCheck.CheckHealthAsync().ConfigureAwait(false);

        var statusCode = result.Status == HealthStatus.Healthy
            ? HttpStatusCode.OK
            : HttpStatusCode.ServiceUnavailable;

        var response = req.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new
        {
            status = result.Status.ToString(),
            checks = result.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description
            })
        });
        return response;
    }
}
