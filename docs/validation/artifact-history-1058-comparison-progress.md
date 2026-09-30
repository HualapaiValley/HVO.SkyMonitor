# #1058 Disposable History Comparison: 10K Checkpoint

This is a non-production, incomplete comparison against merged #1056/#1057
at `662d21e9` in `fix/1058-artifact-comparison`. All test files are disposable
and are removed by the fixture on success. No installed CameraAgent, capture
schedule, production writer/reader, or upgrade backup was changed.

Run the focused Manual test with the pinned SDK in Release:

```bash
HVO_ISSUE1058_CAPTURES=10000 dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj --configuration Release --filter "FullyQualifiedName~CameraAgentArtifactHistoryComparisonTests.TwoHourSourceAndShadowQueriesReturnIdenticalCaptureAndOutputRows" --logger "console;verbosity=detailed"
```

Without the environment variable the same test generates 720 captures. The
10K mode requires at least 60 GiB free in the disposable filesystem before
starting. Source and shadow are the same real-schema fixture: one raw manifest
and four scene-bearing v2 Preview output descriptors per capture, each
containing 2,000 deterministic objects and 190 segments. This is *not* a
representative mixed-role processing graph or physical payload workload. The
fixture uses ten-second UTC exposure spacing (10K covers about 27.8 hours),
not the 62-day distribution of #1056's original lookup test. Its source seed
WAL is explicitly checkpointed before projection and timed reads; the
checkpoint is not itself an online upgrade procedure. A failed run may leave
its unique `/tmp/hvo-gallery-performance-*` fixture behind and must be
inspected before any targeted cleanup.

The test-only shadow index was extended with exact exposure milliseconds and
output role/variant and corresponding query indexes. The previous index had
only the exposure hour and could not correctly answer within-hour time-range
reads. This is not a change to CameraAgent's production schema. The test
compares exact capture identities for 50-row sequence pages, 5-minute ranges,
and four output identities with role/variant for captures on each side of an
hour boundary. It then times 35 varied operations per query, 35 paired raw
manifest reads and 35 paired first-output descriptor reads. Each source
manifest has its stored SHA-256 verified; each shadow fact has its indexed
SHA-256 verified and its wrapped manifest bytes compared byte-for-byte with
the source. Output descriptor bytes are SHA-256 hashed on both sides and
compared byte-for-byte; the shadow fact's indexed hash is verified. Descriptor
source hashes are not persisted independently, so equality and both computed
hashes are checked instead. The shadow wrapper read and parse is included in
its time; the source has no corresponding wrapper. Both sides use a single
warm open SQLite connection. Output descriptor timing excludes the same
capture-ID lookup on both sides. Cross-layout identity, checksum assertions,
path assertions, and byte equality run outside the timed portions; hashing,
file reads and JSON wrapper parse remain inside the corresponding timed reads.
All 35 varied page and range parameters are compared row-for-row before
timing. No warm-up samples were excluded and no
OS-cold claim is made. Full catalog equivalence and incremental production
late-state update timing have not been measured; full offline restart scan
timing appears below.

| 10K captures / 40K outputs, controlled WAL | Bytes |
| --- | ---: |
| Source main DB | 12,968,476,672 |
| Source WAL / SHM | 0 / 32,768 (last run; SHM varies with open connections) |
| Shadow mutable index main DB / WAL | 22,925,312 / 0 |
| Immutable raw facts | 3,422,508,078 |
| Immutable output descriptor/source facts | 13,690,314,324 |

The shadow facts total 17,112,822,402 bytes and are retained outside the
intended *mutable-state upgrade backup*. They are **not saved space**: this
test has the large source journal *and* shadow facts simultaneously. A new
production source-of-truth implementation and installer reference check are
still required before the actual upgrade backup can shrink.

