# Projected scene storage contract (#1055)

New scene-bearing captures use one explicit enabled `ProjectedScene` node. The
node consumes `$raw`; every `Annotation` consumer declares that scene node in
`dependsOn` and sets `requireProjectedSceneDependency: true`. Storage steps that
upload scene metadata or scene-bearing images also declare the scene dependency. The shipped VirtualSky
profiles include these declarations. Configure the node before starting new
captures; the acquisition graph rejects missing, disabled, or duplicate scene
nodes and incomplete annotation dependencies.

Raw acquisition stages the projected geometry before it publishes immutable raw
evidence. New `SceneProvenance` records retain reproducibility identifiers and
`projectedSceneSchemaVersion: "projected-scene-v1"`, with no inline `objects` or
`segments`. This applies before raw manifest serialization, so raw journal rows,
derivative descriptors, filesystem sidecars, and queued manifests agree. The
existing staged/committed scene protocol binds the canonical product to capture
ID, raw artifact ID, and raw descriptor SHA-256. Successful product commitment
releases the transient stage. Archive destinations enqueue the committed product
from the canonical raw root; they do not publish additional scene payloads.
The ordinary upload lane also queues this required dependency whenever it uploads
compact raw, including supported pipelines without any Storage node. It enqueues
raw first, then defers without consuming failure attempts until the canonical
scene commits. The existing required lane retains raw during that wait. A bounded
capture/schema lookup rejects ambiguous products; payload, sidecar and raw-source
identity are authenticated under the storage lifecycle gate before enqueueing.
Both publishers use the committed producer identity and identical manifest bytes,
so retries and optional Storage publication converge on one outbox record.
Optional archive upload policy cannot suppress a compact raw upload's required
scene dependency. Storage image publishers likewise enqueue the canonical scene,
even when the optional metadata upload policy is false or the ordinary upload
lane is disabled. Legacy image manifests already transported geometry under that
policy; the required dependency preserves the same information flow. With no
image upload, the optional metadata mask still suppresses independent scene
publication. Canonical image publication retains the committed producer identity
so its outbox manifest equals its immutable sidecar. Legacy inline captures retain
their raw-only lane behavior.
A scene-specific retention guard checks remaining canonical consumers and every
Storage root recorded in the capture's live/replay graph revisions. Those
revision and execution rows survive canonical output expiry, so old archive
copies and later replay publication remain visible after configuration changes.
Archive discovery is cached per root/day and validates physical root confinement.
It allows at most `min(ReconciliationBatchSize, 512)` scene candidates, 262,144
directory entries, 131,072 sidecars, and 1 GiB of JSON reads per pass, with a
16 MiB individual document bound and cancellation. Exhaustion or
unsafe evidence fails closed through the existing retention failure reporting.
A small mutable journal cursor conservatively defers already checked candidates;
it never authorizes deletion from partial discovery. A complete pass clears the
cursor. Production-workload headroom and many-manifest costs still require the
before/after evidence below. The 10,000-sidecar workload completes without a
budget cursor; the selected local candidate gates passed as recorded below.

Retained old manifests are read in their original representation. Recovery and
archived replay recreate the recorded graph without imposing new-acquisition
configuration requirements. Existing immutable payloads, manifest bytes, hashes,
and journal/outbox identities are not rewritten. Replay with a scene dependency
uses its pinned committed product and fails closed if it is missing or altered;
it does not regenerate geometry from today's catalog. For compact captures,
replay also requires the same output variant and immutable scene artifact
identity. A changed variant or recipe identity fails before durable publication
instead of creating another geometry artifact. Transient focus previews
continue carrying geometry because they are not persisted capture evidence.

