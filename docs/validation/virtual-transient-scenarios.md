# Virtual Transient Scenario Validation

Status date: 2026-07-20

This evidence covers deterministic generic sky tracks and sensor charge for
issue #61. It does not claim physical meteor photometry, detector sensitivity,
classification quality, runtime scheduling, video, clips, or real-event
validation.

## Deterministic Fixtures

`tests/fixtures/virtual-sky/transient-scenarios-v1.json` is the detector-safe
stimulus manifest. Its SHA-256 is
`BAC54BEF38A85BD5B07FA9FDA855A3DC5DD316A51B37F21B8D3BC2E6D9F87F81`.
Expected semantics and output identities live separately in
`tests/fixtures/virtual-sky/transient-detection-oracle-v1.json`, whose SHA-256
is `5338022A7EBF0443CAFEF844C5992771FAE692B16B0D6BCAFFF73AAD6A8749DC`.
Production configuration and provenance contain only opaque primitive
identities and rendering parameters. Tests execute all rendering and detection
before loading the oracle, verify the oracle sentinel and labels are absent
from detector configuration and artifacts, and reject a canonical input
manifest identity other than
`133402E913B61C62542FDDE4C578C39257D4EFF87574516D7F43E463A9E0FC93`.
The canonical identity is independent of checkout line endings.

| Fixture | Raw min | Raw max | Raw mean | First Mono16 SHA-256 |
| --- | ---: | ---: | ---: | --- |
| no event | 0 | 1 | 0.5416666666666666 | `D9438A1E5E3B6CC7D74EF56B4C6054C74D7FDC3F06E22845D9398483CB8211CC` |
| short track | 0 | 836 | 5.259765625 | `C6D59E2BBCF44F6820A9E77E5E9A926BDBC0A9CEAB376544FF32F0D1037E6206` |
| fragmented flare | 0 | 65535 | 420.3030598958333 | `BE4C2467147DDE396598AC78FB1D5F32004F0A443FD2D3DDB2B04C4BB32A0D52` |
| boundary crossing | 0 | 746 | 3.7766927083333335 | `7034977C6C7273EE207FF930B0803213A1BCDAFB7724E1C842A45134CA4381A7` |
| long shadow | 0 | 16 | 0.5729166666666666 | `138BC7259C0B7530C63FA3A86DD4E6FD754045357E195622187CE988FA62D514` |
| blinking track | 0 | 180 | 1.7174479166666667 | `2B45C8EAD0B20364D0F990C1725BB03E42ED20AB05C93F80959B6C1314AB4378` |
| sensor artifacts | 0 | 19973 | 42.858723958333336 | `DCECBCCB97C5869632E3C54A60C0DF2BCA7A422E1E0DAEE693802BC5A870212B` |

The second boundary exposure is pinned as
`B13D64D6E20EF8867AD2DC7374B1F2753E7ACAABC4D206630E27A3F0458463BA`.
Focused geometry tests prove ordered centroids and energy conservation across
adjacent half-open exposures. Other tests cover restart and reversed-order
determinism, azimuth wrap, aggregate bounds, non-overlap, cloud/optics versus
sensor-stage separation, Mono16/RGB24/RGGB16 response, saturation, strict
configuration, manifest integrity, and detector-input isolation.

## Detector Baseline

Issue #119 composes the ordinary VirtualSky captures with the shared temporal
background, candidate extraction, observation promotion, and deterministic
assessment factories. Fixture UUIDs are derived from immutable case, offset,
and slot keys rather than case order. The separate oracle pins raw SHA-256 and
statistics, every offset-to-extraction receipt mapping, every candidate UUID,
event UUIDs,
geometry/features identities, classifications, meteor severity, and assessment
receipt SHA-256. The cloud row also pins raw statistics/SHA-256, empty geometry
identity, and its no-candidate extraction receipt.

| Approved case | Primary candidates | Expected/actual result | Disposition |
| --- | ---: | --- | --- |
| no event | 0 | none / none | true negative |
| short track | 1 | meteor / meteor | scored |
| fragmented saturated flare | 1 | fireball / meteor with fireball severity | scored |
| adjacent-exposure boundary crossing | 1 per exposure | boundary meteor / meteor | scored |
| three-observation long shadow track | 1 per observation | satellite / satellite | scored |
| three-observation blinking track | 1 per observation | aircraft / aircraft | scored |
| cosmic ray plus persistent hot/stuck pixels | 3 | sensor artifact / sensor artifact | scored |
| stable 55% cloud, no transient | 0 | none / none | true negative |

