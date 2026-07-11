---
name: "Implement SkyMonitor Project"
description: "Implement every HVO.SkyMonitor project-plan phase in order with TDD, documentation, coverage, and zero-warning validation."
argument-hint: "Optional: starting phase or initial deliverable"
agent: "agent"
---

Implement all incomplete phases in order from
[the authoritative project plan](../../docs/project-plan.md), continuing until
every phase and the full project success criteria are complete. This is an
autonomous, multi-phase implementation command, not a request for a plan,
analysis, a single slice, or a handoff. Begin repository work immediately. Do
not return a final response while any deliverable, exit criterion, validation
gate, or success criterion remains incomplete. If the user names a starting
phase or initial deliverable in
`${input:scope:Optional starting phase or deliverable}`, begin there after
confirming earlier prerequisites are complete; then continue through all
remaining phases. Also follow the detailed
[virtual-camera specification](../../docs/virtual-camera.md) and pinned
[legacy reference inventory](../../docs/reference-code.md) when relevant.

## Continuation contract

The task remains active across all slices, phases, tool calls, test failures,
context compaction, and progress updates. A progress update is not a request
for permission and must be followed by the next implementation action in the
same run. Do not pause to ask whether to continue, offer the next phase as an
option, or end after reporting a build/test failure, a partial implementation,
a clean baseline, a task list, or a phase boundary.

Create a task list before the first edit that includes every incomplete phase,
its remaining exit criteria, and the final success definition. Keep exactly one
implementation item in progress. Completing a task-list item, exhausting the
current context, or reaching a convenient reviewable commit-sized change does
not complete this command: record the verified checkpoint and continue with
the next unfinished item. Re-read the task list and the authoritative plan
after context compaction before resuming work.

The only permitted reasons to stop before full completion are the genuine
external blockers defined in the non-negotiable quality gate below. A failing
repository test, build, restore, lint, coverage check, migration, package
upgrade, design complexity, missing implementation detail, or a large amount
of remaining work is not an external blocker. Diagnose it, choose the plan's
documented default where one exists, implement the repair, and continue.

Work in small reviewable vertical slices, but do not stop after a slice or
phase succeeds. For each iteration, select the smallest slice that produces
observable behavior and advances a listed exit criterion. State the selected
phase, acceptance criterion, owning production path, and cheapest falsifying
test before editing. Do not batch several phases into one unvalidated change.
Immediately after validating a slice, select and execute the next slice; do
not produce a final answer, await user confirmation, or leave a proposed plan
instead of executing it.

## Required workflow

1. Read repository instructions, the selected plan section, nearby production
   code, and its closest tests. Inspect pinned legacy code only when the plan
   identifies it as a behavioral reference.
2. Check the working tree. Preserve all user changes and avoid unrelated
   refactors, formatting churn, package updates, or documentation rewrites.
3. Convert the selected acceptance criterion into a test matrix covering the
   happy path, boundaries, invalid input, failure behavior, and relevant
   concurrency, cancellation, restart, or compatibility behavior.
4. Use red-green-refactor for deterministic logic and regressions: add one
   focused failing MSTest, run it to verify the expected failure, implement the
   smallest production behavior, rerun it, and then expand the matrix.
5. Keep astronomy/projection in `HVO.SkyMonitor.Astronomy`, reusable pixel and
   rendering behavior in `HVO.SkyMonitor.Imaging`, stable contracts in
   `HVO.SkyMonitor.AgentCore`, edge orchestration in
   `HVO.SkyMonitor.CameraAgent.Common`, and host concerns in their owning host.
   Never duplicate projection or celestial calculations in CameraAgent or
   LogicHost.
6. Make tests deterministic with `TimeProvider`, fixed seeds, temporary
   directories, controlled cancellation, and disposable infrastructure. Do not
   use arbitrary sleeps, live web services, test ordering, or broad golden-
   image comparisons. Pair image fixtures with numeric geometry and statistics.
7. Document public shared APIs with XML comments. Document units, ranges,
   conventions, ownership, failures, and thread safety where applicable. Add
   concise rationale comments only for non-obvious algorithms, formulas,
   concurrency invariants, or durability rules, and cite authoritative or
   pinned legacy sources for astronomy behavior.
8. After each substantive edit, run the narrowest test that can falsify it.
   Before completion, run the owning test project, affected integration or
   architecture tests, and the full non-hardware suite.
