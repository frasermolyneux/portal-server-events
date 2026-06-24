# Portal Server Events

[![Build and Test](https://github.com/frasermolyneux/portal-server-events/actions/workflows/build-and-test.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-events/actions/workflows/build-and-test.yml)
[![Code Quality](https://github.com/frasermolyneux/portal-server-events/actions/workflows/codequality.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-events/actions/workflows/codequality.yml)
[![Copilot Setup Steps](https://github.com/frasermolyneux/portal-server-events/actions/workflows/copilot-setup-steps.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-events/actions/workflows/copilot-setup-steps.yml)
[![Dependabot Auto-Merge](https://github.com/frasermolyneux/portal-server-events/actions/workflows/dependabot-automerge.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-events/actions/workflows/dependabot-automerge.yml)
[![Deploy Dev](https://github.com/frasermolyneux/portal-server-events/actions/workflows/deploy-dev.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-events/actions/workflows/deploy-dev.yml)
[![Deploy Prd](https://github.com/frasermolyneux/portal-server-events/actions/workflows/deploy-prd.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-events/actions/workflows/deploy-prd.yml)
[![Destroy Development](https://github.com/frasermolyneux/portal-server-events/actions/workflows/destroy-development.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-events/actions/workflows/destroy-development.yml)
[![Destroy Environment](https://github.com/frasermolyneux/portal-server-events/actions/workflows/destroy-environment.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-events/actions/workflows/destroy-environment.yml)
[![PR Verify](https://github.com/frasermolyneux/portal-server-events/actions/workflows/pr-verify.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-events/actions/workflows/pr-verify.yml)
[![Release - Publish NuGet](https://github.com/frasermolyneux/portal-server-events/actions/workflows/release-publish-nuget.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-events/actions/workflows/release-publish-nuget.yml)
[![Release - Version and Tag](https://github.com/frasermolyneux/portal-server-events/actions/workflows/release-version-and-tag.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-events/actions/workflows/release-version-and-tag.yml)

## Documentation

* [Platform Settings Contracts](/docs/platform-settings-contracts.md) - Typed settings contract usage and migration guidance for processor runtime settings

## Overview

Portal Server Events provides the shared Service Bus event contracts and the Azure Functions processor used in the XtremeIdiots server-event pipeline. The abstractions package defines queue names and DTO contracts consumed by both publishers and consumers. The processor app subscribes to those queues for persistence, moderation, enrichment, and downstream event handling flows. Terraform and GitHub Actions manage the infrastructure lifecycle and automated deployments.

## NuGet Packages

| Package                                                                                                                                 | Latest                                                                                                                                                                              | Description                                                                              |
| --------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| [`XtremeIdiots.Portal.Server.Events.Abstractions.V1`](https://www.nuget.org/packages/XtremeIdiots.Portal.Server.Events.Abstractions.V1) | [![NuGet](https://img.shields.io/nuget/v/XtremeIdiots.Portal.Server.Events.Abstractions.V1.svg)](https://www.nuget.org/packages/XtremeIdiots.Portal.Server.Events.Abstractions.V1/) | Shared Service Bus queue constants and event DTO contracts for the server event pipeline |

## Contributing

Please read the [contributing](CONTRIBUTING.md) guidance; this is a learning and development project.

## Security

Please read the [security](SECURITY.md) guidance; I am always open to security feedback through email or opening an issue.
