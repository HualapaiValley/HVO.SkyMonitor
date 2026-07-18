# Virtual Cloud Scenario Validation

Status date: 2026-07-18

This evidence covers the deterministic cloud rendering candidate for issue
#104. Physical cloud realism, field calibration, image-derived assessment, and
transient generation are explicitly outside this evidence.

## Deterministic Fixtures

`tests/fixtures/virtual-sky/cloud-scenarios-v1.json` is the test-only semantic
oracle. Production metadata contains only opaque scenario identity and
reconstruction parameters, never these labels.

| Fixture | Equal-area cover | Raw min | Raw max | Raw mean | Mono16 SHA-256 |
| --- | ---: | ---: | ---: | ---: | --- |
| clear | 0 | 0 | 400 | 216.66666666666666 | `A710AB6142EF0A7228FDA0CD821B9D1F110A9A52C4E7135FE05DF468F8B68CC2` |
| scattered | 0.2881944444444444 | 0 | 400 | 174.47493489583334 | `F5F8E5564501D18D49D6B8BE92EAA64C6019A5C43C0AFFC00E39E37192FC8EBB` |
| broken | 0.7430555555555556 | 0 | 400 | 99.587890625 | `65E1B2C96D7663F04133684FC79553C660ADC6D1B0915AB5AA2AF8FFB6FF4D64` |
| overcast | 0.9166666666666666 | 0 | 398 | 51.626953125 | `E706091EDDC07112102B8C2C52D144C88346F60602372B0FBD6DCCA65BBEB8ED` |

The focused tests also prove seed/time repeatability, out-of-order evaluation,
motion, transition overlap, geometric-horizon behavior, pre-sensor ordering,
clear/no-cloud equality, Mono16/RGB24/RGGB16 operation, canonical provenance,
manifest round-trip, deterministic observation identity, and targetless
simulated-fact production. The configured two-host integration fixture proves
raw ingress, derivative processing, provisioning-owned enrichment, durable edge
outbox, authenticated LogicHost ingest, and central `CloudCover` persistence.
Manifest v1 remains legacy-incomplete but round-trips its optional scene and
cloud provenance. Manifest v2 carries the same additive cloud provenance;
existing v1/v2 and old sidecars without that property retain their previous
identities and behavior.

## Performance Candidate

Command:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --no-build --configuration Release \
  --filter "FullyQualifiedName~VirtualSkyCloudPerformanceTests.W1AndW2CloudRenderEvidence"
