# Projected scene storage contract (#1055)

New scene-bearing captures use one explicit enabled `ProjectedScene` node. The
node consumes `$raw`; every `Annotation` consumer declares that scene node in
`dependsOn` and sets `requireProjectedSceneDependency: true`. Storage steps that
upload scene metadata also declare the scene dependency. The shipped VirtualSky
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
budget cursor; full candidate qualification remains pending.

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
on their existing retention rules. Its post-completion tests are pending the next
local gate slot. Raw identity must be authenticated before
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
without weakening immutable-input guards. The complete Tier C candidate gate
and independent immutable review remain pending. Before/after measurements are
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
scene/preview/annotation kernels, canonical raw upload and archive derivative
publication. Central integration is enabled; no hosted uploader runs. The baseline
explicit-v2 validator rejects every metadata upload, so both workload graphs use
identical policies that enable image outbox records and disable metadata upload.
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
attempts remain indexed and are not counted as passing measurements. Full
qualification and independent review remain required before merge.
