# Virtual all-sky astrometry qualification — final report (v1)

> **Superseded for projection families.** This report remains the record of the #1106 measurement. Issue
> [#1126](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1126) publishes the projection-family envelope in
> [v2](virtual-astrometry-qualification-v2.md), which is the current statement of supported lens projections.

Issue [#1106](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1106) (V7 of epic
[#1098](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1098)) reruns every delivered qualification harness
at one integrated `development/v1` revision and audits the epic's nine completion criteria against delivered code
and evidence. It is the single place that states what the virtual milestone supports.

**Verdict.** All nine completion criteria of #1098 are **met for the declared virtual envelope** below. All 16
predeclared processes, covering 8 harnesses and 9 per-process exposure workloads, passed at one revision. Every
budget and tolerance held. The magnitude-5 catalog and selection identities, the HYG database hash, and the #522
configuration and payload hashes all reproduced their earlier recorded values. No margin is under 10 %. The closest are the measured-star time ratio (3.53 against 4×, 11.7 %)
and the 0.99 ellipsoid coverage on `mono-roi-bin2` (11.0 % of its binomial bound). This is a virtual qualification
only. It makes no claim about physical accuracy, and the hardware evidence it does not provide is listed for #1107.

**Not included:**
- physical cameras, lenses or sky, and any claim of physical accuracy. These stay with #1107, which moved from #523
  to the physical-qualification epic #1166 on 2026-10-06.
- new production consumers: astrometry recipes, compact persistence, calculators, registered stacking or
  FITS/WCS export. The handoff below names their seams; it does not implement them.
- deployment or promotion to `main`

## Measured revision and reproduction

- **Revision.** `9061c8f7906bebc214268f59c79773f01c46a129`, based on `development/v1` at `24f0ecfd` (the #1105
  merge), so it contains every V1–V6 and #522 implementation. Only this issue's manifest and runner differ from
  that base. Neither changes production code or any harness.
- **Host.** Intel i9-14900K, 12 logical processors, 47 GiB, Linux 6.8, .NET SDK 10.0.401 (runtime 10.0.12),
  Release, `DOTNET_TieredCompilation=0`, sequential runs on an otherwise idle host.
- **Catalog.** Installed `hyg-v42-production` snapshot `hyg-v4.2-p3-s2-r1`: HYG 4.2, 119,625 rows, database
  SHA-256 `B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2`. Complete magnitude ≤ 5 selection:
  1,637 entries, catalog identity `e29a02ef…25b60b`, selection identity `ce81fa2e…274cb8`.
- **Manifest.** [`issue-1106-qualification-manifest.json`](../validation/issue-1106-qualification-manifest.json)
  was first committed in `e73d0a7d` (SHA-256 `4025bb0d…bcfe09`) before any measurement. `9061c8f7` amended only
  its attachment rule, again before the measured run, giving the measured SHA-256 `b56fa6d0…19db76a`. It pins the harness set, the
  exact number of tests each run must execute, and the report each must attach. Seeds, times, profiles, partitions
  and tolerances are those compiled into the harnesses, and the evidence index records the git blob id of each
  harness source.
- **Runner.** [`issue-1106-qualification.sh`](../validation/issue-1106-qualification.sh) refuses an uncommitted
  tree, builds Release with `-warnaserror`, runs each manifest entry in its own `dotnet test` process, and fails a
  run unless the TRX shows exactly the expected tests executed and passed with none failed. A filter that selects
  nothing is a failure, never a skip. The committed runner also refuses untracked files and any existing output
  directory, resolves a relative output path before it changes directory, treats any failure to copy, hash or
  index evidence as fatal, and validates the final index against the manifest: revision, manifest hash, every
  process, each run's harness blob ids, and well-formed TRX and report hashes. Those safeguards were added after
  the measured run, in response to review, and change no test or measurement. The pack below was produced by the
  `9061c8f7` runner. None of the conditions they guard against occurred: the tree was clean with no untracked
  files, the output path was absolute and new, and the retained index passes the same manifest check.

```bash
HVO_ASTROMETRY_CATALOG_ROOT=<installed hyg-v42-production root> \
  docs/validation/issue-1106-qualification.sh <new output directory>
```

**Earlier attempt.** The first run, at `e73d0a7d`, was stopped after three runs. The tests passed, but the
runner looked for attachments under their partition directory, while MSTest stores them flat, so it recorded the
V1 report as missing. `9061c8f7` corrects only that lookup. The aborted attempt is disclosed in
[#1106](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1106#issuecomment-5947850027), and no number from
it is used here.

**Evidence pack.** The full pack (16 TRX files, every attached report and RAW16 payload, 919 MiB) is retained
outside the repository as `issue-1106-evidence-9061c8f7.tar.zst`, SHA-256
`f59e561d97c8b9d20fe9f28e843161a911514b67a77de8d199560b0d53588459`. The committed
[`issue-1106-qualification-evidence.json`](../validation/issue-1106-qualification-evidence.json) holds the index
(every report's SHA-256 and size, TRX counters and hashes, harness blob ids) and the key metrics below.

## Results

All figures come from the evidence pack at `9061c8f7` unless a prior is named. "Prior" is the value
recorded by the issue that delivered the harness. Times are wall-clock milliseconds unless stated. The extractor
[`issue-1106-metrics.sh`](../validation/issue-1106-metrics.sh) reproduces every figure from the pack's reports.

| Run (criteria) | Elapsed | Report SHA-256 | Outcome |
| --- | --- | --- | --- |
| catalog-selection (2, 7, 8) | 2 s | `ecc5a632…` | 1/1 passed |
| shared-solver-full-frame (8) | 17 s | mono `6657cdb3…`, color `e4103180…` | 2/2 passed |
| actual-pixels-blind-warm-readouts (3, 7) | 141 s | `dcd33634…` | 1/1 passed, 90/90 rows accepted |
| actual-configured-resolution-resources (3, 8) | 56 s | `4e15293c…` | 1/1 passed |
| stellar-exposure, 9 processes (2, 4, 8) | 9–72 s each | per workload in the evidence index | 9/9 passed |
| measured-stars-held-out (6, 7, 8) | 107 s | `bcdc63f1…` | 1/1 passed, 21 cases and window pressure |
| optical-calibration-held-out (5, 7, 8) | 123 s | `b14da574…` | 1/1 passed, 3 cases |
| astrometric-uncertainty-held-out (6, 7, 8) | 331 s | `0af875ae…` | 1/1 passed, 420 frames and 3 session chains |

### Identities

The four held-out astrometry reports (V1 pixels, measured stars, optical calibration and uncertainty) carry the
same catalog identity `e29a02ef…` and magnitude-5 selection identity `ce81fa2e…`, and the catalog report's
magnitude-5 selection has that same selection identity. Its magnitude 5.5 and 7 selections have identities of their
own. Every report that reads HYG records the same database SHA-256, including the configured-resolution resources
and the nine #522 workloads. Those workloads render to magnitude 6.5 and carry no astrometric selection identity.
The two synthetic shared-solver reports read no catalog. All nine #522
stellar configuration hashes and all 270 per-sample source payload hashes are identical to the #522 after-archive
([`issue-522-stellar-resources.json`](../validation/issue-522-stellar-resources.json)). The V1 pixel run produced 90
distinct assessment identities over 90 distinct payloads and 21 configuration hashes. All 60 warm rows link to
their predecessor's assessment, and the harness asserted that payload and compact-assessment identities
reproduce exactly. Report SHA-256 values differ from earlier docs, as expected, because each report records its
revision and timings.

### Catalog selection (V2)

| Metric | Prior (#1101) | This run | Budget |
| --- | --- | --- | --- |
| Entries at magnitude 5 / 5.5 / 7 | 1,637 / 2,865 / 15,598 | identical; complete only at 5 | match |
| Bounded p95, magnitude 5 / 5.5 / 7 | 1.43 / 2.17 / 2.12 ms | 1.32 / 3.49 / 2.47 ms | ≤ 20 ms |
| Bounded allocation, 5 / 5.5 / 7 | 420,304 / 682,504 / 682,504 B | identical | ≤ baseline + 32 KiB |
| Same-process bounded − baseline median, magnitude 5.5 | — | +0.416 ms | investigate above max(20 %, 0.5 ms) |
| Startup | 300 ms | 357 ms | none |

The magnitude 5.5 timing rise (median +35 %, p95 +61 %) is about 1.3 ms in absolute terms. Its own same-process
comparison stays below the 0.5 ms investigation floor, and allocation is byte-identical, so no regression is
attributed.

### Shared solver, full frame (supplementary synthetic fixture)

| Fixture | Cold | Warm median / p95 | Peak working set | Prior (#1093) cold, warm median |
| --- | --- | --- | --- | --- |
| 1936×1216 mono | 1,006 ms | 38.8 / 52.3 ms | 480 MiB | 2,139 ms, 63.6 ms |
| 3096×2080 color | 1,120 ms | 304.6 / 415.9 ms | 1.08 GiB | 1,540 ms, 465.5 ms |

The #1093 prior was measured on a different host (Debian 13), so it is not a like-for-like comparison. The lower
timings are attributed to the host, not to a code change. Allocation is within 0.4 % of the prior. Budgets: cold
≤ 15 s, warm ≤ 500 ms, peak ≤ 2 GiB.

### Actual pixels: blind, warm and readouts (V1)

All 90 rows were accepted: 30 blind and 60 warm, across 10 readout names (mono native, ROI, 2×2, ROI+2×2, mirror,
roll; CFA native and three derived phases) and three held-out seasons.

| Metric | Prior (#1102) | This run | Tolerance |
| --- | --- | --- | --- |
| Blind elapsed median / max | — / 2,660 ms | 2,303 / 3,540 ms | cold ≤ 15 s |
| Warm elapsed max | 13.6 ms | 12.9 ms | ≤ 500 ms |
| Warm hypotheses | 0 | 0 | must be 0 |
| Pose error max | 0.0096° | 0.0103° | ≤ 0.06° |
| Focal relative error max | 6.7e-5 | 1.21e-4 | ≤ 1e-3 |
| Withheld RMS / p95 max | 0.034 / 0.040 px | 0.033 / 0.056 px | ≤ 0.5 / 0.75 px |
| Association precision, missing mappings | 1, 0 | 1, 0 | 1, 0 |
| Cross-frame grid max | — | 0.242 px over 7,272 supported of 22,500 points | ≤ 0.75 px |
| Local scale error max | — | 1.2e-4 | ≤ 2e-3 |

The #1102 prior rendered stationary stars at `SceneUtc`. This run solves #522's exposure-integrated frames,
whose capture path and pixels differ, so a small change in accuracy figures is expected. The cross-frame grid keeps 67 % of its
tolerance, and every other figure at least 76 %. The #1102 grid and inverse-ray inventory was enlarged during its own review, so
those earlier maxima cover a smaller point set and are not compared.

### Configured-resolution resources (V1)

| Profile | Capture p95 | Measure p95 | Prior capture p95 (#1102) | Budget |
| --- | --- | --- | --- | --- |
| ASI174 mono 1936×1216 | 132.7 ms | 58.6 ms | 95.0 ms | capture ≤ 10 s, decode ≤ 2 s |
| ASI178MC CFA 3096×2080 | 733.1 ms | 653.5 ms | 636.9 ms | capture ≤ 20 s, decode ≤ 5 s |

The process peak working set was 1.06 GiB (prior 1.07 GiB), against a 2 GiB limit. The mono capture rise
(+37.7 ms) follows from #522's integrated renderer, which replaced the instantaneous one that #1102 measured.

### Stellar exposure (#522), nine separate processes

| Workload | Median (prior) | p95 (budget) | Peak working set (≤ 2 GiB) |
| --- | --- | --- | --- |
| mono-short | 187 ms (174) | 203 ms (10 s) | 378 MiB |
| mono-long | 302 ms (280) | 319 ms (10 s) | 367 MiB |
| mono-day | 390 ms (360) | 430 ms (10 s) | 349 MiB |
| cfa-short | 612 ms (576) | 633 ms (40 s) | 1.07 GiB |
| cfa-long | 1,025 ms (937) | 1,049 ms (40 s) | 898 MiB |
| cfa-day | 1,166 ms (1,091) | 1,196 ms (40 s) | 872 MiB |
| asi676-short | 1,074 ms (1,030) | 1,093 ms (40 s) | 944 MiB |
| asi676-long | 1,653 ms (1,515) | 2,187 ms (40 s) | 1.45 GiB |
| asi676-day | 1,972 ms (1,876) | 2,093 ms (40 s) | 1.20 GiB |

- Medians are 4–10 % above the #522 after-figures, which is consistent with host variance.
- Allocation agrees within 8 KB per sample.
- The asi676-long p95 is driven by three outliers (2,104, 2,187 and 2,303 ms). Its other 27 samples fall between
  1,595 and 1,791 ms.
- The asi676-short peak is lower than #522 recorded. The peak is cumulative per process, and it depends on what
  the process did first.

### Measured stars and diagnostics (V4)

All 21 cases and the window-pressure case passed, with an empty failure list. Every per-case recall, RMS and
association figure equals the #1103 report to its stated precision. For v2: maximum RMS 0.093 px; RMS regression
at most 0.012 px, against a 0.02 px limit; no false associations. The v2/v1 time ratio was at most 3.53, against
a 4× limit (#1103: 3.58); v2 allocated at most 51.8 bytes per sample. Peak working set was 776 MiB (#1103:
745 MiB). The CFA misses repeat exactly: Jan 3 `crowded`, 1 `masked-aperture`, 1 `saturated-excessive`; May 5
`crowded`; Sep 1 `crowded`.

### Session optical calibration (V5)

Every recovery error, fit diagnostic, withheld score and omitted-distortion outcome is identical to the
[#1104 report](session-optical-calibration-v1.md):
- Focal error at most 3.0e-5, principal point within 0.0083 px, k1 within 1.85e-5, intrinsic map within 0.0145 px.
- Withheld RMS at most 0.013 px with precision 1.
- Omitted k1 rejected on both distorted cases and accepted on the undistorted one.

| Case | Wall time (limit) | Allocated (limit) | #1104 wall time |
| --- | --- | --- | --- |
| 01 cold | 19.2 s (25.5 s) | 18.5 GiB (23.8 GiB) | 19.0 s |
| 05 session | 14.9 s (18.9 s) | 14.8 GiB (17.6 GiB) | 14.5 s |
| 09 session | 16.5 s (21.6 s) | 15.4 GiB (19.1 GiB) | 14.9 s |

Each limit is derived from that case's own single-frame cold-solve baseline. Peak working set was 490 MiB.

### Astrometric uncertainty (V6)

All 420 frames were available, with no false withholding and 420 distinct identities. Coverage, χ²/dof (pooled
0.972 fitting, 0.977 held-out), required floors and all nine session-chain Mahalanobis totals are identical to the
[#1105 report](astrometric-uncertainty-v1.md). The largest total is 10.18 against 18.4668. The narrowest coverage
row is `mono-roi-bin2` at the 0.99 ellipsoid: observed 0.950, deviation 0.040 against a bound of 0.045.

The estimator's maximum ratio to its own frame was 0.091 against a 0.5 limit (#1105 rerun: 0.060). Its maximum
allocation was 1.52 MB against 32 MiB, and the run peak was 1.37 GiB.

### Timing differences from earlier records

Six timing figures rose by more than 25 % against their earlier records:
- catalog magnitude 5.5 bounded selection
- catalog magnitude 5 baseline p95
- V1 blind maximum
- configured mono capture p95
- asi676-long p95
- uncertainty estimator ratio

Each timing increase is under 1 s in absolute terms (the largest are the V1 blind maximum, +880 ms, and asi676-long
p95, +630 ms). The estimator ratio is dimensionless: it rose from 0.060 to 0.091 against a 0.5 limit. Each budgeted figure keeps at least 76 % of its budget, and none carries an
allocation change. Two have explanations above: #522 changed the capture path, and single-sample outliers drive the
tail-statistic rises. No accuracy figure in V2–V6 changed, so no regression is recorded. These budgets assume
sequential runs on an idle host.

## Supported virtual envelope

Supported means qualified by held-out virtual evidence at the measured revision. Anything not listed is
outside the envelope, whether or not a unit test exercises it.

| Dimension | Supported | Explicitly outside |
| --- | --- | --- |
| Lens projection | Equidistant fisheye. ASI174 mono: 1936×1216, 5.86 µm, 595.84 px image circle. ASI178MC RGGB: 3096×2080, 2.4 µm, Fujinon FE185C057HA-1 candidate, 185°. | Equisolid, orthographic, stereographic and perspective/rectilinear ([#1126](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1126)); any physical lens |
| Distortion | Radial k1 only, inside the \|k1\| ≤ 0.5 invertibility domain. Held-out truth up to ±0.8 % focal, 6.5 px principal point and \|k1\| 0.008. | Higher radial, tangential or decentring terms; parameters beyond the qualified perturbations |
| Readouts | Mono native, ROI (240, 96, 1440, 1024), 2×2 digital average, ROI+2×2, mirror and roll. CFA native, plus derived phases GRBG/GBRG/BGGR for scientific reconstruction only. Every view is derived from one native-sensor calibration. | Bayer crop or bin and charge binning (rejected explicitly); separate per-readout calibrations |
| Catalog | HYG 4.2 snapshot `hyg-v4.2-p3-s2-r1`, complete selection to magnitude 5 (1,637 entries), fixed J2000 positions, geometric altitude | Incomplete selections (magnitude 5.5 and 7) are labelled incomplete and never presented as complete; proper motion and refraction |
| Exposure and solving | Exposure-integrated rendering (#522) for every declared workload. Solving, calibration and uncertainty at 1 s exposures and gain 150 across three held-out seasons. | Solving accuracy on long trailed exposures; daytime solving |
| Measurement and uncertainty | `linear-stellar-local-v2`. Systematic floors 0.042 px mono unbinned, 0.028 px mono 2×2, 0.063 px CFA, valid for the VirtualSky renderer and these profiles. | Any other measurer, PSF or sensor response |
| Calibration authority | Virtual-profile review, accept, reject and retention | Activating or writing back any physical rig profile |
| Resources | Configured resolution, sequential processes, ≤ 2 GiB peak per process; budgets as tabulated above | Concurrent heavy workloads on the same host |

Inside this envelope:
- A blind solve takes 2.3 s median (3.5 s max) and a warm solve 6 ms.
- Withheld mapping error stays under 0.06 px.
- A session calibration takes 15–19 s.
- Uncertainty intervals hold their stated coverage.

## Audit of the nine completion criteria

Each row is judged against delivered code at the measured revision and the evidence in this report, not against
issue state. "Met" means met for the declared virtual envelope.

| # | Criterion | Delivered code | Evidence | Verdict |
| --- | --- | --- | --- | --- |
| 1 | Existing-scope reconciliation | #1100 ([plan](../planning/virtual-astrometry-qualification.md)) | Acceptance map, dependency order and retained hardware gates; ticked on the epic at #1108 | Met |
| 2 | Honest HYG selection and resource limits | #1101 `IAstrometricCatalogSource` with complete-or-incomplete selection; #522 explicit candidate bounds that refuse rather than truncate | Every HYG-reading report carries the same database hash, and the four held-out astrometry reports and the catalog report's magnitude-5 selection share one selection identity. Complete selection holds only at magnitude 5 (1,637); 5.5 and 7 are reported incomplete. Bounded p95 is at most 3.5 ms against 20 ms, with byte-identical allocation. The #522 configuration and payload hashes match its archive, and its candidate-bound refusals are Unit-tested. | Met |
| 3 | Final pixels: blind, warm, mapping, crop/bin/CFA, no truth leakage | #1102 harness over production `VirtualSkyCameraModule`; #1087 crop/bin disposition on #1102 | 90/90 production-path rows were accepted, 30 blind and 60 warm, with 0 warm hypotheses. Pose, focal, withheld and scale errors each keep at least 76 % of their tolerance, and the cross-frame grid keeps 67 %. The harness covers mono ROI, bin, mirror and roll and the CFA phases, and rejects unsupported Bayer crop/bin. Scoring truth enters only after solving (`VirtualAstrometryReference`). Configured-resolution resources fit within budget. | Met |
| 4 | Production exposure behavior | #522 integrated renderer, visibility model and exposure-swept masks | All 9 #522 workloads (mono, CFA and ASI676; short, long and day) passed, at most 2.2 s p95 against 10/40 s budgets and ≤ 1.45 GiB peak. The configuration and 270 payload hashes are identical to #522. Every V1/V4–V6 frame is exposure-integrated, with a midpoint `sceneUtc`. | Met |
| 5 | Reusable virtual optical calibration | #1104 `OpticalCalibrationSession` and `VirtualOpticalCalibrationReviewService` | All 3 held-out cases are identical to #1104. Focal ≤ 3.0e-5, principal point ≤ 0.0083 px, k1 ≤ 1.85e-5. Withheld frames scored ≤ 0.013 px, omitted-k1 was rejected where it should be, and cost stayed within limits. Review, accept, reject and retention are covered by Unit tests. | Met |
| 6 | Diagnostics and validated uncertainty | #1103 `linear-stellar-local-v2` and residual diagnostics; #1105 `AstrometricFrameUncertainty` | 21 measured-star cases plus window pressure are identical to #1103, with the ratio at 3.53 against 4×. All 420 uncertainty frames have coverage within 3.5σ for every profile; the narrowest is 11.0 % margin. Session chains reach at most 10.18 against 18.47. Unsupported cases withhold with reason codes (Unit-tested). | Met |
| 7 | Reproducible compatible identities | Identities in #1101–#1105 contracts | Every #1101–#1105 identity reproduced its recorded value or was asserted reproducible within the run. V1 produced 90/90 distinct assessment identities, and 420/420 frames had distinct uncertainty identities. Incompatible reuse rejection and explicit time/epoch semantics are Unit-tested in each contract. | Met |
| 8 | Full-resolution resources, gates and review | Every implementation PR; this rerun | Every resource budget held at configured resolution, and no margin is under 10 %. Every implementation PR has a classifier-recorded gate, an independent CLEAN exact-range review and green protected CI (Delivery lifecycle table). This report's own PR completes the row. | Met |
| 9 | Final envelope and hardware handoff | This report; #1107 | Supported envelope, exclusions and the per-parameter hardware evidence table above; #1107 is open, owned by #523 and linked from this report | Met once this report merges |

## Delivery lifecycle

Every implementation head below passed an independent read-only exact-range review that converged in draft, then
green protected `development/v1` CI, and merged with `--match-head-commit`. Each PR body or its issue ledger
records the `scripts/ci:classify` selection and the local gate results for its reviewed range. Tier C work ran the
complete local candidate gate, with separate Unit and Docker Integration results and the CI-control guards.

| Issue | PR | Reviewed head | Merge | Final review | Correction rounds | Protected CI | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| #1100 scope and acceptance map | [#1108](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1108) | `9feaf2f9` | `ca86db1e` | [CLEAN](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1108#issuecomment-5922483080) | 0 | [run 36798334227](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/36798334227) | Tier A documentation; classifier-selected documentation checks |
| #1109 transient fixture prerequisite | [#1111](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1111) | `74bba9c2` | `8d8e929b` | [CLEAN](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1111#issuecomment-5924260798) | 0 | [run 36811830195](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/36811830195) | Tier B; #1109 was reopened between its two PRs |
| #1109 (second PR) | [#1112](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1112) | `b12ad7c6` | `ffd86fdb` | [CLEAN](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1112#issuecomment-5924997778) | 0 | [run 36817319400](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/36817319400) | Tier B |
| #1101 V2 bounded HYG selection | [#1113](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1113) | `56a5d9f8` | `6b35bcca` | [CLEAN](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1113#issuecomment-5925326228) | 0 | [run 36819880653](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/36819880653) |  |
| #1102 V1 actual pixels and readouts | [#1115](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1115) | `b013846b` | `fceb1000` | [CLEAN](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1115#issuecomment-5927444841) | 1 | [run 36834904662](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/36834904662) | First candidate failed the 2 GiB resource budget (3.09 GB, combined process); the failure and the separate-process repair are retained on #1102 |
| #522 V3 exposure and visibility | [#1116](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1116) | `53a386cc` | `60c7a857` | [CLEAN](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1116#issuecomment-5938642894) | 1 | [run 36912636598](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/36912636598) | Follow-up #1118 (preexisting deployment scheduling) |
| #1103 V4 measured stars and diagnostics | [#1120](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1120) | `9606d2ed` | `2e241b00` | [CLEAN](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1120#issuecomment-5943813573) | 1 | [run 36950647147](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/36950647147) |  |
| #1104 V5 session optical calibration | [#1122](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1122) | `a441d9a8` | `639a724a` | [CLEAN](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1122#issuecomment-5945881707) | 3 | [run 36967786398](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/36967786398) | Three correction rereviews; base-sync review [CLEAN](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1122#issuecomment-5945979953) |
| #1105 V6 uncertainty and conventions | [#1124](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1124) | `7ea84317` | `24f0ecfd` | [CLEAN](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1124#issuecomment-5947604576) | 2 | [run 36980107728](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/36980107728) |  |

The protected `development/v1` profile runs Preflight and Build and Unit. It does not run Docker Integration or
any Manual qualification harness, so those rest on the recorded local gates and on this rerun. On #1122, the
Integration suite was not repeated on the base-synced head; that head is contained in the revision measured here,
and this issue's own complete gate covers it.

## Consumer handoff

The milestone delivers library contracts and virtual-profile review. No production consumer reads them yet.
Later consumers should bind to these contracts instead of introducing parallel records:

| Contract | Owner | What a consumer gets |
| --- | --- | --- |
| `AstrometricFrameAssessment` | Astronomy ([shared astrometry](shared-astrometry-v1.md)) | Compact, canonically serialized per-frame result: mode, status and reason; pose and focal scale at the exposure midpoint; RMS, counts and coverage; source, catalog, selection, calibration, solver and convention identities. A failed frame carries no fitted parameters. |
| `AstrometricMapping` | Astronomy | Pixel ↔ sky ↔ frame mapping, constructible only from an accepted assessment, with per-axis local pixel scale. It is not a global TAN/WCS. |
| `OpticalCalibrationResult` and readout views | Astronomy ([session optical calibration](session-optical-calibration-v1.md)) | One immutable native-sensor model with radial k1, its shared covariance and diagnostics. ROI and binned readouts are derived views, never separate calibrations. |
| `AstrometricFrameUncertainty` | Astronomy ([astrometric uncertainty](astrometric-uncertainty-v1.md)) | Conditional, systematic and shared-calibration components, each with its own withhold reason, plus a clock bound that is never added to the total. |
| Measured stars and diagnostics | Imaging ([measured-star diagnostics](measured-star-diagnostics-v1.md)) | `linear-stellar-local-v2` measurements with centroid covariance and reason-coded exclusions; residual summaries by radius, azimuth, brightness and time. |
| `IAstrometricCatalogSource` | Catalog.Sqlite | Bounded, complete-or-explicitly-incomplete HYG selection bound into every identity. |

The seams for each consumer:

1. **Application recipes.** Add an explicit, versioned Processing astrometry recipe that runs after durable raw
   admission from calibrated linear pixels with exact raw lineage. Bind catalog, calibration, settings and source
   identities into its execution identity. It must never block admission or mutate capture-time descriptors.
2. **Compact persistence.** Store each assessment once, as a typed Metadata product. Keep artifact, assessment and
   calibration references in per-frame joins, and store a calibration once by identity. Do not repeat catalog
   scenes or calibration objects per frame. Scene deduplication stays with #1055.
3. **Calculators.** Pixel/sky calculators consume `AstrometricMapping` and the uncertainty total. A prediction from
   an older solution must be labelled predicted/unverified and pass compatibility checks; it never inherits the old
   RMS or quality. A good fit does not validate time or site. Clock health comes from #1095.
4. **Optical-calibration application.** Real rigs need the operator review, activation and rollback that #1107
   qualifies. The review service blocks physical rigs, and accepting never stages or activates.
5. **Registered stacking.** A separate opt-in versioned recipe, never a change to `rolling-mean-v1`. Its output
   identity must bind the ordered source frames and their accepted solution identities, reference UTC and solution,
   mapping version, interpolation, coverage masks, rejection policy and actual integration. Geometric resampling
   follows linear CFA reconstruction.

FITS/WCS export stays with #141 and #523. `ProjectedSceneV1` still has no measured-solution reference, and
`ImageRegistered` alone does not prove a fit. That schema is unchanged here.

## Physical-hardware handoff (#1107)

[#1107](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1107) owns physical qualification. On 2026-10-06 it
moved from #523 to the downstream physical-qualification epic
[#1166](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1166), and it is not authorized to start. The
criterion 9 row above records ownership as it stood when this report merged. Simulation shows that the process works
under declared simulation assumptions. It does not identify the installed lens, measure its distortion or certify
real-camera accuracy. #1107 needs the following evidence:

| Virtual result | Why it does not transfer | Hardware evidence #1107 needs |
| --- | --- | --- |
| Principal point, focal scale and k1 recovered to within 0.0083 px, 3.0e-5 and 1.85e-5 | Truth was the emulator's own equidistant model, perturbed by at most ±0.8 % focal, 6.5 px centre and \|k1\| 0.008 | Raw exposures from the actual camera and lens across the usable radius and azimuth at several sky times, scored against trusted external astrometry; declare tolerances first |
| Only k1 is fitted; higher radial terms, tangential and decentring terms are not modelled | The emulator renders none of them, so they are unidentifiable virtually | Residual maps from real frames showing whether k1 suffices, and the evidence for any added term |
| Uncertainty systematic floor of 0.042 px mono unbinned, 0.028 px mono 2×2, 0.063 px CFA | Tuned on emulator PSF, noise and v2 measurer | A floor tuned and validated on real tuning and held-out frames with independent coverage tests |
| Stable optics within a session | The emulator lens never drifts | Repeat calibrations over temperature and time to measure stability |
| Linear RAW16 with declared black/white level and Gaussian PSF | Real sensors add non-linearity, hot pixels, amp glow and an irregular PSF | Sensor response and saturation characterization; re-qualified detection thresholds |
| Virtual-profile accept/reject with prior retention | `VirtualOpticalCalibrationReviewService` refuses physical rigs | Operator review, explicit real-rig activation, rejected-candidate retention and rollback on actual rig profiles; no automatic write-back |
| Fixed J2000 positions, geometric altitude | Real images include refraction near the horizon; HYG 4.2 has no epoch or proper-motion contract | Refraction qualification consistent in renderer and solver; proper motion only on the #521 data contract |
| Clock contributes only a bound | No real clock in the emulator | #1095 clock-health facts from the actual host |

## Known gaps and follow-ups

None of these is unfinished in-scope work moved out to allow closure. Each is either a declared limit of the
virtual envelope or a defect outside this milestone, and each has an owner.

- **Lens families other than equidistant fisheye are not emulator-qualified.** Both qualified rigs (ASI174 mono and
  ASI178MC with the Fujinon FE185C057HA-1 candidate) declare `EquidistantFisheye`. The radial model and the
  uncertainty core support equisolid, orthographic, stereographic and perspective (rectilinear) projections, and
  unit-level Monte Carlo coverage passes for each. None of them has run through the VirtualSky pixel path, the
  session fit and the held-out scorer, so they are outside the supported virtual envelope. Supporting another family
  needs that same held-out qualification, which the emulator can provide without hardware: [#1126](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1126) owns it.
- **The V1 harness keeps pre-#522 labels.** `VirtualAstrometryQualificationTests` still names its partition
  `held-out-stationary-baseline` and writes `finalExposureQualification: "pending-522-1106"`. These are stale
  labels, not a stationary renderer. The production `VirtualSkyCameraModule` has a single, exposure-integrated
  stellar path (`stellar-exposure-iau1976-bounded-midpoint-v1` with `bounded-temporal-gaussian-native-v1`), and the
  report measured here shows `sceneUtc` at the exposure midpoint (`08:00:00.5` for a 1 s exposure from
  `08:00:00`). This report is the #1106 rerun that the label refers to. The labels were left unchanged so the
  harness source remains the one #1102 reviewed.
- **Solving is qualified at 1 s exposures.** Every astrometry harness captures one-second exposures at gain 150.
  Longer exposures are qualified for rendering, visibility and resources by the #522 workloads, and the #1103
  `LongExposure_ReportsTrailLengthAndAngle` Unit scenario (180 s) checks trail measurement. Solving accuracy on long trailed exposures is not
  claimed.
- **VirtualSky acquisition callbacks report a zero-length exposure.** The reported start, end and readout times are
  all the requested start. The harnesses declare the logical interval `[start, start + applied exposure]` and keep
  the callbacks separately. Real capture timing belongs to #1095 and #1107.
- **The #1104 shared covariance is somewhat optimistic.** In this rerun, the nine #1105 session-chain totals again average 5.57 against an expected 4, with a
maximum of 10.18 against 18.4668. The rows within one chain share a calibration and are correlated, so this is
not a failure, and all nine stay inside the limit. Shared-calibration intervals may still be modestly narrow. This
needs checking again on real frames under #1107 before the covariance is used for physical uncertainty.
- **Cold blind acquisition is the dominant cost.** A blind solve on the virtual frames takes 2.3 s median and 3.5 s at most, against a 15 s budget. A
warm solve takes 6 ms, and a full session calibration 15–19 s, which is about 7 solves. A consumer should seed
from a compatible warm assessment wherever it can, and should not run calibration per frame.
- **#1118** (open): periodic acquisitions scheduled before a deployment becomes effective. It was found during
  #522 and predates it; it does not affect astrometry.
- **#1123** (open): an intermittent `StructuredCloudAssessment` Integration test. It is outside astrometry.
- **Issue bookkeeping.** #1103, #1104 and #1105 closed with their acceptance checkboxes unticked. They are audited
  and ticked with evidence links after this PR merges.