Central ingest compares scene facts independently of the inline/compact
representation while rejecting inconsistent identifiers and conflicting reported
geometry. A new compact capture's annotation waits for an available source-bound
projected product, regardless of image/metadata arrival order. The scheduler
freezes its artifact identity, checksum, scene identity, source descriptor hash,
and annotation identity in the existing immutable input requirement and input
rows. The reference fits the existing 2 KB selector field; no database migration
or new central graph binding is required. Existing input retention, invalidation,
lease, and frozen input-set rules apply. A compact-only retention reference also
keeps the scene while any non-expired image consumer remains in its exact central
frame, after jobs finish. The hold uses existing rows and reservation transaction
fencing; it clears after those consumers expire and leaves legacy inline captures
on their existing retention rules. Post-completion hold and ordinary release-query
budget regressions passed. Raw identity must be authenticated before
selection; raw arriving later leaves selection waiting. Executor and processing-runner
paths resolve the same pinned scene bytes and embedded raw descriptor hash after
selection, without requiring an undeclared raw pixel retention dependency. The metadata input does not become an image-kernel
input or change legacy image output lineage. A previously frozen legacy decision
to omit annotation stays frozen when later provenance arrives.

## Validation record

SDK 10.0.401 bounded Release builds of AgentCore, CameraAgent and LogicHost test
roots passed with `-warnaserror -maxcpucount:2`. Positive Docker-disabled focused
Unit evidence covers 47 AgentCore tests, 28 CameraAgent tests (21 first-run
passes plus seven passing fixture corrections), and three LogicHost tests, all
without skips. Logs and TRX identities are recorded in the issue ledger.
Additional focused retention/replay contract checks passed 13/13, AgentCore
compact-contract validation passed 1/1, and explicit-v2 structured scene upload
validation passed 1/1. The focused central source-freeze/expiry/identity test also
passed 1/1, all without skips. The 17-capture expiration test retains
every scene while archive consumers exist, then deletes 16+1 across bounded
passes after their payloads expire. Retained unbound VirtualSky recovery and the
existing binding regression passed 2/2. The SQL-backed arrival-order matrix passed
8/8 (four orders across legacy and graph scheduling), including durable waiting
responses, retries, frozen scene retention/recipe identity, and exact annotation
pixels. The first SQL run exposed missing requirement hydration in a lease query
and missing explicit EF insertion for the new graph input; both were corrected
without weakening immutable-input guards. The original complete Unit and Integration attempts exposed fixture and central
compatibility defects; their failed logs remain retained alongside individual
passing corrections. Canonical 23-slot coverage and component floors passed,
including seven additional malformed compact-contract cases. Full deployment
qualification and the affected upload/Storage correction gates passed. The selected
aggregate is Unit 5,825 passes plus seven existing skips (5,832 cases), Integration
693/693 with no skips; canonical coverage is 86.7439% line and 71.6908% branch.
The full-range classifier selects the complete local matrix and deployment shards;
the final Storage delta selects CameraAgent and combined-host gates. Unchanged
passing source is bound by Git-object proofs, and replacement TRXs are explicit.
Independent immutable review is still required. Twelve focused upload checks
passed with no skips:
four real VirtualSky/ingress/lane/SQLite-outbox cases cover both arrival orders
with and without Storage, plus legacy raw-only and two-archive compatibility; six
Storage-only raw/preview cases cover optional metadata=false, disabled image
upload, and missing explicit scene dependencies rejected before capture.
The cases verify deferred retry budgets, retention holds, repeated enqueue,
unaltered evidence bytes, payload/sidecar tampering and raw-source mismatch. Before/after measurements are
recorded below. Neither this document nor the preserved September prototype constitutes
qualification evidence.

Performance evidence must compare the same fixed production-catalog capture
workload on the initial baseline and candidate. Record raw/derived manifest and
journal bytes, scene payload counts, checksums and geometry/output equivalence,
process I/O, allocations/working set, elapsed time, and a fresh-process recovery
run. Existing large-scene histories remain compatibility fixtures; they are not
rewritten into a claimed producer benchmark.

## Identical production workload harness

`tests/HVO.SkyMonitor.CameraAgent.Tests/Issue1055SceneCompactionEvidenceTests.cs`
is the same source in candidate and the baseline harness-only branch. Its current
SHA-256 is `6e8c8d4dd784b47e42158a3e91664b8b5ae1940edbc371b8b7ee002e11d70c62`;
baseline commit `24a60a01f7e0095481fe355ca7b623f5d013cd02` retains production
`fead5666bae05f3a51affee94847de93141c56cd`. The earlier harness-only commit
`1021fc798c0f6b6dd7af95bb81e16a651e7fdbab` was corrected before any workload run. A later whitespace-only harness commit
normalizes repository formatting. The first successful pair used harness SHA
`16021f6286bea3c7893faf3209cd8925a1bb26e6e89a6f95bba4c9e9e16ed2e9`.
The current identical harness adds GC statistics to investigate its transient RSS result.

