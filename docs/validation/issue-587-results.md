# Issue 587 measured results

Status: local workload and kernel-fault evidence complete; the full candidate gate, independent review and protected CI are pending.

Measured on hvo-dev-02, Ubuntu 24.04.4, x64, SDK 10.0.401, runtime 10.0.12, two visible processors. Shared `flock --close` excluded #1005 build/browser/I/O activity. Before production is exact merged #1055 `71e3656aaf6a356e75c8481435bfef55a84c78fd`; after production is `14f7bbf297aae97048da973b38a54df697bd7a23`. Diagnostic harness-only commit `43123ec3` leaves that production tree unchanged. Both sides use identical overlaid harness files and one frozen baseline VirtualSky fixture; original checkpoint and append-only merge are retained. See [method](issue-587-method.md) for commands, acceptance boundaries and failed-probe preservation.

## Primary pair

| Metric | Before | After |
|---|---:|---:|
| w2 medianMilliseconds | 28.357 | 27.081 |
| w2 p95Milliseconds | 34.092 | 31.781 |
| w2 operationsPerSecond | 34.357 | 36.356 |
| w2 cpuMilliseconds | 474.701 | 437.239 |
| w2 allocatedBytes | 15,670,568.000 | 14,426,152.000 |
| w2 workingSetAfterBytes | 207,847,424.000 | 206,913,536.000 |
| w2 lohAfterBytes | 13,425,528.000 | 13,420,000.000 |
| w2 fileFlushes | 90.000 | 90.000 |
| w2 directorySyncs | 60.000 | 60.000 |
| w2 sqliteTransactions | 60.000 | 60.000 |
| w2 databaseBytes | 950,272.000 | 950,272.000 |
| w2 walBytes | 4,190,072.000 | 4,190,072.000 |
| w3m insertMilliseconds | 2,835.867 | 1,957.254 |
| w3m queryMilliseconds | 0.561 | 0.734 |
| w3m restartDiscoveryMilliseconds | 241.844 | 225.698 |
| w3m cpuMilliseconds | 4,368.229 | 3,316.649 |
| w3m allocatedBytes | 1,892,846,568.000 | 1,570,764,136.000 |
| w3m databaseBytesAfterCheckpoint | 55,185,408.000 | 55,185,408.000 |
| w3m walBytesAfterCheckpoint | 7,053,472.000 | 7,053,472.000 |
| w3p recoveryMedianMilliseconds | 427.596 | 426.577 |
| w3p recoveryCapturesPerSecond | 233.866 | 234.424 |
| w3p recoveryCpuMilliseconds | 2,279.900 | 2,487.949 |
| w3p recoveryAllocatedBytes | 70,870,952.000 | 70,654,976.000 |
| w3p initialBacklogCount | 100.000 | 100.000 |
| w3p finalBacklogCount | 100.000 | 100.000 |
| w3p finalBacklogBytes | 1,287,936,000.000 | 1,287,936,000.000 |

All W2, W3P100, fixed-byte publication and derivative payload/sidecar/index SHA-256 maps match exactly. W3M10K common row fields produce the same digest; query plans use `ix_raw_captures_discovery`. Baseline predates the `EvidenceWrittenOff` field, so the digest selects shared durable fields explicitly; it does not compare a different serializer schema as though it were the same. Database/WAL sizes agree. All 100 recovery captures remain pending by design, with no missing/quarantined/index-failure records; this does not claim a processing drain. Derivative reopening/replay preserves all bytes and verifies descriptor and producer lineage. Authenticated staging ownership/recovery is separately covered by the candidate tests, not by this raw-only performance fixture.

## Cost investigation

The primary derivative median increased 24.27 to 28.99 ms; the reversed full-workload pair increased 24.46 to 28.15 ms. The diagnostic therefore isolated derivative work in fresh processes, retaining the required five warmups and 30 operations. Both orders and all failures remain evidence, rather than selecting the fastest run.

