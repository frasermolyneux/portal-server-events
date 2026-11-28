# Platform Settings Contracts

This document defines how `portal-server-events` consumes platform settings after the typed-contract migration.

## Architecture

- Typed contract source: `XtremeIdiots.Portal.Settings.Contracts.V1`.
- The `chatCommands` processor provider deserializes typed documents and runs typed validators.
- Effective settings behavior remains deterministic through provider + merger logic.
- Repository persistence remains dynamic (`namespace + JSON string`) and is not re-shaped by processor code.

## Migration Summary

- Old approach: local contract/validator ownership for chat and welcome settings in processor-adjacent paths.
- New approach: canonical contracts are owned in `portal-repository` via `XtremeIdiots.Portal.Settings.Contracts.V1`.
- `XtremeIdiots.Portal.ChatCommands.Abstractions.V1` is compatibility-only and not canonical for new behavior.

## Chat moderation rules

The staged `chatModerationRules` namespace is disabled by default. When enabled,
events with an occurrence ID are evaluated in dry-run mode only: no configured
rule action is dispatched or recorded as an observation. Legacy Content Safety
observations retain their existing eligibility and share their single analysis
result with the rules; unavailable categories remain unknown.

An explicit event `occurrenceId` takes precedence. For plugin chat events that
lack one, a nonempty UUID in the body `messageId` is used only when it matches
the Service Bus message ID. A synthesized broker ID alone is not sufficient.
Player-age checks use the captured chat time, not the processing or replay time;
missing or contradictory capture/first-seen timestamps leave those checks unknown.

The player-linked evaluation audit contains bounded rule/check outcomes, severity
values and configured policy-entry IDs, never raw chat or full analyzer responses.
Audit write failures are logged without blocking chat processing. Repository
retains these evaluations for 30 days and deletes them with the player. Native
kicks and private nudges remain unavailable until correlated delivery is deployed.

## Welcome cutover

The legacy `welcomeMessages` runtime, its process-local deduplication, delayed
worker sleeps and welcome-only template/settings providers are retired.
Connection processing delegates to shared rules only. Old welcome documents are
not converted or interpreted as rules; the replacement uses the distinct
`welcomeRules` namespace and must remain disabled until its dependency rollout
and clean configuration reset are complete.

Retire Portal's legacy welcome writers before the final legacy global/server
configuration cleanup, so unrelated settings saves cannot recreate them. Disable
the old configurations before deploying the runtime replacement and allow for
the old three-minute settings cache and any in-flight delayed work. Remove only
the legacy welcome documents; unrelated settings, historical audit records,
existing AdminActions and bans remain untouched. Recreate desired welcomes as
ordered rules explicitly, never automatically.

Protection features remain concurrent with each other. Welcome admission waits
for their evaluations, while unrelated features remain independent; this
admission ordering does not replace the durable pre-delivery enforcement check.
Moderation records consume the reason frozen in the durable action claim, not
the original template or refreshed settings.

### Shared welcome execution and text

`welcomeRules` uses the shared ordered rule schema, including `always` for a
catch-all and per-rule timing overrides. Delay and freshness are derived from
the original connection timestamp and frozen settings, never retry time.
Unavailable higher-priority context waits within that original window instead
of selecting a lower-priority greeting. IP enrichment resumes only an already
pending welcome; it does not create another connection greeting.

Only fields explicitly named in `templateParameters` are rendered. Existing
strings remain literal. The shared typed token catalogue owns parsing, values,
fallbacks and diagnostics; profile, tag, IP and server lookups run only when
requested and are cached for the invocation. Missing provider facts remain
unavailable, not invented zeros. Resolved moderation reasons are frozen before
claiming their effects and creating matching audit records.

Execution diagnostics expose current waits and selected rule and delivery
outcomes, including unavailable token sources and fallback use. Text diagnostics
are persisted before the corresponding action is frozen or claimed; waiting
markers clear on resume. The projection contains at most 256 entries and reports
an exact count of any omitted diagnostics.

The feature-level match status is `Matched` only after a rule's aggregate
condition evaluates true. `NoMatch` requires at least one enabled rule and
explicit false results for every enabled rule in the frozen ruleset. Unknown,
expired, unevaluated, and legacy evidence does not establish a match status.
Match status does not establish that an action succeeded.