The approved matrix therefore has six event cases detected and assessed, two
no-event/cloud true negatives, zero misses, zero classification mismatches, and
zero false-positive cases. There is no miss or false positive requiring an
exception disposition. The three sensor components intentionally produce one
reviewed scenario assessment from the longest measured component; the receipt
still pins all three extracted components.

The reviewed range is exactly the versioned 64 x 48 Mono16 fixture definitions,
one-second exposures, listed capture offsets, and extraction thresholds in
`VirtualSkyTransientScenarioTests`. Observed primary integrated signal spans 3
through 699,701 ADU and includes boundary clipping, one fragmented/saturated
flare, one persistent brightness sequence, one blinking sequence, compact
sensor charge, and a stable-cloud negative. These observations define a
deterministic software non-regression matrix, not interpolation outside those
points, physical photometry, field sensitivity, real-sky false-positive rate,
or ARM64 performance.

The complete reviewed scope is composed without exposing truth to production
inputs:

| Concern | Evidence |
| --- | --- |
| Star residuals and persistent masks | `TransientStarMaskStrategyTests.W1W2PersistentProjectedStarMaskEvidence` renders full-resolution W1/W2 stars and asserts the persistent projected mask leaves zero extracted components; the focused temporal test separately pins mask composition semantics |
| Saturation topology | `TransientDetectorInputTests.RggbSaturationMaskPreservesAnyPhotositeClippingHiddenByCellAverage`, temporal saturation-mask exclusion, and the fragmented-flare matrix case |
| Mask integrity and bounds | temporal tamper, incomplete-mask, non-persistent-mask, and malformed-context tests |
| Direct/reconstructed conformance | `LogicHostProcessingConformanceTests.EdgeAndCentralAdaptersProduceEquivalentCenteredTransientBackground` compares background pixels/masks/descriptors, extraction receipts/candidates, and assessment bytes |
| Detector disabled | `TransientTemporalBackgroundTests.OffDoesNotConstructTemporalWindow` warms the call and measures zero current-thread bytes over 1,000 calls while retaining no window |
| Reprocessing | `TransientAssessmentTests.ReprocessingPreservesHistoryAndOnlySupersedesExplicitSameProducerAssessment` |

Focused reproduction:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~VirtualSkyTransientScenarioTests"

dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~TransientTemporalBackgroundTests.OffDoesNotConstructTemporalWindow"

dotnet test tests/HVO.SkyMonitor.CameraAgent.LogicHost.Tests/HVO.SkyMonitor.CameraAgent.LogicHost.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~LogicHostProcessingConformanceTests.EdgeAndCentralAdaptersProduceEquivalentCenteredTransientBackground"

dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~TransientStarMaskStrategyTests.W1W2PersistentProjectedStarMaskEvidence"
```

## CameraAgent Runtime Acceptance

Issue #63 composes durable CameraAgent ingress, the optional transient lane, the
shared detector, candidate persistence, centered finalization, and Hybrid
handoff. Deterministic acceptance tests cover the stable-cloud and no-event
controls, centered timeout to `needs_review`, adjacent-capture time-gap reset,
one-to-many split and many-to-one merge ambiguity, optional and required lane
pressure, retryable SQLite storage failures including `SQLITE_LOCKED`, malformed
record quarantine, and actual database corruption. Database corruption is
reported as unavailable/unhealthy storage; the test restores known-good durable
bytes and reconstructs the provider before proving recovery. It is not treated
as work-level quarantine because writing quarantine state to a corrupt database
is not a credible recovery action. The association checks execute the same
complete bipartite decision helper used by the worker; ambiguous evidence
remains separate and is never guessed into an event.

The candidate-journal matrix injects before and after stage, identity
reservation, candidate, finalization, submission, and acknowledgement commits.
The hosted-worker matrix injects before and after runtime identity allocation,
causal extraction, centered observation extraction, assessment persistence,
successful and unsuccessful frame-history completion, runtime completion, and
retirement, plus the existing candidate, finalization, and handoff journal
boundaries. Each case disposes the interrupted provider and reconstructs the
store and worker. It compares pre/post identity tuples, retention holds,
backlog count/bytes/oldest time, frame and candidate state, workflow phase,
event version links, and canonical candidate, finalization, or submission
payload identities before proving bounded eventual recovery. Focused
reproduction:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~TransientWorkerRuntimeTests|FullyQualifiedName~SqliteTransientCandidateJournalTests.DurableCandidateBoundaryFaultMatrix_RetryConvergesExactlyOnce|FullyQualifiedName~DurableCaptureDistributionTests.OptionalBlockedTransientLaneDoesNotBlockStandardIngress|FullyQualifiedName~DurableCaptureDistributionTests.RequiredBlockedTransientLaneRefusesNextIngressWithoutLosingStandardWork"
```

