# #1056 Disposable Artifact Journal Baseline

Measured on 2026-09-26 in isolated worktree `fix/1056-artifact-baseline`, base
`04057729`, Ubuntu 24.04 x64, .NET 10.0.12, SQLite 3.53.3. No deployed database,
artifact, image or schedule was opened or changed. The fixture writes under a
temporary directory and removes that directory after each run. Transient JSON
results are under the test assembly's ignored `TestResults/issue-1056/`.

Reproduce at a smaller scale or at W3M scale:

```bash
HVO_ISSUE1056_CAPTURES=1000 dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj --configuration Release --filter "FullyQualifiedName~CameraAgentArtifactJournalBaselineTests.DisposableSceneBearingJournalSnapshotRestoreAndIndexedLookup"
HVO_ISSUE1056_CAPTURES=10000 dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj --configuration Release --filter "FullyQualifiedName~CameraAgentArtifactJournalBaselineTests.DisposableSceneBearingJournalSnapshotRestoreAndIndexedLookup"
HVO_ISSUE1056_CAPTURES=12000 dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj --configuration Release --filter "FullyQualifiedName~CameraAgentArtifactJournalBaselineTests.DisposableSceneBearingJournalSnapshotRestoreAndIndexedLookup"
```

The existing gallery fixture initializes the actual `raw-ingress.db` raw and
processing schemas, then writes one raw capture, one original preview output,
and three additional parseable preview output manifests for each capture in
a setup transaction. Each of five manifests retains the same scene geometry:
2,000 deterministic objects and 190 deterministic segments, with a distinct
scene ID per capture. This models the five-copy JSON duplication, **not** a
representative multi-role graph or the current deployed scene geometry.
Capture timestamps span 62 days, not a continuous 10-second capture stream;
only the first three original preview payload/sidecar pairs are written per
fixture; the remaining referenced payloads and sidecars are intentionally
absent. Do not use this
fixture for production retention, cold-start, artifact availability or
upgrade-rollback correctness. The snapshot uses SQLite `BackupDatabase` with
an open WAL writer and a committed, uncheckpointed late update. Restore is
copying the snapshot file to a new database path, opening it and checking the
logical digest, not running a complete installer or host restart. Source and
restored manifest and descriptor bytes match; integrity check is `ok` and the
uncheckpointed retention update is present in the snapshot.

| Scale | Main DB | WAL after explicit checkpoint and late update | SHM | Online snapshot | File copy | Reopen + logical verification |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1,000 captures / 4,000 outputs | 1,297,465,344 B | 8,272 B | 2,555,904 B | 1.17 s | 0.44 s | 1.97 s |
| 10,000 captures / 40,000 outputs | 12,968,484,864 B | 8,272 B | 25,329,664 B | 8.34 s | 9.99 s | 28.69 s |
| 12,000 captures / 48,000 outputs | 15,562,174,464 B | 8,272 B | 30,408,704 B | 11.81 s | 8.17 s | 51.59 s |

All three fixtures verify one source link per output (4,000, 40,000 and 48,000) and
include their identities in the source/restore digest. At 1K, five consecutive
backups of the same populated source, each with a new destination, took
1.170, 1.371, 1.178, 1.125, and 1.130 seconds (median 1.170, min 1.125,
max 1.371). These are correlated warm-cache backup trials, not five
independent fixture builds or host restarts. The 10K and 12K rows are one
trial each. At 12K, process working set was 194,891,776 B before backup,
198,893,568 B after backup and 2,134,700,032 B after full verification;
31,124,348,928 B of extra snapshot and restore files coexisted at the
copy-completion point. This verification is not bounded-memory recovery.