Message actions append `messages` after an optional `message`. Rendering precedes
channel-aware splitting. The complete ordered recipient/item/fragment plan is
persisted before the first send and is never rerendered or resegmented on retry.
Plugin prepared chat requires the bounded-fragment capability and uses its
verified 149-byte native limit. RCON uses Integration's budget metadata and
eligibility checks, including current prefix reservations and unsafe-character
constraints. Other-game 149-byte budgets are an interim policy estimate, not a
verified per-game cutoff. Every fragment is validated before any fragment sends;
mutable transport eligibility is checked again for each child.

`message.private-tagged` snapshots a complete authoritative roster using tag IDs
with Any/All matching. An unavailable or partial roster waits; only a complete
empty snapshot means no recipients. Recipients are deduplicated by connection,
remain separate from the original trigger identity, and are checked again before
dispatch. A replaced recipient is skipped without retargeting another occupant.
Independent tagged alerts bypass greeting delay/protection suppression and can
continue after the triggering player leaves.

Children have stable canonical logical identities and one durable claim each.
Continuations or native polls pace sends without worker sleeps. Completed,
uncertain and terminally skipped children are not resent. A plugin message
receipt means the native API was invoked, not that the player received or saw
the text; its aggregate evidence remains unknown while later ordered fragments
may proceed. Expiry or a later protection decision terminalizes remaining
unsent greeting children without dispatch. Agent-owned pending claims schedule
recovery at their original expiry. Message preparation without a feature expiry
uses a per-action deadline that is persisted before waiting and never extended
on retry.

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
Mapped IPv4 and IPv6 text are canonicalized before snapshot correlation.
An unresolved IP can supplement previously unavailable context without replaying
completed actions. Continuations retain that proven observation's trigger
correlation and IP context; an older retry cannot replace it or substitute a
different connection/runtime. New context arriving during an older pending
native claim is saved without changing that command, state or cursor.

Repository HTTP 400/409 connection-snapshot rejections are logged and cannot
supply fresh rule facts, including when the client throws its typed HTTP error.
These rejections do not fail routine status/disconnect processing. Other HTTP
and transport failures still propagate for normal retry handling.

A rule-progress HTTP 409 is reconciled only for a local final `NoMatch` decision
with no action work. A fresh execution read must confirm the same frozen
execution/configuration, a newer completed `NoMatch`, matching decision evidence
and no recorded or prepared actions. The stale progress is discarded, never
written with a refreshed version. This requires Repository's consistent,
deletion-coordinated execution read. Active, mismatched or unavailable state still
fails for normal retry. Terminal executions remain terminal; this does not
guarantee re-evaluation of later changed IP facts.

Siege requires its active, unexpired session, frozen universal rules, timestamped
activation history and session mode. Baseline membership is not a timestamp.
Dry-run persists `WouldExecute` decisions without moderation records, messages,
forum publication or native enforcement. Existing AdminActions and bans are not
replaced by the engine cutover.

### Execution transports and recovery

The plugin posts bare shared DTOs to `POST /rules/evaluate`, `/rules/outcomes` and
`/rules/poll`. Responses contain `protocolVersion`, `commands`, `executionIds` and
`retryTrigger` (default `false`). Only evaluate can request a trigger retry when
one feature fails before starting a durable execution; healthy commands and
execution IDs remain consumable immediately. The plugin retries the exact
original observation on its existing five-second backoff for at most 120 seconds,
without renewing its lifetime or replacing its connection/IP snapshot.
Outcome/poll responses do not cancel a pending original-trigger retry.
Poll is plugin-only: it requires the original native runtime, connection UUID,
nonzero generation, GUID and slot. The agent has no rules interpreter and does
not poll this protocol.

Plugin-marked queue mirrors do not start a second rules evaluation. Agent and
plugin connection UUIDs are otherwise independent: CoD4x source ownership relies
on the agent's effective `IsCod4xPluginSourceEnabled` configuration flag. When
enabled, the agent suppresses log-derived, status, server-connected and IP-resolved
events; configuration changes restart the agent. Loading the plugin alone does
not enable suppression or share identities. Coordinate that configuration with
plugin emission during rollout to avoid parallel producer streams and independent
logical executions.