### Runtime Performance

The issue #63 x64 Release harness uses canonical W1 1,936 x 1,216 Mono16 and W2
3,096 x 2,080 RGGB16 payloads. Each no-event steady workload has five independent
trials with five warmups and 30 measured operations at concurrency one.
Nearest-rank p95 is calculated within each trial, then
median/minimum/maximum are reported across trials. W3M inserts and recovers
10,000 indexed durable metadata records over shared payload references; W3P
pre-stages and drains 100 W2 payloads containing 1,287,936,000 raw bytes.
Process CPU, approximate process-wide managed allocation, operation-boundary
RSS, post-operation LOH, process I/O, SQLite size, retained bytes, backlog,
health, and durable identities are retained in the raw JSON. Runtime counters
measure evidence loads, opened files, and bytes; `.bin` files are enumerated for
stored-copy counts. Copy counts are not inferred from expected control flow.

Dirty-development values are intentionally not retained here as a baseline or
project claim. The ignored JSON records the complete environment and all trial
distributions. Every accepted development run must exceed the pinned W3P drain
floor, drain to zero, retain zero W3M payload copies, and report measured stored
and reloaded payload counts. Only a clean committed replacement run may supply
citable latency, throughput, CPU, allocation, RSS/LOH, I/O, and checksum values.

Blocked-lane evidence uses five independent interleaved Off/blocked trials and
alternates execution order. The predeclared gate compares the median of each
trial's median and p95 with `min(baseline * 2, baseline + max(35%, 2 ms))`; the
hard ratio cap prevents a greater-than-100% regression for small latencies.
The Off and blocked values are same-run controls rather than a historical main
baseline; exact distributions remain in the fingerprinted ignored JSON.

Five full-resolution positive W1 Edge trials each persisted and centered
candidate components, created version-1 final receipts, and measured evidence
loads/bytes/files. Five full-resolution W2 Hybrid trials each persisted and
handed off candidate components under the same measurement scheme. Every exact candidate, event,
observation, assessment, event-version, candidate-payload, and delivery identity
is validated within its trial; opaque IDs are expected to be unique across
independent trials, while raw payload, normalized durable state, candidate count,
and canonical outcome identities must be stable. A `SQLITE_LOCKED` transition
records unhealthy state and then finalized, zero-backlog recovery in a
reconstructed provider.

Off mode creates zero transient lane definitions, lane rows, or worker tables.
Rendered and structured transient logs, metrics, spans, and health are collected
for Edge, Hybrid, and failure/recovery transitions. The harness allows only
bounded metric/activity/log keys and cardinality, and rejects paths, exact IDs,
payload references, and secret-like values from emitted tags or log bodies.

Command:

```bash
dotnet build \
  tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release --arch x64 -warnaserror &&
DOTNET_gcServer=1 dotnet test \
  tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --no-build --configuration Release --arch x64 \
  --filter "FullyQualifiedName~TransientWorkerAcceptancePerformanceTests.W1W2W3MAndW3PTransientWorkerEvidence"
```

The harness records actual branch, revision, configuration, architecture, a
canonical fingerprint over the complete tracked diff plus every untracked path
and content hash, and SHA-256/MVID evidence for both the test and production
assemblies. It also records each assembly's SDK-generated informational version
and embedded full repository revision at run start and end. A clean candidate
is accepted only when both embedded revisions equal the recorded full HEAD;
start/end Git, source, and binary equality is recorded separately as run-input
stability. A dirty run is skipped unless
`HVO_PERF_ALLOW_DIRTY_DEVELOPMENT=1`; an allowed dirty run is explicitly marked
`dirty-development-not-claimable` and `cleanCommittedReplacementRequired=true`.
There is no clean historical baseline for the composed positive worker path, so
its before value is `N/A`; the five-trial ranges are the recorded noise evidence,
not a regression claim. A clean committed replacement run remains required
before citing an evidence checksum. This is deterministic synthetic runtime and
durability evidence, not a physical sensitivity, real-sky false-positive,
physical-camera, or ARM64 claim.

## Detection Performance