| 35 varied warm operations at 10K | Source median / p95 | Shadow median / p95 |
| --- | ---: | ---: |
| 50-row ID-only keyset page | 0.070 / 0.085 ms | 0.013 / 0.017 ms |
| 5-minute ID-only time range | 0.043 / 0.055 ms | 0.008 / 0.009 ms |
| SHA-checked raw manifest bytes, shadow wrapper parsed | 0.196 / 0.810 ms | 0.364 / 0.905 ms |
| SHA-checked first output descriptor, shadow wrapper parsed | 0.340 / 1.291 ms | 0.463 / 0.573 ms |

A separate 10K run added 35 varied **50-manifest keyset pages** with the
same page cursor on both paths and every returned ID, manifest byte sequence,
source stored manifest hash, shadow fact hash and wrapped manifest hash checked
after timing each page. The timed source operation reads the 50 manifests
and hashes each one; the timed shadow operation reads 50 index rows, opens
50 retained fact files, hashes each fact and wrapped manifest and parses the
fact JSON. Cross-layout and stored-hash assertions are outside both timers.
The measured median/p95 was **9.953/58.070 ms source** versus
**20.997/33.056 ms shadow**. ID-only 50-row pages in that same run took
**0.070/0.080 ms source** versus **0.012/0.020 ms shadow**. The shadow median
was slower for verified full-page reads; the source p95 was higher in this
single run. The two paths perform their respective integrity checks, not
identical work: shadow also hashes a wrapper the source does not have. These
warm synthetic fixture timings do not establish OS-cold, UI-render or
production gallery latency, nor a stable tail-latency ranking. They are a
distinct measurement and do not replace the earlier single-manifest row.

These are one independent 10K fixture run with 35 operations per query,
not five independent end-to-end trials. Earlier shadow raw and output
timings around 5 ms are **superseded**: their timer included cross-layout
assertions on the shadow side but not the source side. The corrected test
times reads/hash/wrapper parsing separately from comparison assertions for
both paths. The shadow medians remain slower in this single run, by 0.168 ms
for raw and 0.123 ms for output, not the prior multi-millisecond claim.
Different fixture runs are not a confidence interval for these read timings.
A run *without* the checkpoint had a ~13 GB source seed WAL and is excluded
from controlled-WAL size comparison. No operator pause/lookup budget has been
agreed and the candidate is not accepted for production by this checkpoint.

The measured SQLite plans are indexed: source page
`sqlite_autoindex_raw_captures_6 (agent_id,capture_sequence)`, shadow page
`sqlite_autoindex_capture_lookup_2 (agent_id,sequence)`; source time
`ix_raw_captures_gallery_time`, shadow time `ix_shadow_capture_time`.
First-output source lookup uses `ix_processing_outputs_capture_node` with a
temporary B-tree for identity ordering; shadow uses
`ix_shadow_output_capture` without a temporary sort. These plans do not
include filesystem fact reads.

An additional bounded 720-capture / 2,880-output Manual test
`BoundedFixtureSnapshotsRestoreCountsAndRetainedReferences` checkpoints the
source seed, changes one raw retention hold, then projects the shadow index.
It makes five new-destination SQLite `BackupDatabase` copies of *each* database
on the same seeded fixture; after the timed call, each snapshot is copied to a
new restore file, reopened and
passes `PRAGMA integrity_check`, capture/output counts and the changed hold.
Every copied index resolves and hashes one retained raw fact, then independently
passes the existing #1057 full-reference verifier over all 720 raw facts and
2,880 output facts. The full verifier checks index readiness, SQLite integrity
and foreign keys, path confinement and symlink rejection, hashes, descriptor
identity/source links and absence of unindexed fact files. The fact files
remain in place outside each copied index. One repeat with alternating copy
order measured source copies `[716.3, 744.0, 828.1, 764.9, 783.5]` ms,
index copies `[30.8, 7.2, 25.3, 6.5, 18.3]` ms and **separate** full
retained-reference checks `[10453.5, 10371.1, 10409.0, 10419.0, 10452.6]`
ms. A later run measured source backup-copy `[724.3, 742.0, 752.3, 723.6,
812.7]` ms, index backup-copy `[12.1, 7.8, 10.6, 8.1, 8.5]` ms,
source restore file-copy `[288.9, 306.3, 328.6, 301.2, 307.7]` ms,
index restore file-copy `[0.8, 0.5, 0.6, 0.6, 0.5]` ms, and separate
full retained-reference checks **against restored indexes** `[10446.1,
10368.6, 10427.7, 10489.8, 10649.0]` ms. These are warm correlated trials,
not five independent fixture builds;
the verification times do not belong to the SQLite backup-copy call. Snapshot
copy-call durations in ms: source `[748.8, 819.9, 767.3, 787.3, 757.8]`,
index `[21.2, 14.9, 8.9, 17.3, 13.8]`. Main files were 934,404,096 B and
1,679,360 B respectively. These five **correlated** warm trials do not
measure snapshot destination creation, closing, directory synchronization,
end-to-end process CPU,
peak disk/RSS, installer work or host downtime. The earlier sample-only
snapshot figures above are superseded as evidence of full reference checking.
The sample fact remains outside the copied index as intended. Neither an
open writer with a controlled uncheckpointed WAL nor a failed-upgrade rollback
was exercised in this test; #1059 owns installer recovery. Do not infer a
100K snapshot time by multiplying these durations.