Plugin-owned effects are returned only to the generation-checking plugin; they
never fall back to RCON. Agent-owned effects use the explicit
`AgentObservedConnection` model with nullable native generation: server-events
checks the current authoritative observed binding and calls the integration's
read-only `Execute(Mode=DryRun)` eligibility path before claiming native work.
This verifies the actual game adapter, configuration, text and live roster;
neither an `RconEnabled` setting nor an invented capability is sufficient.
The integration checks GUID/slot again immediately before sending once.
Agent-owned RCON kicks use the slot-based command; the configured reason is retained
for auditing but is not displayed on screen.
Eligibility is not an atomic guarantee against later configuration/roster changes.
This preserves observed identity
checks, not an atomic native-generation guarantee across a same-GUID reconnect.

Bindings expire at `observedAtUtc + 2 minutes`, never receive time, with at most
30 seconds of future skew. Unknown/absent status rows are not disconnects.
Disconnects retire only an exact identity/runtime match. Native work awaiting an initial binding, or record-producing work awaiting a
new player's profile, is deferred without claiming or dispatching the effect.
An older reconnect binding can wait within that same deadline; evidence of a
newer replacement remains stale, including the immediate pre-dispatch recheck.
The existing `rule-action-continuations` queue resumes agent-owned executions;
plugin-owned executions remain on plugin polling. Context waits are bounded to
two minutes and do not replace the frozen pre-observation facts.
That queue also handles optional forum publication, keeping forum network calls
off the plugin HTTP request. Publication intent is queued before creating the
moderation record, so a queue outage cannot strand an already-created record
without its requested continuation. The worker resolves the atomic record link,
reschedules while the record write is pending, and never retries native effects.
An expired claim without a record surfaces through normal queue retry/dead-letter
handling; a terminal action without a record does not publish a topic.

Execution decisions, cursors, commands and logical action claims are persisted
before dispatch. Logical identity includes server, connection, feature, optional
Siege session, rule and action, not transport observation IDs or settings revisions.
Claim ownership requires Repository's HTTP 201 for a newly-created claim; HTTP 200
is a duplicate. `ShouldDispatch` is only native-dispatch permission, not ownership
of dry-run or cloud-only Observation work.
Claimed/ambiguous native effects are never blindly resent. RCON UDP dispatch is
not confirmed application; a native drop is not proof of continuing Ban/TempBan
cache enforcement. Unknown forum outcomes require manual recovery rather than
posting a duplicate topic. Queue duplicate detection is not required for safety.

Validated plugin receipts produce best-effort `NativeAction.{effect}.Plugin.{outcome}`
server events. A kick `Applied` event means the native drop API was invoked, not
that a ban remains enforced. Message `Dispatched` events do not establish delivery.
RCON events use `Rcon.Dispatched` and similarly do not establish application.
Event writes use a stable key per logical effect and event type; a failed write is
logged and never causes an action to be resent. The key is the .NET GUID made
from the first 16 bytes of SHA-256 over UTF-8
`RulesNativeAction|{LogicalEffectId}|{EventType}`. The .NET GUID constructor
interprets the first 4, next 2 and next 2 bytes as little-endian fields; the
remaining 8 bytes retain their order. For `effect` and
`NativeAction.DropPlayer.Plugin.Applied`, the key is
`0befef6f-1115-39c5-4d7a-cc1e297417f1`. Event type names use
`Unsupported` for `SkippedUnsupported` receipts, whose payload retains the full
outcome. Repository deduplication lasts only as long as the event is retained.

Moderation records are created and linked atomically through the correlated
rule-action endpoint, not the retired strongest-action writer. Observation is
complete once its record exists; other moderation actions still require native
dispatch. Optional forum publication uses the durable record identity and
resolves a missing player name in the worker before taking its publication claim.
The configured `ContentSafety:BotAdminId` attributes the record and is frozen in
its publication intent so asynchronous forum attribution remains consistent.

The current plugin reports native invocations as `Dispatched`. A correlated
plugin receipt for a kick marks the kick `Applied`: success means the validated
native kick API was invoked, not independently verified player disconnection.
Processor-side dispatch without the plugin receipt is not success and an
expired, unacknowledged claim remains unknown without a resend. Receipts for
other effects retain their existing handling. Correlated receipts retain their
truthful source timestamp, including cached redelivery.
Source timestamps use the existing 30-second clock-skew allowance against claim,
command expiry and receipt time; subsecond precision does not establish clock
synchronization. Identical cached dispatch receipts do not advance or resume
again. Verified late native evidence may improve stored evidence after expiry
without rewinding a completed/stopped cursor or dispatching another action.
Ban/TempBan native drop evidence stays separate from the aggregate unknown
continuing-enforcement outcome and retains its linked AdminAction ID.

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