The issue #119 x64 harness runs five independent steady trials for named
`W1-T119` and `W2-T119` workloads. These use canonical W1/W2 dimensions and
formats with a deterministic full-size synthetic elongated residual because
the canonical no-event sky frames do not provide a stable positive extraction
and assessment workload. W2 uses a replicated 2 x 2 RGGB cell that preserves
the controlled detector residual. This is a size/path baseline, not a claim
that the arithmetic pixels are canonical sky content.
Each trial uses five warmups followed by 30 measured operations for direct
extraction, validated extraction, assessment, and a complete source-artifact,
detector-input conversion, centered-background, extraction, promotion, and
assessment path. W2 complete-path input is the full 3,096 x 2,080 RGGB source.
Each p95 is computed from its 30 operation samples; the table reports
median/minimum/maximum across the five independently computed trial statistics
and never infers p95 from five samples. The retained-window value is the logical
five detector-frame production footprint; actual fixture-owned bytes are
reported separately from logical source/window bytes.

Environment: Ubuntu 24.04.3 LTS, .NET 10.0.0, linux-x64, Intel Core Ultra 9
285H, 8 logical processors, 16.77 GB available memory, Release, concurrency 1,
one-second exposure, five-second source cadence, no backlog or external I/O.

| Workload/stage | Median wall ms med/min/max | p95 wall ms med/min/max | Median CPU ms med/min/max | p95 CPU ms med/min/max | Allocated bytes/op med/min/max | ops/s med/min/max |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| W1-T119 direct extraction | 5.603/5.534/10.499 | 15.036/13.372/15.180 | 6.042/5.960/12.506 | 17.166/16.919/18.955 | 11,876,312/11,874,793/11,876,706 | 133.99/102.59/141.17 |
| W1-T119 validated extraction | 19.519/16.491/20.131 | 21.190/20.721/26.232 | 20.275/17.950/25.808 | 27.155/22.177/43.983 | 13,860,997/13,859,746/13,912,983 | 53.98/46.34/60.22 |
| W1-T119 assessment | 0.179/0.109/0.671 | 0.203/0.132/0.769 | 0.181/0.110/0.672 | 0.203/0.132/1.239 | 262,117/262,096/265,468 | 6,384.47/1,454.14/8,960.04 |
| W1-T119 complete detector | 88.647/87.527/95.423 | 122.382/117.236/123.676 | 89.907/89.112/120.781 | 123.496/119.822/189.357 | 31,846,678/31,846,372/31,880,974 | 10.46/10.08/10.57 |
| W2-T119 direct extraction | 3.837/3.779/4.304 | 7.964/7.838/8.431 | 3.839/3.799/4.391 | 7.967/7.891/10.194 | 8,133,356/8,133,342/8,133,513 | 222.37/195.34/225.56 |
| W2-T119 validated extraction | 9.386/9.340/9.434 | 13.510/13.337/16.984 | 9.443/9.353/9.482 | 13.518/13.340/17.728 | 9,930,429/9,930,129/9,930,437 | 102.23/96.47/103.17 |
| W2-T119 assessment | 0.110/0.106/0.114 | 0.129/0.127/0.136 | 0.111/0.107/0.115 | 0.130/0.128/0.552 | 262,111/262,096/262,377 | 8,899.44/8,593.77/9,081.55 |
| W2-T119 complete detector | 112.004/110.754/113.508 | 134.714/134.075/136.728 | 113.820/113.696/115.844 | 137.031/136.657/138.651 | 38,717,285/38,716,737/38,717,371 | 8.49/8.47/8.54 |

W1-T119 logically retains 23,541,760 detector-window bytes, complete-path median
throughput is 10.46 frames/s, and complete-trial start/post-operation-sampled
RSS spans 205,074,432 through 281,427,968 bytes; post-GC LOH size is 26,879,712
bytes. W2-T119 starts
from the 12,879,360-byte RGGB source, converts five source artifacts, and
extracts over its 1,548 x 1,040 Mono16 detector representation. It logically
retains 16,099,200 detector-window bytes, measures 8.49 complete frames/s,
and spans 271,859,712 through 411,033,600 complete-path start/post-operation
sampled RSS bytes; post-GC LOH is 75,366,752 bytes. RSS sampling occurs at
operation boundaries and is not presented as an in-operation native-memory
peak. W1 borrows two unique source buffers across five positions. W2 owns five
converted detector buffers per complete operation. Both own one centered
background plus bounded extraction state/traversal arrays; the raw JSON records
actual fixture-owned and logical five-source bytes. This source/detector-size
distinction explains why W2 direct extraction is faster than W1 while its
complete path is slower.

