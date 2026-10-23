# #1541 measured-product delivery validation

Local candidate evidence only. The parent owns independent review, PR creation, final synchronization and the Development v1 checks. No push, PR transition, merge or issue closure was performed.

## Revisions and method

- Target/base: `development/v1`, `6ea815f24e1522fe1ba81107fd9a3a2ad993e18e`.
- Built/measured/test candidate: `5faac7563fb2fcdb01822de38075530f68ca062f`.
- Baseline binary/test overlay: `f3528296b9ff7ef3beedb925e8a97d46266dec8a`; only the identical corrected acceptance harness differs from the production base.
- Both production source trees: `01b7ad0247c2097a411cf430fccccc0f87635c09`. This slice changes tests and operator guidance; #1540 already merged the foundation.
- SDK 10.0.402, Release, Linux x64; producer measurement uses the predeclared `tc0-side-cell` (`DOTNET_TieredCompilation=0`). It does not establish production-default JIT latency.
- [Protocol](issue-1541-producer-protocol.md), initial declaration [6359702812](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1541#issuecomment-6359702812), replacement declaration [6360029027](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1541#issuecomment-6360029027). Thresholds and aggregation were fixed before replacement measurement.

## Actual-producer results

Ten fresh cost processes (five interleaved pairs) passed: 600 measured full-recipe B operations and 600 nearest-path A operations, with excluded warmup. Both source arms use identical explicit exhaustive magnitude-5/bound-2000 capture settings, full-resolution Mono16/CFA final noisy pixels and the actual production recipe. Every measurement invocation held the shared heavy-work lock; no collectors were attached.

| Profile | Baseline / candidate median wall (ms) | Wall change | CPU change | Allocation change | Candidate max wall (ms) | Candidate max allocated B/pixel |
| --- | --- | --- | --- | --- | --- | --- |
| mono-native | 179.172 / 182.488 | +1.851% | +0.876% | +0.00256% | 320.383 | 30.431 |
| cfa-native | 1065.357 / 1054.827 | -0.988% | -1.041% | +0.00000% | 1330.044 | 57.120 |

Wall/CPU/allocation entries compare medians of five process-level medians. All declared comparative rules passed. Every B operation passed 2500 ms / 64 B per pixel; every cost process passed the 2 GiB lifetime-peak ceiling. These small timing changes are descriptive, not evidence of an optimization; production source is identical.

RSS median: baseline **1,542,922,240 B**, candidate **1,520,160,768 B**; delta **-22,761,472 B / -1.4752%**. Allowed increase: 77,146,112 B (`max(5%,32 MiB)`). All trials remain in `analysis.json`; no maximum-only substitution or historical pooling.

Final-pixel accuracy: **21 cases**, **63 byte-identical durable replays**, **0 wrong eligible IDs**, at least **340 eligible labels/case**, maximum RMS **0.0927892821 px** (limit 0.25). Every centroid subset is nonempty and finite. Detection/association recall ranges **99.2366–100%**, reported without a new threshold. Raw/module/reconstructed bytes, source/scene/settings/recipe identities, payload/content/output checksums and ordered lineage agree within each case.

| Profile | Candidate per-trial p95 wall range (ms) | Candidate full recipe minus bare measurer, median paired increment (ms) | Candidate product bytes |
| --- | --- | --- | --- |
| mono-native | 187.276–305.899 | 43.902 | 307,063–307,063 |
| cfa-native | 1064.433–1216.691 | 38.220 | 352,257–352,257 |

A is the bare qualified measurer, B includes raw decoding, scene validation, association, policy, identity and serialization. B−A is nearest-path overhead, not an equivalent old-stage replacement. Per-operation CPU, allocation, GC, procfs I/O and verification costs, composed capture/publication/node timing and I/O, throughput, product sizes and complete configurations are retained. Stage block I/O is zero; durable composition I/O is separate. The workload drains one capture at concurrency 1 with upload disabled; no sustained arrival, outage, service-capacity or backlog-recovery claim follows.

The production-resolution pixel work above runs on CameraAgent through the shared recipe. Central runner/ingest behavior is covered separately by Integration cases; the 21 numerical cases do not traverse the central runner. Wider #526 adverse scenarios, covariance/SNR calibration, native/ARM execution, physical cameras, default shipped-profile activation and main promotion remain excluded. No measured zero-copy claim is made.

## Initial failed workload

The initial first pair at `6ea815f2` / `735bdee6` failed the strict identity assertion. Current ordinary host composition selected 200 rendered stars, while old full-catalog truth scored all 1,637 magnitude-5 entries. The scene includes `46518` (3.6 mag) and excludes neighboring `118632` (4.65); the oracle chose the unrendered neighbor. This supports a workload correction, not a product wrong-ID fix or relaxed tolerance. A focused Unit guard first failed with `OperatorSceneObjectLimit`, then passed after the MSA harness explicitly selected `Exhaustive`. Full configuration is now recorded and durable scene depth verified. The shared global qualification fixture and shipped profiles were not changed.

No remaining initial pairs or initial 21-case run executed. The baseline wrapper exited 1; the candidate also produced Failed 1/1 TRX and raw JSON, but its wrapper exit was not retained after driver termination 143. The rejected 55-file pack is sealed separately: manifest `20346dd2a0bd2afdb1248395cf3075afb880ef583c05de70c4390293906f7759`. It supplies no qualification credit.

## Candidate gates

The review-range selector was `./scripts/ci:classify pull_request 6ea815f24e1522fe1ba81107fd9a3a2ad993e18e 5faac7563fb2fcdb01822de38075530f68ca062f`:
`mode=full`, `complete=false`, `cameraagent=true`; shared, LogicHost, combined,
delivery, deployment/catalog/shards/installer, secret-scan-contract and
release-scripts flags were false. Tier C and explicit acceptance nevertheless
require the complete local candidate gate. The protected target profile remains
**Development v1 / Preflight + Build and Unit**. No workflow was dispatched.

Every heavy invocation used `flock -o /tmp/hvo-heavy-gate.lock timeout …`;
build/test argv include `-nodeReuse:false -p:UseSharedCompilation=false`.
`gates/*receipts.json` retain exact per-project argv, timeouts, source revision,
UTC starts, exits and durations. Project selection follows the workflow's
positive category filters with the other categories excluded. Each selection
owns a fresh TRX/Cobertura slot, admitted only after exit 0, no failed tests
and exact agreement with its committed inventory. This is the complete local
matrix, not a claim that a focused pass substitutes for it.

| Gate / actual command family | Result at the built candidate |
| --- | --- |
| `dotnet tool restore`; `dotnet restore`; `./scripts/package:audit` | Pass; SDK/package/tool inputs unchanged after the initial candidate; audit reports no vulnerable or deprecated dependencies |
| `dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Debug/Release -warnaserror` | Both pass after the harness correction, 208.96 / 193.81 s, zero warnings |
| `dotnet format HVO.SkyMonitor.v9.slnx --no-restore --verify-no-changes` | Pass, 137.02 s |
| `./scripts/ci:shell-syntax`, `./scripts/test:ci-classification`, `./scripts/test:coordination-guard`, `./scripts/test:pr-review-tools` | All four pass on the corrected candidate |
| Complete Release Unit, invalid Docker endpoint, `HVO_PRODUCTION_CATALOG_BUNDLE` **unset**, coverage settings/collector | **13,329 selected: 13,205 passed, 124 skipped, zero failed**, all 21 projects present |
| Separate Docker Integration, Release, coverage settings/collector | **926 passed, zero skipped/failed**: LogicHost 560; edge storage 286; standalone 18; Astronomy 2; CameraAgent 35; combined 18; architecture 7 |
| `dotnet run --project scripts/test-categories/HVO.SkyMonitor.TestCategoryAudit.csproj --configuration Release` | Pass: Unit 13,329 / Integration 926 / Manual 276 / Soak 1 / External 0 / Hardware 1 |
| `./scripts/ci:canonical-migration cameraagent/logichost`; both `dotnet ef migrations has-pending-model-changes … --configuration Release --no-build` | All pass; no pending model change |
| Architecture Integration publish/graph checks | 7/7 pass; host/runner dependency boundaries and x64 outputs retained |
| Installer x64/arm64 and replay/processing runner arm64 Release publishes | All pass; x64 installer version and both runner capability probes pass. Cross-publish is not ARM execution qualification |

Required unset Unit skips are explicit: 121 CLI catalog conditions, one
Distribution catalog attachment, one CameraAgent macOS-only case and one
LogicHost provider-unreachable ambiguity case. These skips do not qualify the
installer catalog paths. The two new Unit cases passed in the complete matrix;
the inventory and runbook increase by two to 13,329.

Canonical coverage is now **pass** for all five components and the aggregate.
Only the complete CLI and Distribution Unit selections were repeated with the
approved checksum-pinned v4.2 catalog bundle in separate result slots: CLI
2,541 passed / 1 skipped / zero failed; Distribution 116/116 passed.
`CatalogSelect_CrossLineage_SelectsSideBySideRootAndRollsBackExactly` still
requires the absent v4.4 bundle and is not qualified here. Exactly those two
canonical delivery inputs were replaced, with inventory admission and SHA256
provenance. All other successful component inputs/checks were reused unchanged;
only delivery and the exact 23-report aggregate were re-merged/enforced.
Original unset TRXs, coverage and floor failures remain retained. No floor,
filter or assertion was weakened.

| Canonical gate | Line / branch coverage |
| --- | --- |
| Shared | 90.0623% / 74.8605% |
| CameraAgent | 89.9374% / 76.5271% |
| LogicHost | 84.2604% / 67.4030% |
| Combined | 33.7817% / 19.6650% |
| Delivery | 92.3335% / 79.3162% |
| Aggregate | **89.3621% / 75.3928%** |

These are the gate's source-line/branch unions, rather than the merged XML's
class-summed header rates. Both forms are retained in `summary.json`.
ReleaseTool `Program.cs` improved from the unset run's 40.65% line / 35.23%
branch to **89.03% / 62.50%**, above the unchanged delivery floors 59% / 37%
and aggregate risk-file floors 50% / 35%.

The original matrix wrapper exits 1 and is retained. Its only failures were
coverage policy invocation and the catalog-unset delivery/aggregate floors;
all functional selections, model checks and publishes completed successfully.
The private helper incorrectly supplied a SHA where `coverage:policy` expects
a branch name, producing `origin/<sha> unavailable`. Only that invalid gate
was repeated as `./scripts/coverage:policy pull_request development/v1`, which
passed. The observed `origin/development/v1` was
`fc716969eae53bea40fbd29d28f266f0cca8464d`; coverage baseline files were verified
byte-identical to the candidate's base. This was not a base synchronization.
Original failure, corrected command/provenance and exit remain separate.

The initial helper-variant typo and deliberate red configuration guard are
also retained. The private source-retention helper's first SHA-only Git bundle
request failed (`Refusing to create empty bundle`); named, verified branch refs
fixed only the evidence export. The resumed export rechecked binary pins.
Neither measurements nor successful suites were repeated for that helper error.

## Acceptance closure and retained findings

The [operator runbook](../runbooks/measured-stellar-associations.md#acceptance-source-map)
maps every foundation boundary to its current owner and executable acceptance.
Fresh complete receipts cover compact identity/schema/recipe and Mono16/CFA
input validation; scene/raw/catalog/calibration/settings lineage; policy and
fail-closed labels; edge durable publication/restart/replay; frozen central
readers; lease owner/token/expiry and output authorization; scheduler, executor
and runner; SQL/EF frozen-graph concurrency, supersession and old-trigger guards.
`gates/summary.json` binds named acceptance methods and parameter-case counts
to the actual complete TRXs. No old branch was imported and no production
correction or retry was justified by this slice.

- **H1190:F1**, original ingest rejection: corrected historically at `92605593`
  with strict association parse, reconstructed descriptor/capture/raw/recipe
  and ordered-lineage validation. Fresh multipart acceptance passes durable
  retrieval/idempotent duplicate plus four identity/lineage tamper cases.
- **H1190:F2**, original runner capability and scene payload/content conflation:
  corrected historically at `92605593`. Fresh runner claim/download/execute/
  complete passes both scene schemas; four independent payload-checksum/
  content-identity rejection cases pass. Separate reader counterexamples pass.
- **H1190:R0-1**, v2 resolved footprint with a measured star outside its disk,
  and **H1190:R0-2**, expected-position truncation/clipping: current conformance
  and typed/raster marker tests pass. A label survives only with its complete
  expected marker; a clipped diagnostic is suppressed.
- **H1190:F3** remains a history with two distinct outcomes. Correction1 accepted
  bounded numerical/reconstruction/net-new cost evidence on its old head;
  that does not qualify this candidate. Historical W2 RSS median
  **+225,849,344 B / +13.56%** remained blocking through correction2. Its raw heap
  roots remain unrecovered; this report asserts no benign cause or recovery.
  Original workspace STOP 1, Unit 6,290 passed / 2 failed / 23 NotExecuted,
  missing 14/19/10 tail TRXs, deleted outputs, absent measured copy counter,
  endpoint/mixed-build/native limitations remain historical, with no current
  credit. F7/#1188 `VALID 0` is separate.
- **Slice A / #1540**: frozen `c1971fbf` / `26ae1b9b`, six pairs, median
  **-15,534,080 B / -0.8198%**, passed its declared current-scope rule with the
  producer **OFF**. See the [separate slice-A record](issue-1540-w2-memory-results.md).
  Source-transfer review accepted that scope; it did not recover the historical
  roots. Its immutable pack is absent on this host, was not rehashed and was
  never modified. This producer-ON pack is separate.
- **PR1549:F1**: saved v12 descriptor preservation did not install a v12
  implementation. Source verification and the new real draft-execution case
  pin delivery rejection, explicit new local draft/recompile/validate/activate,
  retained option effect, actual v13 stamps and fail-closed labels. Old bytes
  and plan identity stay unchanged; no automatic upgrade or historical engine
  ships. See [migration guidance](../runbooks/measured-stellar-associations.md#saved-scene-overlay-drafts).
- **PR1549:F2**: the pre-existing authorized-source split-query SQL1205 startup
  deadlock occurred under seven concurrent assemblies (12,000 ms row). The
  source query remains byte-identical to `8fc49bf6`; default EF strategy and job
  `RetryableFailure` reexecution remain. Fresh full LogicHost Integration
  passes 560/560 first attempt. The old retained failure/risk is not erased;
  no executable fix or blanket read retry was made.
- **PR1549:F3**: the pre-existing toolkit SQL6106 race remains distinct:
  546 passed / 1 failed, isolated 1 passed, unchanged full retry 547 passed;
  later 550 passed first attempt. The exact failing statement is **unproven**;
  enumeration/KILL race is only a hypothesis. Test/helper source is unchanged
  from `8fc49bf6`. The fresh toolkit case and full 560 pass on their first
  attempt, without retries or a fix claim.

Original review histories:
[PR1190 initial](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1190#pullrequestreview-5481912925),
[correction1](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1190#pullrequestreview-5481945800),
[correction2](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1190#pullrequestreview-5482325438);
[PR1549 F1/F2](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1541#issuecomment-6352713879),
[F3](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1541#issuecomment-6354903876),
[handoff](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1541#issuecomment-6357908428).

## Retained artifacts and delivery boundary

Durable root: `/home/roys/development-state/HVO.SkyMonitor/evidence/1541`.

- Successful producer pack `producer-20261023-5faac756`: **489 files**, sealed
  read-only; `SHA256SUMS` SHA256
  `abf45e15064383836e7c8e07c8b826a400fa630843b68c30d4c7062ad87138f3`.
  Includes all ten cost processes and 21-case accuracy raw JSON/TRX/logs,
  durable raw/scene/product/replay roots, OS/runtime metadata, complete
  configuration and assembly pins, exact HVO binaries, both harnesses,
  approved catalog's four files, drivers/analysis and verified incremental
  source bundle (requires base `6ea815f2`). `producer-seal.json` records closure.
- Rejected initial pack `producer-20261023-735bdee6`: 55 files, separately
  sealed/read-only with the manifest digest stated above. No qualification
  credit; failed and interrupted provenance stays intact.
- `gates/`: exact preparation/matrix/corrected-policy/catalog-closure receipts,
  all failures, 82 original/supplemental TRX and Cobertura files, six final
  coverage reports, original failed delivery/aggregate reports, named
  acceptance/count summary, category admission, retained drivers and publish
  checksum manifest. `SHA256SUMS` seals these plus the final candidate/range
  receipt; its digest is reported in the issue/handoff ledger. The public
  narrative omits private runtime metadata and addresses.
- Builds/publishes remain in ignored worktree output; the publish manifest is
  retained durably. Registered scratch
  `/tmp/hvo-skymonitor-1541-delivery.3yBcTJ` and baseline checkout
  `/tmp/hvo-skymonitor-1541-producer-arms.FNZFl5/baseline` remain for parent
  review. The baseline branch is `evidence/1541-producer-baseline`. Parent can
  remove these and ignored outputs after review/finalization under the scratch
  procedure; retain both durable evidence packs. No #1540 path was touched.

The documentation-only delivery commit containing this report has no delta
from the built candidate in source, tests, fixtures, SDK, packages or build
configuration. Its exact SHA and that reuse check are recorded outside Git in
`gates/final-candidate.json`; binary evidence remains labeled `5faac756`, not
silently relabeled. The parent must perform independent review, final target
synchronization, any resulting affected validation and the target-branch CI.
Default shipped-profile activation and broader qualification remain excluded.
