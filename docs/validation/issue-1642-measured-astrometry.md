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
The repeated same-frame warm result used for resource comparison is 18,358 bytes,
canonical payload/semantic SHA-256
`61C5B015503F57B411D857484985FFFB685830CA5BD4F68E70AAC9849B92A374`.
The input context is 111,653 bytes, the separately retained measured product
77,862 bytes and the raw payload 524,288 bytes. The solution contains no catalog
rows, full scene, full calibration or copied centroid rows. It retains 106 native
association records, preserving their full-precision residuals and verification
flags so the native association hash and measured membership can be checked
without refitting. Caller cancellation publishes nothing; rejection/unavailability/budget outputs contain no mapping.

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
| Native cold | 913.20 / 1267.76 | 9494.79 | 1,209,368,292 |
| Recipe cold + retained parse | 901.05 / 1353.33 | 9865.90 | 1,225,876,949 |
| Native warm | 1.66 / 9.98 | 25.57 | 1,103,787 |
| Recipe warm + retained parse | 15.22 / 24.57 | 231.74 | 17,701,672 |

Cold timing differences are measurement/GC noise, not a speed-up claim. The
wrapper adds about 16.5–16.6 MB managed allocation per operation for strict JSON,
canonicalization and lineage checks. Warm median overhead is about 13.6 ms.
The inherited cold solver allocates about **1.21 GB/op**; no solver replacement
or allocation improvement is claimed. The process peak was 273,903,616 bytes
(about 261 MiB), with sampled working sets from 168,591,360 to 229,875,712 bytes.
There is no per-frame host/live default and cold concurrency must remain bounded.
Retained native association integrity and
uncertainty validation add about 11 KiB to the reference output and about 0.7 MB
managed allocation per recipe operation versus the earlier membership-only
envelope. The software reference remains within its declared byte/work bounds;
these timings do not establish host throughput or production qualification.
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
  --filter 'TestCategory=Unit' --settings tests/coverage.runsettings \
  --collect:'XPlat Code Coverage' --logger 'trx;LogFileName=processing-unit.trx' \
  --results-directory "$EVIDENCE"

flock -o /tmp/hvo-heavy-gate.lock timeout 180 env -u HVO_PRODUCTION_CATALOG_BUNDLE \
  DOCKER_HOST=unix:///tmp/hvo-no-docker.sock HVO_ISSUE1642_EVIDENCE="$EVIDENCE" \
  dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj \
  -c Release --no-build -nodeReuse:false -p:UseSharedCompilation=false \
  --filter 'FullyQualifiedName~MeasuredAstrometryResourceTests' \
  --logger 'trx;LogFileName=resources.trx' --results-directory "$EVIDENCE"
```

The affected Processing Unit selection passed **978/978** and its focused
measured-astrometry selection passed **54/54**. Astronomy Unit passed **791/791**;
both complete Unit selections collected XPlat coverage and built Release with
warnings as errors. Astronomy's separate Integration selection passed **2/2**.
Changed source/test formatting verification and the operations documentation
audit passed. The representative resource method passed once and produced the
report above. Astronomy adds **10** retained
uncertainty validation cases, including native available/withheld/shared/clock
combinations and malformed available covariance. Focused acceptance includes membership replacement, fitting/outlier and
fitting/verification swaps, self-consistent native evidence with nonexistent or
wrong-catalog measured rows, one-bit residual changes, rehashed uncertainty
bindings, contradictory contributors and covariance structure, and measured
producer descriptor/envelope parity between execution and publication.
The available-conditional/missing-shared/missing-clock case proves legitimate
partial-withhold output still publishes and reconstructs. Native assessment and
uncertainty identities preserve their existing algorithms.

Actual `dotnet test --no-build --list-tests --diag <file>` discovery is read from
`TestDiscovery.TestFound` and `TestDiscovery.Completed` messages, once unfiltered
and once for each primary category. Processing discovery is **All=995, Unit=978,
Manual=17, Integration=0, Soak=0, External=0, Hardware=0**; Astronomy discovery is
**All=798, Unit=791, Integration=2, Manual=5, Soak=0, External=0, Hardware=0**.
Category membership is disjoint and its union equals unfiltered discovery.
The aggregate runbook totals are Unit=14049, Integration=957 and Manual=286;
other project inventories remain unchanged. Whole-solution discovery, broad
correction gates and canonical aggregate coverage refresh remain the owner's
final gate, rather than evidence inferred from these component runs.
