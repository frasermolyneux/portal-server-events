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

## Troubleshooting Runbook

1. Processor skips command/welcome processing due validation errors.
   - Inspect provider logs for validation failures.
   - Verify payload `schemaVersion` is supported by the pinned contracts package version.

2. Global/server merge output is unexpected.
   - Reproduce with fixture tests in `Processor.App.Tests` for merge precedence and inheritance behavior.
   - Confirm the server override payload is valid before merge.

3. Cross-repo contract mismatch is suspected.
   - Check that `portal-web`, `portal-server-events`, `portal-servers-integration`, and `portal-server-agent` use the same published `XtremeIdiots.Portal.Settings.Contracts.V1` version.
