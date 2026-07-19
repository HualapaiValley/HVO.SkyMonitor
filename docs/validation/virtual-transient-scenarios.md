# Virtual Transient Scenario Validation

Status date: 2026-07-19

This evidence covers deterministic generic sky tracks and sensor charge for
issue #61. It does not claim physical meteor photometry, detector sensitivity,
classification quality, runtime scheduling, video, clips, or real-event
validation.

## Deterministic Fixtures

`tests/fixtures/virtual-sky/transient-scenarios-v1.json` is the test-only
semantic oracle. Production configuration and provenance contain only opaque
primitive identities and rendering parameters.

| Fixture | Raw min | Raw max | Raw mean | First Mono16 SHA-256 |
| --- | ---: | ---: | ---: | --- |
| no event | 0 | 1 | 0.5416666666666666 | `D9438A1E5E3B6CC7D74EF56B4C6054C74D7FDC3F06E22845D9398483CB8211CC` |
| short track | 0 | 836 | 5.259765625 | `C6D59E2BBCF44F6820A9E77E5E9A926BDBC0A9CEAB376544FF32F0D1037E6206` |
| fragmented flare | 0 | 65535 | 420.3030598958333 | `BE4C2467147DDE396598AC78FB1D5F32004F0A443FD2D3DDB2B04C4BB32A0D52` |
| boundary crossing | 0 | 746 | 3.7766927083333335 | `7034977C6C7273EE207FF930B0803213A1BCDAFB7724E1C842A45134CA4381A7` |
| long shadow | 0 | 16 | 0.5729166666666666 | `138BC7259C0B7530C63FA3A86DD4E6FD754045357E195622187CE988FA62D514` |
| blinking track | 0 | 180 | 1.7174479166666667 | `2B45C8EAD0B20364D0F990C1725BB03E42ED20AB05C93F80959B6C1314AB4378` |
| sensor artifacts | 0 | 19973 | 42.858723958333336 | `DCECBCCB97C5869632E3C54A60C0DF2BCA7A422E1E0DAEE693802BC5A870212B` |

The second boundary exposure is pinned as
`B13D64D6E20EF8867AD2DC7374B1F2753E7ACAABC4D206630E27A3F0458463BA`.
Focused geometry tests prove ordered centroids and energy conservation across
adjacent half-open exposures. Other tests cover restart and reversed-order
determinism, azimuth wrap, aggregate bounds, non-overlap, cloud/optics versus
sensor-stage separation, Mono16/RGB24/RGGB16 response, saturation, strict
configuration, manifest integrity, and detector-input isolation.

## Performance

Baseline revision: `e402bfeb0607101eeec95e292104722d3837c630`.
The nearest clean baseline is the existing transient-disabled W1/W2 complete
module and render-only harness. Candidate cases use the same dimensions, empty
catalog, seed `2025`, UTC `2025-01-15T08:00:00Z`, one-second exposure,
concurrency one, five case warmups, and 30 measurements. Runtime tiering is
stabilized equally for null and active transient paths before measurements.

Command:

```bash
HVO_EVIDENCE_REVISION=<revision> \
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --no-build --configuration Release \
  --filter "FullyQualifiedName~VirtualSkyTransientPerformanceTests.W1AndW2TransientRenderEvidence"
```

| Workload | Scenario | Complete median/p95 | Render median/p95 | Active sparse pixels |
| --- | --- | ---: | ---: | ---: |
| W1 1936x1216 Mono16 | none | 59.65/65.07 ms | 58.53/62.32 ms | 0 |
| W1 | explicit empty | 61.38/63.76 ms | 58.82/62.36 ms | 0 |
| W1 | short sky track | 70.78/78.41 ms | 69.98/74.19 ms | 786 |
| W1 | saturated fragments | 68.84/72.88 ms | 69.00/72.18 ms | 2,222 |
| W1 | long shadow/blink | 71.48/73.14 ms | 70.62/79.58 ms | 1,602 |
| W1 | sensor artifacts | 68.27/71.82 ms | 68.35/72.83 ms | 215 |
| W1 | partial cloud/track | 477.82/482.28 ms | 477.61/484.09 ms | 801 |
| W2 3096x2080 RGGB16 | none | 576.23/598.12 ms | 573.84/591.48 ms | 0 |
| W2 | explicit empty | 574.09/600.24 ms | 570.51/594.74 ms | 0 |
| W2 | short sky track | 668.78/692.69 ms | 670.11/697.42 ms | 818 |
| W2 | saturated fragments | 665.60/690.68 ms | 663.69/682.05 ms | 3,765 |
| W2 | long shadow/blink | 655.12/681.37 ms | 655.02/682.49 ms | 1,624 |
| W2 | sensor artifacts | 633.49/660.58 ms | 628.34/655.55 ms | 215 |
| W2 | partial cloud/track | 2043.59/2062.36 ms | 2025.01/2061.33 ms | 1,218 |