9. Measure coverage for touched production files. Require at least 90% line and
   85% branch coverage for changed domain logic, and 95% line and 90% branch
   coverage for projection, frame layout, exposure, combination, provenance,
   retention, and upload-idempotency logic. Add meaningful cases rather than
   assertion-free coverage.
10. Update plan status only when executable evidence satisfies the stated exit
    criteria. Update relevant runbooks, samples, manifests, and operator docs in
    the same slice.
11. After a slice passes its focused checks, proceed immediately to the next
    incomplete criterion in the same phase. After all phase exit criteria pass,
    run the phase gate, mark the phase complete, and continue into the next
    phase without waiting for another request. A phase status is changed only
    after its gate passes; never mark it complete merely because its code was
    written or individual tests pass.
12. Continue this cycle through every phase, including deferred LogicHost
   phases once their prerequisites are complete. A phase marked `Deferred` in
   the plan describes priority, not permission to leave the project unfinished
   when this prompt is explicitly running the complete implementation.

Maintain an up-to-date task list throughout the run. Provide concise progress
updates at slice and phase boundaries, including validation evidence and the
next slice, but reserve the completion report for the end of the project. Send
progress updates only as interim status, then keep working. Recover from test
or build failures by diagnosing and repairing the current slice before
proceeding.

## Parallel-agent policy

You may use subagents for bounded, independent work such as legacy-source
research, test-matrix review, architecture review, or post-implementation code
review. Give each subagent exact files, questions, and expected evidence.
Keep one primary agent responsible for integration and final validation. Do not
let multiple agents edit the same files or implement competing versions. Treat
subagent reports as review input, not proof: the primary agent reruns all checks.

## Non-negotiable quality gate

- Do not delete, skip, weaken, or recategorize a test to obtain a pass.
- Do not add broad `NoWarn`, lower analysis settings, disable analyzers, or hide
  package audit warnings.
- Fix warnings at their source. A narrowly scoped suppression is acceptable
  only for a documented false positive or required generated/framework pattern.
- Resolve vulnerable dependencies by upgrading, replacing, or removing them.
- Completion requires restore, Debug and Release builds, and applicable tests
  to finish with zero errors and zero warnings. Use warnings-as-errors after the
  Phase 0 warning baseline has been eliminated.
- Do not claim completion for checks that were not run. Hardware, ARM64,
  external planetarium, or long-duration checks may be reported as pending only
   while working through phases that do not require them for an exit criterion.
- Do not stop because a slice or phase is large, tests fail, or context is
  compacted. Preserve current state, continue from the last verified checkpoint,
  and work through the remaining plan. Do not turn this condition into a final
  report or ask the user to reissue the command.
- Stop before full completion only for a genuine external blocker that cannot
  be resolved with repository code or available tools, such as unavailable
  physical hardware, missing credentials that must not be requested through
  chat, or an irreversible product decision with no documented default. Record
  the exact blocker, completed evidence, and first resumable action. Before
  stopping, complete every unaffected task that can be completed without that
  blocker and verify that no repository-local workaround or documented default
  exists.

At each phase boundary and again after the final phase, run this sequence from
the repository root, adjusting only the test filter when that phase requires
Docker, hardware, manual, or long-running categories:

```bash
dotnet restore HVO.SkyMonitor.v9.slnx
dotnet build HVO.SkyMonitor.v9.slnx -c Debug --no-restore -warnaserror
dotnet build HVO.SkyMonitor.v9.slnx -c Release --no-restore -warnaserror
dotnet test HVO.SkyMonitor.v9.slnx -c Release --no-build \
   --filter "TestCategory!=Hardware&TestCategory!=Manual" \
   --settings tests/coverage.runsettings \
   --collect:"XPlat Code Coverage"
dotnet format HVO.SkyMonitor.v9.slnx --verify-no-changes --no-restore
dotnet list HVO.SkyMonitor.v9.slnx package --vulnerable --include-transitive
```

The package audit must report no vulnerable packages. Inspect the build and
test summaries rather than relying only on process exit codes, because an
unenforced warning or skipped category can otherwise look successful.

## Completion report

Only after all phases and project success criteria pass, report the phases and
acceptance criteria completed, behavior implemented, files changed, tests
added, red/green evidence, coverage, commands and results, documentation
updated, and warnings/suppressions introduced or removed. Before sending that
report, compare every project-plan deliverable and exit criterion against
executable evidence and resolve any remaining item. If execution stops at a
genuine external blocker, provide the same report for completed work plus the
blocker and exact continuation point; do not describe partially validated work
as complete. Do not use a normal final response for any other partial state.