The snapshot is another main-file-sized copy. Peak incremental capacity for
the measured sequence is at least one snapshot plus one restore copy while
source main and WAL remain present; this harness does not sample filesystem
peak or force sync of the restored directory. The fixture WAL is explicitly
truncated before the measured late update; an earlier uncontrolled WAL run
grew roughly as large as the main file, but its timings are superseded. The
10K snapshot used 8.22 s process CPU. The reopen verification reads and hashes
all raw and output descriptors and source lineage; it must not be called pure
copy throughput. File copy excludes directory sync and is not a qualified
installer restore. Measured process working set was 190,070,784 B before the
backup, 193,441,792 B just after the backup, and 1,795,436,544 B after
reopening and fully digesting the restore. This process-wide sample includes
the fixture and SQLite cache; it is **not** the CameraAgent cold-start peak or
a bounded-memory restore result. At restore-copy completion, source main/WAL
plus two 12,968,484,864 B files coexist: at least 25,936,969,728 B of extra
file bytes beyond source state; actual peak allocation and disk synchronization
are not measured. The 10K and 12K snapshot/restore measurements each have
only one trial; no independent five-trial noise bound is available.

For 10K captures, 35 warm-cache reads per query (all 35 contribute to the
reported statistics; there is no excluded warm-up window) found:

| Query | Median | p95 | Plan |
| --- | ---: | ---: | --- |
| Varied-sequence raw manifest | 0.770 ms | 5.017 ms | `sqlite_autoindex_raw_captures_6` |
| Varied capture ID first output descriptor | 0.735 ms | 3.937 ms | `ix_processing_outputs_capture_node` |
| Varied 50-capture keyset page | 2.744 ms | 6.231 ms | `sqlite_autoindex_raw_captures_6` |
| Varied 50-capture time range | 0.176 ms | 0.365 ms | `ix_raw_captures_gallery_time` |

The raw/output lookups materialize manifest/descriptor bytes; page and range
reads return capture IDs only. The source database is open throughout; page
and range cursors vary across 35 reads, but there is no cold-cache guarantee.
Page and range results are not yet compared row by row to a candidate; that
belongs to #1058.
At 1K, opening a new unpooled SQLite connection for each of 35 varied
raw-manifest reads gave 0.670 ms median and 0.979 ms p95. This includes
connection startup but **does not flush the Linux page cache**; it is not
cold-disk evidence and must not be compared with an OS-cold candidate run.
The 10K version of the same fresh-connection probe measured 0.752 ms median
and 2.385 ms p95; it has the same cache limitation.
The original existing raw-ingress W3M 10K Manual test separately produced
a 212,172,800 B main DB and 213,016,392 B pre-checkpoint WAL with **no
processing output descriptors**. This explains why it was not sufficient as
the artifact-journal baseline.
The Microsoft.Data.Sqlite build in this test host does not expose `dbstat`;
an attempted table-allocation probe failed with `no such table: dbstat` and
was removed. No table-by-table allocation breakdown is claimed here.

**Decision boundary:** indexes keep these warm lookups fast at 10K, but the
measured mutable journal snapshot/restore grows with duplicated history.
Avoid interpreting the synthetic 1.297 MB/capture as a 30-day capacity or
installer time forecast. A deliberately crude 259,200-capture multiplication
would require about 313 GiB of DB main file and at least 626 GiB for two extra
copies; it does not model retention, revised descriptors, sustained WAL,
filesystem cache, or snapshot rate. The benchmark host had about 152 GiB free
before the 10K run: a 100K instance of this five-copy fixture plus a backup and
restore copy cannot be run here without changing the workload or capacity.
#1057 should project the same real-schema fixture without becoming authoritative;
#1058 owns equivalent OS-cold and warm query work, several independent snapshot
trials and at least 100K captures with bounded resource use. #1059 owns full
installer-style restore and precommit artifact preservation, coordinated with
#1044's separately claimed candidate upgrade/recovery work. The operator
explicitly assigned these qualifications downstream; #1056 does not claim
they passed.

An exploratory 20K run on this host timed out after 10 minutes with MSBuild
child-worker exits and did not produce an evidence JSON. Available disk dropped
from about 151 GiB to 104 GiB during the attempt. Inspection found no open
journal process and identified one 49 GiB abandoned fixture under the test's
unique `/tmp/hvo-gallery-performance-20000-...` directory. Only that fixture
was deleted; free space returned to about 152 GiB. No 20K result is accepted;
the harness cap is 12K pending a less resource-intensive scaling fixture.
Do not extrapolate the timed-out trial into snapshot or lookup performance.