The identical prewarmed no-cloud harness was run in both directions between a
detached parent worktree and the candidate. The retained comparable parent
trial measured W1 complete/render medians of `61.4295/59.3849 ms` and W2 medians
of `602.9345/595.0340 ms`; the candidate measured `63.3649/61.1971 ms` and
`607.5200/610.2003 ms`. Deltas were `+3.15%/+3.05%` for W1 and
`+0.76%/+2.55%` for W2. Trial ranges overlapped, allocations were unchanged,
and all values remained below the pinned 5% investigation threshold. The matrix
control independently measured no-transient medians within that same range.

| Path | CPU parent/candidate | Allocation delta | Peak RSS delta | Throughput delta |
| --- | ---: | ---: | ---: | ---: |
| W1 complete | 2328.670/2384.933 ms | +0.011% | +0.14% | -2.56% |
| W1 render | 1857.603/1908.614 ms | +0.008% | +0.13% | -2.56% |
| W2 complete | 18404.833/18381.704 ms | +0.0001% | -0.06% | -3.66% |
| W2 render | 18040.073/18390.537 ms | +0.0001% | +0.13% | -2.08% |

CPU and peak RSS are unchanged within normal run variance, and allocations are
effectively byte-stable. The W2 complete throughput delta includes one candidate
scheduling outlier (811 ms p95 versus a 607.52 ms median); its CPU decreased,
median changed only 0.76%, trial ranges overlapped, and the repeated matrix
control was faster than the parent median. No resource regression is indicated.

Exact transient-disabled and cloud-only checksums remain unchanged. Every
transient case has pinned complete/render SHA-256 and numeric active-pixel/
deposited-energy invariants; the slowest p95 is 2.062 seconds, over twelve times
inside the 25-second configured arrival interval.

Transient storage is one sparse `Dictionary<int, VirtualTransientPixelSignal>`;
the largest measured support was 3,765 pixels, or 0.059% of W2. It adds no dense
truth mask or full-frame transient plane. W1 retains its normal signal/output
buffers and evaluates cloud values on demand. The combined W2 case reuses the
existing temporary two-float cloud map (51,517,440 bytes) across three channels.
Enabled CPU and latency overhead comes from sparse signal construction and
lookups inside the mandatory full-frame sensor/CFA pass, not retained dense
state. The corrected harness records measured pre/post GC counts, in-sequence
heap/LOH/RSS, allocations, CPU, parse/canonicalization cost, source/config/
provenance bytes, throughput, and exact output length.

## Ordinary Pipeline

The configured two-host integration fixture jointly enables cloud and transient
scenarios. It proves a saturated sensor-stage pixel at `(1,1)` in raw Mono16 and
the exact corresponding Mono8 preview pixel, transitive preview-to-raw lineage,
manifest provenance, local storage, durable ingress, processing, upload, and
central persistence. The retained trial recorded 62 files totaling 2,974,751
bytes, including nine SQLite/WAL files totaling 2,684,464 bytes. Four captures
committed 24,576 ingress bytes and produced 20 processing outputs totaling
86,016 bytes. Final raw-ingress and processing pending/retry/quarantine/
terminal counts were all zero, and the authenticated upload drain reached a
checkpoint with zero pending, leased, retrying, or quarantined records.

The run observed bounded capture, capture-control, raw-ingress, processing, and
environmental metrics plus activities from `capture-cycle` through
`processing-artifact.persist`. Health remained accepting/healthy. Logs, metric
tags, and span tags contained none of the scenario identity/hash, canonical
parameters, coordinates, oracle labels, raw payload, credentials, tokens, or
internal paths. All errors and all VirtualSky/environmental warnings are
forbidden; unrelated TestServer request-limit, fixture-health, and existing EF
query warnings in captured host output are explicitly outside this feature
gate. Ignored JSON/TRX evidence is written under
`TestResults/issue-61/`.
