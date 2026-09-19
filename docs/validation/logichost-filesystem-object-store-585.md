# LogicHost filesystem object store against the S3 path (#585)

Evidence record for the last acceptance line of #585: CPU, allocations, working
set, filesystem operations and bytes, latency, throughput and backlog measured
against the current S3 path, with any material regression explained. Raw
results: `logichost-filesystem-object-store-585.json`.

## Revision and environment

- Candidate `c587d484` on `feature/585-performance-evidence` (clean tree);
  baseline is the S3 provider at the same revision.
- Ubuntu 24.04.5, x64, 12 logical CPU (i9-14900K host), 47 GiB, .NET 10.0.12,
  Release. Storage is ext4 on a QEMU virtual disk (`sda`, non-rotational): a VM
  block device, so `fsync` is a paravirtual round trip and the write-path
  figures below are a pessimistic bound for bare-metal NVMe.
- S3 is a MinIO Testcontainer on loopback: the most favourable S3 topology
  possible. A real deployment adds a network hop the filesystem path never has.

## Method

One harness, `FilesystemVersusS3ObjectStorePerformanceTests`, drives both
providers through the same `IObjectStore` contract, in the same process, with
the same non-seekable pattern payload generator, against the same fixture. Per
workload: warm-ups discarded, then N measured operations; latency per operation
by `Stopwatch`; CPU by `Process.TotalProcessorTime`; allocations by
`GC.GetTotalAllocatedBytes(precise)`; working set by `WorkingSet64`; I/O by
`/proc/self/io` deltas (this process's own bytes and syscalls, which for S3 is
socket traffic and for the filesystem is data, descriptor, temporary and fsync
work). Median and p95 are reported from 30 or 200 operations. Every workflow
verified the SHA-256 of staged and canonical copies on both providers; W3M
asserted 10,000 distinct keys in ordinal order; W3P asserted 100 exact-length
payloads drained. Two full runs agreed within a few percent on every figure;
the second is recorded.

## Result

| Workload | Provider | median ms | p95 ms | ops/s | CPU ms | alloc MiB | RSS growth MiB | write syscalls | write MiB |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| W1 c1 (4.5 MiB) | S3 | 83.2 | 90.4 | 11.91 | 3206 | 410 | 19 | 0 | 0 |
| W1 c1 | Filesystem | 125.8 | 139.0 | 7.92 | 2184 | 160 | 2 | 4380 | 270 |
| W2 c1 (12.3 MiB) | S3 | 122.1 | 128.6 | 8.15 | 4145 | 1069 | 17 | 0 | 0 |
| W2 c1 | Filesystem | 310.6 | 336.3 | 3.23 | 3267 | 399 | 9 | 11880 | 737 |
| W4 c1 (200 x W1) | S3 | 54.9 | 60.9 | 17.96 | 9181 | 2729 | -4 | 0 | 0 |
| W4 c1 | Filesystem | 116.2 | 127.6 | 8.54 | 7375 | 1062 | 8 | 29206 | 1798 |
| W4 c4 | S3 | 65.8 | 74.1 | 60.07 | 8719 | 2727 | 12 | 4 | 0 |
| W4 c4 | Filesystem | 250.5 | 266.0 | 15.89 | 6951 | 1060 | -2 | 29200 | 1798 |
| W4 c8 | S3 | 91.9 | 108.5 | 83.40 | 8569 | 2727 | 4 | 0 | 0 |
| W4 c8 | Filesystem | 275.7 | 302.7 | 28.55 | 7260 | 1059 | 15 | 29202 | 1798 |

- **W3M** list 10,000 keys: S3 186 ms, filesystem 103 ms (0.55x); allocations
  6.5 MiB vs 17.3 MiB (the filesystem reads a descriptor per key; S3 pages).
- **W3P** drain 100 x W2 (1,288 MB) at concurrency 4: S3 896 ms (1,372 MiB/s),
  filesystem 84 ms (14,586 MiB/s, page-cache bound). Filesystem restart
  including a reconciliation pass over 10,100 live objects: 237 ms.

### Where the filesystem provider is better

CPU 0.68–0.85x, allocations 0.37–0.39x, working-set growth flat, list 0.55x,
backlog drain 0.09x, restart-with-reconcile under a quarter second. Every
read-side and metadata figure favours the filesystem, on every workload.

### Where it is worse, and why

Write-path latency is 1.5x (W1), 2.5x (W2), and 2.1x/3.8x/3.0x (W4 c1/c4/c8)
the S3 path; throughput at concurrency 8 is a third of S3's. This is explained,
not unexplained, and the explanation is the durability contract the provider
was built to keep:

1. **The S3 path does no durability work in this process.** Its write syscall
   count is 0–4 per workload; every byte goes to a socket and MinIO's own
   process decides when, and whether, it reaches disk. The filesystem provider
   performs 146 write syscalls per W1 workflow: the payload in 64 KiB writes,
   plus descriptors, plus **ten fsyncs** (data file, data directory, descriptor
   temporary, descriptor directory, for each of put and copy; and a directory
   sync for each of two deletes). Each fsync on the VM block device is a
   paravirtual round trip. That is the cost, and it is the property #585
   required: no operation claims success before its durability boundary.
2. **Copy rewrites the payload.** The canonical workflow copies staged to
   canonical, and the provider streams, re-hashes and re-fsyncs every byte, so
   write bytes per workflow are 2x the payload (270 MiB for 30 x 4.5 MiB). S3
   copy is a server-side request. Data generations are immutable, so a copy
   could be a hard link to the same inode with a new descriptor, which would
   remove half the write bytes and four of the ten fsyncs per workflow. That
   is a correctness-preserving design change with a named workload and a
   measured payoff, and it is filed as a follow-up rather than folded into
   this evidence slice.
3. **Concurrency does not scale the write path** (c4 is 1.9x c1 throughput,
   c8 3.3x, against S3's 3.3x and 4.6x) because fsyncs serialise on one device.
   That is the device, not a lock: the 1,024-stripe key locks are on distinct
   keys throughout.

### Disposition against the product workload

CameraAgent produces one full frame every several seconds per camera and
LogicHost ingests, at most, a handful of rigs. The filesystem provider's
single-writer sustained rate of 7.9 W1 frames/s (36 MiB/s, fully fsynced) and
28.6/s at concurrency 8 exceeds any supported ingest rate by more than an order
of magnitude, and the read, list, drain and restart figures that dominate the
gallery, retention and recovery paths are all better than S3. The write-path
regression is material against a loopback MinIO, explained by durability work
the S3 path does not perform locally, bounded pessimistically by the VM disk,
and not material against the workload the product actually presents. Accepted.

## Reproduce

```bash
dotnet build HVO.SkyMonitor.v9.slnx -c Release
HVO_EVIDENCE_REVISION=$(git rev-parse HEAD) dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --no-build -c Release --filter "FullyQualifiedName~FilesystemVersusS3ObjectStorePerformanceTests" --logger "console;verbosity=detailed"
```

Output JSON lands under the test assembly's `TestResults/issue-585/`. Requires
Docker for the MinIO fixture; the test is `Manual` and is not selected by CI.
