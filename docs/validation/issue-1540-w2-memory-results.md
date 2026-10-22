# #1540 W2 memory evidence and preserved-branch handoff

## Result and scope

The historical +13.56% median RSS increase did **not reproduce** in the declared comparison of the preserved feature after synchronization with development/v1. Six trials per arm passed all 35 operations. Median peak RSS changed by **−15,534,080 B (−0.8198%)**, within the unchanged `max(5%, 32 MiB)` rule. Production memory paths remain unchanged; the synchronized comparison meets the declared RSS rule.

Current traces attribute the large memory envelope to existing full-frame rendering and product restoration, with substantial GC/LOH phase and fragmentation variation. The feature's measured allocation increment is small and proportional to scene objects. This establishes current-source evidence on this host; it does not recover the missing historical heap payloads or prove which objects caused the old observation. Historical F3 needs an explicit independent disposition. [Slice B (#1541)](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1541) still owns the rest of #1187 acceptance, and the PR remains draft unless review establishes that it stands alone.

## Immutable comparison and protocol

- Preserved feature: `926055938c88b986df9e442fef4de2fce75f4aa4`.
- Baseline: `c1971fbf2e640ea82473f7e2dae3aca2a457c251`.
- Synced candidate: `26ae1b9bd258d70f93ee254bdb59772f6700fd4e`, a merge with those exact two parents. Its merge base against that target is exactly the baseline.
- Committed protocol: [issue-1540-w2-memory.md](issue-1540-w2-memory.md); driver: [issue-1540-w2-memory.sh](issue-1540-w2-memory.sh); acceptance guard: [issue-1540-w2-memory.py](issue-1540-w2-memory.py).
- Two detached worktrees, Release, SDK 10.0.402, runtime 10.0.13, the same Linux x64 host with eight CPUs and approximately 31 GiB RAM. Production runtime defaults, workstation/background GC; no GC or JIT overrides. DOTNET_ROOT only locates the installed runtime and is recorded by the harness.
- Unchanged `Issue1170ComposedPipelineEvidenceTests.ComposedShippedGraph`, `steady`, `w2-night`, five warm-up and thirty measured operations, fresh process and durable roots per trial. The harness and cameraagent.sample.json are byte-identical across the compared commits.
- HYG v4.2 production snapshot: 119,625 rows, SHA-256 `B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2`.
- The unchanged shipped W2 graph does not enable the measured-association producer. This is the historical F3 compatibility workload, including label-policy consumers; actual producer cost and final-pixel obligations remain in #1541.
- Six pairs ordered B-C, C-B, alternating, under one shared heavy-lock hold. The one-minute load was below 1.0 at every trial's entry; zero ten-minute load-gate timeouts. No collectors in the acceptance trials.
- All twelve raw image checksum sequences match, including all warm-ups. Per-trial configuration hashes differ because storage roots are deliberately relocated. All original results, TRX attachments, host observations and checksums are retained; no trial was excluded.

## Uninstrumented results

Peak RSS is the process-lifetime kernel high-water metric, never an isolated retained-heap measurement.

| Peak RSS (B) | Baseline | Candidate |
| --- | ---: | ---: |
| Median, n=6 | 1,894,799,360 | 1,879,265,280 |
| Minimum | 1,749,250,048 | 1,736,224,768 |
| Maximum | 1,912,975,360 | 1,912,270,848 |
| Spread | 163,725,312 | 176,046,080 |

Baseline median is 1,807.0 MiB; candidate median is 1,792.2 MiB. The margin is 94,739,968 B (90.35 MiB). The decision uses the median, as declared; the similar maxima do not replace it.

| Descriptive operation metric, median across trials | Baseline | Candidate |
| --- | ---: | ---: |
| Allocated bytes per operation | 1,199,346,036 | 1,199,422,176 |
| Service time (ms) | 2,331.26 | 2,147.27 |
| Gen2 collections over a trial | 63.5 | 64 |

The allocation increment is 76,140 B/operation (about 0.0063%). Timing is descriptive and is not a new speedup qualification. Final LOH snapshots range over hundreds of MiB, including a 1,010,566,616 B candidate spread. A final snapshot cannot be substituted for a retained-object census or for the declared peak metric.

The six paired candidate-minus-baseline peak RSS differences are retained individually:

| Pair | C−B peak RSS (B) |
| --- | ---: |
| 1 | -31,834,112 |
| 2 | +25,092,096 |
| 3 | -28,557,312 |
| 4 | -166,195,200 |
| 5 | +25,804,800 |
| 6 | +114,118,656 |

