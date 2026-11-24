# Platform Settings Contracts

This document defines how `portal-server-events` consumes platform settings after the typed-contract migration.

## Architecture

- Typed contract source: `XtremeIdiots.Portal.Settings.Contracts.V1`.
- Processor provider paths for `chatCommands` and `welcomeMessages` deserialize typed documents and run typed validators.
- Effective settings behavior remains deterministic through provider + merger logic.
- Repository persistence remains dynamic (`namespace + JSON string`) and is not re-shaped by processor code.

## Migration Summary

- Old approach: local contract/validator ownership for chat and welcome settings in processor-adjacent paths.
- New approach: canonical contracts are owned in `portal-repository` via `XtremeIdiots.Portal.Settings.Contracts.V1`.
- `XtremeIdiots.Portal.ChatCommands.Abstractions.V1` is compatibility-only and not canonical for new behavior.

## VPN Protection and Siege rules

VPN Protection schema version 2 uses nested condition trees; version 1 flat rules
are rejected and disable VPN enforcement until the configuration is replaced.
Server overrides can inherit the global condition or replace its entire tree.
Effective settings are cached for up to three minutes (invalid settings for 30
seconds). Coordinate the contract and configuration cutover across consumers;
verify effective VPN settings are disabled after the cache interval before
replacing existing overrides.

Siege evaluates the settings captured when its session was activated, retaining
the existing snapshot JSON shape. Both features use three-valued AND/OR: an
unknown result does not match. VPN reason placeholders for a matching compound
rule contain the corresponding values of contributing true leaves, in depth-first
order, separated by commas.

## Troubleshooting Runbook

Registration uses `!register CODE` to submit a pending game-profile claim, including
the trusted player and server IDs, for approval on the website within 24 hours.
Approval codes expire after one hour; Repository owns both expiry rules. Codes
are public correlation identifiers and chat persistence/moderation is unchanged.
CoD4x registration remains backend-owned even when plugin command lists advertise it.

Request IDs derive from the original Service Bus message ID, server ID, source
sequence and generated timestamp. Existing dead-letter replay retains these
fields; plugin retries can change published timestamps, which are excluded.
Repository handles durable claim replay. Existing command freshness, authorization
and per-player rate limits still apply, so stale replay does not start a new claim.
Transient registration failures propagate to Service Bus instead of being cached
as completed command failures.

During the coordinated Portal migration, only the Repository's specific
invalid/expired-claim-code response permits trying legacy consume. Repository
atomically rejects approval-marked codes on that endpoint; no transient or unknown
response permits fallback. Remove this compatibility path together with legacy
issuance/consume after Portal migrates and the last legacy codes expire. Deploy
against Repository 4.2.112 or later; do not deploy this switch independently.

1. Processor skips command/welcome processing due validation errors.
   - Inspect provider logs for validation failures.
   - Verify payload `schemaVersion` is supported by the pinned contracts package version.

2. Global/server merge output is unexpected.
   - Reproduce with fixture tests in `Processor.App.Tests` for merge precedence and inheritance behavior.
   - Confirm the server override payload is valid before merge.

3. Cross-repo contract mismatch is suspected.
   - Check that `portal-web`, `portal-server-events`, `portal-servers-integration`, and `portal-server-agent` use the same published `XtremeIdiots.Portal.Settings.Contracts.V1` version.
