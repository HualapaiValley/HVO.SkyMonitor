# #1055 Read-Only Scene Deduplication Prototype

Measured 2026-09-26 on the two existing CameraAgent instances. This is a read-only
model, not a migration or a change to capture output. The queries opened the SQLite
main file with `mode=ro&immutable=1`; this deliberately ignores live WAL changes.
No database, sidecar, or projected-scene product was modified.
All counts, status summaries, allocations, and observed-hour rates below describe
the SQLite main file only, not uncheckpointed WAL rows or updates.
`ReconstructableCaptureContractTests.ManifestV2_CompactSceneReferencePrototypePreservesLegacyGeometryAndDescriptorIdentity`
constructs old and geometry-free v2 manifests in memory: both parse, scene
identifiers survive, descriptor identity is unchanged, and the old manifest
round-trips byte-for-byte. AgentCore Debug `-warnaserror` build and 68/68 tests
passed. This proves parser compatibility only; no production capture writer,
LogicHost ingest, or annotation path was changed.

| Instance | Main-file captures | Raw journal | `processing_outputs` pages | `raw_captures` pages |
| --- | ---: | ---: | ---: | ---: |
| `9812e471` | 1,988 | 2.782 GiB | 2.028 GiB | 501 MiB |
| `c357af1c` | 1,726 | 2.414 GiB | 1.759 GiB | 435 MiB |

On `c357af1c`, `json_remove(manifest_json, '$.scene.objects',
'$.scene.segments')` reduces the mean raw manifest from 262,531 to 5,912
bytes, removing 442,925,141 bytes across 1,726 records. The same query on the
6,906 scene-bearing `processing_outputs.descriptor_json` values removes
1,772,212,814 bytes. Together, the raw manifests and large descriptors contain
2,215,137,955 removable logical JSON bytes, approximately five copies per
capture on average (two descriptors beyond four per capture). The corresponding counts on
`9812e471` are 1,988 captures and 7,952 scene-bearing output descriptors;
the latter alone contain 2,043,255,416 removable bytes. These figures are
logical JSON bytes, not immediately reclaimable SQLite pages. A rewrite would
invalidate immutable manifest hashes and is not proposed.

At one capture every ten seconds, 30 days represents 259,200 captures. A linear
model based on both main-file allocations gives roughly 363 GiB of raw journal
at today's format. Subtracting the observed duplicate logical JSON rate yields
roughly 53 GiB as a sensitivity calculation, **not** a measured or predicted
SQLite file size after compact writes. Retention, page packing, indexes, WAL,
output mix, and other per-capture records can change. Eight large raw/archive frame
sidecars per capture would also shrink, but they are separate from the DB and
must not be rewritten in place. Eight typed Metadata JSON payloads per capture
are not included in that estimate. For one inspected capture, the canonical
projected-scene payload is 1,590,160 bytes; its seven other distinct Metadata
payloads are 30,466 (constellation layer), 10,476 (annotation layer), 6,134
(metadata facts), 5,732 (overlay manifest), 1,390 (environment layer), 893
(cardinal layer), and 466 bytes (image-circle layer). These are separate
compositable products, not seven additional full star-scene copies.

Every measured `c357af1c` capture has exactly one available
`projected-scene-v1` processing output. One inspected canonical product is
1,590,160 bytes (277,404 bytes, 0.265 MiB, at zstd level 3), with 2,003 objects and 190
segments. All object IDs, projected coordinates and magnitudes, and all segment
identities and endpoints matched the same capture's inline provenance within
`1e-8`. That sample proves a candidate replacement source exists, not that every
historic product is available or geometrically equivalent. The product's
source/capture identity and checksum must be verified when used.
Scaling that one compressed product size to 259,200 captures illustrates about
67 GiB of separate product payloads. This is not part of the journal sensitivity
calculation and is not a new cost caused by compact provenance; the product is
already retained. One sample does not establish its size distribution.
An every-100th-sequence sample across the review main file covered 18 captures:
all 18 product paths existed, all stored product scene identities matched the
database identities, UUID-normalized source capture IDs matched, and object and
segment counts matched. Comparing by stable object/segment IDs found zero
coordinate or magnitude differences greater than `1e-8` across 36,051 objects
and 3,393 segments. This does not prove equivalence for unsampled captures or
validate the complete product through `ProjectedSceneJson.Parse`. Display names,
rendered annotations, and frozen recipe identities were not compared.

The main-file `dbstat` allocation on `c357af1c` is 1,801.6 MiB for
`processing_outputs`, 434.6 MiB for `raw_captures`, 74.2 MiB for
`processing_executions`, and 47.3 MiB for `processing_execution_nodes`.
All 1,726 `capture_lane_work` rows are `completed`, all 18,988 processing
nodes are `Completed`, and all 20,714 processing outputs are `Available` in
this main-file view. These are historical results co-resident with mutable
coordination, not evidence that every 30-day capture can be sealed promptly.

