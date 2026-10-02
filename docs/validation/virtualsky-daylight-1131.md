# VirtualSky day/night qualification (#1131)

## Accepted visual checkpoint

The operator [accepted the revised POC and starting-sunrise date label on
2026-10-02](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1131#issuecomment-5959368176).
The retained LAN review is <http://192.168.2.45:8095/>; the earlier rejected
orientation remains at port 8094. Accepted output is pinned to rendering commit
`33712845b6b31c9382b24bb8b0eb8ac63723b486`, with per-run harness source,
assembly hashes, source times, settings and raw/display checksums. Later test
and evidence changes do not regenerate these accepted pixels.

The evidence includes 66 matched day/sunset/twilight/night/dawn captures,
fixed-transfer linear and gamma display images, raw mean/percentiles/clipping,
controlled-background comparisons, and actual sensor-path Sun/Moon captures.
The POC rig uses North up, East left, West right (`horizontalFlip: true`).
Sun/Moon locator circles are labels, not angular-size representations. The
1.5-degree detail captures show the true-scale disk and lunar phase; additional
lunar dates are explicitly separate from the October 12 daily period.

The modest sky approximation, defaults, provenance and limitations are described
in [the VirtualSky contract](../virtual-camera.md). Legacy scalar illumination
and explicit electron-rate fixtures retain their defaults; SolarDriven and
resolved disks are opt-in. ControlledNight changes the background, not the
celestial clock. No atmospheric photometry certification is claimed.

## Reproducible workload and performance

The isolated #1134 harness uses verified catalog `hyg-v4.2-p3-s2-r1`, 119,625
rows, database SHA-256
`b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2`.
Its `measure` mode captures 640-square Bayer frames with five warm-ups and
thirty measured captures per setting, one capture at a time, on the same host.
The retained `renderer-comparison.json` records exact clocks/settings, CPU,
allocation, peak working set, output checksums, byte counts and work boundaries.

| Scenario | Median ms | p95 ms | CPU s / 30 | Allocated bytes / 30 | Peak working set bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Day, legacy scalar | 35.16 | 37.95 | 1.35 | 321,549,184 | 171,716,608 |
| Day, SolarDriven | 130.47 | 136.64 | 4.39 | 321,506,648 | 180,617,216 |
| Day, SolarDriven + disks | 220.93 | 254.90 | 6.82 | 4,581,075,912 | 183,287,808 |
| Night, legacy scalar | 136.83 | 148.90 | 4.21 | 5,526,362,888 | 183,287,808 |
| Night, SolarDriven | 137.73 | 146.04 | 4.23 | 5,526,390,488 | 189,771,776 |
| Night, SolarDriven + disks | 137.25 | 146.03 | 4.24 | 5,526,641,728 | 191,078,400 |

The spatial daytime approximation adds per-pixel projection/trigonometry.
A bright disk raises the conservative stellar admission query ceiling, so the
daytime disk case evaluates more catalog candidates and allocates more. These
costs are explicit; the renderer does not hide them by truncating the catalog.
The uniform-night fast path avoids that projection cost. All three measured
night cases have identical raw checksums for this scenario. These are bounded
single-worker measurements, not physical-camera throughput qualification.

The 1,381-source full-day run used 423.90 s elapsed, 429.39 s CPU, and peak
working set 629,460,992 bytes, with sources processed sequentially. The full
planned grid has 1,441 slots, including 15 leading unscheduled slots, a deliberate
30-minute outage, and 15 trailing missing captures. The trail uses only 557
eligible dark-night sources (11,140 seconds of actual integration). Production
scheduling, encoder integration and historical calendar migration remain outside
#1131 and #1134.

## Validation ledger

Local selection uses `scripts/ci:classify` against target
`cb5d7e3f63c69e85f0f079ef63c687fbb7e5f818`: `mode=full`, `complete=false`,
`shared=true`, `cameraagent=true`, `logichost=true`, `combined=true`,
`delivery=true`; deployment flags are false. Tier C additionally requires the
complete local candidate commands in AGENTS.md. Exact final heads, gate results,
independent review and protected CI are recorded in the issue/PR ledger; visual
acceptance alone is not a green gate or a production-completion claim.

Evidence retained under `/tmp/hvo-1131-2a2d0ca8` includes candidate, affected,
source-map recollection and publish-matrix logs. POC inputs and accepted public
artifacts are retained under `/home/roys/.cache/hvo/1134-2a2d0ca8`, with runtime
secrets outside the allowlisted public tree. Source-map reports from different
renderer revisions were not merged for final coverage: affected reports were
recollected against one source revision before enforcing unchanged floors.

## Qualification blocker reproduced on the target

On 2026-10-02 the untouched target
`cb5d7e3f63c69e85f0f079ef63c687fbb7e5f818` was built in a separate detached
worktree with the same SDK/settings. Both relevant Unit suites and both
Integration suites passed. The same component coverage commands failed there:

| Component / metric | Untouched target | Candidate `5685ee09` | Required minimum |
| --- | ---: | ---: | ---: |
| LogicHost line | 69.0897% | 68.9351% | 71.2600% |
| LogicHost branch | 45.1492% | 44.8984% | 47.7500% |
| Combined-host branch | 17.5276% | 17.5188% | 18.2000% |

These are inherited shortfalls, with additional denominator growth from this
candidate; neither result is a passing coverage gate. The checked-in floors
remain unchanged. Shared, CameraAgent and delivery component coverage pass.
Shared `VisibleScene.cs` now has 97.96% line / 90.27% branch coverage and
`SceneRenderers.cs` 97.60% / 89.07%, after adding projection conservatism and
actual sensor-path tests. All selected candidate Unit/Integration slots,
Debug/Release builds, format, audits/guards, architecture/migration checks and
component publishes pass. The standalone recovery fixture's time-dependent
invalid values were corrected without changing production behavior or weakening
its assertions.

The operator explicitly authorized repair of these inherited coverage gaps on
2026-10-02. The accepted visual checkpoint remains independent of code
qualification. Logs `baseline-status.log`, `baseline-coverage-logichost.log` and
`baseline-coverage-combined.log` preserve the target reproduction.

The target's `development-v1.yml` workflow runs Preflight and Build/Unit;
`ci.yml` targets main/release and manual dispatch. Those distinct evidence sets
must not be represented as equivalent classifier-selected protected coverage.

## Authorized coverage repair

Seventeen new cases run in each host test assembly. They exercise the actual
LogicHost reconstruction/recipe adapter: Mono8/Mono16/RGB/CFA previews and
annotations, frozen output contracts, malformed result rejection, invalid
capture descriptors and control evidence, named calibration reference binding,
corrupt/missing/incompatible references, bounded rolling windows and ordered
lineage, and projected-scene metadata consumed by deterministic toggleable
presentation products. The combined assembly additionally checks edge/central
byte, identity and lineage equality for preview/annotation recipes and matching
invalid-option failures. Production behavior and coverage floors are unchanged.

Canonical lane reports after repair (`repair2-*` logs):

| Component | Line | Branch | Result |
| --- | ---: | ---: | --- |
| LogicHost | 71.2747% | 47.7701% | pass |
| Combined-host | 34.1558% | 19.6476% | pass |

Both complete affected Unit suites passed (LogicHost 573 discovered, including
one pre-existing provider-inapplicable skip; combined 25 passed). These
test-only additions use the previously passing Integration reports for the
same production revision; no cross-lane report borrowing or denominator
exclusions were introduced. Global discovery is now 5296 Unit cases. Independent
review and exact-head protected CI remain separate requirements.

## Initial review corrections

Independent review of PR #1139 found off-axis perspective disk undersampling
and resolved disk light entering the cloud background-scatter term. New
regressions fail on the prior `334ef377` renderer (five failures, seven existing
cases pass) and pass with the fixes. Astronomy now bounds projection stretch,
including aperture and distortion, and Imaging rejects an excessive sampling
budget rather than producing holes. Cloud transmission attenuates disk light
without restoring it as diffuse background. Disk raster identity advances to v2.
Twelve numerical projection-bound cases cover all projection families and both
distortion signs; the host calibration corpus additionally checks native 12-bit
normalization and expired profile rejection. Global Unit discovery is 5313.

The accepted POC artifacts remain pinned to their original renderer/assembly
identities. Their clear all-sky and centered detail geometries do not exercise
the two reported defect scenarios. Final correction gates and exact-range
rereview are recorded separately in the PR ledger.
