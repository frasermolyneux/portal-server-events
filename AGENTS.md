# portal-server-events

## Purpose

This repository owns the public Service Bus event contracts and the .NET 9 Azure
Functions processor that consumes them. The processor persists server activity,
executes moderation and command behavior, enriches player data, and supports
dead-letter reprocessing.

## Repository map

- `src/XtremeIdiots.Portal.Server.Events.Abstractions.V1/` — published DTOs and
  queue-name constants.
- `src/XtremeIdiots.Portal.Server.Events.Processor.App/` — isolated-worker
  functions and processing services.
- `src/XtremeIdiots.Portal.Server.Events.Processor.App.Tests/` — unit tests.
- `terraform/` — Function App, storage, APIM ingestion, identity/RBAC, monitoring,
  and remote-state integration for dev and production.
- `.github/workflows/` — CI, NuGet release, infrastructure, and deployment.

## Boundaries

- Abstractions are consumed by both this processor and `portal-server-agent`.
  Preserve serialized DTO shapes and queue-name compatibility; breaking changes
  require a coordinated major-version update.
- Keep queue triggers aligned with `Queues` constants and their event DTOs.
- Preserve the processing contract: deserialize the expected event, apply
  idempotency and validation where present, perform repository/integration effects,
  and surface failures so Service Bus retry or dead-letter behavior remains valid.
- Maintain consistency between durable state changes, moderation/security actions,
  and their audit records. Do not turn high-volume receipt or status telemetry into
  durable audit noise.
- Dead-letter replay must target the original queue safely and must not bypass
  normal processor behavior.
- Keep public contracts free of processor implementation dependencies.
- Azure App Configuration, Key Vault, Service Bus, and other Azure clients use
  managed identity. Do not introduce client secrets or connection strings.
- Preserve isolated-worker hosting, function trigger behavior, health behavior, and
  environment-labelled configuration.

## Change guidance

- Target .NET 9 and use the SDK pinned in `global.json`.
- Use typed settings contracts and validators for migrated settings namespaces;
  keep compatibility paths unless deliberately changing the contract.
- Add focused tests for changed DTO serialization, processor effects, audit
  consistency, command/moderation behavior, or replay behavior.
- For Terraform changes, retain the existing azurerm backend, OIDC remote-state
  access, file-per-resource layout, and dev/prd backend and tfvars selection.

## Useful validation

Choose checks that cover the change:

```pwsh
dotnet build src/XtremeIdiots.Portal.Server.Events.slnx
dotnet test src/XtremeIdiots.Portal.Server.Events.slnx
dotnet format src/XtremeIdiots.Portal.Server.Events.slnx --verify-no-changes
terraform -chdir=terraform fmt -check -recursive
terraform -chdir=terraform validate
```
