# HVO.SkyMonitor Copilot Instructions

`AGENTS.md` is the repository-wide engineering authority. Read it before making
changes. For roadmap work, also read these sources in order:

1. `docs/project-plan.md`
2. The active GitHub issue and dependencies
3. The owning subsystem specification or runbook
4. `docs/planning/requirements-crosswalk.md`
5. `docs/planning/performance-validation.md`
6. `docs/planning/agent-execution.md`
7. `docs/planning/agent-prompts.md`

Do not introduce guidance here that conflicts with those sources.

## Core Boundaries

- CameraAgent and LogicHost must not reference each other.
- AgentCore contains transport-neutral camera, rig, frame, artifact, identity,
  lineage, profile, and capture-timing contracts only.
- Astronomy owns shared time, coordinates, catalog contracts, ephemerides,
  camera geometry, projection, and visible-scene behavior.
- Imaging owns reusable pure pixel/image algorithms and encoding.
- Processing owns host-neutral recipes, operation outcomes, windows,
  environmental/event products, and assessments.
- Common owns reusable ASP.NET security, identity, API, middleware, and
  observability infrastructure, not domain workflows.
- CameraAgent.Common owns edge orchestration and local SQLite/file durability.
- LogicHost owns central SQL Server/Redis/MinIO persistence, workers, and UI.
- Production projects must not reference TestSupport.

## Implementation

- Use the SDK in `global.json` and central package versions in
  `Directory.Packages.props`.
- Prefer the smallest correct design. Add specialized persistence, pooling,
  caching, SIMD, parallelism, or other complexity only with correctness or
  repeatable performance evidence.
- Preserve immutable raw evidence, explicit buffer ownership, stable identity,
  ordered lineage, restart recovery, idempotency, and durable-format
  compatibility.
- Use deterministic tests for domain behavior. Integration tests use the real
  disposable dependency where the boundary matters; do not replace SQL,
  SQLite, MinIO, filesystem, or HTTP behavior with mocks and call it integration
  coverage.
- Do not suppress warnings, weaken tests, hide package advisories, or recategorize
  tests merely to make a gate pass.
- Public shared APIs document units, ranges, ownership/lifetime, failure
  behavior, and thread safety. Astronomy sources and constants cite provenance.

## UI

- Preserve the existing HVO dark visual language and authentication patterns.
- Keep nontrivial Blazor markup, code-behind, scoped CSS, and optional scoped JS
  in sibling files.
- Root/layout components render `data-theme="hvo-dark"` on `<html>`.
- Validate loading, empty, stale, degraded, unauthorized, failed, desktop,
  mobile, keyboard, focus, and contrast behavior.

## Delivery

Before implementation, post the execution protocol's plain-language synopsis:
why the issue is next, its practical outcome and benefit, what it unlocks, and
the main exclusion. Every roadmap PR follows the validation ladder and complete
push, review, correction, replacement-CI, thread-resolution, and
current-head-green merge process in `docs/planning/agent-execution.md`. Use
focused inner-loop tests, one stable-candidate local gate, affected correction
gates, and complete final CI rather than repeating unchanged long suites.
Performance-sensitive changes use canonical workloads and report relevant I/O,
CPU, allocations/working set, throughput, latency, and backlog. Data-producing
changes validate checksums, numerical invariants, provenance, lineage, and
durable state. Host/worker changes inspect logs, metrics, traces, health, and
cardinality.

If work stops or blocks, leave the required resumable issue/epic handoff with
the exact next action.
After a merge, the roadmap coordinator automatically claims and begins the
highest-priority candidate-ready issue after posting its synopsis and `READY`
signal, unless the operator explicitly paused execution or a real
blocker/no-candidate-ready-work condition exists. Other agents return completion
state to the coordinator. Independent issues may run concurrently only in
isolated worktrees with stable dependencies, epic #89 claims, and safe
machine/Docker capacity.
