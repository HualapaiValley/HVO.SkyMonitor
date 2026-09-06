# HVO.SkyMonitor Agent Guide

This is the canonical repository-wide instruction source for every coding-agent
harness. Agent-specific instruction files must load or point to this file and
must not redefine its policy inconsistently.

## Toolchain and Validation

- Use the SDK pinned in `global.json` (`10.0.400`, stable releases only) and the solution `HVO.SkyMonitor.v9.slnx`.
- Package versions are centralized in `Directory.Packages.props`; do not put `Version` attributes on individual `PackageReference` items.
- The complete Tier C/M local candidate gate is:
  ```bash
  dotnet tool restore
  dotnet restore
  dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Debug -warnaserror
  dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release -warnaserror
  dotnet format HVO.SkyMonitor.v9.slnx --no-restore --verify-no-changes
  ./scripts/package:audit
  DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter "TestCategory=Unit" --settings tests/coverage.runsettings --collect:"XPlat Code Coverage"
  dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter "TestCategory=Integration" --settings tests/coverage.runsettings --collect:"XPlat Code Coverage"
  ```
- Reproduce the exact category/project evidence, architecture/publish, migration, and canonical coverage gates with `docs/runbooks/ci-pipeline.md` and `.github/workflows/ci.yml`.
- Unit selection is positive and passes with an invalid Docker endpoint. Integration selection is a separate required gate and requires Docker for the Testcontainers assemblies.
- Run a focused MSTest with `dotnet test <project> --filter "FullyQualifiedName~Namespace.Class.Method"`.
- `tests/coverage.runsettings` excludes test assemblies, `TestSupport`, migrations, and build output; keep coverage configuration aligned when adding projects.
- Use the risk-tiered validation ladder in `docs/planning/agent-execution.md`:
  focused tests in the inner loop, tier-appropriate local candidate evidence
  before the first push, affected gates for corrections, and classifier-selected
  protected CI on the final reviewed head. Tier C/M work runs the complete local
  candidate gate; Tier A/B work relies on focused/affected local evidence plus
  protected CI. Do not repeatedly run unchanged long suites or performance
  harnesses.

## Architecture Boundaries

