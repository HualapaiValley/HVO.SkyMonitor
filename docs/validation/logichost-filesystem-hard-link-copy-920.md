# Filesystem hard-link copy evidence (#920)

Issue #920 replaces the filesystem provider's payload-rewriting copy with a
descriptor-relative hard link for immutable generations. The destination still
receives a new generation name and descriptor; the descriptor remains the sole
commit point. Unsupported Linux filesystem/policy cases and non-Linux platforms
retain the streaming-copy fallback.

Raw evidence: `logichost-filesystem-hard-link-copy-920.json`.

## Revision and method

- Candidate `eee24a1c`; baseline is the forced
  streaming fallback in the same candidate process.
- Ubuntu 24.04.5 x64, 12 logical CPUs, .NET 10.0.12, Release, ext4 under `/tmp`.
- The same `IObjectStore` workflow and deterministic payload generator run on
  separate roots on the same filesystem. Every operation performs put, digest
  read, copy, digest read, and delete of both logical objects. W1/W2 use five
  warmups and 30 measured operations; W4 uses 20 warmups and 200 measured
  operations at concurrency 1, 4, and 8.
- Metrics: operation median/p95, throughput, process CPU, managed allocations,
  RSS, and `/proc/self/io` bytes/syscalls.

## Result

| Workload | Median stream/link | p95 stream/link | Throughput stream/link | CPU ratio | Allocation ratio | Write-byte ratio |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| W1 c1, 4.5 MiB | 142.9 / 83.5 ms | 160.1 / 87.6 ms | 6.95 / 12.13 ops/s | 0.61x | 0.98x | 0.50x |
| W2 c1, 12.3 MiB | 381.4 / 203.3 ms | 455.7 / 213.3 ms | 2.60 / 4.91 ops/s | 0.74x | 0.99x | 0.50x |
| W4 c1 | 133.3 / 78.6 ms | 151.3 / 89.9 ms | 7.41 / 12.46 ops/s | 0.79x | 0.98x | 0.50x |
| W4 c4 | 265.9 / 145.3 ms | 284.1 / 165.9 ms | 14.99 / 26.84 ops/s | 0.74x | 0.98x | 0.50x |
| W4 c8 | 291.8 / 163.9 ms | 314.5 / 186.1 ms | 26.83 / 46.99 ops/s | 0.76x | 0.98x | 0.50x |

The candidate cuts median latency to 0.53-0.59x and p95 to 0.47-0.59x,
increases throughput by approximately 1.7-1.9x, reduces CPU to 0.61-0.79x,
and halves physical write bytes in every workload. Allocation behavior is
effectively unchanged. The result matches #920's expected mechanism: copy no
longer rewrites and fsyncs the payload, but still reads and hashes all bytes
before publishing the destination descriptor.

## Correctness and durability

- `linkat`, open, hashing and identity checks are relative to authenticated
  no-follow directory handles. Device, inode, size and regular-file type match
  between source and linked destination.
- The destination directory handle is flushed after link publication. The
  destination name is reverified against the hashed identity before descriptor
  publication.
- Digest mismatch, cancellation and pre-descriptor failures remove only the
  linked inode owned by the operation and flush that same directory handle.
- Once descriptor publication begins, linked data is retained on failure,
  matching the existing streaming path: descriptor rename may have committed
  before a later directory-sync error, so deleting data would be unsafe.
- Source replacement/reclamation is reclassified through the existing
  Missing/Precondition/CorruptState descriptor recheck. Deleting or reclaiming
  one hard-link name does not remove the other object.
- Reconciliation takes the same destination-key lock as publication and rereads
  the descriptor under that lock, so an in-flight linked generation cannot be
  stamped as retired before its descriptor commits. The benchmark asserts every
  candidate copy used the hard-link path and refuses fallback-labelled evidence.
- The supported topology has one trusted LogicHost writer and excludes external
  mutation of provider-owned physical paths. #586 qualifies and enforces that
  boundary independently.

## Reproduce

```bash
dotnet build tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj -c Release
HVO_EVIDENCE_REVISION=$(git rev-parse HEAD) dotnet test \
  tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --no-build -c Release \
  --filter 'FullyQualifiedName~CanonicalWriteMatrix_HardLinksAgainstStreamingCopy' \
  --logger 'console;verbosity=detailed'
```
