# CameraAgent retained astrometry scan evidence (#2004)

The scan owns one read-only SQLite connection per latest-accepted request.
Each lookup rechecks the journal, WAL and shared-memory identities and queries
current artifact evidence. Commands end before artifact file validation, and a
returned payload stream owns its file handle independently. The history reader
closes its connection on exit and discards it on a failed query or file change.
If the Linux identity call is unavailable, individual reads open fresh handles.

The native binding remains local to retrieval. It reads device and inode for
three physical paths and checks that each path names a regular file. The staging,
catalog and recovery bindings serve their own validation contracts. A separate
filesystem-interoperability refactor would need coverage for those callers.

## Workload and reproduction

The fixture uses 6,961 synthetic 512 × 512 Mono16 captures with one-second
exposures and an offline generated catalog. The normal durable fixture publishes
one accepted fit followed by 6,960 rejected fits, with one assessment every 60
capture sequence positions. This exercises both sides of the 4,096-probe
continuation boundary at about 81% of the rejection depth reported in #2004.
The measured archive occupied 6.2 GiB, with a 262,578,176-byte SQLite journal.
The fixture isolates the retained lookup phase. It uses fewer total captures
than the 518,400-capture archive from #1914.

Run the [manual harness](../../tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Processing/RetainedAstrometryPoolingBenchmark.cs)
on Linux with Python 3 and the pinned .NET SDK. The seed path uses the existing
raw and processing fixtures with their real persisted payloads, manifests and
checksums. The harness skips fixture-only compatibility index maintenance
because it measures journal-backed retrieval. Seeding the default workload is
a one-time operation that can take tens of minutes. Use
`HVO_RETAINED_SCAN_REJECTIONS` to vary the rejection depth.

```bash
set -euo pipefail
scratch="$(mktemp -d /var/tmp/scan.XXXXXX)"
export TMPDIR="$(mktemp -d /tmp/scan.XXXXXX)" MSBUILDDISABLENODEREUSE=1
export DOCKER_HOST=unix:///tmp/hvo-no-docker.sock
export HVO_RETAINED_SCAN_FIXTURE="$scratch/archive"
export HVO_RETAINED_SCAN_CACHE_PROBE="$PWD/docs/validation/retained-astrometry-2004-cache.py"
test "$(dotnet --version)" = 10.0.402
lock=/tmp/hvo-heavy-gate.lock
project=tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj
filter='TestCategory=Manual&FullyQualifiedName~Issue2004RetainedScanBenchmark'

flock --close "$lock" dotnet build "$project" -c Release -warnaserror
flock --close "$lock" env HVO_RETAINED_SCAN_MODE=seed \
  dotnet test "$project" -c Release --no-build --filter "$filter"

journal="$HVO_RETAINED_SCAN_FIXTURE/journal/raw-ingress.db"
python3 - "$journal" <<'PY'
import sqlite3
import sys

connection = sqlite3.connect(sys.argv[1])
assert connection.execute("PRAGMA wal_checkpoint(TRUNCATE)").fetchone()[0] == 0
connection.close()
PY
cp "$journal" "$scratch/journal.snapshot"
sha256sum "$scratch/journal.snapshot"

for mode in unpooled pooled; do
  cp "$scratch/journal.snapshot" "$journal"
  rm -f "$journal-wal" "$journal-shm"
  python3 "$HVO_RETAINED_SCAN_CACHE_PROBE"
  flock --close "$lock" env HVO_RETAINED_SCAN_MODE="$mode" \
    dotnet test "$project" -c Release --no-build --filter "$filter" \
      --logger 'console;verbosity=detailed' | tee "$scratch/$mode.log"
done
```

The cache probe affects only fixture files and reports Linux `mincore` results
for the journal. The harness repeats that probe after store initialization, so
the first scan excludes startup and begins with the measured page residency.
Seven cached refreshes reverify the accepted fit. Three warm full scans start
with fresh scan readers and a warm filesystem cache. A separate task samples
the selected raw artifact about every 20 ms while each lookup runs and checks
its capture ID, artifact ID and checksum. Each result records the headline
capture, artifact, solution hash and exposure midpoint, along with scan opens,
selected latency, process CPU, allocations, peak working set and I/O.

## Revision comparison

The first run compared the planning base
`327a5c0833cc53c9ae4cf40a2ec1d0c6d19dbe8d` with reviewed pooling commit
`67a9753f287f643a73417d53db2e29cea047eafc` on this synthetic archive. The cold
scan took 16.93 s on the base and 9.46 s with pooling. The median warm full scan
took 11.81 s and 4.77 s, respectively. Both runs verified 6,960 rejected probes
and the five accepted-fit inputs in two requests. The journal started at zero
of 64,106 resident pages for each cold scan.

Both modes of the correction benchmark use release build
`64dba637579595dd72cd361493ad99b5fa281d17`. The unpooled test adapter calls
ordinary artifact retrieval for each probe. The pooled mode uses the
production retained scan factory. Both use journal snapshot SHA-256
`3566708623be23e5b626b0aaf1a5fb8272f92d463d067ac32fbf91b602ba08c9`.

The correction pair ran on Linux ARM64 with four logical CPUs and an ext4
fixture filesystem, using the pinned SDK and Release binaries. Both cold
scans started with zero of 64,106 resident journal pages after store startup.
Startup took 2.51 s and 2.50 s, respectively, outside the scan timer.

