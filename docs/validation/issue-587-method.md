# Issue 587 durability and equivalent-cost evidence

The original checkpoint is 44fc12f068f92dfaeb924faf83750e2a29daf355. It is retained
through the append-only merge463aecdb82956a2c2e48b92c8d6264a57eeb2138 of
cddf5bce1b654fae361996ef456a089e80a4930f. The required before-production revision is
the exact merged #1055 baseline71e3656aaf6a356e75c8481435bfef55a84c78fd.

## Linux synchronization regression

On hvo-dev-02 with SDK10.0.401/runtime10.0.12, the required CameraAgent kernel
fault test observed fsync returning EIO but publication completing successfully.
The retained strace and failing TRX prove both sides. Direct invocation of the
installed SystemNative_FSync on procfs returned1 with errno22. The upstream
[v10.0.12 native source](https://github.com/dotnet/runtime/blob/v10.0.12/src/native/libs/System.Native/pal_io.c)
assigns the comparison result to its return value; the managed caller checks for
a negative return. This defect is below the existing shared and private callers.

Shared Linux synchronization now calls libc fsync through SafeFileHandle interop,
retries EINTR, and translates a failed syscall to IOException carrying errno.
The shared fault wrapper and CameraAgent adapter retain the error. Non-Linux
file flush and directory no-op dispatch remain unchanged. Unsupported Linux
filesystem synchronization is a failure, not a durability acknowledgment.
No installed runtime, storage policy, schema, identity, or publication order is
changed. This expanded failure behavior requires independent deep review.

The Manual CameraAgent kernel tests use /dev/full, unprivileged directory modes,
and strace injection filtered to a uniquely allocated test directory. For EIO:

```bash
HVO_ISSUE587_FSYNC_ROOT="$fault_root" strace -f -qq -yy \
  -e trace=fsync -e inject=fsync:error=EIO \
  -P "$fault_root/frames/1970/01/01/Raw" -o "$evidence/kernel-fsync.trace" \
  dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests -c Release --no-build \
  --filter 'FullyQualifiedName~CameraAgentFileSystemKernelFaultTests&FullyQualifiedName!~RetriesInterrupted'
```

Create `$fault_root` using mktemp with `/tmp/hvo-issue587-fsync-XXXXXXXX`, and
precreate its `frames/1970/01/01/Raw` directory. The trace must show injected EIO;
the test must pass without skips, with one file flush, zero successful directory
syncs, complete payload still present, no sidecar, and no temporary left behind.
For EINTR, use a fresh `/tmp/hvo-issue587-eintr-XXXXXXXX` directory,
`HVO_ISSUE587_EINTR_ROOT`, the `DirectorySync_RetriesInterruptedSystemCall` filter,
and `-e inject=fsync:error=EINTR:when=1 -P "$fault_root"`. Require an interrupted
call followed by a successful call. Trace runs are not latency measurements.

## Equivalent workloads

An isolated detached baseline checkout lives beneath the owned remote issue
checkout's ignored TestResults/issue-587 directory. Only the three Manual harness
files are overlaid; `git diff --exit-code -- src` proves baseline production is
unchanged. The same harness bytes run on both revisions. Record source revision,
source/harness hashes, deployed DLL/native hashes, runtime, and host for each run.

`Issue587_EquivalentFixtureAndDurabilityCosts` freezes the W2 VirtualSky payload,
metadata, configuration and deployment location once on baseline production,
using real authenticated projected-scene staging. Candidate reads those exact
files; it does not rerender. Fixture hashes and retained scene document bind the
input. A fixed TimeProvider makes durable timestamps comparable. Every operation
retains the authentic frozen exposure timestamp and scene clock. Deterministic
synthetic source IDs distinguish its captures; changing request timestamps while
reusing that exposure would violate its provenance. This is an accelerated
storage-service workload, not a new simulated observing campaign.

W2 uses3096x2080 RGGB16,12,879,360bytes, seed2025, five warmups then30 operations,
concurrency1. The configured arrival is0.04/s; serial accelerated measurement must
show at least1 committed capture/s. W3M creates10,000 deterministic metadata
records, checks query-index use and restart parsing, and records SQLite/WAL bytes.
W3P commits100 payload-bearing captures and measures five complete raw-ingress
restart/reconciliation trials, retaining every output hash and asserting all100
payloads/manifests plus zero missing/quarantined/index-failure records. Its100
retained pending captures are recovery backlog, not a claimed processing drain.

A separate fixed-byte payload/sidecar publication stage measures the adopted
filesystem path with five warmups and30 operations. It checks exact bytes and
records observed flush/sync/rename counts. Full-ingress differences include all
production changes since #1055; attribute only the isolated publication stage
to filesystem adoption. Preserve raw latency samples, CPU, allocation, LOH, RSS,
process I/O, SQLite/WAL and output hash evidence. Process I/O includes validation
where it surrounds a whole workload; do not label it syscall-only publication I/O.
W6 uses the existing bounded rolling-window smoke (five warmups/six operations)
and asserts five source artifacts. It is not a repeated hardware/topology campaign.

Before examining results, investigate a median or allocation regression above10%
(and above1ms for latency) with a reversed-order paired repeat, retaining both
runs. This is a noise-investigation trigger, not an acceptance waiver: any
unexplained material regression or changed durable output blocks merge. Do not
change acceptance thresholds or golden outputs to make a run pass.

Status: focused EIO/permission/ENOSPC and EINTR passed after the correction.
Equivalent measurements and cost investigation are recorded in
[measured results](issue-587-results.md). Tier C test/publish/coverage evidence,
independent review and protected CI remain required; this is not completion.

## Candidate gate provenance

Bind every result to its actual source revision and tested binary identity. Preserve
failed attempts and select exactly one successful assembly result per canonical
coverage slot. The capacity-restored CameraAgent Unit retry supersedes its earlier
low-disk failure only in the current selection; the failed TRX remains evidence.

The full positive Unit selection includes four compile-linked Unit contract tests
in each of CameraAgent.IntegrationTests, LogicHost.IntegrationTests,
CameraAgent.LogicHost.IntegrationTests and Catalog.Sqlite.PerformanceTests. Run
these sixteen cases with the invalid Docker endpoint in separate result slots in
addition to the workflow's seventeen explicit projects. Preserve the canonical
coverage selection; additional contract-test slots are not extra rollup inputs.

Some Integration cases call `EvidenceSourceIdentity.CaptureAsync`, which requires
Release assembly informational versions to contain the exact checked-out HEAD.
An unchanged source closure does not satisfy that runtime guard with an older
commit-stamped binary. Rebuild the affected marker assemblies at the current
committed head, request that exact evidence revision, and rerun the required
evidence without weakening the guard. The initial three such failures on17c3ec5d
used binaries stamped361d0b84 and remain in the private evidence pack.

Later append-only target merges deliver Deployment CLI and LogicHost archive-read
changes outside the measured CameraAgent/shared-filesystem source closure. Verify
that closure against the actual merge endpoints before retaining measurements,
and run affected builds/tests plus all four CI-control guards. The issue/PR ledger
binds reused results, refreshed results, target profile and exact classifier range.