The pinned source target/background SHA-256 pairs are
`D07A2314618149F33C067CB9B6B1B5B6F30EC2352431F53F0A94E67771CFFA53` /
`F048AB98FF1181D3FEF4FCB0E47DC0BF554F731B6450E51A9705594140B79587`
for W1-T119 and
`7490F8C076950C70803F9D9F843C4292A6FAE8E93DD8E9ABB7B8B88B155A3F03` /
`3119269EE5F9AF20038899CFCBC4603DF391BC86117F3F214203FAD5805518A0`
for W2-T119. Candidate rate is one per complete-path frame. CPU samples use
process-wide `TotalProcessorTime` deltas and include sampler overhead; they are
reported as baseline distributions rather than isolated thread CPU.

The complete-path row measures input reconstruction and temporal background;
issues #113 and #115 remain the detailed stage baselines rather than values
added to another interval. Persistence is N/A: this host-neutral path performs
no filesystem, SQLite, SQL Server, MinIO, or network persistence, and zero
observed I/O is not presented as zero persistence latency. The first complete
five-trial detector composition has no equivalent before value, so its baseline
is `N/A`; merged #121 is the nearest production implementation and issue #119
changes no production algorithm. Raw issue #119 evidence is written to ignored
`TestResults/issue-119/candidate-extraction-performance.json` and had SHA-256
`2092BBA38CF9FB1F47E5AC988922B7198CA5DF7992110FB664526F844D04D410`
for this working-tree run.

Command:

```bash
dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj \
  --configuration Release --arch x64 \
  --filter "FullyQualifiedName~TransientCandidateExtractionPerformanceTests.W1W2CandidateExtractionEvidence"

HVO_PERF_OUTPUT=TestResults/issue-113/local \
dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj \
  --configuration Release --arch x64 \
  --filter "FullyQualifiedName~TransientContractAndInputPerformanceTests.W1W2Evidence"

dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj \
  --configuration Release --arch x64 \
  --filter "FullyQualifiedName~TransientTemporalBackgroundPerformanceTests.W1W2TemporalBackgroundEvidence"
```

The candidate run used branch `feat/transient-baselines-119`, base/HEAD
`c68cf04c1d70701127764445d622eebabb0655eb`, and the dirty-state/source-checksum
disposition recorded in raw evidence. The candidate adds no production
algorithm or host behavior over merged #121. Complete extraction and assessment
receipt identities were identical across all five trials, all stage p95s remain
far below the five-second cadence, complete-stage retained managed memory and
LOH returned to their pre-trial ranges, and no unexplained regression was
observed. A replacement run from the committed clean candidate head remains the
final PR evidence step.

## Performance

Baseline revision: `e402bfeb0607101eeec95e292104722d3837c630`.
The nearest clean baseline is the existing transient-disabled W1/W2 complete
module and render-only harness. Candidate cases use the same dimensions, empty
catalog, seed `2025`, UTC `2025-01-15T08:00:00Z`, one-second exposure,
concurrency one, five case warmups, and 30 measurements. Runtime tiering is
stabilized equally for null and active transient paths before measurements.

Command:

```bash
HVO_EVIDENCE_REVISION=<revision> \
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --no-build --configuration Release \
  --filter "FullyQualifiedName~VirtualSkyTransientPerformanceTests.W1AndW2TransientRenderEvidence"
```

| Workload | Scenario | Complete median/p95 | Render median/p95 | Active sparse pixels |
| --- | --- | ---: | ---: | ---: |
| W1 1936x1216 Mono16 | none | 59.65/65.07 ms | 58.53/62.32 ms | 0 |
| W1 | explicit empty | 61.38/63.76 ms | 58.82/62.36 ms | 0 |
| W1 | short sky track | 70.78/78.41 ms | 69.98/74.19 ms | 786 |
| W1 | saturated fragments | 68.84/72.88 ms | 69.00/72.18 ms | 2,222 |
| W1 | long shadow/blink | 71.48/73.14 ms | 70.62/79.58 ms | 1,602 |
| W1 | sensor artifacts | 68.27/71.82 ms | 68.35/72.83 ms | 215 |
| W1 | partial cloud/track | 477.82/482.28 ms | 477.61/484.09 ms | 801 |
| W2 3096x2080 RGGB16 | none | 576.23/598.12 ms | 573.84/591.48 ms | 0 |
| W2 | explicit empty | 574.09/600.24 ms | 570.51/594.74 ms | 0 |
| W2 | short sky track | 668.78/692.69 ms | 670.11/697.42 ms | 818 |
| W2 | saturated fragments | 665.60/690.68 ms | 663.69/682.05 ms | 3,765 |
| W2 | long shadow/blink | 655.12/681.37 ms | 655.02/682.49 ms | 1,624 |
| W2 | sensor artifacts | 633.49/660.58 ms | 628.34/655.55 ms | 215 |
| W2 | partial cloud/track | 2043.59/2062.36 ms | 2025.01/2061.33 ms | 1,218 |