The harness also renders each retained scene through the real presentation-layer
producer and compositor outside timing, recording a raster checksum independent
of capture GUIDs. This checks layer pixel equivalence alongside annotation output.

The harness uses real VirtualSky capture, raw ingress, durable standard-lane graph,
scene/preview/annotation kernels, Storage-driven canonical raw upload and archive
derivative publication. It does not invoke the ordinary raw upload lane. Central integration is enabled; no hosted uploader runs. The baseline
explicit-v2 validator rejects every metadata upload, so both workload graphs use
identical policies that enable image outbox records and disable optional metadata
upload. Initial candidate pairs omitted required scene delivery. The updated pairs
below include it under the same unchanged graph: compact image upload now queues
the required canonical scene, preserving the information previously carried inline.
The separate two-destination structured-upload test verifies canonical metadata
outbox identity and the absence of archive geometry copies. The candidate accepts
the supported scene upload in explicit-v2 configuration.

`HVO_ISSUE1055_MODE` selects `capture`, `cold`, or `retention`; run these in order
against one disposable `HVO_ISSUE1055_ROOT` per revision. Historical archive roots
are immutable absolute paths, so do not move or rewrite that root between modes.
`HVO_ISSUE1055_OUTPUT` must name a fresh JSON file outside the root, and
`HVO_EVIDENCE_REVISION` must equal the clean worktree HEAD and Release assembly
revision. `HVO_ISSUE1055_EVIDENCE=1` enables the explicitly scheduled Manual test.
Capture additionally requires `HVO_ISSUE1055_CATALOG_ROOT` with the verified
119,625-row production HYG catalog. Capture and retention use five warmups plus
30 measured samples. Cold initialization runs in a fresh test process.

Retention preparation creates 10,000 foreign-capture sidecars sharing one raw
fixture payload copied once outside timing. It deliberately expires canonical
raw/derived pixel files in the disposable fixture while retaining archive
consumers. Raw identity remains bound to the immutable journal manifest.
Per-operation CPU, allocation, working set and process I/O exclude preparation;
aggregate totals explicitly include it. This measures canonical scene discovery
under same-day metadata volume, not an end-to-end retention policy cycle.
The initial successful pair (baseline `384c7114`, candidate `78371b75`) passed
raw/derivative pixels, projected geometry, real layer raster pixels and provenance
comparison. Random stage lease keys differ between independent captures and are
excluded from cross-run reproducibility comparison; each scene remains checked
against its own immutable raw descriptor. It retained 70 versus 35 scene payloads
for 35 captures, with zero candidate inline geometry in frame/outbox/journal
records. Raw journal manifests fell from 1,978,122 to 166,197 bytes and product
descriptors from 4,071,805 to 447,955 bytes. Capture median was 839 versus 593 ms.

The initial candidate peak RSS rose from about 357 to 494 MB, although later
samples returned to about 329 MB. The reversed-order repeat (baseline `24a60a01`,
candidate `5872b69b`) moved the high peak to the unchanged baseline: 493 versus
360 MB. Its GC committed heap reached about 298 MB, compared with about 137 MB
on the candidate, then shrank. This attributes the transient to GC heap sizing
and collection scheduling; it is not evidence of a persistent candidate leak.
Both pairs are retained. Capture latency also varied: the second pair measured
850 versus 877 ms median and 932 versus 1,017 ms p95, so this evidence does not
claim an unconditional throughput improvement. Managed allocations fell slightly
in both pairs; measured physical writes fell from approximately 513 to 481 MB
and write calls from about 25,900 to 18,900 for 30 measured captures.

