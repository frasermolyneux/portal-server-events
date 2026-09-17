# Processor telemetry

The processor has two Application Insights telemetry pipelines:

- The Azure Functions host emits invocation lifecycle traces, request records,
  exceptions, and runtime warnings such as trigger or poison-message diagnostics.
- The .NET isolated worker sends application logs, custom telemetry, and structured
  audit events directly to Application Insights from `Program.cs`.

The worker `TelemetryFilterProcessor` does not receive host-originated telemetry.
Host volume is therefore controlled in `host.json` with deterministic category
filters:

```json
"logLevel": {
  "Function": "Warning",
  "Host.Results": "Error",
  "Microsoft.Hosting.Lifetime": "Warning"
}
```

`Function = Warning` suppresses successful function start/completion traces, which
the Functions host emits at `Information`, while retaining warning, error, and
exception traces. This preserves runtime diagnostics for retries, poison messages,
and dead-letter behavior.

`Host.Results = Error` suppresses successful invocation request records. Microsoft
documents that this setting retains host execution records for failed function
executions in the `requests` table, so the production `processor_failure_rate`
alert remains based on:

```kusto
requests
| where name startswith "Process"
| where success == false
```

Host sampling remains disabled. Failures are retained by severity rather than by
probabilistic sampling. The worker pipeline and its audit/custom telemetry filters
are unchanged.

References:

- [Configure Azure Functions monitoring categories and log levels](https://learn.microsoft.com/azure/azure-functions/configure-monitoring#configure-categories)
- [Configure Azure Functions host settings](https://learn.microsoft.com/azure/azure-functions/functions-host-json#applicationinsights)
- [.NET isolated worker Application Insights](https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide#application-insights)

## Post-deployment validation

Do not treat deployment as complete until these checks pass:

1. Compare seven-day `AppTraces` and `AppRequests` ingestion grouped by
   `SDKVersion` before and after deployment.
2. Confirm successful `Process*` records from SDK versions beginning with
   `azurefunctions:` fall sharply. Successful health-check host records should fall
   under the same host request filter.
3. Trigger or inspect a known failed invocation and confirm its failed request,
   exception, and associated warning/error traces remain visible.
4. Run the `processor_failure_rate` query and confirm it still returns failed
   `Process*` requests.

At the observed production ingestion rate, the expected reduction is approximately
12.8 GB per month, worth roughly GBP 25 per month.
