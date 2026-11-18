# SkyMonitor adoption profile — 2026-11-17.2

This profile accompanies [common policy](common.md) and [adapters](adapters.md).
The portable source is HualapaiValley/HVO.WebSite `main` at
`fcf4df43d857a4e5471d66e81c47eff3a2849639`.
Repository settings and refs below were read on 2026-11-18 UTC; live settings
remain authoritative. This adoption targets `development/v1`, not promotion.

## Repository and integration

- Default and integration branch: `development/v1` (observed
  `3bde4622959b717e920d396774c7592a78c469e7`). Stable `main` (observed
  `f6d397d4b44762028bcd802e9ae51e6a2a17b209`) moves by promotion.
  `promote-main.yml` prepares a draft nightly at 02:00 America/Phoenix;
  qualification and merge remain separate decisions.
- Repository settings allow merge commits and auto-merge; squash and rebase are
  disabled. Automatic branch deletion on merge is enabled in repository settings;
  agents do not manually delete branches.
- Active development ruleset 26838822 requires merge queue method `MERGE`,
  grouping `ALLGREEN`, up to three entries built, one to three merged, a two-minute
  minimum wait and a 45-minute check timeout. Allowed PR merge method is `merge`.
  Review threads must be resolved; required approving review count is zero.
  Classic protection also requires thread resolution and zero approvals and
  prohibits force pushes and deletions. The active release-tags ruleset is separate.
- `development/v1` requires `Development v1 / Preflight` and
  `Development v1 / Build and Unit` from GitHub Actions (app 15368), plus
  `hvo/reviewed-head` from any source (no app ID). Classic `main` protection
  requires `Required CI` from Actions. Admin enforcement is enabled on both.

## CI and merge route

Draft PRs run hosted Preflight, including the secret scan; formal PR review follows
successful draft Preflight. Ready PRs run Preflight and, after it succeeds,
Build and Unit on self-hosted Linux x64. A `merge_group` runs both checks and
`merge-queue-reviewed-head.yml`; the latter checks each queued PR head for a
successful `hvo/reviewed-head` status authored by exactly `RoySalisbury`.

The status is a queue-entry and merge gate, not an admission gate for draft or
ready CI. After reviews converge, threads are resolved, and applicable ready-head
checks pass, Kepler publishes it on the exact reviewed head and enqueues via
GraphQL `enqueuePullRequest` with that expected head. `gh pr merge --merge`
does not enqueue under the queue. Queue CI tests the integration candidate.
The target-specific checks and full-qualification distinction remain in
[development-v1.md](../../development-v1.md); this profile changes no CI route.

## Local contracts and discovery

Root `global.json` pins SDK `10.0.402`, `rollForward: disable`, and stable
releases only. Root [AGENTS.md](../../../AGENTS.md),
[pr-lifecycle](../../../.agents/skills/pr-lifecycle/SKILL.md),
[development-v1](../../development-v1.md), and its
[review](../../runbooks/development-v1-review.md) and
[merge-queue](../../runbooks/development-v1-merge-queue.md) runbooks remain in
force. CLAUDE.md and Copilot instructions route to AGENTS.md. Load repository
skills explicitly where discovery is uncertain; non-pilot roadmap work follows
`docs/planning/agent-execution.md`. Policy text includes AGENTS.md, the policy
snapshot/profile, agent skills and normative workflow process docs; enforcement
includes branch settings, workflows and scripts that gate review, CI or merging.
Policy-text entry points include `CLAUDE.md`, `.github/copilot-instructions.md`,
`.github/prompts/**`, `.agents/skills/**`, `docs/planning/agent-execution.md`,
`docs/planning/agent-prompts.md`, and normative `docs/runbooks/**`. Enforcement
paths include `.github/workflows/**`, `.github/PULL_REQUEST_TEMPLATE/**`,
`.github/pull_request_template.md`, `scripts/ci:*`, `scripts/pr:*`, and live
branch protection and rulesets where they implement the common definition.
The repository has no `.claude/`, `.cursor/`, or OpenCode adapter configuration;
verify harness discovery and load [hvo-coordination](../../../.agents/skills/hvo-coordination/SKILL.md)
and applicable local skills explicitly.

For `main` and `release/**` PRs, `ci.yml` intentionally fails `Required CI`
while draft. No passing draft check for those targets is identified here; the
common draft-check prerequisite must be resolved before review, not waived or
interpreted as a pass. Pilot assignments use the common lifecycle stages and
are designated explicitly by Roy; this profile designates no assignment as a pilot.

Repository labels include `review:*`, `risk:*` and `workflow:in-progress` /
`workflow:blocked`; they do not replace evidence or the existing gates.

## Retained stricter SkyMonitor procedures (Roy t581u)

Roy originated these procedures in t581u and authorized continuing this work,
including retaining them through the 2026-11-17.2 version bump, in t594u. They
remain in force until Roy records a decision to retire them or promote them to
common, or the next shared-policy version bump, whichever comes first. They may
not relax common policy:

1. Kepler alone creates agent PRs, including drafts, marks ready, publishes
   `hvo/reviewed-head`, and enqueues. Coding threads perform none of these.
2. Apply the stricter tier assigned by common policy and SkyMonitor to each
   change. Common policy classifies ordinary behavior/API changes, policy text,
   CI trust and enforcement as HIGH. SkyMonitor then requires one Sol CLEAN for
   LOW, and Sol and Opus CLEAN on the same candidate SHA for HIGH. When unsure, choose HIGH. Only High and Medium findings
   block;
   Critical maps to High, Lows become linked follow-ups, and incompleteness always
   blocks. Mechanical, Standard and Deep set coverage and round limits; they never
   reduce the named-reviewer requirement.

## Evidence translation

SkyMonitor `CLEAN` maps to common `CLEAN`; `CHANGES REQUIRED` and
`CHANGES REQUESTED` map to common `CHANGES_REQUIRED`. The older `APPROVE`
label represents approval only when the required CLEAN and completeness gates
are met; it cannot replace them. Reviewer completeness values are `COMPLETE`,
`INCOMPLETE`, and `BLOCKED`; the latter two and common `INCOMPLETE` / `BLOCKED`
are non-approval. Kepler publishes reviewer output verbatim under the operator
account and links the reviewer session. The `hvo-agentcontrol[bot]` review
publication route described in the older runbook is unused. Model identity is
reported, not authenticated. Roy decided at 4:27 PM PST on 2026-11-17
(widget t594s3390) to drop independent local Sol/Opus review before draft
creation. All formal reviews start only after draft checks pass on the exact
head; HIGH-tier Sol and Opus reviews are bound to the same exact head.
