# Issue #1642 measured astrometry reference evidence

This is the bounded Processing adoption slice on `development/v1`, based on
`3b8d2ede0f2bef1c68c822f0643483bc46ae6a97`. The protected profile is **Development
v1 / Preflight** and **Development v1 / Build and Unit**. This ledger records
implementation-stage affected evidence. The parent owner still runs the complete
Tier C candidate gate, records `scripts/ci:classify` on the committed review
range, obtains deep independent review and owns protected CI. No main/release
qualification, host activation or broader #526 credit is claimed.

## Reference envelope and outputs

`MeasuredAstrometryFixture` generates 560 fixed-position synthetic J2000 catalog
rows (seed 934817), projects a 512 x 512 equidistant circular field at latitude/
longitude/elevation zero, UTC 2026-08-25 00:00:00–00:00:01, focal length 144 pixels,
principal point (256,256), radius 232 pixels, altitude 72 degrees, azimuth 243
degrees and input roll 17 degrees. Gaussian stars have sigma 1.15 pixels and peak
`5000 * 10^(-0.4 * (magnitude - 2))`; the actual rendered roll is 17.04 degrees.
Read noise is Gaussian sigma 4 (seed 1205), encoded into little-endian Mono16.
The existing measured-association recipe measures these actual linear samples;
no truth centroids or correspondences are passed to the astrometry recipe.
This is a synthetic software reference, not an installed production catalog or
physical/native camera qualification. It adds no epoch/proper-motion semantics.

The retained measured product has 118 associations, of which 115 are eligible,
unsaturated and untrailed. 106 of those retain usable positive-definite centroid
covariance after six-decimal rounding. The accepted native fit has 82 fitting
and 24 independent verification stars, 9 qualified outliers and 89 expected
isolated fitting stars. Fitting RMS is 0.0068894334 pixels; verification RMS is
0.0083755082 pixels. Width/height coverage are 0.7823113/0.7444098. The score of
100 remains uncalibrated. Tests require altitude within 0.01 degrees, azimuth
within 0.02 degrees, roll within 0.01 degrees and focal ratio within 0.001 of the
reference. Mapping pixel→sky→pixel is checked to 1e-7 pixels.

Native and recipe cold assessment identities are exactly equal:
`ba9546bd83dc8e68536b3593729b89de43288d19f3a795ed9e19ac124488c710`.
Native and recipe warm assessment identities are exactly equal:
`fde0426eee116baaccf91a0dbd6ebe85c05c6504e3176ba9e0ac8335e3a1f26c`.
The repeated same-frame warm result used for resource comparison is 7,386 bytes,
canonical payload/semantic SHA-256
`11CBFB5ED7810CE19F350C36EFE63A91758AD6325F31FF12F24077BE0337FD50`.
The input context is 111,653 bytes, the separately retained measured product
77,862 bytes and the raw payload 524,288 bytes. The solution contains no catalog
rows, full scene, full calibration or copied centroid rows. Caller cancellation
publishes nothing; rejection/unavailability/budget outputs contain no mapping.

The missing/rounded covariance is explicitly `centroid-covariance-missing`;
conditional and total uncertainty are withheld. No shared session calibration
covariance is supplied. This preserves the current measured product's precision
and avoids inventing a floor. Separate tests also exercise all-null and all-zero
retained covariance. The later-capture warm test renders/measures a distinct
capture two seconds later, checks compatible refinement and repeated canonical
identity, and proves the original accepted evidence is byte-unchanged.

## Resource comparison

The [machine-readable report](issue-1642-reference-resources.json) was collected
on Linux x86_64, Intel Core i9-14900K, SDK 10.0.402, Release, concurrency one.
Each of four arms warms up three operations and measures ten, with no arrival
queue, backlog, outage or artifact I/O. The fixture render/measurement and input
construction happen before timing. The native baseline runs the unchanged solver,
residual analyzer, uncertainty estimator and assessment serializer over prepared
inputs. The recipe arm additionally validates frozen inputs, hashes raw bytes,
parses/canonicalizes bounded context/scene/measured evidence, retains the compact
output and parses it again. The extra retained parse deliberately measures
consumer reconstruction overhead as well as production. CPU is process time;
allocations are process-wide managed allocation deltas, not live heap size.
Working-set/peak counters cover the MSTest process, including prior test work,
and are not isolated per-operation peak measurements.

