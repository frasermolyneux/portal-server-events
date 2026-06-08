# Copilot Instructions

> Shared conventions:
> - [`.github-copilot/.github/instructions/terraform.instructions.md`](../.github-copilot/.github/instructions/terraform.instructions.md)  standard Terraform layout, providers, remote-state, validation, CI/CD.
> - [`.github-copilot/.github/instructions/dotnet-nuget-library.instructions.md`](../.github-copilot/.github/instructions/dotnet-nuget-library.instructions.md)  .NET NuGet library standards.
>
> <!-- Links use `../.github-copilot/` which resolves in the cloud-runner checkout (copilot-setup-steps.yml clones `.github-copilot` to the repo root). In local VS Code with the multi-root workspace, browse `../../.github-copilot/` instead. -->
>
> **Cloud agents (GitHub Copilot coding agent etc.):** read [`AGENTS.md`](../AGENTS.md) at the repo root first — it is the canonical brief that survives outside the local VS Code multi-root workspace.

## Project Overview

This repository contains the XtremeIdiots Portal server event contracts and processor. It has two components:

1. **Abstractions NuGet package** (`XtremeIdiots.Portal.Server.Events.Abstractions.V1`) — Service Bus event DTOs and queue name constants shared between the agent (publisher) and processor (consumer).
2. **Processor Function App** (`XtremeIdiots.Portal.Server.Events.Processor.App`) — Azure Functions that subscribe to Service Bus queues and process events (persistence, moderation, GeoIP enrichment, live stats).

## Repository Layout

- `src/` — .NET 9 solution with Abstractions, Processor App, and Tests projects.
- `terraform/` — Infrastructure-as-code for Azure resources (Function App, Storage, health alerts).
- `.github/workflows/` — CI/CD pipelines for build, deploy (dev/prd), and environment management.

## Tech Stack

- .NET 9, C# 13, Azure Functions v4 (isolated worker)
- Azure Service Bus (queue triggers)
- Application Insights (telemetry)
- Terraform with azurerm provider
- GitHub Actions CI/CD

## Development Guidelines

- Run `dotnet build src/XtremeIdiots.Portal.Server.Events.slnx` to build.
- Run `dotnet test src/XtremeIdiots.Portal.Server.Events.slnx` to run tests.
- Terraform: `terraform -chdir=terraform init -backend-config=backends/dev.backend.hcl` then `terraform -chdir=terraform plan -var-file=tfvars/dev.tfvars`.
- Ensure `terraform fmt -recursive` before committing Terraform changes.

## Terraform Conventions

- Use `data` sources for existing Azure resources (resource groups, client config, remote state).
- Follow file-per-resource pattern.
- Variables declared in `variables.tf` with environment-specific values in `terraform/tfvars/`.

## Platform Settings Contracts

- Chat command and welcome message settings consumed by the processor are now sourced from `XtremeIdiots.Portal.Settings.Contracts.V1`.
- Keep runtime settings validation on the typed validators from that package; avoid reintroducing local duplicate validators for the migrated namespaces.
- `XtremeIdiots.Portal.ChatCommands.Abstractions.V1` remains compatibility-only and must not be treated as the canonical settings contract source.
- Do not remove compatibility shims unless shim-removal gate criteria are met and evidenced in the implementation log.
- Use `docs/platform-settings-contracts.md` for migration and troubleshooting procedures.