In a separate repeat of that 720-capture test, after snapshot checking, a
second source hold update was committed and a new projector instance scanned
the full source into the existing index. That restart re-projection took
7,449.3 ms, updated the indexed hold and left one sampled immutable fact
byte-for-byte unchanged. The same run's correlated source snapshot-copy calls
were `[706.0, 752.0, 741.5, 723.2, 797.3]` ms and index copies were
`[22.5, 24.4, 20.3, 34.6, 19.6]` ms. A later focused run with
alternating source/index copy order measured `[714.5, 769.4, 809.3, 820.1,
721.5]` ms and `[15.6, 10.6, 22.6, 7.1, 17.6]` ms respectively, and a
7,579.1 ms restart rescan. The 7.45 s is a full 720-capture
offline rescan, **not** the cost of a production incremental state update,
installer rollback, or a 10K restart. It highlights that fast index backup
alone does not establish a fast restart/reconciliation procedure.

The same test has a 10K opt-in mode (the command above with the method filter
changed to `BoundedFixtureSnapshotsRestoreCountsAndRetainedReferences`). It
requires 90 GiB free before starting, creates one disposable 10K/40K fixture
with the **same five-copy scene-bearing v2 record mix**, and runs one snapshot
and restore per layout followed by full restored-index reference verification.
The Release run passed: source main 12,968,476,672 B, shadow index 22,925,312 B;
SQLite backup-copy call source **11,865.4 ms** and index **82.2 ms**; separate
restore file-copy source **4,804.6 ms** and index **9.3 ms**; full verification
of all 10,000 raw and 40,000 output retained fact references through the
restored index **169,974.0 ms** (about 170 s). A further committed source
retention-hold change and full re-projection into the existing index took
**110,396.9 ms** (about 110 s), updated the hold and preserved the sampled
immutable fact. This is **one fixture and one trial**, with no p95 or variance
claim. Fact verification is separate from the upgrade-state backup and cannot
be omitted from a restore that must validate its retained references; the
current test verifies source restore integrity, counts and hold but does not
fully hash every source row after restore. Physical image/metadata payload
recovery, installer orchestration, directory durability and OS-cold I/O are
not measured. Neither the copied index nor the retained facts are production
state yet.

