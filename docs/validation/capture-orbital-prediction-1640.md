# Capture-bound orbital prediction evidence (#1640)

Target `development/v1`, base `f57dfc1b2ddf0a646f1455a15b4e4eaac4e23c29`.
Risk Tier C: new retained canonical Processing product boundary.
Protected profile `development-v1`: Preflight and Build and Unit; full CI is
not dispatched. Parent owns independent deep review, GitHub publication and
finalization. This evidence is software/fixture integration, not production,
native ARM, hardware or five-second whole-frame qualification.

## Workload and declared bounds

`CaptureOrbitalPredictionResourceTests.NearestEngineAndRetainedRecipeReportBoundedResourceCosts`
is Manual and runs alone. `1640-capture-metadata-640-v1` uses the approved ISS,
Starlink 56838 and Starlink 65283 track-03 cases, their exact six-facts or
two-starlinks snapshots and pinned predicted EOP/leap bytes. Raw payload is
819200 zero-filled bytes (640x640 Bayer16), fixed capture/artifact IDs, one
requested object, no random seed. Concurrency 1; 5 warmups then 30 measured
operations per fixture/stage. No arrival stream, initial backlog, outage, queue,
external-service or database/object-store operations apply to this metadata
recipe. Raw is borrowed, hashed once and never cloned by the recipe. Plan,
snapshot/time parsing and raw fixture setup happen outside timing; all executor,
identity, numerical engine, envelope validation/serialization and product hashing
happen inside the candidate timing. Correctness assertions and report/file
publication happen outside timing, apart from checking Produced status.

Nearest existing baseline is `OrbitalPredictionEngine.PredictExposureAsync`
plus canonical track serialization on the same query. Candidate additionally
binds and verifies raw, retains capture/selection/source context and validates
and serializes that larger metadata envelope. No prior equivalent capture
recipe exists. This measures the cost of the required retained boundary, not an
optimization claim. Bounds were declared before the measurement: per-operation
p95 <100 ms, allocation <25 MiB; CPU <5000 ms/30 operations; process working-set
growth <64 MiB. Stopwatch latency, process TotalProcessorTime,
GC.GetTotalAllocatedBytes(true), WorkingSet64 and process peak are sampled.
Median uses the upper middle order statistic for 30 samples; p95 uses nearest
rank (29th of 30). Process allocation/CPU/working-set include test
instrumentation and GC/JIT noise.
No retained pixel buffers, LOH clones, disk/network bytes or backlog are created
by the recipe. Selected records/candidates, samples, output bytes and query work
have explicit hard caps documented in the [consumer contract](../contracts/capture-orbital-prediction-v1.md).

Initial measurement environment: Ubuntu 24.04.5 LTS, native x64, .NET SDK
10.0.402/runtime 10.0.13, Release, on a KVM guest exposing 12 Intel Core
i9-14900K vCPUs, 15 GiB RAM and ext4 local storage. Complete raw results are in
`capture-orbital-prediction-1640-resources.json` beside this file.

| Fixture | Engine median / p95 ms | Recipe median / p95 ms | Engine / recipe allocated bytes/op | Engine / recipe CPU ms/30 | Recipe WS delta bytes | Engine / recipe output bytes |
| --- | --- | --- | --- | --- | --- | --- |
| ISS | 2.4162 / 2.9637 | 6.2771 / 6.8303 | 971168 / 2210921 | 75.673 / 217.070 | 2105344 | 8856 / 19616 |
| Starlink 56838 | 2.1426 / 2.5441 | 5.1532 / 5.7788 | 888959 / 2126736 | 65.314 / 166.157 | 311296 | 8616 / 19053 |
| Starlink 65283 | 2.0649 / 2.4544 | 4.8165 / 5.3551 | 888988 / 2126969 | 63.646 / 172.150 | 151552 | 8616 / 19059 |

All declared absolute bounds passed. Recipe median adds 2.75–3.86 ms and
1.18–1.24 MB allocated bytes per operation for raw integrity, identity and
additional canonical retained context. This is an explained new-path cost;
physical/whole-pipeline performance and larger-population throughput remain
unqualified. Process peak across the run was 123056128 bytes. No speedup or
portable latency guarantee is inferred.

## Commands and candidate evidence

All heavy commands use `flock -o /tmp/hvo-heavy-gate.lock timeout <seconds> ...`.
Build/test commands add `-nodeReuse:false -p:UseSharedCompilation=false`.
Unit environment unsets `HVO_PRODUCTION_CATALOG_BUNDLE` and sets
`DOCKER_HOST=unix:///tmp/hvo-no-docker.sock`. Integration uses the available
Docker daemon; no shared infrastructure is modified.

Focused command:

```bash
flock -o /tmp/hvo-heavy-gate.lock timeout 240 env -u HVO_PRODUCTION_CATALOG_BUNDLE \
  DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test \
  tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj \
  --configuration Release --filter 'FullyQualifiedName~CaptureOrbitalPrediction' \
  -warnaserror -nodeReuse:false -p:UseSharedCompilation=false \
  --logger 'trx;LogFileName=1640-focused.trx'
```

