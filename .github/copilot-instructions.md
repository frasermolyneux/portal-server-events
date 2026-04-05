# Copilot Instructions

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

- Run `dotnet build src/XtremeIdiots.Portal.Server.Events.sln` to build.
- Run `dotnet test src/XtremeIdiots.Portal.Server.Events.sln` to run tests.
- Terraform: `terraform -chdir=terraform init -backend-config=backends/dev.backend.hcl` then `terraform -chdir=terraform plan -var-file=tfvars/dev.tfvars`.
- Ensure `terraform fmt -recursive` before committing Terraform changes.

## Terraform Conventions

- Use `data` sources for existing Azure resources (resource groups, client config, remote state).
- Follow file-per-resource pattern.
- Variables declared in `variables.tf` with environment-specific values in `terraform/tfvars/`.