A later **independent disposable 10K fixture run** added a streaming source
digest check, reusing #1056's method over all raw manifest bytes, output
descriptor bytes and ordered output source links, and compares restored source
to the pre-backup source. This is not a full hash of every source table or
every physical artifact. It passed alongside the unchanged full restored-index
fact verification. On this one run, source backup-copy took **14,538.2 ms**
(process CPU **12,345.9 ms**) and index backup-copy **82.9 ms** (process CPU
**34.0 ms**); source restore file-copy **5,157.0 ms**, index **9.1 ms**.
Source streaming digest verification took **34,644.0 ms** and restored-index
full retained-reference verification **178,537.6 ms**. The process working
set at the boundaries before/after the snapshot/restore trials was
1,798,123,520/1,834,426,368 B; these process-wide samples include fixture
setup and hashing, and are **not peak RSS**. The full restart re-projection
after one hold change took **103,399.3 ms**. This new run has a stronger source
verification method than the prior 10K run. Neither gives a p95 or a
repeatability guarantee for the 10K workflow.
This run computed its source digest before backup timing and thus warmed only
the source journal; its backup wall/CPU timings are not a balanced cache-state
comparison. The corrected harness computes the source digest after all measured
backup and restore copies, then compares it to the saved restored digests.
The extended full-reference verifier also checks the index's exact exposure
milliseconds and output role/variant against retained facts for every row.
Focused two-capture negative tests commit each of those three query-field
mutations to separate disposable index copies and confirm rejection. Earlier
10K full-reference times preceded these checks and should not be attributed
to the new validation method. One corrected-method 10K run passed:
source/index SQLite backup-copy **10,191.5/80.3 ms**, backup process CPU
**10,088.8/29.9 ms**, separate restore file-copy **5,278.4/14.9 ms**,
source streaming digest verification **22,069.9 ms**, and full restored-index
reference and query-field verification **169,412.6 ms**. The subsequent full
re-projection after one hold update took **116,147.7 ms**. Process working-set
boundary samples before backups and after verification plus source digest were
231,800,832/2,972,823,552 B, **not peak RSS**; cache and GC residency vary
substantially across runs. The source digest follows the measured copies, so
it does not prewarm the journal for this run's backup timing. This remains
one 10K fixture and one trial, not OS-cold or an installer pause result.

A later 10K run sampled process working set and filesystem available bytes
every 100 ms from just before the snapshot trials through source digest
verification. It passed with source/index backup-copy **10,464.5/77.8 ms**,
restore file-copy **4,959.8/9.1 ms**, source digest **21,306.2 ms**, full
restored-index/fact verification **170,385.1 ms**, and later full restart scan
**124,749.9 ms**. Process working set was 230,019,072 B before and
3,345,928,192 B after the trial phase; sampled high-water working set was
**3,431,596,032 B**. The lowest sampled filesystem free space was
**36,496,179,200 B** below the level at the start of the trial phase, while
the source, index and retained facts already existed. This captures the
coexisting snapshot/restore files plus any other filesystem activity, not
fixture creation or an exact per-fixture peak. Sampling can miss short peaks;
the filesystem counter includes unrelated host activity. CPU during the
individual backup calls was **10,484.6/24.5 ms** source/index, process-wide
and sensitive to concurrent work. These numbers are one warm run, not an
installer pause or cold-disk result.

The host reported ~282 GB available on its ext4 `/dev/sda1` filesystem
before this 10K run (decimal GB). A linear 100K capacity sensitivity with
**the same five-copy fixture** is about 129.7 GB source main + 171.1 GB
retained facts + 0.23 GB mutable index, or ~301 GB before snapshot/restore
and temporary capacity. One source snapshot brings coexistence to ~431 GB;
a separate restore copy to ~561 GB. These are *storage estimates, not 100K
observations*; SQLite page growth, WAL, directory entry/inode overhead and
nonlinear runtime are not covered. Do not free unrelated data or silently
substitute a smaller record mix to manufacture a 100K pass. A bounded storage
strategy or additional disposable disk is required to satisfy the issue's
>=100K same-mix and equivalent-verification gate. At a ten-second cadence,
30 days would have 259,200 captures; multiplying these fixture bytes to
that count is only a sensitivity calculation, not an operational forecast.

## Native Pi Pilot (XFS, Not Ext4 Qualification)

The operator authorized a dedicated scratch directory on Raspberry Pi 5
`allsky01` (`192.168.2.49`, native aarch64, four CPUs, 15 GiB RAM, SDK
10.0.401). `/var/lib/hvo-qualification-devices` is XFS on NVMe with about
3.3 TiB free; `/` is a separate microSD filesystem with only 58 GiB free.
Current uncommitted #1058 source was staged under the uniquely named
`/var/lib/hvo-qualification-devices/issue-1058-pilot-0927/source` without
`.git`, build output, or prior results. Both pilot runs set `TMPDIR` to its
separate `fixtures` directory on the same NVMe. Existing Docker and
qualification state was not modified. Successful fixture runs clean their
generated data; do not remove other Pi directories.

