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

Both features consume the universal schema version 1 rule documents from
`XtremeIdiots.Portal.Settings.Contracts.V1`. Global enablement, server enablement
overrides and inherited/local rules are merged by the shared contract merger.
Effective valid settings are cached for up to three minutes; malformed documents
fail explicitly. Old VPN/Siege rule formats are not interpreted or converted.
Keep both features disabled through the coordinated reset and dependency rollout.

The registered feature catalogue owns trigger subscriptions and permitted
conditions/actions. Rules run in configured order; each matching rule's actions
run in order, followed by its `StopProcessingRules` or `ContinueToNextRule`.
A stop affects only that feature, even if every action is unsupported. Unsupported
actions are skipped individually; failed prerequisites skip dependent actions,
not unrelated ones. There is no strongest-action selection.

Conditions use three-valued AND/OR: False dominates AND, True dominates OR, and
only True acts. Missing capabilities, histories or intelligence remain Unknown.
Tag conditions compare tag IDs using any/all/none semantics. GUID first-seen
history is game-specific; IP history spans games. Immutable pre-observation fact
snapshots prevent current player/IP/tag writes from changing the answer on retry.
An unresolved IP can supplement previously unavailable context without replaying
completed actions.

Siege requires its active, unexpired session, frozen universal rules, timestamped
activation history and session mode. Baseline membership is not a timestamp.
Dry-run persists `WouldExecute` decisions without moderation records, messages,
forum publication or native enforcement. Existing AdminActions and bans are not
replaced by the engine cutover.

### Execution transports and recovery

The plugin posts bare shared DTOs to `POST /rules/evaluate`, `/rules/outcomes` and
`/rules/poll`. Responses contain `protocolVersion`, `commands` and `executionIds`.
Poll is plugin-only: it requires the original native runtime, connection UUID,
nonzero generation, GUID and slot. The agent has no rules interpreter and does
not poll this protocol.

Plugin-owned effects are returned only to the generation-checking plugin; they
never fall back to RCON. Agent-owned effects use the explicit
`AgentObservedConnection` model with nullable native generation: server-events
checks the current authoritative observed binding, then the integration checks
GUID/slot immediately before sending once. This preserves observed identity
checks, not an atomic native-generation guarantee across a same-GUID reconnect.

Bindings expire at `observedAtUtc + 2 minutes`, never receive time, with at most
30 seconds of future skew. Unknown/absent status rows are not disconnects.
Disconnects retire only an exact identity/runtime match. Native work awaiting an
initial binding is deferred without claiming or dispatching the effect; the
existing `rule-action-continuations` queue resumes the original feature execution.
That queue also handles optional forum publication after the moderation record
is durable, keeping forum network calls off the plugin HTTP request.

Execution decisions, cursors, commands and logical action claims are persisted
before dispatch. Logical identity includes server, connection, feature, optional
Siege session, rule and action, not transport observation IDs or settings revisions.
Claimed/ambiguous native effects are never blindly resent. RCON UDP dispatch is
not confirmed application; a native drop is not proof of continuing Ban/TempBan
cache enforcement. Unknown forum outcomes require manual recovery rather than
posting a duplicate topic. Queue duplicate detection is not required for safety.

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
