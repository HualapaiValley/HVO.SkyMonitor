# VirtualSky long-exposure uncertainty requalification

[#1179](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1179) tests whether
fitting a separate systematic floor for each profile and exposure resolves the
uncertainty failures in [#1168's held-out envelope](../astronomy/virtual-astrometry-long-exposure-v1.md#held-out-astrometric-uncertainty).
The public uncertainty estimator retains the one-second release boundary
`astrometric-exposure-release-v1`. Supported mappings use their own exposure
midpoint and can be consumed without covariance.

## Frozen study

The [manifest](issue-1179-qualification-manifest.json), evaluator and
[runner](issue-1179-qualification.py) were committed together before capture
at `d2a961fc1a130f3079c6caca67a52b7489de5657`, an ancestor of this report.
The study used a Release build, SDK 10.0.402 and the
`hyg-v4.2-p3-s2-r1` SQLite snapshot. It reused gain 150, the original
magnitude-5 unvaried render, configured sensor/readout dimensions, trail-aware
measurement, independent scoring and session calibration.

| Frozen file | Git blob ID | File SHA-256 |
| --- | --- | --- |
| `AstrometricUncertainty.cs` | `01c2a03a232c3c0c8ef0ba0a57269ea5ba92f105` | `c6ba7c6cd5d15f89d41f6fa82e6534d7af09462be66def3a068790ec6a509d10` |
| `AstrometricUncertaintyEstimatorTests.cs` | `f2505679b08b11d7434d9fc5bb8aa9f21fa15551` | `38b4b8f8b127326ce4fe1471c06559cae48b78c8e029b61b113859aae6fdd2e4` |
| `VirtualAstrometricUncertaintyQualificationTests.cs` | `3fd73104fa9584b5872f3752fb3e47948bc408cc` | `4499510fb61aacb38ae7b42644b85b7a5829ba7ee9f89927a9f333e6467676d0` |
| `VirtualAstrometricUncertaintyQualificationTests.LongExposure.cs` | `9daf2a309fc4cd1759a5a174d7d9f1eff12eb682` | `bc3148cd806a96a4898be7e5f7b02a1427ec8eb6f6dada602d20681470459ab5` |
| `issue-1179-qualification-manifest.json` | `43502ad82d20390b7c92001ac88032c0590dd324` | `2789ef41309707fd6fc1963aac1480d5a114228e2fa029634c1c451b846af953` |
| `issue-1179-qualification.py` | `018381a74f1b05fabe19dc49ab607aea05418f57` | `73969a48c05523fc5b5a38c0be42ac7a9a3b757d037bfc9083b47a32a2f9844e` |

Tuning estimated a per-frame floor where fitting chi-square equals its degrees
of freedom. The candidate uses the nearest-rank 90th percentile of the 16
tuning floors per cell and profile, rounded upward to 0.001 readout px.
Separate nights and seeds provided the 60 held-out realizations per profile.
The runner sealed the chosen floors before opening held-out. Both the published
#1168 baseline and this candidate use the same numerical estimator and
acceptance thresholds. The baseline uses #1168's historical dates and seeds.
The new campaign measures a budget change on a fresh population, which limits
direct attribution of differences in empirical rates to the floor alone.

The internal evaluator shares the numerical refit and validity tests with the
public estimator. Its schema `astrometric-long-exposure-qualification-v1`
identifies study output. Retained validation and cross-covariance reject that
schema. The same accepted measurements were checked through the public
entry point with the study budgets and their authoritative exposure duration.

## Held-out results

| Cell | #1168 diagnostic withholds | #1179 diagnostic withholds | Session totals | Failing criteria | Public covariance released |
| --- | ---: | ---: | ---: | ---: | ---: |
| Equidistant 20 s | 61/420 | 3/420 | 9/9 | 2 | 0/429 |
| Equidistant 60 s | 128/420 | 27/420 | 9/9 | 7 | 0/429 |
| Rectilinear 20 s | 21/360 | 15/360 | 7/9 | 9 | 0/369 |

Held-out completed 1,263 configured-resolution captures, including the session fit and evaluation captures. It checked 1,227 accepted mappings through the public estimator.

### Failed criteria by cell

**Equidistant 20 s**

- `mono-native: parameter 3 interval coverage 0.817 at 0.95 (bound 0.098, n 60)`
- `pooled: parameter 3 interval coverage 0.602 at 0.6827 (bound 0.080, n 417)`

**Equidistant 60 s**

- `mono-native: 4 of 60 accepted frames withheld: model-invalid-residual-excess`
- `mono-mirror: 4 of 60 accepted frames withheld: model-invalid-residual-excess`
- `mono-roll: 7 of 60 accepted frames withheld: model-invalid-residual-excess`
- `cfa-native: 6 of 60 accepted frames withheld: model-invalid-residual-excess,model-invalid-held-out-prediction`
- `pooled: 27 of 420 accepted frames withheld: model-invalid-residual-excess,model-invalid-held-out-prediction`
- `pooled: parameter 0 interval coverage 0.794 at 0.6827 (bound 0.082, n 393)`
- `pooled: parameter 3 interval coverage 0.972 at 0.99 (bound 0.018, n 393)`

**Rectilinear 20 s**

- `03-session-native: total withheld model-invalid-held-out-prediction model-invalid-held-out-prediction`
- `11-session-native: total withheld model-invalid-held-out-prediction model-invalid-held-out-prediction`
- `mono-native: 3 of 60 accepted frames withheld: model-invalid-held-out-prediction`
- `mono-roi: 4 of 60 accepted frames withheld: model-invalid-held-out-prediction`
- `mono-mirror: 4 of 60 accepted frames withheld: model-invalid-held-out-prediction`
- `mono-roll: 4 of 60 accepted frames withheld: model-invalid-held-out-prediction`
- `pooled: 15 of 360 accepted frames withheld: model-invalid-held-out-prediction`
- `pooled: ellipsoid coverage 0.777 at 0.6827 (bound 0.088, n 345)`
- `pooled: parameter 0 interval coverage 0.771 at 0.6827 (bound 0.088, n 345)`

Parameters 0–2 are the east, north and up components of the ENU rotation
vector in radians. Parameter 3 is log focal scale. Diagnostic coverage uses
available diagnostic frames and the separate withholding limit counts every
withheld result. Production covariance stayed withheld across all cells.
The earlier rectilinear mono 2×2 up-component over-coverage did not recur.
Its interval covered 0.617 at level 0.6827, within the 0.210 bound on 60
frames. The new pooled failures and two withheld session totals still rule out
rectilinear 20 s covariance.

## Frozen floors

| Cell | Mono native | Mono ROI | Mono 2×2 | Mono ROI+2×2 | Mono mirror | Mono roll | CFA native |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Equidistant 20 s | 0.016 | 0.016 | 0.022 | 0.021 | 0.016 | 0.016 | 0.047 |
| Equidistant 60 s | 0.024 | 0.024 | 0.017 | 0.017 | 0.024 | 0.024 | 0.043 |
| Rectilinear 20 s | 0.017 | 0.017 | 0.018 | 0.018 | 0.017 | 0.016 | — |

All 20 floor groups had complete tuning samples. Two of the three tuning
processes passed with the historical floors. Equidistant 60 s failed
withholding, including two withheld session totals. Tuning outcomes did
not select or replace any held-out cell.

## Resource evidence

| Phase and cell | Median estimator wall / CPU (ms) | Max wall (ms) | Max estimator allocation (MiB) | Peak process working set (MiB) |
| --- | ---: | ---: | ---: | ---: |
| tuning, Equidistant 20 s | 2.84 / 2.84 | 47.51 | 1.47 | 1138 |
| tuning, Equidistant 60 s | 2.76 / 2.79 | 59.55 | 1.43 | 1417 |
| tuning, Rectilinear 20 s | 0.93 / 0.95 | 59.85 | 1.00 | 1218 |
| held-out, Equidistant 20 s | 3.10 / 3.18 | 61.99 | 1.44 | 1385 |
| held-out, Equidistant 60 s | 2.44 / 2.45 | 49.89 | 1.42 | 1445 |
| held-out, Rectilinear 20 s | 0.93 / 0.93 | 54.01 | 1.01 | 1133 |

All sampled estimator stages met the original per-frame elapsed-time and
32 MiB allocation bounds. Peak working set includes capture and measurement.

## Evidence and decision

- Tuning `SHA256SUMS` SHA-256 `74883a52c1d06a79d70b9c7fe658512469966285db290f0551a7d27f58aa8677`.
- Held-out `SHA256SUMS` SHA-256 `35d3f106d1acdd2e6a673b9772d8aabb5631b7e9928c8478189bd767515b0076`.
- Frozen bundle `frozen-study.bundle` SHA-256 `0369dac1364594662ea0bf06e7fb2443650a6de923a64c1ddac99217eb484708`.

The phase seals identify the reports, per-capture hashes and UTCs, logs and
TRX. The bundle preserves the pre-capture commit identified above.
#1168's published baseline and seals remain historical evidence.
The profile-specific budgets did not qualify either duration for
production covariance. The supported mapping rows retain the
exact one-second covariance release boundary and explicit gap dispositions.
Physical accuracy remains outside the VirtualSky claim.

## Renderer and mono cloud readout

The original #1168 renderer probe still reproduced the 56 s full-frame
rectilinear render and the 57 s refusal. Its historical mono cloud refusal
assertion failed because #1004 already admits the full-frame native-depth
Mono16 identity readout. The updated capacity probe requires a 60 s partial
cloud capture with scenario provenance for that mono profile and CFA, and
the exact initialization refusal for cropped and binned mono. This probe
checks the renderer and readout boundary. Astrometric coverage for cloudy
mono is outside the supported envelope. See the [#1004 evidence](cloud-profile-1004.md).

The probe and fixture correction affect only the cloud refusal expectation
and capacity reporting. The sealed covariance campaign used the unvaried
render and never called the refusal helper.

## Reproduction

Use a clean Release checkout, the verified catalog install root and an idle
Linux host. Choose an unused path for `pack`. Tuning creates it and seals its
files before held-out reads them. The phase exits nonzero if a workload is
incomplete or a result is missing. The summary records statistical failures
as evaluation outcomes alongside the test logs and TRX.

```bash
export HVO_ASTROMETRY_CATALOG_ROOT=/path/to/verified/catalog-install
export MSBUILDDISABLENODEREUSE=1 DOCKER_HOST=unix:///tmp/hvo-no-docker.sock
export TMPDIR=/tmp/sm1179s
mkdir -p "$TMPDIR" && chmod 0700 "$TMPDIR"
flock --close /tmp/hvo-heavy-gate.lock dotnet restore HVO.SkyMonitor.v9.slnx
flock --close /tmp/hvo-heavy-gate.lock dotnet build HVO.SkyMonitor.v9.slnx --no-restore -c Release -warnaserror
pack=/path/to/new/issue-1179-evidence
flock --close /tmp/hvo-heavy-gate.lock python3 -B docs/validation/issue-1179-qualification.py tuning "$pack"
flock --close /tmp/hvo-heavy-gate.lock python3 -B docs/validation/issue-1179-qualification.py held-out "$pack"
(cd "$pack/tuning" && sha256sum -c SHA256SUMS)
(cd "$pack/held-out" && sha256sum -c SHA256SUMS)
```
