# Centered-view journal retention measurement

## Published snapshot growth

`CenteredViewRetentionPrunesExpiredCaptureHistoryAndPreservesRetainedReferences`
registers one synthetic single-view base and publishes one distinct layer per
revision through the normal centered-view writer. SQLite measured the UTF-8
bytes of its definition and every retained revision's manifest and layer JSON.

| Layers published | Revision snapshots (bytes) | Definition and snapshots (bytes) |
| ---: | ---: | ---: |
| 0 | 593 | 8,039 |
| 1 | 3,217 | 10,663 |
| 2 | 7,874 | 15,320 |
| 3 | 14,564 | 22,010 |
| 4 | 23,287 | 30,733 |
| 8 | 78,509 | 85,955 |
| 16 | 286,565 | 294,011 |

Each publication writes a full snapshot. For this fixture, each additional
layer increases both the latest revision and the cumulative storage of earlier
revisions. These are measured payload bytes for one single view. Actual layer
types, stack definitions, and repeated enrichment change the totals.

## Journal size and write duration

The disposable probe uses the old and corrected SQL and schema on Linux x64
with SQLite 3.45.1, WAL, synchronous FULL, and foreign keys enabled. It stores
five revisions per base, with the byte lengths measured for layers zero through
four. The 31-day reviewer scenario and the backlog controls use 4,800 bytes for
the definition. That definition size and 1,200 bases per day are workload
assumptions. The probe measures SQLite page bytes assigned to the centered-view
tables and their indexes with `dbstat`.

| Scenario | Bases before | Bases expired | Longest write before | Longest write after | SQL sweep before | SQL sweep after | Active view pages after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Configured 31-day steady state | 38,400 | 1,200 | 55.840 s | 0.0414 s | 55.899 s | 0.559 s | 1,312,051,200 bytes |
| Backlog control | 8,000 | 6,800 | 15.980 s | 0.0520 s | 16.051 s | 1.860 s | 42,536,960 bytes |
| Configured 31 days reduced to one day | 38,400 | 37,200 | Unmeasured | 0.0708 s | Unmeasured | 12.219 s | 42,549,248 bytes |
| Default 30-day steady state, 7,446-byte definition | 37,200 | 1,200 | Unmeasured | 0.0311 s | Unmeasured | 0.548 s | 1,380,573,184 bytes |

The corrected 31-day steady-state model retained 37,200 bases and 1.22 GiB of
active view pages. The default file-storage policy is 30 days. Its model
retained 36,000 bases and 1.29 GiB of active view pages using the measured
single-view definition size. Each steady-state run needed 20 write
transactions. The one-day pressure model
needed 582 transactions and freed 320,222 pages for reuse. The pressure
baseline was measured on the smaller backlog control. The probe includes
SQLite candidate discovery and temporary staging. Write timings run from
acquisition through commit and include any synchronous commit work. The
measurements exclude raw filesystem probes, raw and processing payloads, other
journal tables, and contention from other writers. ARM and operational capture
rates were unmeasured. The physical database file can stay larger than the
active view pages because SQLite retains freed pages.

Reproduce with the pinned SDK and a temporary directory on the test filesystem.
Run the test under the shared heavy gate.

```bash
export TMPDIR="$(mktemp -d /tmp/cvr.XXXXXX)" MSBUILDDISABLENODEREUSE=1
export DOCKER_HOST=unix:///tmp/hvo-no-docker.sock
gate=/tmp/hvo-heavy-gate.lock
flock -o "$gate" dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  -c Release --filter 'FullyQualifiedName~CenteredViewRetentionPrunesExpiredCaptureHistoryAndPreservesRetainedReferences' \
  --logger 'console;verbosity=detailed'

probe=tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Processing/centered_view_retention_probe.py
flock -o "$gate" python3 "$probe" --bases 38400 --expired 1200 --definition-bytes 4800
flock -o "$gate" python3 "$probe" --bases 8000 --expired 6800 --definition-bytes 4800
flock -o "$gate" python3 "$probe" --bases 38400 --expired 37200 --mode after --definition-bytes 4800
flock -o "$gate" python3 "$probe" --bases 37200 --expired 1200 --mode after
```

## Scope

The sweep still visits retained bases in read pages. An indexed capture-age
projection would require a new persisted capture-time contract. Revision-only
saved selections would need an explicit retention and reopen contract for the
base, its lineage and live history. Both are separate design decisions.
Trail-work retention is outside #2000. The existing window publication workers
continue to own their retry and settlement policies.