| Scan or interaction | Unpooled | Pooled |
| --- | ---: | ---: |
| Cold full scan | 13.26 s | 7.50 s |
| Warm full scan, median of 3 | 9.47 s | 3.13 s |
| Cached refresh, median of 7 | 41.58 ms | 41.62 ms |
| Physical journal opens per full scan | 6,965 | 2 |
| Physical journal opens per cached refresh | 5 | 1 |
| Concurrent selected reads during cold scan | 575 | 335 |
| Cold selected read p50 / maximum | 1.85 / 84.48 ms | 1.60 / 79.49 ms |
| Concurrent selected reads during the three warm full scans | 1,242 | 421 |
| Warm full-scan selected read median p50 / maximum | 1.63 / 4.46 ms | 1.42 / 4.28 ms |

Every measured headline matched capture `00000000-0000-0000-0000-000000000001`, artifact
`0091238e-e0e8-8112-9fea-60631ad0f753`, solution
`947DC629346E531E252FC89E251182BBC6B44FD569E4A49B97664E2970DA6AB1` and
exposure midpoint `2026-08-25T00:00:00.5Z`. All 2,604 selected reads verified
identity and checksum.
The full scans took two requests in both modes and performed the same 6,965
artifact validations. The shorter pooled scans produced fewer overlapping
selected samples at the fixed probe cadence.

Median process CPU for a warm full scan fell from 10.67 to 4.12 s and the
median read syscall count fell from 348,151 to 68,678. Both warm modes recorded
zero physical storage bytes. The cold scans read about 150 MB and 149 MB
from storage. Median warm managed allocations were 3.69 GB and 3.64 GB,
with a peak process working set of about 315 MB and 319 MB respectively.
Cached-refresh ranges overlapped at 37.83–47.75 ms and 34.48–46.60 ms.

## Requirement evidence

| Requirement from #2004 | Local witness |
| --- | --- |
| Bounded reuse and unchanged headline identity | `RetainedAstrometry_LatestScanOpensOneJournalConnectionAndMatchesUnpooledHeadline` runs two real rejected fits ahead of an accepted fit. The unpooled control makes seven physical artifact-journal opens. The production scan checks the same seven artifacts with one open, returns the exact same summary, and uses one new open for its next request. |
| Artifact identity and evidence validation | `ValidRawAndDerivedContentReturnsExactCommittedBytesAsync` checks committed bytes, role and checksum. Both ordinary and history variants of `MissingAndCorruptEvidenceFailsClosedAsync` and `ValidationCacheRehashesWhenPayloadChangesWithoutMetadataIdentityChangeAsync` verify changed journal or file evidence. |
| Deletion and concurrent stream lifetime | Both variants of `SlowContentLeaseDoesNotHoldRootLifecycleLockAndPinnedHandleRemainsReadableAsync` delete a payload under the lifecycle lock, preserve the opened stream, and report `Gone` to a later caller. They also exercise an overlapping selected read, cancellation while waiting for the lock, and history disposal. |
| Retention | `RetainedAstrometry_EvictedContextDoesNotHideReadableFallback` removes a required input and verifies an older eligible fit both with an active reader and after reopen. |
| Quarantine and restoration | `CurrentSkyHidesQuarantinedFitWhileEarlierFitIsTemporarilyUnreadable` verifies that a quarantined result withdraws the active fit. `RetainedAstrometry_TransientArtifactFailureKeepsNewestFitPendingAcrossRestart` checks recovery of the same solution identity after a transient failure. `RetainedAstrometry_InterruptedRefreshesKeepVerifiedRejectionProgress` checks real output availability restoration and revalidation of repaired payload bytes. |
| Journal file replacement | `RetainedHistoryRejectsLinkedJournalAndFollowsReplacementAsync` rejects a symbolic link, closes the old connection after a journal swap, checks the replacement's evidence, and reads the original evidence after restoration. |
| Connection faults and disposal | `RetainedHistoryDiscardsFailedConnectionAndRecoversAsync` injects a SQL query failure, checks physical close and recovery, and disposes a scope with a borrowed handle. `RetainedHistoryClosesDisplacedIdleConnectionAsync` checks that a displaced idle connection closes. |
| Matched latency and concurrent selected reads | The manual harness records cold and warm results on the same fixture with pooling enabled and disabled. |
| Deep review | The two round-one reviews and this finding disposition go to Kepler for independent delta review. |

## Regression controls and discovery

The two mutation controls failed on Linux ARM64 and x64. Passing `history: null`
from the real history loop produced seven physical opens where the scan test
expects one. Removing disposal in `Return` left the displaced connection open.
The corrected Linux ARM64 build passed 661 targeted Unit tests and 204 targeted
Integration tests. Exact discovery in the changed assembly found 9,298 Unit,
761 Integration, 108 Manual, one Soak, zero External and one Hardware case.
Their disjoint union matched all 10,169 unfiltered case IDs.

For the correction range, `scripts/ci:classify` selected CameraAgent and the
combined CameraAgent–LogicHost lane with `complete=false`. The Linux x64
Release component builds passed with warnings treated as errors, and the
selected suites passed 789 Unit and 239 Integration cases. The x64 discovery
audit matched the same 10,169 cases. Whitespace verification and
`scripts/docs:audit-operations` passed. The protected target remains
`development/v1`, with coverage at CI.

[Original issue](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/2004) ·
[Deferred scan finding](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1992#issuecomment-7093303908)
