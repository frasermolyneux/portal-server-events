---
description: "Keep processor effects and durable audit records consistent without auditing routine event traffic."
applyTo: "src/XtremeIdiots.Portal.Server.Events.Processor.App/{Commands,Functions,Moderation,Services,VpnProtection}/*.cs,src/XtremeIdiots.Portal.Server.Events.Processor.App.Tests/{Commands,Functions,Moderation,Services,VpnProtection}/*.cs"
---

# Processor auditing

- Emit an audit record for durable state changes, moderation or security actions,
  privileged commands, and other externally consequential outcomes.
- Keep the audit outcome aligned with the repository write or external action; do
  not report success before that effect succeeds.
- Use metrics and logs for message receipt, polling, status snapshots, retries,
  idempotency skips, and other high-volume operational signals.
- Preserve warning/error logging and exception propagation on failure paths; an
  audit event is not a replacement for processor failure visibility.
- Tests that change an audited effect should verify that the effect and audit
  record remain consistent.
