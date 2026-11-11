---
name: "Implement Roadmap Issue"
description: "Implement and validate an assigned roadmap issue, obtain local review, and hand the candidate to Kepler."
argument-hint: "Required: GitHub issue number"
agent: "agent"
---

Implement GitHub issue `${input:scope:Required GitHub issue number}` in this
repository. This command requests implementation, validation, independent local
review, and corrections for one ready issue, followed by a candidate handoff to
Kepler. Coding threads push only when instructed and never create PRs, mark
ready, set `hvo/reviewed-head`, or enqueue. Kepler owns formal review and the
merge queue. Do not take another issue unless the user authorized that scope.

Before editing, read:

1. `AGENTS.md`
2. [The product roadmap](../../docs/roadmap.md)
3. [The authoritative project plan](../../docs/project-plan.md)
4. The selected issue, its owning roadmap epic, and every dependency
5. The owning subsystem specification or runbook
6. [The requirements crosswalk](../../docs/planning/requirements-crosswalk.md)
7. [The performance validation plan](../../docs/planning/performance-validation.md)
8. [The execution protocol](../../docs/planning/agent-execution.md)
9. [The PR lifecycle skill](../../.agents/skills/pr-lifecycle/SKILL.md)
10. [The relevant agent prompt](../../docs/planning/agent-prompts.md)

Confirm the issue is open, has the correct milestone when one is assigned, and has defined acceptance and
performance evidence and dependencies are ready. Inspect current claims, assign
yourself on GitHub if possible, apply `workflow:in-progress`, and post a claim
comment naming a unique person/agent identity, branch/worktree, scope, and next
checkpoint. An assignee alone is insufficient when agents share an account.
Use `<harness>:<provider>:<host>:<session-short-id>` for agents without secrets.
Re-read issue comments, assignees, and labels after claiming: these writes are
not atomic. Stop on a competing claim and resolve ownership explicitly. If
permissions prevent claiming, report the blocker rather than assuming ownership.
Keep the claim through review and CI; release explicitly with a handoff, never
through stale-claim auto takeover. Post the plain-language synopsis before
implementation. Inspect the current worktree, branch, recent commits, tests,
durable formats, and related PRs. Preserve unrelated changes.

Work is dispatched over SSH to T3 Code on the dev hosts `home-dev-01`,
`home-dev-02`, `home-dev-03`, `hvo-dev-02`, `hvo-dev-03`, and `hvo-dev-04`.
Each task runs in its own git worktree off `development/v1`, so the main
checkout is never modified.

Assess capacity per shared resource, not a global two-issue limit; isolate
concurrent work in separate worktrees. Preserve sandbox and permission
boundaries; never route a denied action to another session.

Create one issue-focused branch. Implement the smallest coherent slice that
satisfies the full issue gate. Preserve all architecture boundaries. For durable
changes, cover migration, compatibility, crash, restart, retry, idempotency,
quarantine, and retention. For performance-sensitive changes, record equivalent
baseline and after measurements for relevant I/O, CPU, allocations/working set,
throughput, latency, and backlog. An unexplained regression blocks merge.

Add focused, integration, migration, fault, output, and UI tests as applicable.
Validate checksums, numerical invariants, layout, recipe identity, provenance,
lineage, and durable state. Inspect logs, metrics, traces, and health behavior.

Use the execution protocol's validation ladder: focused tests in the inner loop,
one tier-selected local candidate gate before the first push, affected local
gates for corrections, and classifier-selected protected CI on the final
reviewed head. Do not repeat unchanged long suites or performance harnesses.
Commit only intended files. Before independent local review, map every in-scope
original-issue requirement to implemented behavior and local evidence at the
candidate SHA. Missing scope or evidence returns to implementation. Follow the
PR lifecycle skill for LOW/HIGH review requirements, exact ranges, explicit
completeness verdicts, and verified finding dispositions. Hand the candidate,
requirement mapping, guard results, and required local CLEAN reports to Kepler.
Push only when instructed; stop at that handoff.

Report milestones and blockers directly to the operator and retain UTC local
evidence at least every thirty minutes during active work. Keep the issue claim
through review and CI; Kepler maintains the PR ledger and operates readiness,
reviewed-head publication, and the queue. Target-tip movement alone does not
require synchronization or new review; real conflicts and changed candidates
follow the lifecycle skill. Long gates and reviews get interim notes.

If execution must stop or blocks, complete every unaffected action and leave the
exact resumable handoff required by `docs/planning/agent-execution.md`. A failing
build, test, review, migration, or CI run is not a handoff condition until it has
been investigated and all available corrections have been attempted. Record
claim retention or explicit release and transfer in the handoff; age or silence
does not authorize takeover.
