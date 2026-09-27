# #1057 Non-Authoritative Artifact-History Layout Probe

This is an isolated acceptance-test prototype on `fix/1057-hourly-shadow` at
base `df8a8810` (#1056). `raw-ingress.db` remains authoritative and unchanged.
No production storage writer, gallery reader, migration, installed container,
capture schedule, or LogicHost code is modified.

Operator decision 2026-09-27: immutable raw-manifest/output-descriptor fact
files are retained historical artifacts, **not** copied into every upgrade
state backup. The intended backup contains only mutable databases and other
mutable deployment state; after restore, index references to retained facts
must be checked for identity and checksum, without rewinding fact files or
capture image/metadata artifacts. The current `raw-ingress.db` still contains
all authoritative historical rows and remains large: this shadow projection
alone makes no existing upgrade backup smaller.

Run:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj --configuration Release --filter "FullyQualifiedName~CameraAgentHourlyArtifactFactsPrototypeTests"
```

To exercise the optional exported-dataset read test, first run the dense-hour
test with `HVO_ISSUE1057_EXPORT=1 HVO_ISSUE1057_EXPORT_LABEL=<fresh-label>`;
then run the filter above with `HVO_ISSUE1057_EXPORT_LABEL=<same-label>`.
Without a matching export, only that read test is Inconclusive (14 pass, one
skip); with `dense-hour-bound`, 15/15 focused Manual tests passed. An existing
export is never overwritten.

The optional #1056 fixture mode also seeds one dense UTC hour: 360 captures
at ten-second intervals and four scene-bearing output manifests per capture.
Those 1,440 outputs are v2 Preview manifests, not the production mixed graph.
A separate focused two-capture fixture inserts one valid
`DurableTypedMetadataProductManifestV3` row, projects its exact descriptor
and source link, and verifies it through an index-only SQLite backup. It
independently rejects content identity, algorithms, compatibility, and
integration-ticks mismatches between the row and descriptor. This
proves that typed Metadata is accepted by the offline parser; it does not
make the 360-capture byte and timing figures representative of all metadata
product types or their physical payloads.
The offline converter also dispatches durable processing-product v1 and encoded
v2 descriptors. A separate two-capture fixture inserts one structurally valid
row of each, checks the projected descriptor bytes and source link, and verifies
both through an index-only backup. This covers those schema/producer-step
bindings, **not** their physical payload bytes or a representative mixed graph.
On one local Release run the source SQLite main file was 467,619,840 B;
the shadow main index and its online SQLite-only backup were each 634,880 B,
with zero index WAL bytes after closing the projection. The 360 separate raw
fact files totaled 123,208,416 B and 1,440 separate output descriptor/source
fact files totaled 492,848,388 B. Offline conversion took 8,698.2 ms,
SQLite backup-copy call 4.8 ms, full retained-fact reference verification 2,952.5
ms, and 35 varied warm-cache fact lookups measured 0.320 ms median and 0.660
ms p95. Two later independent disposable runs reported conversion 9,199.4
and 8,932.2 ms, backup-copy calls 4.6 and 5.4 ms, full verification 2,836.2 and
2,747.7 ms, with unchanged byte counts. A fourth independent run after adding
complete-row and source-lineage checks measured conversion 9,175.2 ms,
backup-copy call 5.2 ms, and full retained-fact verification 2,759.2 ms.
The next run measured conversion 8,743.7 ms, backup-copy call 9.4 ms,
and full verification 2,818.5 ms. A reviewed export with tightened checks
measured conversion 8,794.4 ms, backup-copy call 4.6 ms and verification
2,758.0 ms. A later identity-bound export measured conversion 9,061.1 ms,
backup-copy call 5.5 ms and verification 2,894.2 ms. The last run used the copied
index to locate capture sequence 180 and checked its retained fact checksum.
It also verified 360 capture and 1,440 output index rows, SQLite integrity
and foreign-key checks; each output fact's source list matches its descriptor.
The backup-copy timing excludes destination creation/opening and closing,
and is **not** end-to-end upgrade backup time. The first three trials preceded
the integrity/source-lineage assertions; only the final two share that
verification method, so no five-trial median or comparable variance claim
is available. These are not cold-read, disk-synchronized backup or
restore-time guarantee. The emitted facts total 616,056,804 B: this layout
moves historical evidence outside the mutable DB backup, but does not claim to
reduce total storage. The existing large authoritative journal still must be
backed up until a later ownership change; a 635 KB shadow-index snapshot alone
is not yet a CameraAgent upgrade backup.
After adding typed-descriptor support and stronger retained-fact validation,
a later independent dense-hour run measured conversion 8,917.2 ms (process CPU
5,916.1 ms), full reference verification 5,082.1 ms (CPU 5,904.0 ms), and a
5.6 ms SQLite backup-copy call. Process working set before/after conversion
was 167,645,184/190,898,176 B and before/after verification
190,935,040/201,699,328 B. These are process-wide boundary samples,
**not peak RSS**; the earlier 2.7-3.0 second verification figures did less
validation and are not comparable with this run.
One separate, opt-in full **offline export** trial on a fresh labelled dataset
timed 3,893.4 ms for an additional source SQLite snapshot, copying the index,
copying and verifying all 1,800 retained fact files, and final directory
rename. This copies historical facts to prepare a reusable test dataset, not
the operator-selected steady-state per-upgrade procedure; fact files would
remain in place across a qualified future upgrade. Neither this 3.89 s value
nor the 4.9 ms index backup-copy call measures a CameraAgent installer pause.

With `HVO_ISSUE1057_EXPORT=1`, the dense-hour test retains an *offline*
dataset under ignored test-binary `TestResults/issue-1057/dense-hour`:
`source.db` (consistent SQLite backup of the disposable journal), `mutable.db`
(shadow index snapshot), and `hours/` plus `products/` fact files. The export
occupies about 1.1 GiB including BOTH layouts, never production data; normal
test execution does not retain it and export refuses to overwrite an existing
dataset. `HVO_ISSUE1057_EXPORT_LABEL` can select a new 1-64-character ASCII
label such as `dense-hour-reviewed`; the test builds and verifies that dataset
in a temporary sibling before renaming it into place. The `dense-hour-bound`
export includes `dataset.json` with both SQLite snapshot hashes, expected row
counts and fact-file **counts**, verified before read timings; it is not a
cryptographic inventory of the fact trees. `VerifySnapshotAsync`
checks every indexed retained fact separately, rejects extra/unindexed files,
and runs again when an export is reused for a read benchmark. The current
prepass also compares all 360 raw manifest bytes, 1,440 output descriptor
bytes, ordered source links, raw holds and output availability against
`source.db`. It changes cache state; lookup figures from earlier runs without
this prepass are not directly comparable. Independently queried
SQLite snapshots both have 360 captures and
1,440 outputs; index `ready=1` denotes a completed scan only. This export
allows further read tests without regenerating the fixture, not a production
backup inventory.

One warm-cache comparison against the reviewed export with 35 varied captures, both
paths reading and validating raw manifest SHA-256 and parser, measured source
SQLite 3.804 ms median / 9.891 ms p95 versus index + per-capture file +
wrapper SHA-256 and JSON decode 4.044 ms median / 10.731 ms p95.
Another read against `dense-hour-bound` measured source 5.574 ms median /
11.791 ms p95 and shadow 5.664 ms median / 11.315 ms p95; the warm-cache
ranges overlap and do not establish a speed difference. The shadow
path performs additional wrapper verification: this is **not** exact
equal-work or OS-cold latency. It does show that moving evidence out of
mutable SQLite is for backup isolation, not inherently faster raw detail
reads. Every sampled manifest byte array matched. #1058 owns equivalent
cold/warm, page and product lookups at scale; one-file-per-fact I/O and inode
costs remain a design risk.
After adding full export verification before timing, a single subsequent
read measured source 1.470 ms median / 2.209 ms p95 and shadow 1.723 ms
median / 2.390 ms p95. Both paths benefited from the warm prepass; this
number replaces the earlier figures as the current harness behavior, not
evidence of improved persistence performance.
A subsequent run with full source-to-shadow equivalence before timing measured
source 1.325 ms median / 1.800 ms p95 and shadow 1.555 ms median / 2.057 ms
p95. It is one post-verification warm-cache sample, not a cold-read or
performance acceptance threshold.

The #1056 disposable fixture seeds 100 real-schema raw captures and 400
scene-bearing output manifests across 62 days. This probe reads their committed
SQLite rows in one deferred source transaction. It writes raw-manifest bytes
and identities into per-capture JSON files under UTC-hour directories, output
descriptor bytes and ordered source IDs into identity-named JSON facts, and
indexes relative paths, fact SHA-256, output identity, availability, and raw retention hold in a
separate SQLite `mutable.db`. The output fact is not the physical image or
metadata payload; the fixture intentionally has almost no such files.

Focused Release tests pass for all 100 raw manifest byte comparisons and all
400 output descriptor/ordered-source byte comparisons, repeat projection,
late retention-hold and output-availability updates without changing immutable
file lengths, insertion of an output after capture, addition of a late raw
capture to an existing exposure-hour directory without rewriting its neighbor,
and refusal to reproject corrupted or missing indexed fact files. The projection also checks raw
manifest/descriptor identities and parsed output identity before publication.
Removing previously indexed output rows from the source journal marks their
mutable lookup state `RemovedFromSource` on a completed full scan without
deleting immutable facts. The fixture checks one source per
output, **not** ordered multi-source provenance.
`Measure()` counts retained lookup rows including `RemovedFromSource` tombstones;
its capture/output counts after deletion are not live authoritative counts.
These tests use a tiny, sparse-hour fixture, not a 360-capture hour or a 30-day
resource profile. One local Release run: mutable main DB 188,416 B, WAL 0 B
after close; 100 raw fact files total 34,224,368 B; 400 output fact files total
136,901,108 B. An unchanged full 100-capture reprojection took 905.6 ms on
this host, one warm-cache measurement, not steady-state cost or an at-scale
throughput forecast. Five files per capture is a significant inode/lookup/
cleanup cost at longer retention; hourly grouping is only a directory namespace,
not an endorsed final file granularity.

## Production Blockers

- This code is test-only. It fsyncs new fact file data and synchronizes the
  containing directory after rename. Disposable before-rename and after-rename
  injected exceptions reproject from the authoritative journal and preserve
  idempotence (15/15 focused Manual tests with a labelled export); these are process interruption
  simulations, not power-loss proof or a complete SQLite-commit fault matrix.
  Parent-directory sync uses the CameraAgent raw-ingress filesystem primitive.
  A disposable SQLite-only backup of `mutable.db` is reopened and verified
  against the retained fact files. Removing a referenced fact or changing
  only an output fact's source list makes verification fail; raw/output fact
  bytes are not copied into that backup. This is a fact-reference check, not
  installed-instance restore or physical artifact recovery. #1059 owns
  power-loss and installer-restore qualification.
- Late output and late raw capture both work with per-identity files. This
  does not qualify a compact sealed-hour file; aggregating captures later
  needs an explicit completeness or late-fact protocol.
- Real replay pins, output deletion and retention holds must be projected
  independently of immutable facts and exercised with authoritative late-state
  transitions. The test covers effective `retention_hold` and output
  availability, plus one deleted-output reconciliation. The prototype still
  scans all source rows and is not bounded for production retention/replay.
- Product facts duplicate descriptor JSON outside SQLite; no physical artifact
  payload/sidecar existence or checksum is proven. They must not be described
  as recovered capture files. The fixture's missing files preclude an upgrade
  recovery claim.
- The current projection reads one capture and its outputs at a time but
  rechecks every prior file, reads whole descriptor blobs and holds a long
  source transaction. It is not bounded-I/O or a valid at-scale performance comparator.
  Neither the small index observed in the prior synthetic Python layout nor
  this 100-row fixture predicts a production mutable DB size.
- `projection_state.ready` is cleared before each full pass, but set after
  the source read snapshot ends; it denotes a completed scan, **not** a
  current view. It proves neither currentness relative to
  concurrent source writers nor atomic visibility with concurrent projectors.
  A controlled concurrent source commit during traversal is detected by
  comparing `PRAGMA data_version` on the same long-lived source connection;
  the pass fails unready and a later pass succeeds. That value is not durable
  across connections/restarts, and a commit after the check still races with
  setting ready.
  A production design needs a revision-bound generation and serialized
  projection/read boundary. The test-only index snapshot verifies retained
  references, but does not qualify a concurrent, crash-safe upgrade backup.

An earlier #1057 scope considered a production source-revision/fence contract;
the operator subsequently deferred production migration in favor of offline
data and layout measurements. The following revision experiments are
**disposable research**, not part of a runnable CameraAgent version. One
SQLite trigger
test adds a revision table and raw-hold/output insert/delete triggers after
fixture initialization: the revision remains zero after rolling back a raw
update, then advances to nine after one committed raw update, four output
deletes and four cascaded source-link deletes. Creating one disposable replay
execution raw-input pin advances it to ten; releasing that pin advances it to
eleven. This proves SQLite transaction behavior for those trigger
shapes only. A second disposable populated-v13/v8 fixture rolls back a
revision-table/trigger DDL transaction and verifies the retained manifest
bytes and absence of new schema objects; a retry commits both. It does **not**
exercise CameraAgent migration validation, version marker, installer preflight
or old-image compatibility. No production trigger or migration has been added. The real raw
version-13 and processing version-8 canonical audits reject unexpected
triggers, and a populated database needs an explicit reviewed migration,
covering output sources, replay input/output pins, cascades and autocommit
writes. Row-level trigger costs and busy/lock behavior at capture cadence are
not qualified. Five independent disposable fixtures per variant, each with
20 unmeasured operations and 1,000 measured autocommit raw `retention_hold`
updates (WAL truncated before measurement), measured a no-trigger median
756.8 ms (min 708.5, max 814.3) versus `AFTER UPDATE` revision-trigger median
770.2 ms (min 718.3, max 887.7). The timing ranges overlap, so this does
**not** establish a material latency regression. WAL length was 8,240,032 B
without versus 12,360,032 B with the trigger in all trials, demonstrating
write amplification for this one-row update pattern. No CPU/RSS, contention,
bulk processing updates, or real capture DAG was measured; this is not the
qualification budget or an endorsement.
Do not interpret the temporary trigger test as a durable current
generation or a production upgrade-ready schema.

## Future Source Revision Contract

The proposed production token is a durable, non-reused source epoch and
monotonic revision in the existing shared journal. Every relevant committed
raw/output/source-link/replay-pin/retention mutation must advance it in the
same SQLite transaction, including autocommit updates and cascades; rollback
must not advance it. `PRAGMA data_version` remains a same-connection test
race detector, never the persisted token. A per-row trigger is the simplest
database-level coverage candidate, but the narrow microbenchmark above does not
qualify its write/WAL/lock cost, no-op update behavior, or bulk workload.
Application-managed increments are only correct if every independent writer
and cascade is covered; an omitted mutation silently breaks the token.

Build the shadow from a tagged source snapshot and publish verified fact files
before an index generation. For a strict current read, a short source write
fence must check that the token still matches before committing the index
generation; readers must bind their source token check and index read to a
documented consistent boundary. A comparison after the read transaction
ends, or a naked `ready=1`, cannot close the race. The source and index are
different SQLite files: this does not make their commits atomic, so restart
and index-only restore must revalidate epoch/revision plus retained fact
inventory. Operator decision 2026-09-27: use revision-tagged snapshot-at-read
semantics, not a source write lock for every lookup. A read may reflect the
completed source revision rather than a later commit; it must name that
revision and remain internally consistent, never imply `ready=1` is latest.

Future production migration would need an explicit populated raw v13-to-v14 path
and processing-v8 ownership coordination, pre/post canonical schema audits,
transactional version/trigger installation and rollback on interruption.
Older images must refuse a newer version; #1044 still has an unqualified
precommit artifact-preservation path. Do not run this migration on installed
instances from a benchmark branch. Required disposable tests include every
writer family, multi-source and cascade mutations, no-op updates, concurrent
writers/projectors/readers, an interrupted generation, restore mismatch and
write-amplification trials at a named capture cadence. Operator decision:
set the revision overhead acceptance limit only after repeated representative
capture/processing/WAL/lock measurements, not the one trigger microbenchmark.
No production schema edit is claimed in this prototype.

The current deployment preflight is a **release blocker** for a raw schema
version increase. `CameraAgentStatePreflight.EvaluateRawIngressSchema` accepts
only the image-declared exact `user_version` or a truly empty version-zero
database, and otherwise tells the operator to reset. A candidate declaring
raw v14 is rejected against a populated v13 journal before its host migration
can execute. Once migrated to v14, a schema-v13 old image cannot resume that
state; precommit rollback must restore the original small mutable DB snapshot
and preserve every referenced preexisting artifact, which is precisely the
currently unqualified #1044 boundary. A release must coordinate a narrowly
authorized preflight compatibility window, canonical migration and rollback
qualification with #1044; unit tests of a standalone v13-to-v14 migration
would not authorize an installed image change. Do not edit the separately
claimed #1044 CLI or run it against :5130/:5131 from this issue.

Do not commit to hourly shards or a production writer/read switch from this
prototype. The immediate next boundary is a bounded, replay-safe capture
publication/sealing protocol and independent late-state projection, followed
by #1058 equivalent at-scale warm/OS-cold comparisons and #1059 crash/restore
qualification. Artifact `.bin`, `.jpg`, and sidecar `.json` stay outside
upgrade backups.

## Populated Journal Migration Gate

The next implementation test must create a populated, canonical raw-v13 and
processing-v8 database with WAL-resident changes and immutable raw/output
bytes. It must first prove both existing validators accept it. The candidate
then validates that old schema without writing, begins one immediate SQLite
transaction, checks the version and definitions again, installs one durable
non-reused epoch and revision row plus the complete trigger set, advances the
raw version, and validates the new canonical schema **before** committing.
An injected failure before commit must leave the v13/v8 database and every
manifest, descriptor, source link, pin and WAL-visible update unchanged; the
same migration must succeed on retry and restart without replacing the epoch.
An older binary must refuse the new schema rather than silently using or
resetting it. A missing, changed or unexpected trigger must be rejected by
the appropriate owner audit on restart.

The trigger inventory must cover INSERT/UPDATE/DELETE on `raw_captures`,
`processing_outputs`, `processing_output_sources`,
`processing_execution_input_pins`, and
`processing_execution_output_input_pins`. Verify implicit cascade deletes,
autocommit availability changes, replay-pin release, multi-row retention
recomputation, rollback and no-op update semantics separately. Processing
owns its tables and any triggers on them; raw validation must permit exactly
those processing-owned definitions, while the processing audit still rejects
rogue triggers. Legacy processing-v5/v6/v7 migrations rebuild tables and
must preserve or install the final trigger set *after* rebuilding; legacy
schema generation splits statements on semicolons, so do not add a trigger
body to that split-derived `SchemaSql` without restructuring the derivation.
On a fresh journal, raw initialization precedes processing initialization;
on a populated journal, both old and new checks must preserve that ordering.

A disposable-only matrix test now installs 15 `AFTER INSERT/UPDATE/DELETE`
triggers on those five tables in one transaction with a proposed version-14
marker and an epoch/revision row. Rolling that transaction back preserves the
populated fixture's original manifest bytes and v13 marker, and a retry
commits all 15 triggers. The existing v13 journal validator refuses that
v14 copy. This proves old-image refusal and SQLite DDL atomicity on the test
fixture, **not** new-version canonical validation, migration coverage on
processing v5/v6/v7, or installer acceptance. It does not turn the proposal
into production migration code.

Even a passing disposable migration does not clear the installer gate:
candidate preflight currently refuses v13 before the v14 host can start.
The exact preflight compatibility window and precommit restoration of v13
mutable state, including preservation of preexisting referenced artifact
files, require coordinated #1044 qualification before PR finalization or
installed-image release. The source token also does not by itself make
separate-file index publication atomic: a generation is labeled with its
source epoch/revision and must be validated against retained fact inventory
on restore; it may be older than concurrent new commits under the chosen
revision-tagged snapshot semantics.