Their median is −1,732,608 B and spread is 280,313,856 B. The acceptance decision remains the predeclared difference of arm medians. The median paired service-time delta is +24.79 ms; timing and CPU totals are descriptive rather than a speed qualification.

Additional predeclared snapshots and resource observations follow. Final snapshot values describe their sampling instant and remain distinct from retained-object evidence.

| Median snapshot / counter | Baseline | Candidate |
| --- | ---: | ---: |
| Final anonymous RSS (B) | 1,115,533,312 | 1,154,279,424 |
| Final file RSS (B) | 129,951,744 | 130,295,808 |
| Last-collection LOH size (B) | 453,289,304 | 731,609,480 |
| Last-collection POH size (B) | 57,192 | 57,192 |
| GC pause (ms) | 1,949.310 | 1,786.589 |
| Gen0 collections | 2,510.5 | 2,512.0 |
| Gen1 collections | 239.5 | 242.0 |
| Whole-trial elapsed (ms) | 90,908.25 | 82,678.07 |
| Whole-trial CPU (ms) | 88,616.15 | 79,053.93 |
| Logical reads, rchar (B) | 8,431,911,406 | 8,433,322,018 |
| Logical writes, wchar (B) | 6,839,011,310 | 6,839,156,851 |
| Physical reads (B) | 45,056 | 61,440 |
| Physical writes (B) | 6,846,193,664 | 6,846,316,544 |
| Retained trial filesystem size (B) | 6,663,366,723 | 6,663,339,590 |

The raw summary retains every sample, minimum/maximum/spread and peak-setting operation ordinal. Filesystem page caching affects physical reads. This steady pack has `saturation=null` and supplies no offered-load/backlog or capacity qualification.

## Managed and native attribution

Two additional interleaved pairs were instrumented, and do not feed acceptance. Each keeps System.Runtime counters at one second, status/smaps_rollup at 250 ms, full smaps every five seconds, a verbose GC/stack EventPipe trace, and a final gcdump. All four diagnostic trials passed. Decoding found **783,827 allocation ticks with stacks, 10,064 full per-heap histories, and zero lost trace events**. Each history retains the values for all five generations, including size before/after, object space, fragmentation and new allocation. The original nettrace is retained as the authority, alongside the decoder source and its output.

Allocation stacks identify the shared paths:

- `Mono16SceneRenderer.RenderCore` through `BayerRggb16Renderer.Render` and `VirtualSkyCameraModule.CaptureCoreAsync`: full-frame render arrays.
- `TemporalPointSpreadRaster.Rasterize`: point-spread accumulation and result arrays.
- `CaptureProcessingPersistence.ReadCommittedFileAsync` / `RestoreOutputAsync`: frozen product byte buffers.
- `BayerRggb16Demosaicer.DemosaicToRgb24` through `EncodedPreviewRecipe`: RGB preview buffers.

Both arms have the same leading allocation types: byte arrays, double arrays, strings, point-spread dictionary entries and raster pixels. For example, baseline pair 1 attributes approximately 17.37 GB of sampled allocation weight to byte arrays and 5.55 GB to double arrays. These are sampled weights, not exact per-site allocation counters. Those paths are unchanged by this feature.

The following is each trial's five-second mapping snapshot with the highest summed mapped RSS. GC ranges come from timestamped segment-create/free events intersected with smaps VMAs. Ambiguous overlaps and startup GC segments absent from the trace are conservatively included in the unassigned/native upper bound. GC committed bytes are **not** subtracted from RSS and called native memory.

| Diagnostic trial | Fully matched GC segment RSS (MiB) | Unassigned anonymous RSS upper bound (MiB) | File/memfd RSS (MiB) | Final collected heap (MiB) |
| --- | ---: | ---: | ---: | ---: |
| Base 1 | 1,443.61 | 123.11 | 184.22 | 59.487 |
| Candidate 1 | 1,335.97 | 123.73 | 185.86 | 59.644 |
| Base 2 | 1,306.82 | 143.37 | 185.42 | 59.180 |
| Candidate 2 | 1,428.57 | 122.70 | 188.25 | 59.665 |

The committed [attribution analyzer](issue-1540-w2-attribution.py) recomputes these mapping and history results from the retained decoded exports. For example, `python3 -I docs/validation/issue-1540-w2-attribution.py <evidence-pack> --output <new-output.json>` leaves the input pack unchanged.