Fresh-process initialization measured 553/585 ms on the baseline and 362/377 ms
on the candidate, with 30.3 versus 13.4 MB allocated. These are individual
observations, not percentiles or OS cache-cold startup claims. The first retention
run safely rejected valid numeric storage-policy enums in historical options;
the corrected reader matches the pipeline factory's string/numeric enum semantics,
with both forms in the historical-root regression.

The corrected W3M workload passed with 10,000 same-day sidecars. Every candidate
pass retained all 35 scenes required by archive consumers and completed with no
cursor backlog. Median/p95/max discovery cost was 1.044/1.425/1.817 seconds on
one CPU, about 33.7 MB logical reads and 255 MB transient managed allocations per
pass, with 223 MB median and 231 MB maximum RSS. The baseline measured 2.7 ms
because its canonical scenes had expired while duplicate archive geometry
remained. This is an explicit cost of protecting the one remaining geometry
product. It fits the supported workload under the configured bounds and the
default 30-minute retention sweep. The 17-capture Unit test separately proves
bounded progress and deletion after every archive consumer disappears.

The complete numbers, both capture pairs, GC samples, output-equivalence results,
and raw log/TRX/JSON SHA-256 index are retained in
[the measurement summary](scene-compaction-1055-measurements.json). The exact
[comparison script](scene-compaction-1055-compare.py) reads capture JSON and both
immutable journals; run it before retention preparation. Failed
attempts remain indexed and are not counted as passing measurements. The measured CameraAgent endpoint is `5872b69b`; subsequent changes add a
module-type validation-cache key and its regression, central consumer retention,
and canonical-scene delivery from the ordinary raw upload lane. The shared scene
publisher now authenticates committed payload/sidecar/source evidence before
enqueue. These changes do not change this workload's geometry or storage
representation, but their costs are not included in these timing samples:
optional metadata publication was disabled identically and the ordinary lane was
not run. Updated Storage-path measurements follow. Local qualification is complete;
independent review and final target synchronization remain required before merge.

## Required Storage dependency delivery: final production measurements

Fresh pair 4 and reversed capture-only pair 5 compare baseline `24a60a01`
(production `fead5666`) with qualified candidate `a4f834e5`, using the identical
GC-instrumented harness SHA above. Source and assembly hashes are frozen in the
issue evidence. Each capture run uses five warmups plus thirty samples, one CPU,
and no competing builds/tests. Pair 4 also repeats fresh-process ingress recovery
and the 10,000-sidecar retention workload. Pair 5 investigates one long sample;
it does not replace the first pair. Both output comparators passed.

The candidate queues 140 records: 105 compact images and 35 authenticated canonical
scenes. The baseline queues 105 images containing inline geometry. Canonical scene
payloads remain 70 → 35; outbox manifest bytes are 5,966,566 → 701,309, and total
retained file bytes are 535,196,965 → 509,830,238. Raw journal, derivative descriptor
and frame sidecar reductions remain as above, with zero inline geometry in the
candidate journal. Thirty measured captures write 513.3 → 486.4 MB and make
25,897 → 20,329 write calls in pair 4.

| Capture measurement | Pair 4 baseline → candidate | Reversed pair 5 baseline → candidate |
| --- | --- | --- |
| Median elapsed | 850 → 570 ms | 916 → 1,003 ms |
| p95 elapsed | 1,117 → 913 ms | 1,043 → 1,271 ms |
| Maximum elapsed | 1,186 → 2,050 ms | 1,056 → 1,278 ms |
| Median CPU | 730 → 459 ms | 798 → 821 ms |
| Median allocated bytes | 270.3 → 285.9 MB | 271.8 → 289.7 MB |
| Peak process working set | 500.4 → 419.7 MB | 364.2 → 485.3 MB |

Required scene delivery adds a repeatable 15.6–17.9 MB of transient allocation per
capture (5.8–6.6%). Its new path restores, parses and authenticates the canonical
scene for both Storage publishers, while the outbox retains one record. This cost
is included in these measurements. Pair 5 has 9.5% higher median and 21.8% higher
p95 wall time, with 2.9% higher median CPU; no unconditional throughput improvement
is claimed. The pair-4 2.050-second sample did not recur, and its 15.791 ms GC pause
delta does not explain it. That sample remains an unclassified timing outlier.
Peak RSS varies by order; final live heaps were 83–86 MB candidate and 88–95 MB
baseline, without retained-heap growth across the workload.

