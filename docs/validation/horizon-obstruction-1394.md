# Virtual horizon-obstruction acceptance (#1394)

Production source candidate: `a1e4a39dfba24817fe716e3e4e8fa46c3afdd878`.

**Current outcome: BLOCKED.** The prepared merge is local and unpublished. No
ready transition or protected CI run was started for this candidate.
This candidate has complete behavioral evidence, but the mandatory canonical coverage gate is blocked.
Initial target/merge base: `7d5ec63b09a67ab4b675ba6a29641013df1247d8` (`development/v1`).
Current merged target: `6220c722ec069dbd13296575c544083d0b6df098` (#1727), incorporated by normal merge
`a1e4a39dfba24817fe716e3e4e8fa46c3afdd878` under the operator-assigned finalization slot.
The prior normal merge `54f86e00be3af875f3455174ce2a86aedf13115f` incorporated
`ad78013bc3b4e5b36e24b7e8a5cbc2019d58f0f1` (#1737).
The prior normal merge `bc42850307a5c324949aae587c15fd4ffed82bab` incorporated
`4708bbc3099019f1b8c2ec2531ff21a9d69dd404` (#1722).
The earlier normal merge `69f1b9998cd816f23db3a409ebd4619d558348dc` incorporated
`35da16df25546bd6adbadf5a6613e50fc04cd052` (#1736).
Tier C; protected profile `development-v1`. The local gate ran while draft.
The operator authorizes marking ready after the green push to start protected CI,
then Kepler obtains formal base-sync reviews. Reviewed-head status, labels,
auto-merge and merge remain reserved for Kepler.

## Shared behavior and ownership

The [frozen contract](../contracts/horizon-obstruction-v1.md) owns conventions,
bounds, unknown/gap handling, immutable identities and library/host ownership.
AgentCore carries the profile inside immutable rig revisions; Astronomy evaluates
angular eligibility; Imaging generates separate obstruction/unknown/outside bit
planes; Processing authenticates source-bound products and filters annotations;
CameraAgent owns the manual editor and existing SQLite activation/history path.

Versioned sites bind both coordinates and retained location ID/version. Changing
site revision at identical coordinates invalidates compatibility. Geometry binds
sensor, optics/calibration, orientation and readout; profiles must be explicitly
revalidated after changes. A profile absent from an older rig preserves baseline
behavior. An active empty profile is unknown, never clear sky.

Raw pixels, original orbital tracks and projected-scene evidence remain intact.
Legacy half-open rectangles remain separate. Fixed obstruction and unknown
terrain add typed hard exclusions; calibration/bad pixels, bounds, saturation,
star rejection and dynamic clouds keep their own identities. There is no global
or persistent mask cache. Each generation is limited to 16,777,216 output pixels,
720 angular samples and three bit planes, with row cancellation.

Capture overlays conservatively withhold a whole track with an ineligible point
or segment; planning arcs split at those boundaries. Display segment tests use
linear altitude and shortest azimuth, testing every crossed knot and interval
midpoint; they do not assert physical intersample trajectories. Flattened annotation
and both layered scene aliases use one source-bound eligibility view. Effective,
native and native-ROI sources, including ImageRegistered scenes, are supported
when calibration, projection, site and readout relationships match. Recorded mirrors
and quarter rotations are inverted before testing; refraction is removed over the
supported above-horizon domain. Incompatible scenes retain evidence and supply no
eligible annotation. Sky-map selection rejects ineligible stars before its result
bound and computes truncation from eligible stars.

## Acceptance evidence

| Criterion | Evidence |
| --- | --- |
| Bounded host-neutral installation/site/orientation/calibration profile | `HorizonObstructionProfile`, immutable sample snapshot, geometry/site identities; invalid geometry and limits tests |
| Frozen representation, unknown rules and ownership | Contract above; north wrap, null knots, empty/single profiles, oversized gaps, exact and very fine angles |
| Create/edit/preview and explicit activation; retained history | Camera & Rig editor tests and `HorizonObstructionStoreIntegrationTests`: preview/save preserve active state; stage/virtual initialization/rollback preserve archived revisions; failure leaves no partial history |
| Source/profile/calibration identity and incompatible geometry rejection | Mask/prediction binding tests; source changes alter identities; site ID/version, calibration, pointing, flip, sensor and readout changes reject reuse |
| Compatible exclusion combination and raw retention | Real synthetic star/meteor/satellite extraction with unchanged raw bytes; transient worker tests retain rectangle boundaries and stable temporal mask identities |
| Wrap, gaps, crossings, models, transforms, revisions | All five projector families; crop/bin/mirror/quarter rotations; numerical roundtrip ≤1e-8 pixel; peaks and unknown gaps cannot bridge clear endpoints; SQLite rollback and publication fault |
| Bounded generation/caching and full-resolution evidence | Measurements below; exact packed-mask equality/checksums, cancellation and area-limit tests; no retained cache |

The change adds 111 Unit cases, 5 Integration cases and 1 Manual measurement case.
All are virtual/software evidence; physical survey accuracy and camera/ARM cadence
qualification are outside this acceptance.

## Local candidate gates

SDK 10.0.402, Ubuntu 24.04.5 x86_64, Intel Core i9-14900K, 12 exposed CPUs,
15,406 MiB host RAM. Native Release test hosts; Integration uses isolated
Testcontainers and SQLite/filesystem fixtures. No production host was restarted
or deployed. No networking or secret configuration was changed, and no branches
were deleted.

Base-sync selector: `scripts/ci:classify pull_request e35fcf78 a1e4a39d` and full
PR selector `scripts/ci:classify pull_request 6220c722 a1e4a39d` both returned
`mode=full complete=false shared=true cameraagent=true logichost=true combined=true
delivery=true deployment=false secret_scan_contract=false release_scripts=false`.
The full local matrix is required by Tier C and the operator. The target profile
remains `development-v1`; no full protected qualification was dispatched.

- Tool restore and solution restore: passed.
- Debug and Release solution builds with `-warnaserror`: passed, zero warnings/errors.
- Whole-solution `dotnet format --no-restore --verify-no-changes`: passed.
- Package audit, Bash syntax, CI classification, coordination and PR-review guards: passed.
- Operations documentation audit: passed.
- Complete Docker-disabled Unit final unique outcomes with coverage: 15,334 passed, 3 conditional skips, 0 failures (15,337 selected), all on the production source above.
- Full Integration selection with coverage: 1,090 passed, 0 skips/failures.
- Canonical coverage: exactly 23 prescribed category/project reports, copied byte-identically with manifest checksums and merged once. Aggregate line 89.7651%, branch 75.9061%; the per-file gate FAILED for EnvironmentalObservationJson.cs: 243/275 covered branches (88.36%) against 90%. Line 245/247 (99.19%) passes its 95% floor.
- Manual full-resolution measurement: 1 passed (retained unchanged-path evidence).

Unit ran with `DOCKER_HOST=unix:///tmp/hvo-no-docker.sock` and the approved local
`HVO_PRODUCTION_CATALOG_BUNDLE=/home/roys/.cache/hvo-catalog/hyg-v4.2-p3-s2-r1.bundle`.
Conditional skips are the macOS-only rejection, unreachable provider delete ambiguity,
and optional HYG 4.4 cross-lineage transition. None is a horizon test or production
qualification credit. The earlier superseded Integration run was deliberately
canceled after source changes and is not credited.

Commands are the complete Tier C block in AGENTS.md; tests use
`--no-build --configuration Release` with separate `--filter "TestCategory=Unit"`
and `--filter "TestCategory=Integration"` selections, `tests/coverage.runsettings`, and XPlat Code Coverage.
Fresh complete TRX/Cobertura evidence is under `TestResults/issue-1394/base1727/`
(ignored). `summary.json` records all current project/category results. The exact
23-slot canonical coverage manifest records original report paths and byte-identical
literal copies (avoiding wildcard interpretation of bracketed filenames);
`coverage-canonical/` contains the credited rollup. Earlier full-resolution
measurement evidence remains under `TestResults/issue-1394/final/performance/`.
The measured mask-generation and fixture paths did not change, so that measurement
was retained. Earlier complete and refreshed evidence remains in its original
result roots and is superseded for the changed source by this fresh complete run.

## Reserved-slot synchronization after #1727

Both formal base-sync reviews are CLEAN at `e35fcf78958474ac7c31538827b4a9f5c1d49e81`:
[Opus 5820837988](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1734#pullrequestreview-5820837988)
and [Sol 5821181077](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1734#pullrequestreview-5821181077).
Their complete reports and the consolidated carried-finding checklist were read.
Those reports verify all earlier findings corrected and identify no new blocking
or non-blocking review finding. Optional observations and historical draft/ready
wording remain informational; no correction or scope change was introduced here.

The operator assigned this PR the finalization slot after #1727 and required a
fresh exact-target check. Fetch confirmed `6220c722ec069dbd13296575c544083d0b6df098`.
The normal merge has parents `e35fcf78` and `6220c722`; it neither rebases nor
rewrites reviewed history. Conflicts were limited to the four expected records:

- CHANGELOG retains both the horizon and environmental-adapter entries.
- CameraAgent Unit: 7,650 merge base + 59 PR + 86 upstream = 7,795.
  Integration: 335 + five PR + 55 upstream = 395.
- Processing Unit: 1,188 + 43 PR + four upstream = 1,235. Manual remains 19,
  preserving the one PR measurement case over the base's 18.
- The runbook totals sum every merged inventory: Unit=15,337,
  Integration=1,090, Manual=297. Upstream adds 90 Unit and 61 Integration
  relative to the previous reviewed head; PR-owned additions remain 111 Unit,
  five Integration and one Manual case relative to the current target.

No product-source file conflicted. Excluding exactly those four records, the
reviewed PR-owned diff (`ad78013b..e35fcf78`) and merged PR-owned diff
(`6220c722..a1e4a39d`) are byte-identical: SHA-256
`2fb4ed8ab11d1998262ca2104a1d92d4a6acb4e0dc315f224ad43977f8e5c57c`.
The only later commit updates this validation report. The fresh full suites cover
both prior horizon behavior and the upstream environmental evidence/adapters.
All hardware adapters are virtualized by their fixtures; no physical hardware or
production endpoint is used.

Per-project TRX, temp stores and coverage outputs are isolated beneath
`TestResults/issue-1394/base1727/`. The credited isolation manifest records one
complete passing result per selected project/category. Corrected test processes
use a private physical `/tmp` bind-mounted from their project backing directory,
so even hardcoded temporary paths remain under that root and Unix socket paths
stay short. UID/GID 1000, native system-root ownership and mode 0700 temp roots
are preserved; host mounts, configuration and other jobs are unchanged.
Conventional build outputs belong only to this worktree. Unit project jobs are
bounded to two based on host
capacity; all Integration projects execute serially, including the complete
combined-host selection. Unit instrumentation completes before Integration starts,
and no assembly/output directory is instrumented concurrently. No unchanged
performance benchmark is rerun.

The first Unit attempt used symbolic-link TMPDIR aliases created by this worker.
Storage, installer, backup and causal-evaluation guards correctly rejected those
linked ancestors. All 21 projects completed; five complete selections failed:
CameraAgent.Tests (154 cases), CameraAgent.IntegrationTests Unit (six), installer
(1,223), LogicHost.Tests (16), and Processing.Tests (39). These 1,438 failed case
outcomes and their coverage receive no pass credit and remain retained separately.
After a lightweight private-mount probe verified normal ownership, physical paths
and backing-file identity, only those five complete project selections were rerun.
The 16 passing projects were retained without duplication. The final Unit totals
above are unique outcomes from those complete selections, not added retry counts.
No product source, assertion, timeout or guard was changed. Integration runs once
per project with the corrected physical layout, serially. The original gate logs,
failed TRX and per-case errors are retained beside the corrected evidence.

The initial host check found 12 effective cores, approximately 44.9 GiB available
RAM, 688 GiB free disk, load1 0.05, and no other visible worktree .NET jobs.
The runtime is full-access/xhigh; service-tier selection is not exposed by the
harness, so no priority/FAST setting is attested.

Per the operator's explicit sequence, the PR is draft for this local gate, then
pushed normally and marked ready only after every required local gate is green.
The coverage blocker below currently prevents both actions. Kepler
launches formal base-sync reviews after CI is green. No reviewer is launched by
this worker; no review label, reviewed-head status, auto-merge or merge is set.
The operator's reservation is used without mutating lock labels. Worktree and
TRX/coverage evidence remain retained until merge.

### Canonical coverage blocker

The complete Unit and Integration selections above passed, but
`scripts/coverage:enforce --merged` exited 1. Its sole failure is
`HVO.SkyMonitor.Processing/EnvironmentalObservationJson.cs`: 243/275 covered
branches (88.36%), below the configured 90% floor. Line coverage is 245/247
(99.19%), above its 95% floor. Aggregate line/branch results are not a substitute
for that per-file gate.

The prescribed 23 input slots were checked against the runbook and workflow.
Every retained canonical copy is byte-identical to its raw report and manifest
SHA-256. Supplemental reports were not added to inflate coverage. The failing
file's Git blob is `ee30342461726499aed6ed56b5d13619a576b796`, identical on
target `6220c722` and the prepared merge. The new upstream metadata validation
around line 171 has 15/22 branch outcomes covered. No target-only baseline run
or product defect is claimed; this is the observed merged candidate's coverage
failure.

The operator requires an unchanged PR-owned diff apart from the four conflict
records. Adding environmental coverage tests, changing source or lowering the
floor would exceed that scope. The local gate is therefore not green. The
prepared merge and report are retained locally; no push, ready transition,
protected workflow dispatch, review, reviewed-head status, label, auto-merge or
merge is performed. Resolution needs an upstream coverage correction or an
explicit scope decision from Kepler. All raw TRX, canonical coverage, failed
setup attempts and corrected project results remain under `base1727/`.

## Base synchronization after formal review convergence

Both full formal reviews returned CLEAN on `18323af8a5275267b0a265a500671ebda81da918`:
[Opus 5820085473](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1734#pullrequestreview-5820085473)
and [Sol 5820206577](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1734#pullrequestreview-5820206577).
They independently support acceptance criteria 1–7 within the virtual/software scope.
The [consolidated finding checklist](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1734#issuecomment-6729256235)
records all carried fixes and optional observations from both reports in one place.
No new defect was identified and no optional nit was promoted into correction scope.
The operator subsequently requested this normal merge of target `ad78013b` (#1737).
Kepler owns the formal base-sync reviews and merge; no new reviewer was dispatched
by this worker and no review label or reviewed-head status was set.

Conflicts were limited to four records:

- CHANGELOG: keep both the #1394 horizon entry and #1714 Products Library entry.
- CameraAgent inventory: Unit 7,605 + 59 PR cases + 45 upstream cases = 7,709;
  Integration 331 + five PR cases + four upstream cases = 340.
- Processing inventory: Unit 1,168 + 43 PR cases + 20 upstream cases = 1,231;
  the retained Manual horizon measurement keeps Manual=19.
- CI runbook: recompute solution totals from every project inventory:
  Unit=15,247, Integration=1,029, Manual=297. The upstream CameraAgent Integration
  assembly adds eight more Integration cases, so the solution delta is 12.

No source files conflicted. Excluding exactly those four records, the PR diff from
`ad78013b` to the merged candidate is byte-identical to the reviewed PR diff from
`4708bbc3` to `18323af8`: SHA-256
`dc9964c41106680709dc9f720ad48ac376abe3aec31dad81044b9873caf70c6b`.
Both sides' product behavior is retained without a scope change. The fresh gates
above run on the merged source. Integration projects execute serially (`-m:1`) to
bound host load, with the original coherent-publication predicate and 20-second
budget unchanged. Unit instrumentation completed before Integration began, and all
new TRX/coverage outputs use the isolated `base1737/` root named above. Temporary
fixture storage remains under per-test generated roots; logs/helpers use the unique
`/tmp/hvo-1394-base1737-*` prefix. A host snapshot during validation showed load
5.75/7.07/8.04, 5,365 MiB available memory and 667 GiB free disk. One other test
job used a separate review worktree/output tree. No overlapping instrumentation
of the same assembly/output directory or duplicate suite was launched here.
The requested single capacity observation at 2026-11-08 16:31:17 PST measured
12 effective logical cores (affinity/cpuset 0–11, no cgroup CPU quota), load1/cores
0.128, CPU busy 7.25%, iowait 1.17%, available RAM 9,820 MiB and no swap-in/out.
Host IO PSI avg10 some/full was 1.68%/1.67%; CPU/memory PSI was zero. The exposed
workspace device measured 17.95% utilization and 0.24 ms await. One active local
suite group and one idle .NET worker group were visible; the earlier #1739 review
testhost was absent. Docker reported four containers including Ryuk, with the
busiest at 60.23% CPU/1.644 GiB; daemon placement was not established, so those
values are not added to this host's totals. This was observation, not a concurrency
cap or a benchmark. The 2.01-second interval plus 2.06-second metadata read is
retained in the isolated `capacity-sample.json` and `capacity-metadata.json`
helper records under the `/tmp/hvo-1394-base1737-*` prefix.
Earlier intermittent readiness failures remain an explicitly unresolved cause,
not a claimed product-source fix.

The PR was ready when this work began and was returned to draft before the new
push, keeping protected Build and Unit held for Kepler's base-sync reviews. The
only commit after the tested merge updates this validation report. Unchanged
full-resolution measurements below remain applicable and were not rerun.

## Independent-review corrections

Initial deep review of `7d5ec63b..eca14076` requested six corrections. Independent
correction review 1/3 of `eca14076..deb102f1` verified R1–R6 corrected and found R7
in downstream curated composition. Correction review 2/3 verified R1–R7 corrected on `deb102f1..6fca2458`.
The operator's subsequent full draft pre-reviews on `6fca2458` reopened convergence;
the fixes below have fresh local evidence, with the next exact-range correction
verification tracked in the append-only PR ledger.

| Finding | Correction and regression evidence |
| --- | --- |
| R1, calibration candidate reuse | Deduplicate by current horizon content as well as equipment. Reuse an existing optics identity while composing a fresh installation candidate. Two Integration sequences accept calibration before a missing/older horizon, activate a newer horizon, accept again, stage and initialize; the retained horizon requires revalidation after calibration changes. |
| R2, layered eligibility bypass | Both aliases consume the same source-bound eligibility as flattened annotation. Eight cases cover blocked, empty/unknown, incompatible and clear profiles, each in live and archived execution; source scene bytes remain unchanged. Identical empty layers retain distinct horizon-bound product identities. |
| R3, policy changes block editing | Compare installation geometry, module and obstruction identities independently of capture policy. Real-store service tests activate control-policy and exposure-envelope changes, then preview/save; a geometry change after preview still rejects save. |
| R4, inaccurate preview | Render actual knots and only supported intervals, split at north and geometric zero. Numerical tests cover fractional peaks/nulls, oversized gaps, negative terrain and wrap; component checks assert the fractional peak and never paint below-zero sky clear. |
| R5, filtering after truncation | Apply eligibility inside the scene builder before the magnitude/result bound, retaining existing candidate work bounds. A limit-one regression returns the dimmer clear star behind two brighter blocked stars and derives truncation only from the eligible population. |
| R6, registered/native alignment | Validate complete retained projection and calibration against effective/native/native-ROI geometry and configured crop/binning. Five cases each exercise all mirror/quarter rotations, including refraction, through archived flattened annotation. Explicit calibration, pose, parity, readout and observer mismatch cases reject reuse. |
| R7, curated deep-sky bypass | Both layered aliases and actual flattened composition consume a source-bound immutable selection, capped at 128 IDs. Known footprints require clear points/segments and glyphs require an eligible source position. Canonical auxiliary JSON carries the selection into archived processing and binds recipe identity without changing retained scene bytes. Twelve host cases cover four states in both live/archived execution through both aliases and the real flattened executor; seven Processing cases cover transport/freeze/identity, source/object/hash/schema/missing-source failures and bounds. Legacy actual-output compatibility cases also pass. |

## Operator draft pre-review corrections

Both full reports are retained on PR #1734: Opus review 5818887039 and Sol review
5818887787. That correction added 23 Unit cases (then-declared total 15,139) and
retained the original five horizon Integration cases. All 58 focused interaction/
regression cases passed in that round and remain included in the complete fresh
Unit gate above.

- Opus B1: both layer aliases and flattened annotation resolve exposure-time
  location from protected history. Tests serialize real receipt-bearing contexts
  through the redacted lane envelope, including archived execution, ordinary
  stars, constellation segments and curated objects through the actual flattened
  executor. Missing history explicitly refuses processing; current coordinates
  never replace capture-time evidence.
- Opus H1: central masks refuse any rig containing a horizon, under legacy and
  swept policies. The existing executor publishes MaskEvidenceUnavailable /
  NeedsReview. Four new cases ensure obstructed and unknown terrain cannot run
  through central detection with only legacy masks. This supported limitation is
  documented; no central visibility/detection confirmation is claimed.
- Opus M1: the transient runtime generates directly at detector resolution and
  keeps at most one geometry/site/profile/transform-bound set within a window.
  Numerical tests check every detector pixel against the inverse source transform
  for Mono16 and RGGB, prove same-binding object reuse, changed-profile regeneration,
  and terminal rejection of incompatible geometry. No global cache is retained.
- Opus M2: active, selected and pending rig views report horizon compatibility;
  quick geometry editing warns before stage. A real-store test stages/initializes
  the horizon, stages changed geometry, checks pending status, then initializes
  and checks active status. Component regressions cover all three displayed states.
  Runtime tests require the non-retryable revalidation reason.
- Sol M1 / Opus L4: the shared footprint rule checks all edges including closed
  last-to-first. Eight Sun/Moon cases put a peak or unknown knot between clear
  limb samples on ordinary and closing edges; both flattened overloads and
  layered output withhold the footprint and label.
- Sol M2: form/selection generations discard stale preview responses. Four
  delayed-response component cases change samples, installation, gap or selected
  version in flight; Save cannot reappear with stale evidence.
- Opus L1: the real frozen-data ISS planner searches and produces arc eligibility
  using a compatible versioned-site horizon; Clear samples must exist and no
  sample may be Incompatible.
- Opus L2: retain the deliberate conservative frozen v1 sensor/readout fingerprint,
  including levels/packing. Narrowing it needs a versioned compatibility decision;
  no unsafe reuse is permitted. The new revalidation warnings expose its effect.
- Opus L3: document that mask generation, legacy fallback and sky-map inputs are
  geometric/unrefracted. Explicitly refracted retained scenes use the existing
  source-bound refraction inversion. Apparent directions are outside the
  geometric-only input contract.

Transient-mask work is now bounded by the detector grid and one retained set for
an identical window binding. For a three-frame causal 4096x4096 RGGB window,
full-source horizon evaluations decrease from 3 x 16,777,216 to one 4,194,304-pixel
detector pass; three detector-sized packed planes occupy exactly 1,572,864 bytes.
A five-frame centered window likewise uses one pass instead of five. These are
exact work/storage counts, not measured end-to-end timings or a cadence claim.
RGGB eligibility now samples detector cell centres consistently with existing
geometric masks; its new algorithm identity and difference from the former
four-source-centre union are explicit in the shared contract. Existing rectangular
exclusions are unchanged. The standalone full-resolution generator measured below
is unchanged; no redundant component performance run is credited.

The prior correction raw TRX and coverage remain under
`TestResults/issue-1394/m3-synced/`; the current base-sync results are recorded above.
Exactly 23 canonical reports are copied byte-identically to literal paths before
merging for each credited rollup. The source above is the current test/build
candidate; later report-only commits are identified in the PR ledger. Protected
CI, reviewed-head status and merge remain held; the PR stays draft. Earlier
unsuccessful inner-loop builds and fixture runs receive no final validation credit.

## Sol correction at b0b51fb2 and target merge

Full source report: [Sol review 5819523041](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1734#pullrequestreview-5819523041).
The operator will launch fresh Sol/Opus correction pre-reviews after the green push;
this evidence does not assert their result. This acceptance/evidence blocker
correction follows the three completed rereviews under the lifecycle cap exception;
no acceptance finding is deferred.

- **Sol M3:** compatibility checks compare the revision's geometry identity before
  evaluator construction and contain expected projection/readout errors per
  revision. An invalid draft returns a revalidation warning without aborting the
  catalog. The real SQLite/runtime regression activates a compatible horizon,
  composes a never-staged Perspective/180-degree candidate retaining that horizon,
  proves its projector throws, then requires successful catalog retrieval, the
  active compatible revision, no pending revision and the candidate warning.
- **Remaining Opus M2:** active/selected/pending compatibility remains visible even
  with the invalid draft. No-profile wording refers to the revision. Paged history
  explicitly reports unavailable compatibility and asks for preview/revalidation;
  the history component regression asserts that warning. This complements the
  retained supported-geometry staging/initialization and runtime-rejection cases.
- **Integration evidence:** the complete native combined-host probe passed 24/24,
  including `ConfiguredPipelinePublishesPersistsReportsAndQueuesVirtualFrame`
  (about 3.96 seconds). The complete solution Integration gate with coverage then
  passed 1,017/1,017, including the same full 24-case selection. The prior review's
  stale-publication failure did not reproduce here. Its root cause is unestablished;
  no timeout, readiness predicate or capture lifecycle was weakened to obtain green.
- **Target merge:** conflicts were confined to CHANGELOG, the CI runbook totals and
  CameraAgent category inventory. Both changes were retained and independent deltas
  added, including the identical Integration count additions on both branches.
  The merged total is Unit=15,182, Integration=1,017 and Manual=297. The new catalog
  regression adds one Unit case; upstream #1736 contributes 24 Unit, five
  Integration and one Manual case, and #1722 adds 18 Unit cases. The cloud-step
  requirement and horizon eligibility merged without source conflicts and remain
  independently applied. The pre-advance candidate at `32b4085a` also passed its
  full 15,164-case Unit and 1,017-case Integration selections; those raw results
  are retained separately under `TestResults/issue-1394/m3/`. The fresh gate above
  is credited only to the final merged source.

The initial focused attempt passed 73/74 cases; an unrelated assertion expected the
empty-pipeline harness's ordinary preview to reject the invalid draft. It was
removed while retaining the direct throwing-projector check and all requested
catalog/active/history/never-staged assertions. The final focused service/page rerun
passed 74/74; the complete Unit run also includes the corrected regression. Failed
earlier evidence is preserved under `TestResults/issue-1394/m3/focused/` and receives
no gate credit.

## Full-resolution numerical and cost evidence

Reproduce after the Release build:

```bash
dotnet test tests/HVO.SkyMonitor.Processing.Tests --no-build --configuration Release \
  --filter "FullyQualifiedName~HorizonObstructionPerformanceTests" --logger trx
```

Synthetic flat 30-degree horizon, configured fisheye, pixel-centre samples,
concurrency 1. The sizes match W1/W2 dimensions; this is a component measurement,
not the canonical end-to-end W1/W2 campaign. One warmup and three equivalent trials
per path. Reference is the nearest simple flat-cut implementation: unproject each
pixel and set one exclusion plane. After evaluates the bounded angular profile
and returns three separately typed planes. Their union must match the reference
byte-for-byte on every trial. No filesystem, database or network I/O or queue/backlog
exists in the measured operation. Logging and result serialization are outside timing.

| Pixels | Reference median ms | After median ms (min–max) | CPU median ms, reference / after | Allocated bytes median, reference / after | Peak process RSS bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1936x1216 | 42.440 | 87.957 (87.675–90.696) | 42.926 / 91.222 | 294,296 / 883,952 | 107,266,048 |
| 4096x4096 | 462.130 | 977.790 (972.734–995.287) | 521.860 / 988.534 | 2,097,176 / 6,292,592 | 137,056,256 |

4K output storage is exactly 6,291,456 bytes (three 2 MiB planes); 1936×1216 uses
882,816 bytes. Process RSS includes the test host and previous allocations.
The added lookup and separate unknown/outside planes explain the opt-in cost;
there is no additional horizon scan when no profile is active. The 4K reference
trial range was 461.057–826.934 ms, so these three trials establish an absolute baseline,
not a tail percentile or claimed throughput improvement. No production cadence
or physical-target budget is inferred.

Union SHA-256 on every reference/after trial:

- 1936×1216: `6B18FC4A89D45226C44A2AE135D55C0FE112044B4695AEB4AC13C79292D18511`
- 4096×4096: `62521C6530FF6EC1C467D916653F88FAA53E93EADC49900E74BD5D8AF6527565`

Performance fixture profile hashes, CPU/allocations and each trial are retained in
the measurement TRX. Coordinate roundtrips, product identities, canonical retained
inputs and durable profile history are independently asserted by the regression suite.