The unassigned bound includes native allocator arenas, SQLite caches, stacks and unclassified GC memory; it is not a precise SQLite allocator census. File/memfd mappings include assemblies, native libraries and the runtime's JIT double mapping. Neither category shows a candidate increase of the historical magnitude. Five-second mapping snapshots do not decompose the exact unsampled instant of the kernel RSS maximum.

Recorded GC history shows LOH sizes reaching about 1.3–1.6 GB and fragmentation reaching hundreds of MiB in **both** arms, while gen2 object-space maxima stay around 90–97 MB. The varying large temporary arrays and fragmentation account for the observed GC-dominated envelope. This is direct evidence of current GC variation, and only a hypothesis about the lost historical observation.

The final gcdump is taken after provider/module disposal, before testhost exit. It is a post-scope retained-heap supplement, not a proof of production steady-state retention. The tool also warns that .NET 10 lacks its .NET 11 non-lossy buffering option; the dumps are treated as best effort. Ongoing collection histories, full trace payloads and source inspection supply the in-operation evidence. No long soak or physical-device qualification is claimed.

The feature's additional label work is in `StellarLabelPolicy.Apply` and the presentation producer's bounded object/list/dictionary projections. `ProjectedSceneJson.MaximumObjectCount` caps the primary scene-object collection at 10,000; the additional solar-system footprint collection is separately bounded. These per-operation objects have no new full-frame buffer, static accumulation, persistent cache or new native allocation path. Their measured small increment is intentional work for fail-closed stellar labels, proportional to that bounded scene. There is no remaining material median RSS increase to repair in this comparison.

## Later synchronization and compatibility

Development/v1 advanced to `b1de8353e5a1bf3c8bae3de15d4bbab45bdec1ef` during measurement. Merge `1e8030cc` reconciled additive inventories/runbook totals. Upstream added the LogicHost exact-capture viewer and three option-descriptor classes; no pre-existing W2 production method changed. Production references to the new descriptor catalog/codec exist only within those three new files, with no module initializer or W2 caller. This is the source argument for reusing the frozen comparison; its endpoints are not relabelled as newer binaries. Independent review must verify that argument.

A concrete integration mismatch was corrected: upstream's descriptor pinned the v12 producer while this feature ships v13. The exact v12 descriptor remains; v13 gets a separate versioned descriptor. No automatic saved-draft upgrade is introduced. `BothSceneVersionsRoundTripWithoutChangingSavedDrafts` checks exact versions, option bytes and graph revisions for both. The source-level failing version assertion and the first failed Debug build are retained with their corrections.

The next fetched target, `954534e6149452ec35bf1345146b5d61d53c6df3`, was merged in `4dc9e915`. Its 33 changed paths add LogicHost scoped operation/delivery read APIs and Current Sky UI, with a new catalog-summary read method and service registration. It changes no CameraAgent, Imaging, Astronomy, catalog-adapter or Processing source. Inventory/runbook conflicts were reconciled additively. These central read/UI paths are absent from the CameraAgent-only W2 harness. The measured endpoints remain c1971fbf and26ae1b9b; no new-head measurement is claimed.

The full local Integration run also exposed a real shutdown-health race in pre-existing central graph recovery: SqlClient can wrap an aborted command in a provider exception rather than `OperationCanceledException`. The worker already avoided reporting that as an outage after cancellation, but batch graph convergence did not. `ConvergeBatchDoesNotReportShutdownAsDependencyFailure` reproduced the wrong health signal before the fix; its live-fault control passed. Commit `10d20dde` adds the cancellation check to that health report while preserving failure logging and recovery control flow. Both rows then pass. This central-only change does not execute in W2.

A second concrete sync interaction came from upstream camera-operation read-history fixtures. They left synthetic graph executions unsealed in the shared Integration database. The window tests' existing worker-isolation update then correctly hit the SQL guard against advancing an unsealed graph. Fourteen such failures were retained before that invalid run was canceled through its exact owned process; the process and testhost both exited. `ReadHistoryFixturesDoNotPoisonWorkerIsolation` reproduced the same rejection on a fresh database before correction. Commit `c1e8f3b0` declares the fixture's actual frozen node/output counts, inserts terminal history nodes, and completes the normal sealing/terminal transition. The regression then passes. No production trigger, worker-isolation helper or health assertion changed in this correction. Its affected builds are warning-clean, formatting passes, and all eighteen Docker-disabled supplemental Unit cases pass.

## Guard, validation and custody

The guard rejects a median increase above the declared margin and fails on invalid pack modes, incomplete pairs/operations, too few samples, failed tests, wrong binary revisions, identical comparison revisions and collectors in measurement data. Eleven self-tests pass, including a +13.6% median with equal arm maxima; the two hardening cases were recorded failing before the fix.