Result: 16 passed (15 Unit cases and 1 Manual resource case), no skipped/failed
cases. Subsequent candidate Unit selection also verifies the three committed
canonical capture-output fixtures against independent recipe executions; the
Manual harness need not be repeated for fixture-only comparisons.

Full Tier C command/results, classifier selector, checksums and counts are
recorded below. Transient logs/TRX/coverage and
command ledger remain in registered scratch
`/tmp/hvo-skymonitor-1640-implementation.DHuzgM` and repository `TestResults`.

## Compatibility and scope

New opt-in v1 schema, no storage migration or shipped default change. Exact
replay validates retained plan/source/time identities and fails closed on
corruption. Existing canonical tracks stay byte-identical. Host registration,
publication, source-reference retention and UI composition are #1641, with
[exact API/schema/fixture instructions](../contracts/capture-orbital-prediction-v1.md).

## Exact fixture checksums

| File | SHA-256 |
| --- | --- |
| `tests/fixtures/processing/capture-orbital-iss-v1.json` | `F7B713C181939EEAA434265A98286A472DFAFD67D34D890BF5C98F59081D419A` |
| `tests/fixtures/processing/capture-orbital-starlink-56838-v1.json` | `9503E4B013A9131E0C34C31FB7C10234F79B7C9116C6A7E901E0E91BA275668D` |
| `tests/fixtures/processing/capture-orbital-starlink-65283-v1.json` | `0C33EC0DC3003E4268B5C860D98D615FC58EF0115CE2BB715FBC695D4CDF50E6` |
| `tests/fixtures/orbital-prediction-v1/virtual-camera/snapshots/six-facts/manifest.json` | `D31AE9DABE8F3FCEDD2BA6CFC3894B760CC8ADC484771EE2DEFD2AB5CDC000EC` |
| `tests/fixtures/orbital-prediction-v1/virtual-camera/snapshots/six-facts/records.json` | `C77AED1EA73B3BADCCB3E8112339A6F7CD21B3BFC19FA2C60432E993556979C9` |
| `tests/fixtures/orbital-prediction-v1/virtual-camera/snapshots/two-starlinks/manifest.json` | `2A392F699C1172ECD59EAEB99192D5275A4FEC3EE903470F5DEED4F041FFFEE7` |
| `tests/fixtures/orbital-prediction-v1/virtual-camera/snapshots/two-starlinks/records.json` | `732013C27045BA6F1F896A888CF200D84F2212C9F12450D7B7C9C94CE18091AA` |
| `tests/fixtures/orbital-prediction-v1/time/predicted-2026.finals2000A` | `A11AD9627063DC10FA59FE25B9F9C07F0BACF81EA65F9405F40E4055DAA42E8B` |
| `tests/fixtures/orbital-prediction-v1/time/leap-seconds.list` | `DB5A895F16853B03BFC865E8D68F9FC8710EF1740E3400C701CD46A5BBBC3433` |

## Affected output coverage and runtime review

The final 920-case Processing Unit run passed all cases. Its Cobertura report
covers 150/150 executable lines in `CaptureOrbitalPredictionContracts.cs` and
37/38 in `CaptureOrbitalPredictionRecipe.cs` (187/188 combined). This is line
coverage evidence, not an assertion that every invalid-input combination has
been exhaustively qualified. The independent approved track bytes and wrapper
goldens, retained selector failures, mismatch tests, time gaps, clipping, ROI/bin
versus ReadoutView equivalence, cancellation and budget cases supply the
behavioral checks.

This slice changes no host/worker registration, logs, metrics, health endpoint,
queue, persistence or deployed configuration. Its observable evidence is the
retained typed output, including query-level no-result and budget/failure state;
no failure state is collapsed to an empty successful overlay. #1641 owns host
observability and durable reference/publication integration.

## Final candidate gate results

`profile=development-v1`; no full CI dispatch. All requested candidate commands
returned exit 0. Times below include any wait for the host lock.

| Gate | Exit | Seconds |
| --- | --- | --- |
| classifier | 0 | 0.136 |
| tool-restore | 0 | 0.122 |
| restore | 0 | 1.274 |
| debug | 0 | 203.821 |
| release | 0 | 201.494 |
| format | 0 | 143.267 |
| package-audit | 0 | 43.788 |
| shell-syntax | 0 | 0.582 |
| ci-classification | 0 | 16.519 |
| coordination-guard | 0 | 0.199 |
| pr-review-tools | 0 | 8.264 |
| unit | 0 | 430.237 |
| integration | 0 | 790.775 |

Full solution Debug and Release builds reported zero warnings/errors. The
affected Debug build after the final input-bound/transform-test refinement also
passed with zero warnings/errors (12.08 seconds in the build log). Whole-solution
format verification, package audit (zero vulnerabilities/deprecations), all four
CI-control guards and the six-baseline coverage policy check passed.

Unit: **13857 passed, 124 inconclusive/skipped, 0 failed**, 13981 selected.
The existing skips are 122 production-catalog-bundle cases with the required
variable unset, one macOS-only SDK case on Linux and one unreachable filesystem
delete-ambiguity case. Integration: **957 passed, 0 skipped, 0 failed**, on real
Docker 29.8.0. The focused run passed 15 new Unit cases and one Manual resource
case; the final 920-case Processing Unit selection verifies all final code,
goldens and ROI/bin transform equivalence.