Fresh-process ingress initialization was 607 → 395 ms, allocating 30.23 → 13.46 MB;
working sets were 200.68 → 200.39 MB. These are individual observations, not full
host/cache-cold percentiles. Candidate W3M retained all 35 canonical scenes with
no budget cursor; its median pass cost was 926 ms and 255.24 MB transient allocation
versus the baseline's 2.82 ms and 62 KB after its archive scene copies allowed
canonical expiration. The direct expiry API receives empty external holds to
exercise archive discovery; normal outbox/queue/window retention aggregation is
outside that measurement. The separate bounded deletion and durable-hold tests
remain the correctness evidence for those paths.

## Independent review corrections and performance disposition

The initial exact-range review (`fead5666..e955733a`) identified late inline
provenance replacing compact authority, new replay being blocked after raw expiry,
and insufficient performance attribution. The implementation correction is
`c36beaba`; the focused SQL selection passes all thirteen cases with no skips.

Mixed inline/compact arrivals now authenticate inline geometry against the
source-bound canonical product before acceptance. Missing canonical metadata
returns retryable HTTP 425; altered, extra or missing geometry conflicts. Once
established, compact authority cannot be replaced by inline enrichment. Existing
inline authority and previously frozen jobs also remain unchanged. The eight
arrival-order cases submit late inline provenance, execute annotation and verify
that completed jobs do not release the scene while compact consumers remain.
Four further cases cover both representation orders and altered/empty geometry.

New calibrated-source annotation replay can select the exact authenticated raw
identity after its genuine retention tombstone. Pending, rejected or arbitrarily
expired rows remain insufficient. The SQL regression first proves the existing
rolling-window hold, cancels it through the supported service, expires raw through
real retention, then creates and executes a new replay with identical pixels and
frozen scene/recipe identity. Raw pixels are not an undeclared replay dependency.

The subsequent expiry audit reproduced a scene status retry returning HTTP 425
after authentic raw retirement. Ingest, status retry, background reconciliation
and scene selection now share the same narrow source-eligibility predicate.
Only projected-scene metadata may use an expired raw identity with a retention
token, completed reconstruction and preserved publication verification. Other
products still require available source pixels. Retirement preserves prior
verification for already-available legacy rows but cannot add it to unpublished
intents; no schema or frozen-input mutation is needed. Three focused SQL cases
cover status retry and background repair, metadata arriving after authentic expiry,
and a real failed-copy intent retired without acquiring publication evidence.
The original HTTP 425 reproducer and the initial thirteen-case proof are retained.
The extended scene selection contains fifteen SQL cases.

### Five paired process trials

