> Historical reproducer at `25847588c1800296b2db65562384d32e3e793ee8`.
> The coordinator corrected its shipped-edge interpretation and authorized a scoped
> fix in [the current conformance record](issue-529-host-adapter-parity-r2.md).
> Original failures and the original hashed archive remain retained; the record
> below describes the initial generic-factory experiment.

# Frozen satellite host-adapter conformance — #529

Status: **BLOCKED / IMPLEMENTATION REQUIRED**, 2026-11-13. This is a retained
failing reproducer, not a merge-ready candidate or completed cross-host acceptance.
The explicit Hub brief requires preserving a minimal failing example and naming
the affected owner/path when a runtime mismatch prevents conformance. No runtime
contract, adapter, fixture, golden, numerical tolerance or default was changed.

## Assignment and original sources

Roy explicitly assigned this bounded slice to
`codex:cliproxy:home-dev-02:a7186bdb`, actual Codex/CLIProxy `gpt-6.1-sol`, xhigh.
Fast was authorized; this record does not independently attest transport service
tier. Branch `t3/satellite-host-adapter-parity`, worktree
`/home/roys/.t3/worktrees/HVO.SkyMonitor/t3-ddd27ea6-45a1bdab0e07e61e`, freshly
fetched `development/v1` base `b961cd6a4dca75bba606cd4b66674983d3312320`.
The immutable reproducer head and archive hashes are in the external handoff
packet; this document is included in that head.