- Read `docs/roadmap.md` for portfolio initiatives, stable `RM-###` IDs, and future direction. `docs/project-plan.md` remains authoritative for architecture, virtual-first requirements, phase order, and completion; it forbids references between `CameraAgent` and `LogicHost`.
- `AgentCore` contains stable transport-neutral camera, rig, frame, and artifact contracts only; do not add ASP.NET, EF Core, object-storage SDK, SkiaSharp, or camera-SDK dependencies.
- `HVO.SkyMonitor.Processing` (introduced under issue #93) owns host-neutral recipe definitions and execution contracts; it may reference AgentCore, Astronomy, and Imaging but no host or persistence infrastructure.
- `HVO.SkyMonitor.Common` contains reusable ASP.NET security, identity, API, middleware, and observability infrastructure only; do not move capture, recipe, image, edge-workflow, or central-workflow ownership into it.
- `HVO.SkyMonitor.Storage.FileSystem` (introduced under issue #592) owns only host-neutral root confinement, symlink/reparse protection, atomic publication, durable file/directory synchronization, and bounded cleanup/error primitives. It must not own CameraAgent or LogicHost storage contracts, capture/object identity, manifests, persistence policy, ASP.NET, EF Core, SQLite, or provider SDKs. Register it explicitly in the solution graph, architecture rules, coverage policy, and CI component classifier.
- `CameraAgent.Common` owns edge capture orchestration: module registration/factory, SQLite/file durable ingress, durable lanes, local storage, outbox, retention, telemetry, and configuration loading. The `CameraAgent` host registers this through `AddCameraAgentInfrastructure` and hosts the local UI/API/identity.
- `HVO.SkyMonitor.CameraAgent.Replay` owns the authenticated bounded local transport and immutable projection for explicitly requested archived replay; `HVO.SkyMonitor.CameraAgent.ReplayRunner` is its self-contained local executable. CameraAgent retains durable job, lease, and publication authority, and newly acquired/live work never dispatches to the replay runner.
- `LogicHost` is the central ASP.NET host; it owns SQL Server/Redis/provider-neutral object-storage services and provider adapters and runs EF migrations plus seed data at startup. Its `IObjectStore` contract never becomes the CameraAgent storage contract.
- New reusable astronomy/projection behavior belongs in `HVO.SkyMonitor.Astronomy`, and reusable image algorithms in `HVO.SkyMonitor.Imaging`; do not create host-specific projection math. Astronomy catalogs are versioned read-only SQLite snapshots deployed locally to LogicHost and every CameraAgent, never part of the shared SQL Server schema.
- Concrete read-only catalog persistence belongs in the optional `HVO.SkyMonitor.Catalog.Sqlite` infrastructure adapter shared by both hosts. It may reference Astronomy contracts; Astronomy must remain storage-neutral and must not reference the adapter.

## Runtime and Infrastructure

- SQL Server, Redis, MinIO, and Mailpit are persistent shared services on `hvo-docker`; configure their endpoints and credentials in the ignored `.env` using `.env.template`.
- Use `./scripts/infra:start [logichost|cameraagent]` rather than raw Compose for application containers. `--reset` only deletes application container state and rebuilding refreshes the image.
- Run hosts directly through `./scripts/with-env dotnet run --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj` and `./scripts/with-env dotnet run --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj`. Compose exposes them at ports `5174` and `5130` respectively.
- LogicHost Data Protection keys and CameraAgent local Identity, Data Protection, and provisioning files are runtime state under `data/`, `App_Data/`, `DataProtection-Keys/`, and the configured `DeviceProvisioning:StateDirectory`; do not add them to commits.
- Keep local secrets in user secrets, `.env`, or `.devcontainer/devcontainer.local.env`. These are intentionally ignored; `.env.template` contains the supported local defaults.

## Tests and UI

- LogicHost integration tests start SQL Server, Redis, MinIO, and Mailpit through Testcontainers; CameraAgent integration tests reuse that central-host fixture and wire in-process HTTP handlers.
- CameraAgent module/pipeline configuration comes from `cameraagent.sample.json` and the `CameraAgent` configuration section. Preserve configuration-driven module discovery and explicit pipeline graphs that use stable step aliases rather than adding host-specific branches.
- For Blazor components with logic, keep markup, code-behind, scoped CSS, and optional scoped JS in sibling `.razor`, `.razor.cs`, `.razor.css`, and `.razor.js` files. Root/layout components must render `data-theme="hvo-dark"` on `<html>`.

## Roadmap Execution

### Agent Workspaces

- Run agents from the native host, an SSH session, or remote-agent tooling.
  Keep concurrent work isolated in Git worktrees at durable, host-owned paths
  appropriate to the selected tool. Do not rely on a devcontainer's writable
  layer for authoritative work or resumable state.

- Use the active initiative's owning roadmap epic for coordination, claims, and
  handoffs. Epic #89 and the `Virtual-First Platform Completion` milestone retain
  the completed virtual-first delivery history.
- Identify every coordination participant as
  `<harness>:<provider>:<host>:<session-short-id>`; provider-only names such as
  `Claude` are display labels, not identities. Do not include credentials or a
  complete bearer/session token in the ID. A new session remains
  `UNREGISTERED/WAIT` until it sends `JOIN REQUEST`, the coordinator returns a
  participant-bound `JOIN ACK`, and it returns `JOINED ACK`. Loading this file
  is not enrollment, and an unrelated operator-assigned repository task does
  not join or mutate the roadmap epic unless explicitly enrolled. Follow the
  registry, lease, and targeted-command mechanics in section 4 of
  `docs/planning/agent-execution.md`.
- Before implementing a roadmap issue, follow `docs/planning/agent-execution.md` and the relevant section of `docs/planning/agent-prompts.md`.
- Before opening, reviewing, updating, finalizing, or merging any PR, read and
  follow `.agents/skills/pr-lifecycle/SKILL.md`. This instruction is mandatory
  even when the current harness does not discover repository skills natively.
- Use `docs/planning/requirements-crosswalk.md` for the owning detailed specification and `docs/planning/performance-validation.md` for canonical workloads and evidence.
- Keep one implementation issue per branch/PR unless dependencies explicitly coordinate stacked PRs.
- Before implementation, post the protocol's plain-language synopsis explaining
  why the issue is next, its practical outcome and benefit, what it unlocks, and
  the main exclusion.
- Use concurrent subagents for non-overlapping exploration, review, failure
  analysis, and evidence. By default, up to two independent ready issues may
  proceed in isolated worktrees when dependencies are merged and machine/Docker
  capacity permits; raise that limit only after explicitly verifying capacity.
  The roadmap coordinator records claims in the owning roadmap epic and never
  lets agents edit the same worktree.
- Treat those implementation slots as shared capacity, not per-component
  reservations. Fill them from the highest-priority Current initiative in
  `docs/roadmap.md`; do not start a lower-horizon issue while candidate-ready
  work remains there unless the active epic records a real blocking dependency.
  Follow the lane and unrelated-component failure rules in section 4 of
  `docs/planning/agent-execution.md`.
- Every PR uses a draft-first convergence cycle. Protected CI must not run until
  review has converged and the target branch has been finally synchronized and
  integration-reviewed when synchronization changes the target base. A freshly
  fetched target SHA already covered by the converged review needs a recorded
  unchanged-base proof, not a redundant base-sync review. Use a
  coordinator-launched local review agent, or an
  equivalent execution route that can bind the requested immutable range and
  record its actual provider, model, and reasoning effort, for convergence
  evidence. Provider-side PR bots may supplement this with a current-head audit
  but do not replace exact-range evidence. Select the review agent from the
  issue tier, review mode, finding severity, and cross-boundary risk using the
  PR lifecycle skill; never leave model or effort selection implicit. Initial
  review covers the full PR diff; correction
  rereviews cover only the delta from the previous reviewed head and its concrete
  interactions, and verify every prior finding individually. A finding neither
  verified fixed nor explicitly deferred to a linked issue remains unresolved.
  If the primary reviewer does not start within fifteen minutes, use
  the other available provider; if neither Copilot nor Codex starts within its
  fifteen-minute window, record an exact-head review-unavailability waiver.
  Allow at most three correction rereviews per PR before moving remaining
  non-blocking findings to one linked follow-up issue. Security, data-loss,
  acceptance, failing-CI, and material-correctness defects always block.
- Tier A/B PRs receive one initial exact-range review plus only finding-driven
  correction rereviews. Use `standard` by default and promote Tier B to `deep`
  for concurrency, durability, security, CI-control, or cross-boundary risk.
  Tier C/M review depth is unchanged. Generate and dispatch review requests with
  the repository scripts named by the PR lifecycle skill.
- Only one PR may hold the repository-wide finalization lock. Acquire it before
  final target synchronization and hold it through any required base-sync
  review, protected CI, and merge. If the target branch advances, return the PR
  to draft, release the lock, synchronize and review again, then run CI on the
  new reviewed head.
- Opening a PR assigns its implementing agent ownership of the complete lifecycle:
  request review, actively monitor review and checks, disposition findings, drive
  corrections and bounded rereviews, mark the converged head ready, monitor final
  protected CI, merge when green, synchronize `main`, and remove merged branches.
  Continue without a routine operator prompt unless the operator explicitly
  pauses, limits, or reserves the merge decision, or a real blocker requires a
  decision.
- Performance-sensitive work requires reproducible baseline/after evidence for relevant I/O, CPU, allocations/working set, throughput, latency, and backlog. Unexplained regression blocks merge.
- Validate produced outputs through checksums, numerical invariants, provenance, lineage, and durable state where applicable. Inspect logs, metrics, traces, and health behavior for host/worker changes.
- If work stops or blocks, leave the resumable handoff required by the execution protocol and update the owning roadmap epic with the exact next action.
- After merging a roadmap issue, the roadmap coordinator automatically claims
  and starts the highest-priority candidate-ready issue after posting its
  synopsis and `READY` signal. Other implementing agents return completion state
  to the coordinator. Pause only on explicit operator request, a decision
  blocker, no candidate-ready work, or exhausted safe capacity; do not require a
  routine `continue` prompt.

### Progress Reporting

Progress reporting is mandatory while delegated work, review acquisition, long
gates, or CI is active. Section 4 of `docs/planning/agent-execution.md` is the
single canonical source for the complete monitor, heartbeat, ledger, and
cross-provider channel procedure. These independently loaded invariants remain:

- The coordinator arms exactly one five-minute monitor whose cadence actively
  wakes or messages it, proves that path with an immediate baseline after every
  start/restart, and polls directly after a signaling gap. Buffered output is
  not a working monitor, and delivery to the main conversation cannot be
  delegated.
- Cross-provider fallback uses two participant-leased mutable epic slots,
  compact sequenced/acknowledged state, append-only durable decisions, and
  metadata/cursor reads instead of epic rescans. Keep `C1.1` until the execution
  protocol's identity, CAS, and explicit `C1.2` cutover conditions are met.
- On every wake, inspect each active issue/PR, current review and CI state, and
  shared locks. Attach terminal-only CI watchers; do not repeatedly run or
  report unchanged expensive work.
- Relay every wake to the operator in fixed MST (UTC-7), including the literal
  `still running, no change` when applicable, plus every `STARTED`, milestone,
  blocker, and completion immediately. Each item retains finished/current/next/
  blocker; repository comments alone are not operator-visible reporting.
- Enrolled agents echo participant and command IDs in their reports and write
  milestones in UTC to the issue or PR ledger at least every thirty minutes.
  Long gates and reviews get an interim note, and a blocked agent still leaves
  the resumable handoff.