The identical prewarmed no-cloud harness was run in both directions between a
detached parent worktree and the candidate. The retained comparable parent
trial measured W1 complete/render medians of `61.4295/59.3849 ms` and W2 medians
of `602.9345/595.0340 ms`; the candidate measured `63.3649/61.1971 ms` and
`607.5200/610.2003 ms`. Deltas were `+3.15%/+3.05%` for W1 and
`+0.76%/+2.55%` for W2. Trial ranges overlapped, allocations were unchanged,
and all values remained below the pinned 5% investigation threshold. The matrix
control independently measured no-transient medians within that same range.

| Path | CPU parent/candidate | Allocation delta | Peak RSS delta | Throughput delta |
| --- | ---: | ---: | ---: | ---: |
| W1 complete | 2328.670/2384.933 ms | +0.011% | +0.14% | -2.56% |
| W1 render | 1857.603/1908.614 ms | +0.008% | +0.13% | -2.56% |
| W2 complete | 18404.833/18381.704 ms | +0.0001% | -0.06% | -3.66% |
| W2 render | 18040.073/18390.537 ms | +0.0001% | +0.13% | -2.08% |

CPU and peak RSS are unchanged within normal run variance, and allocations are
effectively byte-stable. The W2 complete throughput delta includes one candidate
scheduling outlier (811 ms p95 versus a 607.52 ms median); its CPU decreased,
median changed only 0.76%, trial ranges overlapped, and the repeated matrix
control was faster than the parent median. No resource regression is indicated.

Exact transient-disabled and cloud-only checksums remain unchanged. Every
transient case has pinned complete/render SHA-256 and numeric active-pixel/
deposited-energy invariants; the slowest p95 is 2.062 seconds, over twelve times
inside the 25-second configured arrival interval.

Transient storage is one sparse `Dictionary<int, VirtualTransientPixelSignal>`;
the largest measured support was 3,765 pixels, or 0.059% of W2. It adds no dense
truth mask or full-frame transient plane. W1 retains its normal signal/output
buffers and evaluates cloud values on demand. The combined W2 case reuses the
existing temporary two-float cloud map (51,517,440 bytes) across three channels.
Enabled CPU and latency overhead comes from sparse signal construction and
lookups inside the mandatory full-frame sensor/CFA pass, not retained dense
state. The corrected harness records measured pre/post GC counts, in-sequence
heap/LOH/RSS, allocations, CPU, parse/canonicalization cost, source/config/
provenance bytes, throughput, and exact output length.

## Ordinary Pipeline

The configured two-host integration fixture jointly enables cloud and transient
scenarios. It proves a saturated sensor-stage pixel at `(1,1)` in raw Mono16 and
the exact corresponding Mono8 preview pixel, transitive preview-to-raw lineage,
manifest provenance, local storage, durable ingress, processing, upload, and
central persistence. The retained trial recorded 62 files totaling 2,974,751
bytes, including nine SQLite/WAL files totaling 2,684,464 bytes. Four captures
committed 24,576 ingress bytes and produced 20 processing outputs totaling
86,016 bytes. Final raw-ingress and processing pending/retry/quarantine/
terminal counts were all zero, and the authenticated upload drain reached a
checkpoint with zero pending, leased, retrying, or quarantined records.

The run observed bounded capture, capture-control, raw-ingress, processing, and
environmental metrics plus activities from `capture-cycle` through
`processing-artifact.persist`. Health remained accepting/healthy. Logs, metric
tags, and span tags contained none of the scenario identity/hash, canonical
parameters, coordinates, oracle labels, raw payload, credentials, tokens, or
internal paths. All errors and all VirtualSky/environmental warnings are
forbidden; unrelated TestServer request-limit, fixture-health, and existing EF
query warnings in captured host output are explicitly outside this feature
gate. Ignored JSON/TRX evidence is written under
`TestResults/issue-61/`.
