# CameraAgent Current-Profile Lane Evidence: Issue 257

This document records the reviewed absolute baseline for issue
[#257](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/257). It replaces
the obsolete issue-95 profile/schema comparison; it does not claim a
cross-profile improvement.

## Identity And Method

- Revision: `4cb0cdcef7f1d9df2e0923ee9b993454e0252843` on
  `perf/cameraagent-lane-rebaseline-257`, clean working tree.
- Environment: Ubuntu 24.04.3 LTS, x64, 8 logical CPUs, .NET SDK `10.0.100`,
  .NET `10.0.0`, Release/server GC, SQLite `3.53.3`, overlay storage.
- W2 profile:
  `src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json`, SHA-256
  `227FB3C0484AB5BBAFC4CA3674EA0D68B473315EBDD63F547CB2851D0A00F499`.
- Raw-ingress schema: `10` in every trial.
- Test assembly SHA-256:
  `3601965960288E6E8299C7810637D106B9F68BB9DA035B917C164938AAF25286`.
- Production assembly SHA-256:
  `1FB2971101E252C255A7F2451D72B94CA8A92697F5907EE4A0667AD288466FC1`.
- Five independent trials each used separate capture-control and durable-lane
  processes, with trial-level order `control-UB`, `BU-control`, `control-UB`,
  `BU-control`, `control-UB`. Each W2 measurement used five warmups plus 30
  measured operations. Each live scenario persisted 100 full-resolution W2
  payloads. Production claim/ack statistics used five warmups plus 30 measured
  samples; ingress statistics used all 100 commits.
- Reproduce the complete source-attributed campaign with
  `./scripts/test:cameraagent-lanes-257`. The runner rejects dirty/mismatched
  source and assemblies, existing output, invalid trial order, altered
  per-trial manifests, and concurrent cooperating issue-257 campaigns.

## Results

Values below are five-trial minimum / median / maximum. Cross-trial p95 is not
inferred.

| Measurement | Result |
| --- | ---: |
| W2 ingress median | `27.5571 / 29.1736 / 29.4557 ms` |
| W2 ingress p95 | `33.8366 / 39.1925 / 43.2450 ms` |
| W2 ingress throughput | `33.0514 / 33.1691 / 33.8907 captures/s` |
| W2 CPU | `542.148 / 560.269 / 582.144 ms` |
| W2 allocated | `16,084,144 / 16,117,344 / 16,983,600 bytes` |
| W3M schema migration | `1,560.4967 / 1,814.6258 / 1,874.5424 ms` |
| W3M allocated | `509,516,944 / 511,399,864 / 512,351,240 bytes` |
| W3M restart discovery median | `187.4967 / 191.2716 / 195.9849 ms` |
| W3M restart allocated | `748,592 / 770,744 / 778,984 bytes` |
| Synthetic 10,000-row indexed traversal | `61.8761 / 63.3989 / 74.0017 ms` |
| Production claim p95, unblocked | `36.7457 / 38.5210 / 41.8305 ms` |
| Production claim p95, blocked | `4.8022 / 33.9531 / 36.4264 ms` |
| Required-lane drain | minimum `22.1164 captures/s` |
| Optional-lane recovery | minimum `199.1772 captures/s` |
| Sparse W2 metering wall p95 | `0.107135 / 0.109220 / 0.127183 ms` |
| Sparse W2 metering allocated | `0 / 0 / 0 bytes` |

The W3M direct SQL loop is labeled `synthetic-indexed-traversal`; it is not
production claim latency. Production claim measurements come from the ordinary
`SqliteCaptureLaneStore.ClaimAsync` worker path. Concurrent live W3P allocation
is `N/A`: process-wide allocation counters were not reliable across those
worker windows. W2 ingress, W3M migration/restart, and sparse metering retain
the claimable allocation evidence.

## Isolation And Correctness

- Every blocked trial retained exactly 100 optional rows until release while
  all 200 required standard/upload rows completed. Optional recovery then
  drained at least 199.1772 captures/s.
- Every live scenario ended with 300 completed lane rows and zero pending,
  leased, retry-wait, quarantined, or retention-held rows.
- Every payload length, checksum, sidecar, manifest/journal identity, profile,
  layout, and sequence was validated. W2 produced 35 unique durable captures;
  each W3P scenario produced 100 captures and 1,287,936,000 raw bytes.
- W3M consistently migrated 10,000 raw rows, 30,000 reference-only lane rows,
  and 1,000 legacy contexts; SQLite integrity was `ok`, the ordered index was
  used, and every restart discovered 30,000 rows.
- Required lane metrics/activities and graceful drain event `2058` were present.
  No forced-shutdown, claim, handler, or lease-loss event `2059`-`2062`
  occurred. Maximum observed within-trial RSS median growth was 6,160,384
  bytes against the 67,108,864-byte limit.

Blocked-minus-unblocked ingress p95 deltas were `-11.3262`, `-11.3933`, and
`-7.2230 ms` for UB order, and `+2.1104` and `-5.1937 ms` for BU order. The
causal latency result is `N/A`: mixed order-stratified results from a 3/2
scenario ordering are insufficient for blocked-versus-unblocked attribution.
No latency pass or improvement claim is made; functional optional-lane
isolation is the hard result.

## Artifact Integrity

- Authoritative root:
  `$HVO_AGENT_STATE_ROOT/issues/257/4cb0cdcef7f1d9df2e0923ee9b993454e0252843/`.
- Five-trial summary: 7,877 bytes, SHA-256
  `F98217EFD7858165071A1A44EF617D5A2DDFAB9B0E7C41C8E38CFADC5C6BC002`.
- Manifest: 4,592 bytes, SHA-256
  `0CDA35AEEA7B1AC5BC76B9C8751708CD398733F37B79914A6334A1E70426C035`.
- All 26 manifested artifacts were independently rehashed and matched; the
  aggregate was regenerated byte-identically from the retained trial records.

Physical camera/SDK behavior, non-overlay storage, and a causal latency
comparison remain outside this baseline. Physical x64/ARM64 performance is
owned by #268. SQL Server, Redis, MinIO, central HTTP/network, and LogicHost
behavior are also `N/A` because this CameraAgent-only campaign did not invoke
them.
