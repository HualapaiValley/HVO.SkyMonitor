---
name: "Implement Roadmap Issue"
description: "Own an assigned roadmap issue through review, green CI, authorized merge, and cleanup."
argument-hint: "Required: GitHub issue number"
agent: "agent"
---

Implement GitHub issue `${input:scope:Required GitHub issue number}` in this
repository. This command requests implementation, validation, review correction,
and authorized merge and cleanup of one ready issue per branch and PR, not
another plan and not an unbounded multi-issue change in one branch. The owner is
an authorized human or operator-directed agent; no coordinator is required.
Do not take another issue after merge unless the user authorized that scope.

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

Confirm the issue is open, milestone-assigned, and has defined acceptance and
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

`JOIN`, targeted command receipts, slot leases, and coordinator heartbeat/relay
mechanics apply only to optional legacy managed sessions explicitly enrolled by
the operator, not to this default workflow. Existing resumed fleet tools retain
their identity and command guards. Assess capacity per shared resource, not a
global two-issue limit; isolate concurrent work in separate worktrees. Preserve
sandbox and permission boundaries; never route a denied action to another session.

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
Commit only intended files, push, and open a draft PR linked to the issue and its
owning roadmap epic. As PR owner, request independent human or one-shot agent
review against immutable base/head SHAs and record reviewer identity, findings,
and dispositions. Record actual provider/model/effort for agent reviews, and
N/A for human model/effort. Follow the PR lifecycle skill for the full initial
review, applicable agent start timeouts and fallback, correction-delta rereviews, the
three-rereview cap and follow-up issue, the finalization lock, target-branch
synchronization and base-sync review or unchanged-base proof, protected CI,
authorized merge, and cleanup. Mark the
PR ready only after review convergence and final synchronization. Merge only
when the reviewed current head has green required checks and no actionable
thread remains. If merge is reserved or not authorized, report the boundary and
leave a handoff rather than bypassing it.

While working, post a short progress comment at every milestone and at least
every thirty minutes: on the issue until the draft PR exists, then in the PR's
append-only review ledger. Each comment states, with a UTC timestamp, what
finished, what is running now, the next step, and any blocker. Long gate or
review runs get an interim note rather than silence. This does not replace the
final completion report. Relay milestones and blockers directly to the operator;
no coordinator or fleet heartbeat is required for this default workflow.

After merge, confirm issue closure, synchronize local `main`, preserve unrelated
worktree changes, clean up merged branches/worktrees safely, and update the
owning roadmap epic with completed evidence and the exact next action. Explicitly
release the issue claim with a completion handoff and remove its in-progress
label. Do not select or claim another issue without user authorization.

If execution must stop or blocks, complete every unaffected action and leave the
exact resumable handoff required by `docs/planning/agent-execution.md`. A failing
build, test, review, migration, or CI run is not a handoff condition until it has
been investigated and all available corrections have been attempted. Record
claim retention or explicit release and transfer in the handoff; age or silence
does not authorize takeover.