The [predeclared protocol](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1159#issuecomment-5997066493)
uses five independent process pairs, alternating baseline/candidate order, one
CPU, five warmups and thirty measured captures per process. Baseline `b89c1a01`
retains production `fead5666`; candidate `c36beaba` has exactly the same edge
production sources as `a4f834e5`. The identical optional harness probe has SHA-256
`13a0b64fbae5ac42445692b5cf4ce121521fe102cf46563ffe5788a7b06fa70c`.
It records capture, ingress, the complete processing lane, and all five node spans.
Node spans include persistence and overlap the lane span; they must not be summed
twice. Process CPU/allocation counters include background runtime work. Probe
cost is included symmetrically, making this a separate instrumented series.
All ten Manual runs and five pixel/geometry/layer comparators pass.

[The analysis script](scene-compaction-1055-stage-analysis.py) uses process means
as the units for paired differences and log-ratio Student-t intervals (five pairs,
four degrees of freedom). Within-process captures are not independent replicates.
Small sample size, approximate normality and temporal host effects limit the
intervals. Distribution and individual-stage intervals are exploratory, without
multiple-comparison adjustment. An interval including one does not prove
performance equivalence; no acceptance tolerance was chosen after these results.

| Measurement | Candidate minus baseline | Paired ratio, 95% interval |
| --- | --- | --- |
| Mean capture wall time | +64.2 ms, interval −80.1 to +208.5 ms | 1.080, 0.917–1.271 |
| Mean process CPU | +71.0 ms, interval −54.4 to +196.4 ms | 1.104, 0.934–1.304 |
| Mean allocated bytes | +16.35 MB, interval +15.71 to +16.99 MB | 1.060, 1.058–1.062 |
| Per-process median wall time | +81.5 ms | 1.099, 0.927–1.302 |
| Per-process p95 wall time | +211.2 ms | 1.209, 1.015–1.441 |
| Canonical Storage mean wall time | +32.0 ms, interval +10.2 to +53.7 ms | 2.467, 1.768–3.444 |

The allocation increase is localized: canonical Storage adds 9.12 MB and archive
Storage 8.49 MB per capture while compact ingress and other nodes save allocation.
These publishers restore and authenticate durable scene payload/sidecar/source
identity before converging on one outbox record. Their additional transient
allocation is a measured cost of that correctness contract. The complete lane's
mean difference is +11.6 ms (interval −46.5 to +69.7 ms); capture itself contributes
+64.7 ms with substantial variation. Measured writes consistently fall from
513.2–513.3 MB to 486.2–486.6 MB per thirty captures. Candidate peak RSS ranges
415.7–461.3 MB across the five processes; baseline ranges 349.8–514.5 MB.

### Runtime attribution and limits

Long samples are not confined to scene publication. Candidate trial 2's 2.734 s
sample contains 1.508 s in Preview (1.250 s process CPU); baseline trial 2's
2.383 s sample contains 1.505 s in ingress (1.075 s CPU). Candidate trial 5's
1.845 s sample contains 1.239 s in canonical Storage (1.188 s CPU). Their GC
pauses of 16–19 ms do not explain them.

A separate diagnostic pair used `dotnet-trace 10.0.745401` with runtime and sampled
thread events. Both captures and their comparator passed. The traces ended with
`Read past end of stream`; raw traces, conversion warnings and decoded events
are retained, and are not treated as complete stack profiles. Before that boundary,
the decoded events contain 918 candidate and 941 baseline matched background-JIT
bursts, totaling 2.10 and 2.22 seconds of burst wall time. Thousands of methods
remain queued near process end. This establishes ongoing tiered compilation,
not the cause of an individual earlier sample.

Two further diagnostic pairs changed only `DOTNET_TieredCompilation=0`, in opposite
orders. No application source or production runtime setting changed. Both output
comparators passed:

| Diagnostic pair | Mean baseline → candidate | p95 baseline → candidate | Maximum baseline → candidate |
| --- | --- | --- | --- |
| Baseline first | 611.8 → 609.9 ms | 642.8 → 648.1 ms | 649.6 → 656.4 ms |
| Candidate first | 635.4 → 635.5 ms | 696.7 → 705.4 ms | 707.1 → 735.3 ms |

The first control still shows canonical Storage increasing from 18.2 to 37.4 ms,
with other compact stages offsetting it. Allocation remains about 17 MB higher.
Together with the runtime events, the controlled runs support tiering as a material
contributor to the short-process variation. They do not replace default-runtime
results, establish production steady state, or retroactively identify the original
2.05-second sample's exact cause.

The disposition is an explicit cost tradeoff: retain the approximately 6% transient
allocation increase and required-publication stage cost in exchange for the single
authenticated durable scene, smaller immutable metadata and lower write traffic.
The default-runtime p95 increase remains a reported startup-tail regression;
there is no unconditional speedup or equivalence claim. All observed default
captures finish within 2.74 seconds. The harness advances timestamps by 25 seconds
but submits captures back-to-back without wall-clock pauses; it measures
accelerated service cost, not a host operating at that cadence. On one CPU,
capture work and background compilation compete throughout this short process.
The runtime controls diagnose that interaction without proving how much
compilation would finish between live captures. Neither cadence arithmetic nor
the controls establish a production SLO or full-host/backlog qualification.
Original pairs, outliers, W3M retention costs,
profiler limitations and all raw evidence remain retained. Independent correction
review must assess this disposition before convergence.
