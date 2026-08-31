# Copilot Instructions

This repository contains a versioned .NET 10 abstractions package for Service Bus
events and an Azure Functions isolated-worker app that processes those events.

## Work in this repository

- Shared DTOs and queue constants are in
  `src/XtremeIdiots.Portal.Server.Events.Abstractions.V1/`; processor code and tests
  are in the adjacent Processor App projects; infrastructure is under `terraform/`.
- Treat abstraction serialization and queue names as public contracts shared with
  `portal-server-agent`. Keep the abstractions package independent of processor
  implementation details.
- Keep each Service Bus trigger paired with its expected DTO and queue. Preserve
  retry, failure propagation, idempotency, and dead-letter replay semantics.
- Keep repository writes, moderation/security outcomes, and audit records
  consistent. Use logs and metrics for routine high-volume processing telemetry.
- Retain typed settings contracts and validators for chat commands, welcome
  messages, moderation, and related migrated settings.
- Use managed identity for Azure services and preserve environment-labelled Azure
  App Configuration and Key Vault loading.
- Preserve Azure Functions isolated-worker registration and processor behavior.

Use the exact SDK from `global.json`. Add focused tests for changed contracts or
processing behavior and select the relevant validation commands in `AGENTS.md`.