Release warnings-as-errors focused Manual tests passed at 720 and 10K with
the same scene-bearing record mix, sizes, WAL checkpoint and query plans as
the development host. The 10K Pi XFS run took 14m34s end to end. Its 35
varied warm verified 50-manifest pages had source median/p95
**38.469/45.557 ms** versus shadow **56.478/68.737 ms**; ID-only page
**0.292/0.319 ms** versus **0.054/0.057 ms**. Raw manifest reads were
**0.707/3.106 ms** versus **0.947/1.467 ms**. Source main 12,968,476,672 B,
index 22,925,312 B, raw facts 3,422,508,078 B, output facts 13,690,314,324
B, WAL zero after checkpoint. This is native ARM64/XFS/NVMe evidence, **not**
the issue's ext4 or OS-cold gate; neither Pi environment nor available
filesystem bytes match the local ext4 run.

A first opt-in 100K query-comparison attempt began on this dedicated XFS
scratch with an explicit 600 GiB free-space preflight. Its 30-minute SSH
tool window expired while the test process was still actively seeding the
real-schema journal. Read-only monitoring saw source seeding finish, its
121 GiB main DB checkpoint with an empty WAL, and shadow facts reach roughly
32 GiB raw / 128 GiB output with an index near 219 MiB. The process exited
and its fixture was cleaned, but no console verdict, `.trx`, result log,
exit code or final query metrics were recovered. Fixture cleanup happens on
both success and failure. **No verified 100K pass or lookup timing is
available for that XFS attempt.** The operator chose on 2026-09-27 to accept this unverified run
for planning and proceed to the next gate rather than repeat the ~10-hour
campaign. This was an explicit evidence waiver, not a measured pass, and did
not establish the issue's >=100K equal-result acceptance criterion. The
scratch NVMe returned to its pre-run free-space level; no deployed state was
used. A subsequent retained ext4 run below did produce a verified 100K result.

Still open after the verified ext4 100K run below: OS-cold paired reads using
an isolated repeatable method without touching deployed host caches;
five independent end-to-end snapshots/restores, disk/CPU/peak RSS and at-scale
independent full index-reference verification and late-state/restart timing;
full gallery filter parity and
an agreed pause/lookup target. These are blockers to claiming #1058 complete.

## Reboot-Safe Cold-Read Checkpoint

An opt-in Manual test `CameraAgentArtifactColdReadTests.DisposableExt4FileScopedColdManifestReads`
builds a separate disposable 100-capture fixture on local ext4. Run with
`HVO_ISSUE1058_COLD=1` and the focused Release test filter; without opt-in it
is inconclusive. It checkpoints the source, rejects nonempty SQLite WAL files,
and checks ext4 filesystem type. For each of 35 paired, varied manifest reads,
it alternates order, uses file-scoped `POSIX_FADV_DONTNEED` on the relevant
fixture-owned source main file or index and selected fact, and requires `mincore`
to report zero resident pages immediately before timing. It compares bytes and
hashes and records post-read page residency. No host-wide cache flush is used.

One local run passed: source median/p95 **1.969/2.980 ms**, shadow
**0.910/1.283 ms**; the sums of post-read resident pages for the source main,
shadow index, and selected fact were **5321/608/2940**. These are *file-page-cold
attempts on one disposable ext4 fixture*, not a fully OS-cold application path:
fact paths are looked up before timing, directory/metadata and controller
caches are uncontrolled, and residency is checked at a point in time. This
does not replace a dedicated isolated cold-disk comparison or the 10K warm
evidence. The test source and this checkpoint remain uncommitted on
`fix/1058-artifact-comparison`.

## Retained 100K Ext4 Backup And Restore

