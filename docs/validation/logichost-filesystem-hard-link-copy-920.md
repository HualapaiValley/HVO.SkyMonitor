# Filesystem hard-link copy evidence (#920)

Issue #920 replaces the filesystem provider's payload-rewriting copy with a
descriptor-relative hard link for immutable generations. The destination still
receives a new generation name and descriptor; the descriptor remains the sole
commit point. Unsupported Linux filesystem/policy cases and non-Linux platforms
retain the streaming-copy fallback.

Raw evidence: `logichost-filesystem-hard-link-copy-920.json`.

## Revision and method

- Candidate `1f0d4bb0bd7885f2c44f04afc00aacd0801a46d2`; baseline is the forced
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
| W1 c1, 4.5 MiB | 127.9 / 75.2 ms | 141.2 / 79.8 ms | 7.80 / 13.39 ops/s | 0.60x | 0.97x | 0.50x |
| W2 c1, 12.3 MiB | 321.8 / 185.8 ms | 352.2 / 198.3 ms | 3.09 / 5.41 ops/s | 0.81x | 0.99x | 0.50x |
| W4 c1 | 121.0 / 72.1 ms | 130.1 / 79.4 ms | 8.23 / 13.77 ops/s | 0.81x | 0.98x | 0.50x |
| W4 c4 | 257.5 / 141.7 ms | 273.5 / 152.3 ms | 15.45 / 27.77 ops/s | 0.85x | 0.98x | 0.50x |
| W4 c8 | 284.9 / 160.3 ms | 308.8 / 179.8 ms | 27.18 / 48.45 ops/s | 0.65x | 0.98x | 0.50x |

The candidate cuts median latency to 0.55-0.60x and p95 to 0.56-0.61x,
increases throughput by approximately 1.7-1.8x, reduces CPU to 0.60-0.85x,
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
