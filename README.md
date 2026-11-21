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
* [Processor Telemetry](/docs/telemetry.md) - Host and isolated-worker telemetry pipelines, filtering policy, and production validation

## Overview

Portal Server Events provides the shared Service Bus event contracts and the Azure Functions processor used in the XtremeIdiots server-event pipeline. The abstractions package defines queue names and DTO contracts consumed by both publishers and consumers. The processor app subscribes to those queues for persistence, moderation, enrichment, and downstream event handling flows. Terraform and GitHub Actions manage the infrastructure lifecycle and automated deployments.

## Playtime event metadata

The `playtime-observation` queue carries a dedicated `PlaytimeObservationEvent`,
separate from the legacy event queues. Its required `playtime.observationId` must
identify the same source observation across retries and replay, while `eventUtc`
and `timestampQuality` describe the actual source time:
`1` is a parsed gameplay timestamp, `2` a producer event timestamp, `3` a roster
snapshot timestamp, and `4` log parse time. `0` means unknown. A producer may
also include `producerEpoch`, `producerSequence` and `connectionCorrelationId`;
an instance-local sequence alone is not a durable identity.

For roster observations, `rosterCompleteness` is `0` not applicable, `1` complete,
`2` partial or `3` failed. This value records the producer's source assessment and
is forwarded unchanged; consumer-side identity resolution or staleness checks must
not rewrite it. Producers may mark a roster complete only when the source provides
an authoritative full snapshot. A partial roster can describe observed presence
but must not be used to infer absence. Consumers and the repository separately
decide whether a source-complete roster is fresh and identity-complete enough to
reconcile. Current parser slot maps are not, by themselves, evidence of a complete
roster. Existing event queues and CoD4x ingest routes are unchanged. Parse-time
observations must carry `playtime.freshness: 1` (Live), and producers may assert
it only with evidence that the underlying event is live. `0` (Unknown) is the
default; unknown-age log catch-up must not be presented as current activity.

Player join and leave observations, and each roster player, may supply
`isBot`: `true` for a known bot, `false` for a positively identified non-bot,
or null/absent when unknown. A nonzero game identifier alone does not establish
that a player is human. Unclassified activity must not be reported as confirmed
human-only coverage.

Roster observations may also carry `playtimeCoverage`, with UTC `producerStartedUtc`
and cumulative non-negative `droppedCapacity`, `droppedAttempts` and `droppedAge`
counts for the accompanying `playtime.producerEpoch`. The consumer timestamps a
coverage report with the envelope's `eventPublishedUtc`, not the gameplay
observation time. These counters record possible local-retention gaps, not
confirmed downstream loss. Reordered snapshots must be aggregated by per-epoch
maxima. Missing or zero counters and changes of epoch do not establish completeness
or prove a restart.

### Playtime dead letters and recovery

The playtime queue is excluded from automatic dead-letter replay. Invalid,
rejected or unresolved observations remain in its dead-letter queue with the
failure reason; a partial roster can already be accepted for known players
while its original message remains available for unresolved identities.
Operators can inspect it with `v1/ReprocessDeadLetterQueue` using
`queueName=playtime-observation&dryRun=true`, then explicitly replay that same
queue after fixing the cause. Manual replay does not discard messages merely
because they have been replayed three times.

The proposed repository API rejects a first submission more than 24 hours after
`eventUtc` and rejects replay or enrichment more than 24 hours after initial
acceptance. Dead-letter retention extends neither deadline. Monitor and recover
promptly; after a deadline, leave the message in the dead-letter queue and
arrange a repository-supported correction. Requeuing a message does not prove
the repository accepted or projected it.

On replay, the consumer retries canonical identity lookup for supplied game
identifiers. The repository does not automatically resolve identities, and
ordinary replay cannot supply a missing source identifier or classify an unknown
bot. Those cases require a separate supported correction or remain unresolved.

## NuGet Packages

| Package                                                                                                                                 | Latest                                                                                                                                                                              | Description                                                                              |
| --------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| [`XtremeIdiots.Portal.Server.Events.Abstractions.V1`](https://www.nuget.org/packages/XtremeIdiots.Portal.Server.Events.Abstractions.V1) | [![NuGet](https://img.shields.io/nuget/v/XtremeIdiots.Portal.Server.Events.Abstractions.V1.svg)](https://www.nuget.org/packages/XtremeIdiots.Portal.Server.Events.Abstractions.V1/) | Shared Service Bus queue constants and event DTO contracts for the server event pipeline |

## Contributing

Please read the [contributing](CONTRIBUTING.md) guidance; this is a learning and development project.

## Security

Please read the [security](SECURITY.md) guidance; I am always open to security feedback through email or opening an issue.