The operator provided a dedicated 1 TB disk `/dev/sdb`, formatted as ext4
UUID `5d6a29c7-eca9-47f5-b5e5-5aa694987ce5` and mounted at
`/mnt/hvo-1058`. The opt-in 100K Manual query comparison retained its exact
source journal, shadow index and fact files under `fixtures/` on that disk;
exit code `0`, a passing TRX and console output are retained under
`results/100k-ext4/`. It passed at 100,000 captures / 400,000 v2 Preview
outputs in 3h04m. The source main was **129,686,736,896 B**, controlled WAL
zero; shadow index main **229,740,544 B**, raw facts **34,225,458,079 B**
and output facts **136,904,274,340 B**. The fixture remains retained; no
cleanup is authorized yet.

A separate Manual test reused those **existing** databases and retained facts
without reseeding or re-projecting. On the same ext4 disk it created one
SQLite backup per layout and copied each to a new restore file under
`results/restore-100k/`. The stage log, snapshots and restored databases
remain there; `results/restore-100k.trx` reports one passed test. The restored
journal passed integrity/count checks and a matching source-versus-restored
streaming digest of raw manifests, output descriptors and ordered source links.
The restored index passed integrity/count checks and exhaustive validation of
all 100,000 raw and 400,000 output fact references against the retained facts,
including hashes, identities, lineage, path confinement and indexed query
fields.

| Measured stage, one 100K run | Journal baseline | Shadow-index candidate |
| --- | ---: | ---: |
| SQLite backup-copy call | 152.329 s | 0.703 s |
| Restore file copy | 122.562 s | 0.085 s |
| Reopen, integrity check, counts | 464.429 s | 0.716 s |
| Restored journal digest | 507.257 s | N/A |
| Source journal digest for comparison | 532.843 s | N/A |
| Full retained-fact reference verification | N/A | 1,692.937 s (28.2 min) |

The index's backup and restore **copy calls** together took about **0.789 s**
versus **274.890 s (4.58 min)** for the journal. A restore requiring exhaustive
fact-reference verification cannot call 0.789 s its total recovery time:
the candidate's measured copy, reopen and verification stages sum to about
**1,694.44 s (28.24 min)**. The baseline's copy, integrity and two digest
stages are not an equal-work installer procedure; they sum to about **29.66
min**, including two digest passes. These totals do not establish an
end-to-end pause improvement. Backup timing excludes destination opening and
closing, and restore copy excludes durable directory synchronization. No
qualified protocol currently allows reference verification to happen outside
the upgrade pause. These are warm-cache single-trial measurements, not an
installer pause guarantee. Image and metadata payloads are not backed up.

**Decision boundary:** The mutable index dramatically reduces backup bytes and
copy time, but exhaustive post-restore reference verification currently takes
28.2 minutes. Decide which checks must block startup and the acceptable pause
before claiming the upgrade problem solved. Production `raw-ingress.db` remains
authoritative, so actual CameraAgent upgrades still back up the large journal.

## Latest-Day Pre-Start Check

The operator set a target of **one to two minutes**, with **five minutes
maximum** for an upgrade pause. Before startup, validate the latest UTC
calendar day present in the restored index; scan older fact references in a
low-priority resumable background job over subsequent hours. A damaged older
fact must be reported and fenced until it is repaired from independently
verified existing data. Such a background worker, persisted progress,
generation/revision contract, read fencing, repair provenance and installer
integration **do not yet exist**; this is an offline timing and fault-boundary
prototype, not an implemented upgrade procedure.

The bounded verifier still checks restored SQLite integrity and foreign keys
globally, then hashes, parses and checks identity/source lineage/path confinement
only for raw captures whose indexed exposure is in the latest UTC day and
their linked outputs. It deliberately does not enumerate older unindexed
files. On the retained 100K restored index, the latest day is **2026-01-12**:
**4,960 raw facts and 19,840 output facts**. The focused Manual test passed,
measuring **73.761 s** for bounded fact verification. Adding separately
measured index backup copy **0.703 s**, restore file copy **0.085 s**, and
reopen integrity/count check **0.716 s** yields about **75.27 s** for these
stages, inside the operator's **timing target only**. This is a sum of different calls/runs,
not an end-to-end installer pause; index source generation and protected
publication, directory sync, host startup and retry costs remain unmeasured.
The full older-history scan measured **28.2 min** in one uninterrupted run,
not a validated low-priority background workload.