| Isolated derivative run | Median ms | CPU ms / 30 | Allocated bytes / 30 |
|---|---:|---:|---:|
| baseline | 21.767 | 387.898 | 5,269,952 |
| candidate | 24.755 | 477.183 | 5,709,944 |
| candidate-reverse | 29.252 | 411.567 | 5,771,984 |
| baseline-reverse | 48.947 | 586.027 | 5,338,104 |

Latency and CPU vary strongly with execution history: the isolated reversed baseline is slower than either candidate. These samples do not establish a stable derivative latency regression or a speedup. The observed candidate median remains below 30 ms, well within the 1/s minimum drain requirement; no threshold was changed. W2 commits 36.36/s against configured arrival 0.04/s.

Allocation cost is repeatable and explicit: fixed-byte publication adds approximately 18–19 KiB/capture (about 21% of this small allocation slice, less than 0.16% of the 12,879,360-byte payload); derivative publication adds roughly 12–15 KiB/capture. LOH remains flat during each measured slice. Source inspection identifies newly repeated `PhysicalRoot` confinement checks and their path/FileInfo/DirectoryInfo allocations; separate syscall diagnostics corroborate the extra checks. This is an explained safety-check cost, not evidence of extra payload copies or durable writes. The end-to-end W2 allocation decreases from 522,352 to 480,871 bytes/op because that comparison also includes already-merged post-#1055 changes. Do not attribute that improvement solely to this refactor.

| Traced stage (35 writes, plus derivative replay/validation) | Before | After |
|---|---:|---:|
| Publication readlink | 1,470 | 2,940 |
| Publication pwrite64 / rename / fsync | 70 / 70 / 145 | 70 / 70 / 145 |
| Derivative readlink | 2,555 | 3,535 |
| Derivative pwrite64 / rename / fsync | 105 / 70 / 391 | 105 / 70 / 391 |

These counts filter paths below the workload directory, include setup, warmups and validation/replay, and exclude root-only operations. They are not the measured-30-operation counters. The latter fixed-byte publication counters are exactly 60 file flushes, 60 directory syncs and 60 renames on both revisions. Traced timings are excluded from latency comparisons. Raw `/proc/self/io` records read/write bytes and syscall counts; outer W2/W3 I/O includes validation.

W6 smoke uses five warmups and six 3552x3552 RGGB16 captures, with five-source rolling lineage asserted. Total median is 340.27 to 256.03 ms; CPU 2467.165 to 1522.754 ms, allocation 1,830,295,360 to 1,224,804,624 bytes, peak working set 1,753,653,248 to 1,013,276,672 bytes. Its inputs/rendering and whole pipeline include upstream changes; it is a smoke result, not a new topology or hardware campaign.

## Kernel faults and provenance

Real unprivileged permission denial, `/dev/full` ENOSPC and directory-fsync EIO injection pass through the CameraAgent adapter (3/3, no skips). EIO preserves the original IOException errno 5, leaves a complete payload, writes no sidecar and acknowledges no successful directory sync. An additional EINTR test passes (1/1, no skips); strace records `fsync = -1 EINTR (INJECTED)` followed by `fsync = 0`. Failed pre-fix traces prove the pinned runtime had swallowed the kernel failure; they remain alongside the direct native-runtime repro and library hash.

Private evidence root: `hvo-dev-02:/home/roys/.cache/hvo-issue-completion-20261015/587/`. It contains original/reversed/isolated JSON, chronological samples, all output hashes, frozen fixture, source tree IDs, all three harness hashes, exact tested Release DLL hashes, TRX, syscall traces, failed attempts and scripts. The original primary harness SHA-256 is `10EF4978DCF925611ABB0569BE8CDA25CBD3DC156C84A9237200A2F7137D9F10`; the isolated harness has a separately recorded identical before/after hash. Evidence must be archived and verified before deleting its work roots.