Exact stable-candidate commands (Unit explicitly ran with
`HVO_PRODUCTION_CATALOG_BUNDLE` unset and the invalid Docker endpoint; Integration
ran with the real Docker endpoint):

```bash
timeout 120 ./scripts/ci:classify pull_request f57dfc1b2ddf0a646f1455a15b4e4eaac4e23c29 5ed315ba08b19ed39a14f2142604fb621ba8ac02
flock -o /tmp/hvo-heavy-gate.lock timeout 180 dotnet tool restore
flock -o /tmp/hvo-heavy-gate.lock timeout 300 dotnet restore
flock -o /tmp/hvo-heavy-gate.lock timeout 600 dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Debug -warnaserror -nodeReuse:false -p:UseSharedCompilation=false
flock -o /tmp/hvo-heavy-gate.lock timeout 600 dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false
flock -o /tmp/hvo-heavy-gate.lock timeout 300 dotnet format HVO.SkyMonitor.v9.slnx --no-restore --verify-no-changes
flock -o /tmp/hvo-heavy-gate.lock timeout 300 ./scripts/package:audit /tmp/hvo-skymonitor-1640-implementation.DHuzgM/package-audit
timeout 120 ./scripts/ci:shell-syntax
timeout 120 ./scripts/test:ci-classification
timeout 120 ./scripts/test:coordination-guard
timeout 120 ./scripts/test:pr-review-tools
flock -o /tmp/hvo-heavy-gate.lock timeout 900 dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter TestCategory=Unit --settings tests/coverage.runsettings '--collect:XPlat Code Coverage' -nodeReuse:false -p:UseSharedCompilation=false --logger trx --results-directory /tmp/hvo-skymonitor-1640-implementation.DHuzgM/unit
flock -o /tmp/hvo-heavy-gate.lock timeout 1200 dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter TestCategory=Integration --settings tests/coverage.runsettings '--collect:XPlat Code Coverage' -nodeReuse:false -p:UseSharedCompilation=false --logger trx --results-directory /tmp/hvo-skymonitor-1640-implementation.DHuzgM/integration
flock -o /tmp/hvo-heavy-gate.lock timeout 180 dotnet build tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj --no-restore --configuration Debug -warnaserror -nodeReuse:false -p:UseSharedCompilation=false
./scripts/coverage:policy pull_request development/v1
```

The initial classifier range used an unreferenced evidence snapshot object
(`5ed315ba08b19ed39a14f2142604fb621ba8ac02`) without moving the branch before
validation. Final source refinements stayed within the same 16 selected paths;
the implementation commit range is classified again for the parent handoff.
Classifier output:

```text
CI classification: mode=full deployment=false complete=true shared=true cameraagent=true logichost=true combined=true delivery=true secret_scan_contract=false release_scripts=false reason=at least one path is outside the reduced-mode allowlist deployment-reason=pull request does not change deployment-contract inputs complete-reason=at least one path is outside every component boundary paths=16
mode=full
deployment=false
deployment_catalog=false
deployment_shards=false
deployment_installer=false
complete=true
shared=true
cameraagent=true
logichost=true
combined=true
delivery=true
secret_scan_contract=false
release_scripts=false
```

`complete=true` selects the affected local/full-qualification plan; it does not
override the Development v1 target profile or authorize a full workflow dispatch.
TRX, original collector GUID coverage reports, all per-gate logs, exact command
ledger (`gates.json`), test/skip summary (`test-summary.json`) and source fingerprints
remain in the registered scratch root. Coverage was collected with the requested
Tier C solution commands; no historical report glob was merged and no full
main/release qualification is claimed. Parent still owns independent deep review,
final base synchronization, protected checks, publication and merge.

Final validated source/test fingerprints (documentation-only ledger completion
does not change these validated files):

```text
7FE566D11EA5DD74525322AB82B7979CB248BF7C9F50EEED80CD27329718B204  src/HVO.SkyMonitor.Processing/CaptureOrbitalPredictionContracts.cs
10913C7DAAABD5B24CFB77B4E98A50B531501BD30F4299F667CE672F19CE9B85  src/HVO.SkyMonitor.Processing/CaptureOrbitalPredictionRecipe.cs
623A9D0BBAB81BF67BB51872FEE909B3621366A1ABA99075316CAD87C49D4999  tests/HVO.SkyMonitor.Processing.Tests/CaptureOrbitalPredictionFixtures.cs
F441363512CBA0856D8A75E98A20F3AC2F582FF83ABA2CED6808C9BE1D047D77  tests/HVO.SkyMonitor.Processing.Tests/CaptureOrbitalPredictionRecipeTests.cs
3258603381CACB7C4BB3E5EEC48F22F7A56E47C93826565371BC8F708C286ED1  tests/HVO.SkyMonitor.Processing.Tests/CaptureOrbitalPredictionResourceTests.cs
```