A two-capture test straddling UTC days demonstrates the intended fence:
corrupting an older retained fact leaves latest-day verification green but
full verification fails; corrupting a current-day fact fails the pre-start
check. A green latest-day result must **not** label older history verified.
Use verified on-access reads or explicit pending/unavailable state for older
facts until the background verifier commits their status. An existing source
can repair only after its identity, checksum and lineage are independently
validated; never recreate missing fact bytes from index metadata alone.

**Safety blocker found in independent review:** The latest day is derived
from the restored index itself. Deleting an index row, shifting its timestamp
to an older day or relinking its output to an older indexed capture can remove
that reference from the bounded selection without making SQLite integrity
fail. A focused negative test moves the latest capture's indexed timestamp
one day earlier: bounded verification of the original latest day stays green,
while the full verifier rejects the mismatch. Therefore the measured 73.761 s
proves only that **selected indexed references** are sound, not that the latest
day is complete. An independent, durable generation-bound publication/day
inventory must authenticate expected capture and output membership and day
boundaries before this can be a safe pre-start gate. Tests must also reject
missing raw/output rows and output relinking. Until that contract and a
qualified fault-tested restore exist, the ~75 s estimate cannot be used as
an accepted upgrade pause. Older history must remain pending/unavailable or
be verified on access; neither policy is implemented in production.

## Last-500 Pre-Start Probe

The operator narrowed the requested blocking check from the latest UTC day
to the **last 500 captures by agent sequence**, including their indexed
outputs. On the retained 100K ext4 fixture, the restored index reports one
agent with maximum sequence 100,000. The Manual test requires all sequences
99,501-100,000 to exist without gaps and verifies their indexed raw and
output facts against retained files. It passed in **9,336.0 ms** for the
bounded verifier (including global SQLite integrity and foreign-key checks).
Its passing TRX is `results/last-500-100k.trx`. Combined with the separately
measured index backup (703.2 ms) and restore copy (85.2 ms), this is well
below the one-to-two-minute timing target, **not** a measured installer pause.

The restored index still supplies its own maximum sequence and output
membership. Contiguous rows do not establish that its maximum is current,
and a missing output row can go undetected. A separately durable,
generation-bound publication watermark and expected output identities must
anchor the window before a green result can authorize startup. Older facts
must remain pending or be verified on access while a resumable low-priority
scan checks them; repair may use only independently verified existing data.
No such production writer, watermark, scanner or repair protocol exists.

### Source-Anchored Offline Inventory Probe

The #1058 acceptance-only `LastCaptureInventoryPrototype` now publishes a
durable, rename-published 500-capture inventory based on the disposable source
journal's last 500 contiguous sequences. It checks each retained raw and output
fact against source bytes, records capture identities and expected output
identities/hashes, and rejects a source or index `data_version` change before
publication. Against a copied index it rejects a missing newest capture,
interior capture, selected output, relinked output, or changed fact hash.
Fault tests also reject an interrupted `.pending` publication and a source
commit immediately before publication. Membership reads are pinned to one
SQLite snapshot. These tests do **not** assert that the source and index had
a common transactional generation: `data_version` is an offline race guard,
not a production revision contract.

On the retained ext4 100K fixture a fresh inventory publication took
**1,243.6 ms**, wrote **399,070 B**, restored-index membership took **11.8 ms**,
and the separate last-500 selected-fact verification took **8,357.8 ms**.
`results/published-last-500-100k-v2.trx` records the passing run; the v2
inventory and earlier snapshots remain retained. These are offline stages
against a frozen journal and index, not an installer pause or a production
trust anchor. A replaced inventory and index could agree on the same false
history without an independently authenticated generation/high-water record.
Production must bind that anchor to a durable publication and a frozen restore
snapshot, then qualify old-history pending/on-access behavior, background
verification, and repair using verified existing data before installation.