The 1,726 captures span 14 observed UTC hours (36–144 captures per hour,
average 123.3). Their raw manifest plus processing-output descriptor JSON
averages 156.7 MiB per observed hour, with a maximum of 188.3 MiB. A
continuous ten-second cadence instead produces 360 captures per hour and 720
hourly periods over 30 days. Hourly SQLite shards or immutable manifest/day
indexes should be sized and benchmarked at that density, not the observed
partial-hour density. Late replay, delivery, and retention must remain
versioned mutable facts or delay sealing; combining closed shards daily would
rewrite history and recreate the upgrade-copy cost.
An hour cannot be sealed merely because all its current nodes are Completed:
`raw_captures` later changes evidence state and retention holds, and
`processing_outputs` later changes availability or is removed with its node.
Replay can pin an old capture and produce or change output facts after its
exposure hour. A sharded prototype should seal **immutable capture facts** and
keep late work/status in a writable projection. Gallery keyset pagination by
capture sequence, product pagination by output commit time, calendar time
ranges, and cross-capture processing windows must retain their current semantics
across shards.

## Compatibility Gates

- Do not strip producer geometry yet. LogicHost's `CentralDerivativeJobExecutor`
  constructs annotation input from `SceneProvenance.Objects` and `Segments` and
  skips when both are absent. `CentralProcessingGraphSchedulerTests` already
  verifies that geometry changes the frozen annotation recipe identity and that
  provenance without geometry produces no annotation. A compact producer alone
  regresses central output; product lookup must be source-bound and resolved
  before that identity is frozen.
- Edge `Annotation` can use a declared projected-scene dependency or an in-memory
  scene, but graphs without that dependency rely on inline legacy geometry after
  restart. Require and test the dependency before compact output is admitted.
- Existing manifests, output descriptors, and sidecars must remain byte-for-byte
  unchanged. The v2 parser accepts the optional lists, so old captures remain
  readable; new writes need cross-host annotation and lineage equivalence first.
- The stage key is transient. Replay must use the source-bound committed product,
  not assume the stage still exists after processing commits.

## Smaller First Slice

Keep geometry in the **raw** manifest, raw sidecar, and raw journal row, while
omitting only `Objects` and `Segments` from newly persisted pixel-derivative
manifests. On `c357af1c` this targets 1,772,212,814 logical descriptor bytes
(6,906 output rows, 256,619 bytes per row on average), about 80% of the
measured five-copy DB opportunity. The scene's reproducibility identifiers
would remain in each derivative. The matching filesystem sidecar and the
SQLite `descriptor_json` must be serialized from the same compact manifest;
the committed output restoration code requires byte equality.
Scaling only this derivative-descriptor logical saving to 259,200 captures
yields about 248 GiB of avoided repeated JSON. That is not a forecast of
reclaimed SQLite allocation or a claim about filesystem sidecars.

This slice is not yet safe to enable for central delivery. `FileStorageCaptureProcessingStep`
also constructs upload manifests, and the outbox requires their bytes to match
the physical sidecar. LogicHost currently compares any two non-null scene
provenance JSON strings for the same frame exactly; a compact derivative would
conflict with a full raw scene, including when the derivative arrives first.
The central ingest must accept identifier-equivalent compact provenance without
discarding full raw geometry, in both delivery orders, before changing writers.
Existing committed output rows and sidecars must never be rewritten.

Next prototype: on a disposable copy of representative scene records and
products, bind a compact provenance reference to its immutable product and
compare edge and central annotation output and recipe identities. Only then
consider changing new capture writes. Keep SQLite for mutable schedule, lanes,
leases, and outboxes while evaluating immutable day/hour history separately.

## Disposable History Layout Benchmark (CameraAgent Only)

Run `python3 scripts/scene-history-prototype.py --captures 360
--payload-samples 30 --payload-mib 2` from the repository root. It refuses an
existing output root and creates only new disposable files under `/tmp` by
default. No installation database, artifact, instance, or schedule is opened.
Seed 1055; one hour at 10-second cadence for each dimension: W1 ASI174
1936 x 1216 and W6 ASI676MC 3552 x 3552. The baseline is a single SQLite
history table with a 256,000-character synthetic raw geometry field repeated
in four derivative descriptors. The candidate retains that raw geometry once
per capture in `hours/00000.jsonl`, keeps four compact derivative facts there,
and indexes sequence/hour/byte offset/length/raw SHA-256 plus late-changing
status in `mutable.db`. Large opaque products go into `products/` with a
length/SHA-256/path reference. This is a **layout experiment**, not CameraAgent
serialization, a real scene sample, a retention implementation, or a migration.
The same 256 KB synthetic geometry is used for both dimensions: the W6 run
**does not model additional stars, longer vector overlays, or larger rasters**.

