---
name: "Implement Virtual-First Roadmap Issue"
description: "Implement one ready issue from the Virtual-First Platform Completion milestone through green review and merge."
argument-hint: "Required: GitHub issue number"
agent: "agent"
---

Implement GitHub issue `${input:scope:Required GitHub issue number}` in this
repository. This command requests implementation, validation, review correction,
and merge of one ready issue, not another plan and not an unbounded multi-issue
change.

Before editing, read:

1. `AGENTS.md`
2. [The authoritative project plan](../../docs/project-plan.md)
3. The selected issue, epic #89, and every dependency
4. The owning subsystem specification or runbook
5. [The requirements crosswalk](../../docs/planning/requirements-crosswalk.md)
6. [The performance validation plan](../../docs/planning/performance-validation.md)
7. [The execution protocol](../../docs/planning/agent-execution.md)
8. [The relevant agent prompt](../../docs/planning/agent-prompts.md)

Confirm the issue is open, milestone-assigned, dependency-ready, and has defined
acceptance and performance evidence. Inspect the current worktree, branch, recent
commits, tests, durable formats, and related PRs. Preserve unrelated changes.

Create one issue-focused branch. Implement the smallest coherent slice that
satisfies the full issue gate. Preserve all architecture boundaries. For durable
changes, cover migration, compatibility, crash, restart, retry, idempotency,
quarantine, and retention. For performance-sensitive changes, record equivalent
baseline and after measurements for relevant I/O, CPU, allocations/working set,
throughput, latency, and backlog. An unexplained regression blocks merge.

Add focused, integration, migration, fault, output, and UI tests as applicable.
Validate checksums, numerical invariants, layout, recipe identity, provenance,
lineage, and durable state. Inspect logs, metrics, traces, and health behavior.

Run the repository and issue-specific validation gates. Commit only intended
files, push, and open a PR linked to the issue and epic #89. Wait for CI and
automatic review. Correct every actionable finding, push correction commits,
and require replacement CI on the corrected head. Reply to and resolve review
threads only after correction evidence exists. Merge only when every required
current-head check is green and no actionable thread remains.

After merge, confirm issue closure, synchronize local `main`, preserve unrelated
worktree changes, and update epic #89 with completed evidence and the next ready
issue.

If execution must stop or blocks, complete every unaffected action and leave the
exact resumable handoff required by `docs/planning/agent-execution.md`. A failing
build, test, review, migration, or CI run is not a handoff condition until it has
been investigated and all available corrections have been attempted.