Before editing, fresh primary GitHub reads retrieved #529's original body, all
21 comments and 90 timeline events, and all 58 open issue/PR objects. #529 had
no assignee, workflow claim or linked open implementation PR. Historical
[producer release](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/529#issuecomment-6281755752)
and [Roy-authorized reservation release](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/529#issuecomment-6381001322)
were read. This explicit assignment authorizes the slice independently of the
ordinary `agent:ready` queue condition. The unique
[claim and synopsis](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/529#issuecomment-6863147136)
were posted and sole ownership re-read before editing. Other claims/worktrees
were preserved, including the retained #1393 UI owner.

Requirement sources are [#529](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/529),
its [remaining-scope clarification](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/529#issuecomment-6298543561),
Roy's 2026-11-13 bounded Hub brief, accepted
[#1640](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1640)/[PR #1645](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1645)
and [#1641](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1641)/[PR #1649](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1649),
the [capture contract](../contracts/capture-orbital-prediction-v1.md),
[orbital contract](../contracts/orbital-elements-v1.md), and
[accepted qualification index](orbital-prediction-v1/README.md).
Fresh issue JSON and discussion snapshots are retained in the evidence archive.

Measured home-dev-02 capacity before implementation: Linux x64, 12 CPUs,
approximately 18 GiB available RAM, 852 GiB disk available, load 0.13/0.03/0.01,
SDK 10.0.402, Bash 5.2, `flock`, Docker 29.9.3, no running containers.
Primary T3 enumeration found only this active thread, with no next page.
hvo-dev03 and M5 reviewer reservations were preserved. No agents/reviewers were
launched and no push, PR, deployment, infrastructure or hardware action occurred.

## Minimal reproducer and observed mismatches

`HostRecipeContractConformanceTests.Orbital.cs` is shared by source link with
the standalone LogicHost tests. Both projects link the original
`CaptureOrbitalPredictionFixtures.cs` source and explicitly copy its exact
committed inputs; there is no test-project reference or duplicated fixture schema.
Each test constructs its own fixture, restores a separate immutable plan, and
constructs a fresh real recipe/executor. LogicHost receives the descriptor and
payload; it does not receive an already-normalized artifact. The combined lane
also calls the real edge `CreateArtifact` factory before its execution adapter.
Factory output fields are not overwritten to obtain a pass.

Before numerical execution the recording wrapper asserts capture, sequence,
artifact, descriptor, role, variant, raw recipe, readout/layout, observation
interval, effective integration and raw-byte checksum bindings. The outcome
assertion records creation-time and media-type differences, then requires a
produced result and the unchanged canonical golden bytes/checksum/source lineage.

| Owner/path | Required versus observed | Classification / severity |
| --- | --- | --- |
| LogicHost Processing — `LogicHostRecipeExecutionAdapter.CreateArtifact` | The capture contract requires `CreatedUtc == descriptor.Artifact.CreatedUtc`. Reconstruction sets it to `observation.StartedUtc`. In all three fixtures it is 5.020 seconds earlier, so the real recipe returns `TerminalFailure / capture-orbital-input-mismatch`, with zero products. | Existing host-binding behavior exposed by a new test; Medium blocker. |
| CameraAgent.Common Processing — `CameraAgentRecipeExecutionAdapter.CreateArtifact` | The retained fixture descriptor requires `application/octet-stream`. The edge factory sets `application/x-hvo-frame` despite receiving that descriptor. Creation time and the other asserted bindings match, but the recipe rejects the media mismatch, with zero products. | Existing mismatch for the accepted frozen fixture seam; Medium blocker. This does not establish a deployed/default-profile failure. |

`CaptureOrbitalPredictionInput.Matches` deliberately requires both facts to
equal the retained descriptor. Removing those checks, rewriting the descriptor,
passing `fixture.Raw` instead of factory output, or using the artifact bypass
overload would conceal this gap. The frozen direct-executor controls reproduce
all three existing golden wrappers and embedded tracks byte for byte, so the
failures are not evidence of different numerical predictions or corrupt goldens.

Exact ISS creation times are expected `2026-10-20T13:03:47.3076740+00:00`,
reconstructed `2026-10-20T13:03:42.2876740+00:00`. Both Starlink cases expect
`2026-10-20T04:00:15.0200000+00:00`, reconstructed
`2026-10-20T04:00:10.0000000+00:00`.

## Requirement/evidence map

These rows map the authorized Hub acceptance, not a new definition derived from
the reproducer. Existing accepted evidence remains credited at its original scope.

| Requirement | Evidence supplied | Status / remaining work |
| --- | --- | --- |
| R1 — exact ISS/two-Starlink snapshot, records, time bytes, rig/readout, descriptor/raw, policies and goldens | Original fixture source and twelve committed data files are reused unchanged. [Hash manifest](issue-529-host-adapter-parity-fixtures.sha256) includes those inputs and existing provenance/license documents. Direct controls pass in both assemblies. | PROVED for reuse and direct golden validity. |
| R2 — independent immutable plans/executors and actual host boundaries | Separate Restore/recipe/executor per case; real edge factory and LogicHost descriptor reconstruction are exercised with pre-execution binding assertions. | BLOCKED by the two mismatches above; host parity is not established. |
| R3 — exact canonical payload/checksum/identities/lineage/model/availability and truthful Unknown/null | Direct controls compare complete golden wrappers and embedded tracks, checksum/content identity and raw source IDs. | MISSING at successful host boundaries; full identity and Unknown/null assertions remain to implement after mismatch disposition. |
| R4 — restored-plan repeatability, separately labeled OriginalAvailability/RetrospectiveReassessment and later-data rejection | Existing Processing tests remain credited, without substituting them for adapter evidence. | MISSING for this host-specific slice. |
| R5 — unavailable snapshot/object, descriptor/payload/plan rejection, caller cancellation and targeted negative controls | Authentic adapter misbindings produce zero-result terminal failures while exact-input direct controls pass. Original failed TRXs are retained. | MISSING for the remaining host-specific negative/cancellation matrix and separate mutation controls. |
| R6 — explicit acceptance boundary and parent tracking | This document and retained issue claim name the partial software scope and exclusions. | PROVED as a scope record; #529 remains OPEN. |

Normal host execution is blocked. Failure behavior above is evidenced; remaining
negative/cancellation/repeatability/interaction cases are unavailable, not passed
or N/A. Durable-state recovery/publication/retention and deployed two-service
adoption are N/A to this in-process test/evidence slice; accepted #1641 evidence
and its owners are not replaced. Full active-population resource acceptance,
qualified physical illumination/uncertainty and integrated #1395 cadence remain
outside this slice. No performance campaign was repeated or newly qualified.

Owner verdict: **IMPLEMENTATION REQUIRED** for the assigned conformance slice.
Max must disposition the named adapter-owner paths before expanding scope;
then complete the declared matrix, Tier C gates and separate Sol **and** Opus
deep CLEAN reviews on the same immutable candidate. No review-ready or issue
resolution claim is made.

## Local results and reproduction

Both focused Release project builds passed with zero warnings/errors. Original
focused runs used an invalid Docker endpoint and executed all selected cases:

| Assembly | Total | Passed | Failed | Skipped | Exit |
| --- | --- | --- | --- | --- | --- |
| Combined | 9 | 3 direct controls | 6 adapter cases | 0 | 1 |
| Standalone LogicHost | 6 | 3 direct controls | 3 adapter cases | 0 | 1 |

```bash
dotnet build tests/HVO.SkyMonitor.CameraAgent.LogicHost.Tests/HVO.SkyMonitor.CameraAgent.LogicHost.Tests.csproj --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test tests/HVO.SkyMonitor.CameraAgent.LogicHost.Tests/HVO.SkyMonitor.CameraAgent.LogicHost.Tests.csproj --no-build --configuration Release --filter 'FullyQualifiedName~FrozenOrbital' --logger trx
dotnet build tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj --no-build --configuration Release --filter 'FullyQualifiedName~FrozenOrbital' --logger trx
```

All four control guards and `scripts/docs:audit-operations` passed. The initial
scoped whitespace check failed at the frame initializer; whitespace was fixed
and the scoped verification passed. Original failure output is retained.
Actual filtered discovery matches all six categories in each affected inventory:
combined Unit=103/Manual=1, LogicHost Unit=1031/Manual=5, all others zero.
The repository declared Unit total increases by 15 to 16844; whole-solution
discovery was not run and is not claimed.

Classification is selected by `scripts/ci:classify` on the immutable base..head
range, with exact output retained in the packet. Assigned classification remains
**Tier C / HIGH / deep**. Target profile is **development-v1: Preflight plus
Build and Unit**; no workflow dispatch is authorized by classifier output.
The full Tier C candidate gate, canonical coverage manifests/native coverage
gates, architecture/publish/migration gates and independent CLEAN reviews are
**NOT RUN / NOT SATISFIED** because implementation is blocked at its first host
boundary. Focused discovery and checks do not replace them. No skipped test is
counted as a pass.

The external evidence packet binds the immutable head, exact commands/exits,
source and fixture hashes, TRXs, discovery logs and raw source snapshots. It
also records any final same-source verification after the whitespace correction.
The issue claim is retained pending Max's owner/scope disposition. The specified
coordinator environment was not linked from this session, so the packet is
reported in the implementation thread for Hub polling; no duplicate review or
new coordinator thread was created.