First run on this host (2026-09-26, SDK 10.0.401 installed, Python 3,
`/dev/sda1` with 157 GB free; output `/tmp/scene-1055-j6_y6_bp/runs`):

| Per 360 captures | W1 | W6 |
| --- | ---: | ---: |
| Baseline SQLite main file | 461,733,888 B | 461,733,888 B |
| Candidate mutable SQLite main file | 49,152 B | 49,152 B |
| Candidate immutable JSONL facts | 92,439,930 B | 92,439,930 B |
| Separately stored 30 x 2 MiB opaque products | 62,914,560 B | 62,914,560 B |
| Baseline random DB raw lookup, 100 ops median / p95 | 0.048 / 0.059 ms | 0.090 / 0.141 ms |
| Candidate DB + fact read + raw SHA-256, 100 ops median / p95 | 0.256 / 0.279 ms | 0.260 / 0.297 ms |
| 50-sequence keyset page, 100 ops median / p95 | 0.008 / 0.009 ms | 0.008 / 0.008 ms |

The baseline vs candidate lookup comparison is **not apples-to-apples**: only
the candidate hashes raw bytes; the baseline returns raw JSON without checking
its digest. All timings are warm-cache single-process timings, one run, with
no independent trial/noise estimate, no fsync per capture, no outage, and no
Docker or CameraAgent work. SQLite allocation includes page overhead; the
candidate's immutable bytes are still present and must be retained and
accounted for, not described as eliminated. At equal synthetic density a
straight-line 720-hour extrapolation gives ~309.6 GiB baseline main file,
~62.0 GiB candidate fact files, and ~34 MiB candidate mutable main file.
This is **not** a 30-day CameraAgent capacity forecast or backup estimate.
The previous ~363 GiB observed-allocation estimate and ~115 GiB
derivative-only sensitivity are different models; do not add or equate them.

At 259,200 captures, **each 1 MiB of retained per-capture metadata payload is
253.125 GiB**. A single 2 MiB overlay every capture is 506.25 GiB; a 5 MiB
overlay every capture is 1,265.625 GiB (1.236 TiB), *in addition* to the
facts, other metadata products, sidecars and image bytes. The run writes only
30 such payloads per scenario (60 MiB), so neither its disk total nor its
lookup latency represents a continuous large-overlay month. Dimension by
itself does not imply payload size: actual object density, tile counts, encoding,
compression and retention must be sampled from representative large-camera
captures. Current typed tile masks are bounded to 65,536 tiles, but vector
layers and separate projected-scene products can vary independently.

The prototype checks ordered 50-row pagination equality, reads randomly by
indexed offset, validates raw SHA-256 and one product SHA-256, and changes a
status in SQLite without rewriting the hour facts. It does **not** guarantee
atomic publication across the fact file and SQLite, directory fsync, crash
recovery, replay/retention races, older-hour late outputs, cross-shard time
queries, multi-camera isolation, or stable filesystem cleanup. Those are
required before production adoption. Keep `.bin`, `.jpg`, and artifact sidecar
`.json` outside upgrade backups regardless of this historical facts layout.
Next evidence should use real W6 capture products and compare distributions of
all metadata and sidecar lengths, then test a crash-consistent hourly seal and
late-state reconciliation on disposable state; do not rotate existing mutable
`raw-ingress.db` tables into immutable shards.

### Fault Injection And Sparse W6 Probe

`python3 scripts/scene-history-recovery-prototype.py` publishes three capture
facts per disposable hour with file fsync, directory fsync, atomic rename,
directory fsync, then one SQLite transaction indexing offsets and the sealed
file's SHA-256. Forced interruption after staging discards one unsealed file
and indexes zero rows; after rename, restart verifies the complete sealed file
and indexes three rows; after DB commit it indexes no new rows. A second
reconciliation is idempotent in all three cases. Updating a late status keeps
the indexed facts intact. Appending corrupt bytes to an indexed hour is
rejected rather than silently indexed. This is fault-*point* simulation, not
power-loss testing or proof of durable cross-filesystem transactions; product
files and capture acknowledgement are not yet bound into the protocol.