```

Environment: `.NET 10.0.0`, `linux-x64`, 8 logical processors. Each case used
five warmups and 30 complete `VirtualSkyCameraModule.CaptureAsync`
measurements plus 30 render-only measurements with an empty deterministic
catalog, fixed five-second UTC progression, and exact output-length/checksum
assertions. First-10 and complete-30 sequence medians are retained in JSON.
`O x T` is noise octaves by exposure midpoint samples.

| Workload | Scenario | Exposure | O x T | Complete median/p95 | Render median/p95 | Frames/s | CPU/frame | Final complete SHA-256 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| W1 1936x1216 Mono16 | base | 1 s | 0 x 0 | 64.61/71.02 ms | 63.57/67.86 ms | 15.300 | 79.91 ms | `3FBC9C0786E50F5C88468949000D9AAA97C32875B00009E28EBE88C360A291E1` |
| W1 1936x1216 Mono16 | explicit clear | 0.1 s | 1 x 1 | 45.43/49.36 ms | 44.09/46.15 ms | 21.845 | 56.54 ms | `FAB9A6BEA6DA154DEF8F23C192300B1222BF37B933EF83695A69C5CB27714EB5` |
| W1 1936x1216 Mono16 | partial sustained | 1 s | 3 x 2 | 471.29/490.50 ms | 475.39/497.49 ms | 2.112 | 478.89 ms | `1190FC57EF910CC45EFD1F9B3722E74792D78172514B0C67FDB364F5CE6574F0` |
| W1 1936x1216 Mono16 | transition | 4 s | 5 x 8 | 2072.13/2128.86 ms | 2106.17/2129.35 ms | 0.481 | 2085.09 ms | `41FB82F653B8A0ED6D91C946F4805AA8AEE21AECFAB17DE508F27720F6C51664` |
| W1 1936x1216 Mono16 | overcast | 10 s | 4 x 4 | 927.37/949.76 ms | 941.07/961.38 ms | 1.073 | 934.35 ms | `7DEC92F9A133884D236B1A75627C3019A1C9D97E9E82A98083A957A781A058B9` |
| W2 3096x2080 RGGB16 | base | 1 s | 0 x 0 | 614.20/644.08 ms | 615.28/635.89 ms | 1.623 | 619.94 ms | `6CA0EE6A978B2044B96B88C9998A32344D0109E1FE60576DAB56F79F24F12A1B` |
| W2 3096x2080 RGGB16 | explicit clear | 0.1 s | 1 x 1 | 415.53/434.85 ms | 418.11/430.37 ms | 2.403 | 419.11 ms | `8D6BE092D14FB0E5EB9283C7947852CBF39C82E054D76F3488CCEE16CFC5C75C` |
| W2 3096x2080 RGGB16 | partial sustained | 1 s | 3 x 2 | 2043.93/2082.22 ms | 2046.04/2071.40 ms | 0.488 | 2054.06 ms | `83C8076903518D8C7F2E1272CD3FC35FE3F1FB0512924420462FADD2640B48BB` |
| W2 3096x2080 RGGB16 | transition | 4 s | 5 x 8 | 7978.77/8170.65 ms | 7912.98/8153.56 ms | 0.125 | 8006.89 ms | `C97C4B8ACD717BEDA61C16F4C6956432D5630BBFF78FE25D07AAD90A9A79046F` |
| W2 3096x2080 RGGB16 | overcast | 10 s | 4 x 4 | 3752.60/3792.65 ms | 3808.33/3817.99 ms | 0.266 | 3762.97 ms | `05DDD55D8AE3CB982EC7F52EC4885396807CE369E82796CF3DC24D3A6EA6CC92` |

| Workload | Scenario | Output | Allocated/frame | Peak RSS | Post-GC live delta |
| --- | --- | ---: | ---: | ---: | ---: |
| W1 | base | 4,708,352 B | 23,636,280 B | 243,658,752 B | 5,016,592 B |
| W1 | explicit clear | 4,708,352 B | 23,629,559 B | 253,259,776 B | 4,732,320 B |
| W1 | partial sustained | 4,708,352 B | 23,630,355 B | 255,934,464 B | 4,731,408 B |
| W1 | transition | 4,708,352 B | 23,637,412 B | 254,914,560 B | 4,730,600 B |
| W1 | overcast | 4,708,352 B | 23,629,314 B | 254,910,464 B | 4,732,160 B |
| W2 | base | 12,879,360 B | 167,497,308 B | 955,924,480 B | 12,901,840 B |
| W2 | explicit clear | 12,879,360 B | 167,520,999 B | 956,616,704 B | 12,894,848 B |
| W2 | partial sustained | 12,879,360 B | 219,039,616 B | 1,253,146,624 B | 12,903,224 B |
| W2 | transition | 12,879,360 B | 219,054,937 B | 1,254,649,856 B | 12,895,248 B |
| W2 | overcast | 12,879,360 B | 219,042,106 B | 1,254,821,888 B | 12,903,024 B |

W1 evaluates non-clear cloud fields on demand and adds no full-frame
allocation. W2 adds one temporary two-float effect map, approximately 51.5 MB,
so three sensor channels reuse identical cloud values. Explicit-clear scenarios
bypass field evaluation and the W2 map while retaining cloud provenance. The
worst measured W2 transition p95 is 8.17 seconds, 3.06 times faster than the
configured 25-second arrival cadence. Render-only allocation follows the same
expected ownership: about 23.6 MB/frame for W1, 167.5 MB/frame for W2 without a
field map, and 219.0 MB/frame for non-clear W2 cloud cases.

The CPU increase follows field complexity and exposure sample count: each
active pixel requires one sky unprojection and a three-dimensional seeded field
evaluation for each octave and midpoint. W2 working-set pages remain reserved
by the runtime after large-object-heap collections, but post-collection live
growth stays approximately one retained output buffer in every case, including
both 30-frame sequences. The harness records Gen0/1/2 collections, working-set
start/peak/end, managed heap start/end, LOH size and fragmentation before/after,
and total CPU in its ignored JSON output. Complete W1 cases ended with a
61,307,352-byte LOH containing 51,792,248 fragmented bytes; non-clear W2 cases
ended with a 128,892,208-byte LOH containing 103,035,088 fragmented bytes.
Render-only W1 and warmed non-clear W2 cases added no further LOH segment, and
post-collection live growth remained one output buffer. The retained fragmented
segments explain RSS retention without evidence of frame-by-frame live-object
growth. No cloud map or full-frame truth survives a render, and no unexplained
regression was observed.

## Observation Enqueue

The actual canonical VirtualSky partial-cloud payload was measured through the
cloud processing step and durable SQLite enqueue with five warmups and 30
measurements. Median latency was 3.6631 ms, p95 was 4.6303 ms, throughput was
282.78 observations/s, CPU was 56.447 ms total, and allocation was 395,577
bytes/operation. The 35 warmup/measured rows occupied 53,703 payload bytes and
1,108,096 SQLite/WAL bytes. Peak RSS was 108,605,440 bytes, with one Gen0 and no
Gen1/Gen2 collections. This is over 5,000 times inside the 25-second configured
arrival cadence.

The two-host integration run reported every configured processing node as
successful, returned healthy raw-ingress, processing, and environmental-delivery
checks, correlated one exact simulated cloud observation with capture
provenance, and observed that fact leave the durable edge outbox. It also
observed emitted measurements from edge enqueue, edge delivery, and central
ingest instruments plus fixed
`environment.enqueue`, `environment.deliver`, and `environment.ingest`
activities. Metric tags included categorical `CloudCover` and `Simulated`
values. Captured environmental logs had no warning/error entries, and combined
metric, activity, and log text contained none of the scenario identity/hash,
canonical parameters, site/device GUIDs, test credentials, or field parameter
names.

These measurements were taken in the authorized implementation worktree. The
same harness must be rerun on the exact clean candidate revision before the PR
performance gate is considered final.
