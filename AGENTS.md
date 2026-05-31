# AGENTS.md — portal-server-events

Two components: (1) an **Abstractions NuGet package** (Service Bus event DTOs + queue-name constants shared by [`portal-server-agent`](../portal-server-agent/) as publisher and this repo as consumer); (2) an **Azure Functions Processor App** (.NET 9 isolated worker, Service Bus queue triggers) that handles persistence, moderation, GeoIP enrichment, and live stats.

This file is the brief for the **GitHub Copilot coding agent** (and any other agent that follows the [agents.md](https://agents.md) convention) when it runs in a cloud runner without the local VS Code multi-root workspace context.

> If you are a human reading this in VS Code, prefer `.github/copilot-instructions.md` for project orientation. `AGENTS.md` is the agent execution brief.

---

## Required reading (read these BEFORE doing any work)

The `copilot-setup-steps.yml` workflow checks out `frasermolyneux/.github-copilot` at `./.github-copilot/` in the runner, so the paths below resolve.

1. `.github/copilot-instructions.md` — repo-specific orientation, build commands, conventions
2. `.github-copilot/.github/instructions/personal.working-preferences.instructions.md`
3. `.github-copilot/.github/copilot-instructions.md` — org-wide catalog
4. Stack-specific files — see **Stack guardrails** below

---

## Stack guardrails

### Tenant facts (always-on)
- `tenant.subscriptions`, `tenant.regions`, `tenant.identity`

### Enforceable standards
- `standards.oidc-and-secrets` — **no client secrets**
- `standards.dotnet-project`
- `standards.azure-naming`, `standards.azure-tagging`, `standards.terraform-style`
- `standards.branching-and-prs`

### Patterns
- `patterns.api-client` — consumes Portal Repository client
- `patterns.nbgv-versioning`
- `patterns.terraform-remote-state`
- `dotnet-nuget-library.instructions.md` — Abstractions package conventions

### Platform consumption contracts
- `platform.workloads`, `platform.monitoring`, `platform.hosting`

### Shared
- `shared.api-client-abstractions`
- `shared.observability-appinsights`

---

## Build, test, format

```pwsh
dotnet build src/XtremeIdiots.Portal.Server.Events.sln
dotnet test src/XtremeIdiots.Portal.Server.Events.sln --filter "FullyQualifiedName!~IntegrationTests"
dotnet format src/XtremeIdiots.Portal.Server.Events.sln --verify-no-changes

terraform -chdir=terraform fmt -check -recursive
terraform -chdir=terraform init -backend-config=backends/dev.backend.hcl
terraform -chdir=terraform validate
terraform -chdir=terraform plan -var-file=tfvars/dev.tfvars
```

---

## Do NOT

- ❌ Do not `git commit`, `git push`, force-push, rebase, or branch-mutate. Work on the assigned branch only.
- ❌ Do not introduce client secrets. Service Bus auth via managed identity only.
- ❌ Do not bypass `dotnet format`, `dotnet test`, `terraform fmt`, or `terraform validate`.
- ❌ **Do not change Abstractions DTOs or queue-name constants in a breaking way without bumping the NuGet major version** — `portal-server-agent` consumes this package as the publisher. Coordinate the change there.
- ❌ Do not add FTP / RCON / log-tailing logic here — wrong repo (belongs in `portal-server-agent`).
- ❌ Do not modify `.github/workflows/`, `.github/dependabot.yml`, or `version.json` unless that is the explicit task.

---

## Opening the PR

You MUST use `.github/PULL_REQUEST_TEMPLATE.md` as your PR body — do **not** write a freeform body. The org template is inherited from `frasermolyneux/.github` and GitHub pre-populates it when you open the PR. Concretely:

1. Fill `## Summary` (one line) and `Closes #<issue>`.
2. Tick the relevant `## Type of change` box.
3. Paste the **actual command output** from your Build, Tests, and Format check runs into `## Validation evidence`. Show the real summary line, not "tests passed".
4. Fill `## Risk and rollout` — blast radius, auto-deploy?, manual steps post-merge, rollback plan.
5. Tick **every** box in `## Agent attestation`.
6. Delete `## Consumer impact` only if no published contract (Abstractions / Client NuGet / Service Bus DTO / Terraform output) changed.

Complete the `## Agent attestation` section before requesting review; reviewers use it as a readiness checklist.

---

## Pre-PR checks (run before you open the PR)

- [ ] `dotnet build` succeeds (clean)
- [ ] `dotnet test --filter "FullyQualifiedName!~IntegrationTests"` passes
- [ ] `dotnet format --verify-no-changes` passes
- [ ] `terraform fmt -check -recursive` passes
- [ ] `terraform validate` + `terraform plan -var-file=tfvars/dev.tfvars` succeed
- [ ] If Abstractions DTOs / queue names changed, `portal-server-agent` impact noted in PR body
- [ ] No new secrets / GUIDs / connection strings
- [ ] PR body cites each acceptance criterion
- [ ] Risk/rollout section filled in

---

## Escalation

If you hit any of the conditions below, **open the PR as draft** and **apply the `needs-decision` label** instead of pushing forward to ready-for-review. Post a comment on the originating issue summarising what's blocking you and what decision is needed.

Stop and escalate when:

- The change requires a breaking Abstractions change without a coordinated `portal-server-agent` update (also apply the `breaking-contract` label).
- A `code-review` finding is **High** and cannot be resolved in-scope.
- The Service Bus queue contract needs to add/remove a queue without coordinated provisioning in `portal-environments`.