The focused Manual test
`ExecutionEvidenceExportPerformanceTests.W6SparseCatalogDisposableCaptureReportsMetadataPayloadAndSidecarSizes`
uses the actual 3552 x 3552 Bayer12-in-16 W6 config and a disposable
CameraAgent capture. Run with `dotnet test
tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj
--configuration Release --filter
"FullyQualifiedName~ExecutionEvidenceExportPerformanceTests.W6SparseCatalogDisposableCaptureReportsMetadataPayloadAndSidecarSizes"`.
Its deliberately **two-object in-memory catalog** does not represent the
119,625-row W6 production snapshot. At fixture UTC 2026-09-01T04:00Z one
capture published one Metadata JSON file (5,823 B), its manifest (2,344 B),
one raw sidecar (8,279 B), and one 25,233,408 B raw frame; it did **not**
produce a dense collection of overlay products. Output is under the test
binary's ignored `TestResults/issue-1055/w6-metadata-sizes.json` by default.
The result bounds only this sparse fixture, not per-frame metadata at high
catalog density or high-resolution overlays; it cannot validate the 2 MiB
stress assumption. An actual full-catalog standalone W6 graph with many
times/scenes and product-type distribution remains the required next size
measurement before selecting a 30-day disk budget.

### Verified Catalog W6 Capture Probe

Run the focused Manual test
`ExecutionEvidenceExportPerformanceTests.W6InstalledCatalogMultiSceneReportsMetadataPayloadAndSidecarSizes`
with `HVO_ISSUE1055_CATALOG_PATH` set to the installed, read-only
`hyg_v42.sqlite` snapshot. The test verifies pinned SHA-256
`B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2`,
schema 2, preprocessing 3, version 4.2 and 119,625 rows before taking any
capture. It uses the W6 3552 x 3552 Bayer config (`maximumResults=300`) and
three isolated captures at 2026-09-01 04:00, 08:00 and 12:00 UTC. Results
appear in ignored `TestResults/issue-1055/w6-full-catalog-multi-scene-sizes.json`.
The physical catalog is read only; all capture outputs are disposable.

| Scene UTC | Projected Metadata JSON | Metadata manifest | Raw sidecar | Raw Bayer payload |
| --- | ---: | ---: | ---: | ---: |
| 04:00 | 274,256 B | 2,346 B | 71,682 B | 25,233,408 B |
| 08:00 | 270,934 B | 2,346 B | 64,920 B | 25,233,408 B |
| 12:00 | 292,941 B | 2,346 B | 83,110 B | 25,233,408 B |

As a **straight-line sensitivity only**, the mean 279,377 B projected product
would be ~67.4 GiB over 259,200 captures; the ~73,237 B raw sidecar adds
~17.7 GiB, and the 2,346 B metadata manifest ~0.6 GiB. These are separate
filesystem files, not bytes saved by SQLite descriptor compaction or files to
include in an upgrade backup. Three scene samples do not define an upper
bound, a night/day distribution, retention behavior, or a 30-day forecast.
An earlier run varied only the request UTC, while W6 pins
`fixedSequenceStartUtc`, so it sampled effectively the same sky three times;
its 287 KB / 80 KB results and the previous ~89 GiB sensitivity are superseded
by the table above. This corrected run sets `fixedSceneUtc` separately on each
disposable module configuration. The W6 config limits projected scene results to 300 even though the verified
catalog contains 119,625 rows. This path published **one** Metadata product
per capture, not all layered overlays; therefore the operator's multi-MiB
overlay scenario remains unmeasured and the per-MiB sensitivity above still
applies. A representative graph exercising scene, cloud and other overlays
with the verified catalog must be measured before setting capacity budgets.

The additional focused Manual test
`ExecutionEvidenceExportPerformanceTests.W6InstalledCatalogProjectedSceneReportsTypedSceneOverlaySizes`
loads each of those committed projected-scene payloads and calls the same
`PresentationLayerProducers.FromProjectedSceneGroupsV2` and
`PresentationLayerPayloadJson.Serialize` used by the CameraAgent scene-layer
step. It does **not** exercise graph scheduling, publication, cloud or
environment layers. In the three corrected scenes, object counts are
307/312/319, constellation segment counts 146/110/195; serialized
star-annotation layers are 8,302/8,628/9,852 B, constellation layers
23,685/17,989/31,530 B, cardinals 903 B, and image circle 446 B. Thus the
typed vector scene overlays are small for this bounded W6 fixture; pixel
dimensions do not force a full-resolution raster overlay payload. This does
not bound other recipes, other cameras, or a higher `maximumResults` setting.

**Recommendation at this evidence level:** keep large immutable products and
artifact sidecars as separately retained, content-identified files; retain
only compact references, offsets and late-changing coordination in mutable
SQLite. Keep full raw scene provenance while central ingest requires it; do
not compact production derivatives until upload/sidecar/ingest compatibility
is established. Prototype immutable historical facts separately from mutable
replay/retention state; do not rotate the existing mutable SQLite tables or
include image/metadata artifact files in upgrade backups. No 30-day capacity
or safe deployment guarantee follows yet: the remaining qualification needs
real graph-published cloud/environment/overlay products, a representative
larger `maximumResults` workload, persistent product/checksum reconciliation
across acknowledgement, replay and retention, and a measured crash/upgrade
restore test on disposable ext4 state.
