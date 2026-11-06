# Split-host preflight implementation plan

> Execute inline in the assigned worktree. The parent owns PR/review/finalization; no further delegation or GitHub writes are authorized here.

**Goal:** Port #1593's inventory/preflight and ancillary assertions with the exact existing refusal, state and evidence contracts.

**Architecture:** Reuse `SplitHostInventory`, `DeploymentHost`, private-file validation and run-state locking from #1588/#1589. Put transport-neutral probe/continuity/transient decisions in Deployment.Contracts and phase orchestration/publication in Deployment.Cli. Remote adapters expose bounded read-only OS, TCP and HTTP primitives; they never call the old phase. Preserve the shell twin until complete comparison passes.

**Tech stack:** .NET 10.0.402, System.Text.Json, MSTest, Linux file/lock primitives, SSH/Docker process adapters, Bash 5.1+ comparison boundary.

### 1. Baseline and assertion inventory

- [x] Read latest issue/comments, repository rules, lifecycle/worker/review skills, prior cutover evidence and design.
- [x] Record every assertion/vector in `docs/validation/preflight-1593.md`, keyed to original file/line and exact typed regression.
- [x] Retain base source, configuration hashes and original full preflight cycle in the tracked scratch directory. Capture complete fake-target inputs/outputs before changing authority.

### 2. Probe contract and typed phase

Files: `src/HVO.SkyMonitor.Deployment.Contracts/PreflightContracts.cs`, `src/HVO.SkyMonitor.Deployment.Cli/SplitHost/Preflight*.cs`, `DeploymentHost.cs`, existing `SplitHostInventory.cs`.

- [x] Test the probe sequence, correlation and ordered diagnostics using a disposable typed fake target before implementing it: valid split/same host, capacity, architecture, tool absence, roots, ports, TCP/HTTP.
- [x] Implement `SplitHostPreflight.RunAsync(PreflightRequest, CancellationToken)` using the existing inventory and secret validators before opening any host. Keep timestamps/fields/check order identical to `preflight.sh`.
- [x] Implement exact prior-manifest/evidence validation before publication; publish running, incremental checks, evidence then passed manifest. Retain `before-manifest-rename` and `after-evidence-before-passed-manifest` boundaries; failed accepted reruns publish failed evidence, rejected input preserves prior bytes.
- [x] Test cancellation, symlink/hardlink/lock contention, quoted paths and bounded secret-free diagnostics.

### 3. Ancillary preflight assertions

Files: contract validators and `tests/HVO.SkyMonitor.Deployment.Cli.Tests/Preflight*Tests*.cs`.

- [x] Port persistent capture continuity, transient allowlist/detail/audit/mail/evidence decisions and regress every original vector.
- [x] Exercise private-upload registration/publication/move failure, tampered registry paths and Docker absence/outage through shared typed host primitives.
- [x] Assert example/schema agreement, workload source/config/options hashes, product layout invariants and every inventory/secret rejection vector. Retain production `bash -n` as a named shell boundary.

### 4. Comparison and authority

- [x] Run the complete shell assertion cycle and a corresponding typed cycle on exact retained inputs; compare exits and complete state/evidence/remote snapshots, normalizing only phase timestamps and recorded clock offsets.
- [x] Record same-workload wall/peak RSS with source/config/binary pins and investigate any regression. Do not claim whole-lane improvement.
- [x] Only on complete parity, update CLI dispatch/help, compatibility forwarding, shard manifest/dispatch/category inventory and runbook while preserving order and deliberate boundaries. Keep the twin if parity fails.
- [x] Run focused tests and affected shell/control checks under `flock -o /tmp/hvo-heavy-gate.lock timeout ...`; all builds/tests use `-nodeReuse:false -p:UseSharedCompilation=false`. Unit environment unsets HVO_PRODUCTION_CATALOG_BUNDLE and pins invalid DOCKER_HOST. Parent runs the complete Tier C candidate gate.
- [x] Review `git diff --check`, source pins and evidence, then commit intended files locally. Return commits, assertion/evidence paths, exact commands/results and retained scratch/resources to parent.

Execution evidence and all original assertion vectors are recorded in `docs/validation/preflight-1593.md`. The parent retains the complete candidate gate and lifecycle.