| Path | p50 / sample p95 ms | CPU ms for 10 | Allocated bytes/op |
| --- | ---: | ---: | ---: |
| Native cold | 888.69 / 1053.52 | 9172.94 | 1,209,363,387 |
| Recipe cold + retained parse | 874.67 / 1019.04 | 9109.80 | 1,225,163,957 |
| Native warm | 1.66 / 9.82 | 25.08 | 1,097,884 |
| Recipe warm + retained parse | 17.93 / 29.53 | 267.53 | 16,986,084 |

Cold timing differences are measurement/GC noise, not a speed-up claim. The
wrapper adds about 15.8–15.9 MB managed allocation per operation for strict JSON,
canonicalization and lineage checks. Warm median overhead is about 16.3 ms.
The inherited cold solver allocates about **1.21 GB/op**; no solver replacement
or allocation improvement is claimed. The process peak was 256,983,040 bytes
(about 245 MiB), with sampled working sets from 173,944,832 to 227,151,872 bytes.
There is no per-frame host/live default and cold concurrency must remain bounded.
The current recipe performs no new full-frame decode/copy; its raw work is one
checksum pass. Input/JSON work is bounded by 16M samples, 2,500 catalog rows and
the existing measured/scene byte bounds. Existing solver work bounds still own
triangle search. Mapping application is a separate capped 4,096-point operation.

## Executable evidence and discovery

All heavy commands use the shared host lock and explicit timeout. Unit runs
unset `HVO_PRODUCTION_CATALOG_BUNDLE` and set the invalid Docker endpoint. From
the issue worktree, with `EVIDENCE` an existing directory created by `hvo scratch`:

```bash
flock -o /tmp/hvo-heavy-gate.lock timeout 300 env -u HVO_PRODUCTION_CATALOG_BUNDLE \
  DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj \
  -c Release --no-restore -nodeReuse:false -p:UseSharedCompilation=false -warnaserror \
  --filter 'TestCategory=Unit' --logger 'trx;LogFileName=processing-unit.trx' \
  --results-directory "$EVIDENCE"

flock -o /tmp/hvo-heavy-gate.lock timeout 180 env -u HVO_PRODUCTION_CATALOG_BUNDLE \
  DOCKER_HOST=unix:///tmp/hvo-no-docker.sock HVO_ISSUE1642_EVIDENCE="$EVIDENCE" \
  dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj \
  -c Release --no-build -nodeReuse:false -p:UseSharedCompilation=false \
  --filter 'FullyQualifiedName~MeasuredAstrometryResourceTests' \
  --logger 'trx;LogFileName=resources.trx' --results-directory "$EVIDENCE"
```

The final affected Processing Unit selection passed **942/942** in 28 seconds,
with Release build and warnings-as-errors, including the separate later-capture
warm fixture and publication-contract source checks. The focused recipe
selection separately passed **18/18** in 12 seconds. The Manual resource case
passed and produced the retained report. Focused source and test formatting
verification passed. There are **18** new Unit acceptance methods and **1**
Manual resource method. Acceptance includes accepted/rejected/no-data/sparse/clustered/degenerate/
outlier inputs, incomplete measurements, malformed/mismatched artifacts,
catalog/time/observer/calibration/readout lineage, changed catalog coordinates,
canonical/size/native evidence rejection, bounded caller/internal cancellation,
warm settings/package compatibility and immutable mapping reconstruction.

Actual `dotnet test --no-build --list-tests --diag <file>` discovery was read
from `TestDiscovery.TestFound` and `TestDiscovery.Completed` messages, once
unfiltered and once for each primary category. Processing discovery is
**All=959, Unit=942, Manual=17, Integration=0, Soak=0, External=0, Hardware=0**;
category membership is disjoint and its union equals unfiltered discovery.
The project inventory is updated to those observed counts. The aggregate
runbook totals become Unit=14003 and Manual=286; other project inventories are
unchanged. Whole-solution discovery and complete candidate checks remain the
parent's gate, not evidence inferred from this component run.