Target profile is `development-v1`; classifier output and the complete Tier C local gate are recorded in the PR ledger. This work does not dispatch full ci.yml or claim production qualification. Raw packs have verified SHA256SUMS; custody and final gate outcomes follow.

Raw custody is complete at `/home/roys/development-state/HVO.SkyMonitor/evidence/1540/w2-20261022-c1971fbf-26ae1b9b`. The portable analyzer reproduced the original attribution JSON byte-for-byte. All 314 payload entries verify against `raw-payload.SHA256SUMS`, whose SHA-256 is `7314978b8c9f6eca9b9b9af539c5f29bfededa5270a0a2c8672ba25190f9f4e0`. The custody manifest covers the immutable raw/derived data, catalog and analysis tools; subsequently appended gate/review records are separate. Frozen source worktrees and original scratch remain preserved for slice B.

The nested measurement manifest has SHA-256 `611b65fe012061a8fd03e7326f3ac33dee4ec3e8440afcc596df6e9e3078af51`; the attribution manifest has SHA-256 `1d9c43d68fe0772908979683690fc80126a68583288e4b9882f2c93476ba272b`. Both manifests and the durable copy were verified successfully.

## Final local candidate evidence

The selector is `scripts/ci:classify pull_request <review-merge-base> <review-head>`: full mode, complete matrix, shared/CameraAgent/LogicHost/combined/delivery selected; deployment/catalog/shards/installer/release-contract flags false. The protected target profile remains **development-v1 / Preflight and Build and Unit**. The local Tier C gate and the additional canonical coverage evidence do not claim production qualification or authorize a full `ci.yml` dispatch.

- Tool restore and solution restore passed. Full solution Debug/Release builds on `c947a631` passed with zero warnings/errors, followed by full format verification. Later test-only corrections received affected Debug/Release warning-as-error builds and format verification. Production source is byte-identical from `c947a631` through `abd72b63`; the later corrections do not invalidate the other successful component receipts.
- The complete positive Unit selection passes with the invalid Docker endpoint. The explicit final receipt matrix has **12,925 passed, 3 skipped, zero failed; 12,928 total**. This includes catalog-enabled local reruns of the installer/distribution selections, which executed 121 cases skipped by the default v1 environment. The remaining skips are a cross-catalog-lineage case requiring an additional HYG v4.4 bundle, an unreachable provider state, and a macOS-specific guard on Linux. These skips remain recorded. Processing's current 639 Unit cases include nine new malformed-payload, declared-identity/media/schema, capture/scene-mismatch and valid-binding controls; production behavior did not change for this coverage correction.
- The complete Integration selection has **896 passed, zero skipped/failed**. The final affected full projects are standalone 35, combined 18 and LogicHost 547; their results replace only their explicitly identified older receipts. The other four successful Integration selections remain retained. The seven architecture/provider-boundary/publish cases passed in that selection.
- All four post-sync control guards, the package audit, documentation audit, canonical migration-source checks and both EF pending-model checks passed. Package/toolchain pins are unchanged across synchronization. Focused driver Bash syntax/ShellCheck and all eleven median-guard self-tests pass.
- The canonical coverage merge binds exactly **23 distinct full-project reports** from the workflow's slot list. Each attachment is tied to its TRX/project/category and staged under a plain filename with a matching SHA-256. The supplemental Unit and focused diagnostic runs supply no canonical coverage. The combined Unit slot was refreshed so every LogicHost-bearing canonical report covers the corrected runtime source. The unchanged coverage gate passes: **89.2968% line (187,842/210,357), 75.2509% branch (88,021/116,970)**, including every file floor.
- Failed attempts are preserved rather than counted as passing evidence: the first complete Integration run's three failures and focused reruns; the deterministic read-history fixture failures and canceled invalid run; failing-before/passing-after regression logs; first failed builds; and coverage-binding failures. MSTest renamed three colliding attachments with `[1]`; ReportGenerator interpreted those brackets as patterns and dropped them. Explicit checksum-matched staging restored all 23 inputs. The remaining installer floor needed the approved catalog bundle, and the annotation floor needed the new validation controls. No threshold or production guard was weakened.

The gate directory contains `candidate-test-receipts.json`, `canonical-coverage-inputs.json`, exact commands, source-head fingerprints and all original logs. Gate records and the final report are retained separately alongside the immutable raw payload at the custody location above. Independent review must still disposition historical F3 and the complete synchronized feature. Slice B retains the remaining feature acceptance and final synchronization/CI obligations.
