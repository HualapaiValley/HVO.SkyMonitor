# Virtual horizon-obstruction acceptance (#1394)

Production source candidate: `bc42850307a5c324949aae587c15fd4ffed82bab`.
This fresh complete local gate includes the invalid-draft catalog correction and the operator-authorized target merge.
Initial target/merge base: `7d5ec63b09a67ab4b675ba6a29641013df1247d8` (`development/v1`).
Current merged target: `4708bbc3099019f1b8c2ec2531ff21a9d69dd404` (#1722), incorporated by normal merge
`bc42850307a5c324949aae587c15fd4ffed82bab` under explicit operator authorization.
The earlier normal merge `69f1b9998cd816f23db3a409ebd4619d558348dc` incorporated
`35da16df25546bd6adbadf5a6613e50fc04cd052` (#1736).
Tier C; protected profile `development-v1`. The PR remains draft. Protected CI,
reviewed-head status and merge remain unauthorized.

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

Full PR selector: `scripts/ci:classify pull_request 4708bbc3 bc428503` returned
`mode=full complete=false shared=true cameraagent=true logichost=true combined=true
delivery=true deployment=false secret_scan_contract=false release_scripts=false`.
Correction selector `scripts/ci:classify pull_request b0b51fb2 bc428503` returned
`mode=full complete=true shared=true cameraagent=true logichost=true combined=true
delivery=true deployment=true deployment_shards=true deployment_installer=true`,
with deployment_catalog/secret-scan/release flags false. Full qualification remains
unauthorized for the v1 target.
The full local matrix is required by Tier C and the operator; these selectors do
not authorize the full protected workflow for the v1 target.

- Tool restore and solution restore: passed.
- Debug and Release solution builds with `-warnaserror`: passed, zero warnings/errors.
- Whole-solution `dotnet format --no-restore --verify-no-changes`: passed.
- Package audit, Bash syntax, CI classification, coordination and PR-review guards: passed.
- Operations documentation audit: passed.
- Full Docker-disabled Unit selection with coverage: 15,179 passed, 3 conditional skips, 0 failures (15,182 selected), all on the production source above.
- Full Integration selection with coverage: 1,017 passed, 0 skips/failures.
- Canonical coverage: exactly 23 category/project reports, copied byte-for-byte to literal paths and merged once; all floors passed. Enforced aggregate line 89.7064%, branch 75.8465%; all enforced per-file floors passed.
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
Fresh complete TRX/Cobertura evidence is under `TestResults/issue-1394/m3-synced/`
(ignored). `summary.json` records all current project/category results. The exact
23-slot canonical coverage manifest records original report paths and byte-identical
literal copies (avoiding wildcard interpretation of bracketed filenames);
`coverage-canonical/` contains the credited rollup. Earlier full-resolution
measurement evidence remains under `TestResults/issue-1394/final/performance/`.
The measured mask-generation and fixture paths did not change, so that measurement
was retained. Earlier complete and refreshed evidence remains in its original
result roots and is superseded for the changed source by this fresh complete run.

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

Fresh raw TRX and coverage reside under `TestResults/issue-1394/m3-synced/`.
Exactly 23 canonical reports are copied byte-identically to literal paths before
merging. The source above is the test/build candidate; later report-only commits
are identified in the PR ledger. The operator-authorized normal target merge is
recorded above. Protected CI, reviewed-head status and merge remain held; the PR
stays draft. Earlier unsuccessful inner-loop builds and fixture runs receive no
final validation credit.

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
