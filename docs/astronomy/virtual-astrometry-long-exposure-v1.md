# Virtual all-sky astrometry qualification — long exposures (v1)

Issue [#1168](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1168), under epic
[#520](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/520), adds the exposure axis to the virtual
astrometry envelope. The #1106 envelope ([v1](virtual-astrometry-qualification-v1.md)) and the #1126
projection-family envelope ([v2](virtual-astrometry-qualification-v2.md)) were measured on 1 s frames. This report
runs the same #1102, #1103, #1104 and #1105 harnesses, with the trail-aware v2 measurer, on 20 s and 60 s VirtualSky
sensor-pixel frames, using the [#1168 manifest](../validation/issue-1168-qualification-manifest.json), its
[runner](../validation/issue-1168-qualification.sh) and its [metrics](../validation/issue-1168-metrics.sh). It is
the single statement of which exposures the virtual milestone supports. v2 remains the 1 s projection-family
envelope.

**Status: measured; revision rerun pending.**

- **Declared before measurement.** The matrix, refusals, withheld cells, tolerances, floor rule and continuity
  rules below were committed in the manifest at `27c611dc`, before any continuity, tuning or held-out run.
- **Re-frozen once.** The first continuity run found two harness declaration defects. Their correction re-froze the
  manifest before any evidence run; see [harness defects found on continuity](#harness-defects-found-on-continuity).
- **Measured.** Continuity, tuning and the single held-out pack then ran on the re-frozen manifest; see
  [results](#results). Three held-out uncertainty processes failed, so covariance above 1 s is withheld from the
  [supported envelope](#supported-envelope).
- **Rerun pending.** `development/v1` has since changed paths that this manifest's revision-rerun rule covers. The
  envelope is final only after the [revision rerun](#revision-rerun).

This is a virtual qualification only. It makes no claim about physical accuracy, and no profile here is a physical
lens certification. No product code changed: `src/` is identical to `development/v1` at `0639e27d`.

## Reference time: the exposure midpoint

A long exposure's stars trail across the sensor. Every frame here is rendered at its exposure midpoint, and solved,
scored and mapped there:

- The renderer's scene time is `start + exposure / 2`.
- `AstrometricFrameContext.MidpointUtc` equals the rendered scene time exactly. Every case records
  `midpointDeltaTicks`, which must be 0.
- `CameraFrame` timestamps are the exposure **start**, not the midpoint.

**Consumer obligation.** A consumer of this envelope, first #1169 (registered stacking), must bind the solve and
every cross-frame mapping to the exposure midpoint, not to the frame timestamp. The pixel harness's negative
control binds start-UTC instead. It reports the resulting pose error and start-time sky displacement against the
predicted half-exposure sidereal motion, so the size of the mistake is on record. The negative control is a
diagnostic, not a pass condition; its numbers are under [results](#negative-control-start-utc-binding).

## Frozen matrix

Every cell uses gain 150, a PSF of σ 1 px with a 4 px radius, and the shared HYG 4.2 snapshot
`hyg-v4.2-p3-s2-r1` (119,625 rows, database SHA-256 `b51d18b7…b0f9e2`). The solver catalog is unchanged at
magnitude 5 (1,637 entries) and owned by #1167. The `product-depth` variant deepens only the render; no
deeper-catalog claim is made.

Families:
- `equidistant`: the v1 fisheye, mono (ASI174) and CFA (`cfa-native`, ASI178MC).
- `rectilinear`: the v2 Kowa LM6HC 6 mm substitution, mono.

Every other v2 family stays qualified at 1 s only. Long exposure is not qualified for them.

| Harness | Cells |
| --- | --- |
| Pixels, blind/warm/readouts (#1102) | equidistant and rectilinear at 1, 20 and 60 s with no variant; equidistant at 20 and 60 s under each variant |
| Measured stars (#1103) | equidistant at 1, 20 and 60 s; rectilinear at 1 and 20 s |
| Session optical calibration (#1104) | as measured stars |
| Astrometric uncertainty (#1105) | as measured stars |
| Configured-resolution resources | equidistant 1, 20, 60, 60, 20, 1 s, then rectilinear 1, 20, 20, 1 s, in one session |
| Renderer capacity | the rectilinear boundary and every 60 s refusal, once |

Each runs as a tuning pack, then once as a held-out pack, except resources and renderer capacity, which run only in
the held-out pack. Pixels and resources run the v2 measurer.

### Variants

Each variant other than `none` renders only `mono-native` and `cfa-native`, on equidistant.

| Variant | Render depth | Max results | Background (e⁻/s) | Clouds | Expected outcome |
| --- | --- | --- | --- | --- | --- |
| `none` | mag 5 | 2,000 | 2 | — | accepted |
| `product-depth` | mag 6.5 | 32,768 | 2 | — | accepted or fail-closed |
| `bright-background` | mag 5 | 2,000 | 20 | — | accepted or fail-closed |
| `clouds-partial` | mag 5 | 2,000 | 2 | 40% coverage, opacity ≤ 0.9 | accepted or fail-closed |
| `overcast` | mag 5 | 2,000 | 2 | full coverage, opacity 1 | rejected |

"Fail-closed" means every case either accepts and passes or rejects; nothing is accepted wrongly. An accepted
assessment with association precision below 1, pose error above 0.06° or focal relative error above 0.001 is a
failure in every variant.

### Declared refusals and withheld cells

Each refusal is listed in the manifest with its exact exception type, message and stage. The harnesses catch only
that refusal, in that cell. Any other exception, or a refusal in an unlisted cell, fails the run.

| Cells | Disposition | Stage | Exception |
| --- | --- | --- | --- |
| Every mono view of a cloud variant | unsupported-render-refused | initialize | `NotSupportedException`: the native mono readout does not support clouds |
| Rectilinear full-frame views (`mono-native`, `mono-bin2`, `mono-mirror`, `mono-roll`) at 60 s | unsupported-render-refused | capture | `InvalidOperationException`: `stellar-exposure-temporal-budget-exceeded` |
| Measured stars, calibration and uncertainty for rectilinear at 60 s | withheld-render-refused | — | Not run: these harnesses render the refused full-frame views |

## Renderer capacity

The renderer integrates each star's trail in temporal slots:

- Each slot moves the star at most 0.15 px: the configured bound, which the σ 1 px PSF does not tighten.
- A source needs `ceil(exposureSeconds × speedBound / 0.15)` slots, where `speedBound` is its fastest sensor-plane
  motion.
- More than 64 slots refuses the capture.

On the rectilinear 6 mm `mono-native` view, the fastest source moves 0.16882 px/s. That view renders at 56 s
(64 slots) and refuses at 57 s (65 slots); 60 s would need 68. The derived maximum exposure is 56.86 s. The
renderer-capacity run measures this boundary, and
[#1179](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1179) tracks raising it. The rectilinear `mono-roi` and
`mono-roi-bin2` views are not refused at 60 s, and the renderer-capacity run renders them.

## Tolerances

Every #1106/#1126 tolerance is unchanged. Nothing is weakened. The manifest names these additions:

- Same-frame and cross-frame round trips within 1e-4 px. Points that leave the horizon or aperture are counted as
  excluded, not failed.
  - Near the optical axis the fisheye projectors take θ = acos(Up). A ray whose Up returns k ulps below 1 lands
    about √(2kε)·f px off axis, with ε = 2⁻⁵³, so one ulp is f·2⁻²⁶ px.
  - The bound covers a single-ulp axis error only while f ≤ 6,711 px/rad. The matrix satisfies that: its largest
    fitted focal is 735.6 px/rad, where one ulp is 1.096e-5 px and the bound admits 83 ulps.
  - The bound is 7,500 times below the unchanged 0.75 px mapping tolerance.
  - The report records the maximum round-trip error for each destination, so the typical value and the on-axis value
    are both visible.
- Truth is the midpoint of each star's rendered trail. A trail truncated by the horizon, the aperture or the frame
  is excluded from scoring and counted.
- Saturated detections are scored unless excluded as `saturated-excessive`. Every exclusion reason is counted per
  cell.
- An unmodeled-source association is a failure. That is an association whose source lies more than 1.5 px from
  its catalog star's trail and nearer another rendered star, for example a fainter `product-depth` star matched to a
  catalog star.
- Measured stars: the v1 recall, centroid-regression and time-ratio comparisons gate only 1 s. At 20 s and 60 s,
  v1 is reported, not compared. `no-candidate` is a reported missed-star category, not a failure.
- Trail metadata, for truth trails of at least 3 px:
  - length error p95 within max(0.5 px, 15%);
  - angle error p95 within 10°.

  This gates only the trail-metadata envelope row, never solving.
- Resources: capture p95 within the #522 renderer budgets:

  | Exposure | Mono | CFA |
  | --- | --- | --- |
  | 1 s | 10 s | 20 s |
  | 20 s, 60 s | 10 s | 40 s |

  Warm solve stays within 500 ms.

### Uncertainty floors

The #1105 floors are declared for 1 s only. The 20 s and 60 s floors are derived on the tuning pack by the #1105
rule, then committed in one commit before held-out:

- Each tuning frame reports its required floor: the floor that brings χ² per degree of freedom to 1.
- Within each format group (mono, mono 2×2, CFA), take the median across every tuned family at that exposure.
- Round half-up to 0.001 px. Null floors are excluded and counted.

The tuning runs at 20 s and 60 s estimate at the provisional 1 s floors only to report each frame's required
floor. Their coverage outcome is not a result. A held-out coverage miss is recorded, and covariance for that
exposure is withheld; it is never retuned.

The declared floors come from tuning pack `tuning-f7e69d99` alone: revision `f7e69d99`, `SHA256SUMS` sha
`c8d9c647ff486f45e34df29b52e8268ad8c511b4034cc2f0d57b9554ac6b697d`. Running `issue-1168-metrics.sh floors` on that
pack reproduces them. Its output is committed as
[`issue-1168-floors.json`](../validation/issue-1168-floors.json). The C# initializer it prints is committed unchanged
in `VirtualAstrometricUncertaintyQualificationTests`.

| Exposure | Group | Families | Frames | Excluded | Median (px) | Declared (px) |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| 20 s | mono | equidistant, rectilinear | 64 | 0 | 0.014950 | 0.015 |
| 20 s | mono 2×2 | equidistant, rectilinear | 32 | 0 | 0.018835 | 0.019 |
| 20 s | CFA | equidistant | 8 | 0 | 0.044229 | 0.044 |
| 60 s | mono | equidistant | 32 | 0 | 0.022492 | 0.022 |
| 60 s | mono 2×2 | equidistant | 16 | 0 | 0.015683 | 0.016 |
| 60 s | CFA | equidistant | 8 | 0 | 0.038144 | 0.038 |

The 60 s mono median, 0.0224921 px, is 7.9e-6 px below the 0.0225 half-up tie. The rounding to 0.022 is still
unambiguous.

Rectilinear at 60 s is refused, so the 60 s floors are equidistant only. The three provisional processes failed
pooled coverage checks: 3 for equidistant 20 s (56 frames), 4 for equidistant 60 s (56) and 4 for rectilinear
20 s (48). Every one is over-coverage, between 0.929 and 1.000 against 0.6827. That is the expected result when
the 1 s floors are larger than these exposures require. The processes recorded no other failure. As declared,
these are not outcomes. The 1 s floors are unchanged.

## Continuity with #1126

With every selector unset, the harnesses reproduce the #1126 1 s v1 equidistant matrix. The continuity run builds
base `0639e27d` and this head in Release. It then runs every #1126 manifest entry on base and then on head, in one
heavy-lock session on hvo-dev-02. No `src/` path differs between the two.

A pair passes only when all of these hold:

- Both processes complete, with every listed report present.
- **Pinned base failures.** The base's deterministic failures equal exactly the set the manifest pins for that pair,
  and none for any other pair. Only `pixels-tuning` has a pinned set: the two #1126 tuning failures v2 records,
  `02-mono-roi-bin2-0` rejected as ambiguous and `02-mono-roi-bin2-1` expected Warm.
- **Same deterministic failures.** The head's deterministic failures equal the base's, with the same case IDs and
  reason codes.
- **Explained outcomes.** A process that records no failure passes. A failed process fails with exactly its
  harness's final `Assert.IsEmpty(failures)` message, listing its report's failures in order. The `pixels-tuning`
  base is the one exception: it must fail with exactly the pinned #1126 grid-count assertion, 725 of 750, as v2
  records. The head counts only accepted sources there, so it reaches `Assert.IsEmpty`.
- Every differing report leaf is classified.

A base that records any other deterministic failure, or fails any other way, invalidates the run as environment or
setup. That pair is `invalid`, the pack is incomplete, and it is not a continuity pass.

**Timing is kept apart.** A report failure that matches one of the manifest's anchored resource-failure patterns is
a timing or resource outcome, not output: a measured time, allocation or working set over its declared budget.

- The patterns cover the pixel solve time budget, and the measured-stars, resources, calibration and uncertainty
  time, allocation and working-set budgets.
- None matches a solve mode, reason code, association, accuracy, count, mapping or round-trip failure.
- With no `src/` change, a resource failure that appears or disappears between revisions is a classified timing
  difference. Both revisions' elapsed seconds are recorded. It never fails continuity.
- A deterministic failure that appears or disappears always fails continuity.

Every report leaf that differs must be classified:

| Class | Meaning |
| --- | --- |
| identity | The revision fields. |
| run-varying | Timing and resource fields. |
| resource-outcome | A resource failure, split out of `failures` before classification. |
| declared-changed | An exact base and head value named in the manifest, such as the schema versions. |
| declared-added | A new field, such as `exposureSeconds`, `cases`, `cells` or `negativeControl`. |
| scorer-by-design | Under a scorer prefix: the scorers became trail-aware by design. |

Any unclassified leaf fails continuity. So does any difference in solver, measurement, mapping, uncertainty or
identity hashes.

### Harness defects found on continuity

The first continuity run, at `27c611dc`, found two harness declaration defects. Neither is a product change, and
neither changed a deterministic output between base and head. That run is kept as a labelled diagnostic, not as
evidence:

- Pack `continuity-27c611dc`, status `failed`.
- Archive `continuity-27c611dc.diagnostic.tar.zst`, SHA-256
  `9e3512d122f12a83fc5168b5b55cf02c6f42270f21fcf38e209244396b20d0bd`.

The correction changes only the test's tolerance constant, the manifest, the runner, the metrics script and this
report. Every continuity pair is rerun from scratch on the re-frozen manifest, including the six that passed.

1. **Round-trip tolerance.**
   - Cause: the declared 1e-6 px bound was below the projectors' conditioning on the optical axis. 16 head round
     trips, in `pixels-tuning` and `actual-pixels-blind-warm-readouts`, missed it at 2.83e-6 to 1.096e-5 px. Each
     equals f·2⁻²⁶ for its source's fitted focal (189.7, 379.3 or 735.6 px/rad) to within 1e-8 relative: one ulp of
     Up at a grid point on the axis. Every other round trip was at most 1.8e-10 px.
   - Correction: the 1e-4 px bound [derived above](#tolerances).
   - Check: the largest diagnostic error is 9 times below the new bound.
2. **Process-status equality.**
   - Cause: continuity required equal process status on both revisions. Two #1126 outcomes break that without any
     output difference:
     - A timing budget can fail on one revision only. The `measured-stars-held-out` base failed
       `01-mono-roi-bin2: v2 measurement 44 ms vs v1 10 ms`, the time-ratio miss v2 records; the head passed.
     - The frozen #1126 pixel harness stops on its grid-count assertion before its failure list.
   - Correction: the pinned base failures, the exact process-outcome rule and the resource-outcome class above.
   - Check: applied to all nine diagnostic pairs, the corrected rules explain every base and head process outcome,
     and every base matches its pinned set. `measured-stars-held-out` passes with two `resource-outcome` leaves. The
     only remaining failures are the 16 round-trip misses that the first correction removes. Truncated, reordered and
     wrong failure messages are rejected.

## Reproduction

On an idle host with the verified snapshot installed:

```bash
export HVO_ASTROMETRY_CATALOG_ROOT=<snapshot root> HVO_HEAVY_LOCK=/tmp/hvo-520-heavy.lock
docs/validation/issue-1168-qualification.sh continuity <new pack directory>
docs/validation/issue-1168-qualification.sh tuning <new pack directory>
docs/validation/issue-1168-metrics.sh floors <tuning pack>
docs/validation/issue-1168-qualification.sh final <new pack directory>
docs/validation/issue-1168-metrics.sh summary <pack>
```

The runner refuses a dirty tree and runs one process per declared cell. It records the host, the TRX counters,
and the harness source blobs. Every pack is sealed with `SHA256SUMS`.

## Revision rerun

The #520 coordinator pre-declared this rule at 2026-10-07T08:08:39Z, before any rerun data. It was committed here
before the branch was synchronized with `development/v1`.

**Trigger.** Manifest rule 10 requires a revision rerun: after `350044f8`, `development/v1` changed paths in
`revisionRerunPaths`.
- #1170's `58f7b095`, merged in `f7d19865`, changes `src/HVO.SkyMonitor.Imaging/Linear16ArithmeticMean.cs` and
  `Mono16DisplayStretch.cs`.
- PR #1176 (#518), not yet merged when the rule was declared, changes AgentCore, Astronomy, Imaging and VirtualSky
  module paths.

**What runs.** The rerun runs once, after #518 merges, at this branch's synchronized head:
1. Continuity: the new `development/v1` base against the synchronized head, under the continuity rules above.
2. The complete final manifest set, all 40 processes, with the committed floors unchanged.

There is no tuning rerun. This is a revision rerun, not a re-roll.

**Both-packs rule.**
- The envelope claims support only for a cell that passes in both `final-350044f8` and the rerun pack.
- A cell whose outcome differs between the packs is listed with both outcomes and the code change that explains it.
- An unexplained difference stops the work and is reported. It is not settled by another run.
- [List, don't absorb](#continuity-result) and the [allocation tripwire](#continuity-result) apply to the rerun
  pack as they did to the held-out pack.

## Results

All three packs ran sequentially under the heavy lock, from a clean tree on hvo-dev-02:
- Intel Core Ultra 9 285H, 8 logical processors, SDK 10.0.401;
- manifest SHA-256 `cd04647689fb1ab40b2c1f9513d8f61fb7bb3c33dc776a58e8a281a6359cfe65`.

Each pack is sealed by its `SHA256SUMS`, and the table gives that file's SHA-256. Times are UTC on 2026-10-07.

| Pack | Revision | Time | Outcome | `SHA256SUMS` SHA-256 |
| --- | --- | --- | --- | --- |
| `continuity-f7e69d99` | `f7e69d99` against base `0639e27d` | 05:22:25–06:05:30 | passed, 9 of 9 pairs | `63a1d0efc58af4cac6d65171600c61f5c6e991e115498249600388978caa5dac` |
| `tuning-f7e69d99` | `f7e69d99` | 06:07:02–06:28:14 | 26 of 29 processes passed; the 3 failures are the provisional uncertainty runs, which are not outcomes | `c8d9c647ff486f45e34df29b52e8268ad8c511b4034cc2f0d57b9554ac6b697d` |
| `final-350044f8` | `350044f8` | 06:30:36–07:59:38 | `status: failed`: 37 of 40 processes passed and 3 uncertainty processes failed | `ef50c44284f28eb38ea2193efece64350881a0a8d3d6f69d9a188f01d6c6d690` |

**The held-out pack.**
- `350044f8` adds only the declared floors to `f7e69d99`: the C# initializer, `issue-1168-floors.json` and the
  floors table above.
- Held-out ran once at `350044f8`. Nothing was re-run, and no floor, tolerance or partition changed afterwards. The
  pack stays failed.
- Every run executed exactly its expected test and attached exactly one report.

**Its metrics summary.**
- It was extracted with `issue-1168-metrics.sh summary` at `6b545747`, which changes only that script. The script
  had passed whole reports to jq as arguments, which exceeded the argument-length limit; it now passes them through
  files.
- Both versions produce byte-identical output on packs small enough for the old one to read.
- The summary's SHA-256 is `42010f94113d100ba568d2cd0b913db86b02c1d1bdb9f90e928f91371db261cc`.
- An earlier `summary` invocation, over the tuning pack, ran on hvo-dev-02 from 06:31:14Z to 06:31:18Z and failed
  on that argument limit. That was during the held-out pack's build, before its first measured process started at
  06:34:00Z.

**Independent verification.** The #520 coordinator independently verified:
- the pack hashes, the index and every run's report and test counts;
- that no held-out failure matches a resource-failure pattern;
- the floors, re-derived from the tuning pack's reports: they are bit-identical medians with the same declared
  values.

### Continuity result

`continuity-f7e69d99` compared base `0639e27d` with head `f7e69d99` on the #1126 manifest
(`issue-1126-qualification-manifest.json`, SHA-256 `8b12ad63bc564b80ef3752ddbe53fbf97b5c2ceccb887a16a7399f5410cbacee`).
Its verdict is `passed`:
- Every pair passes, and every differing report leaf is classified.
- No solver, measurement, mapping, uncertainty or identity hash differs.

| Pair | Base / head | Elapsed base / head (s) | Identical leaves | Classified differing leaves |
| --- | --- | ---: | ---: | --- |
| `pixels-tuning` | failed / failed | 49 / 53 | 462,641 | declared-added 3,672, declared-changed 2, identity 1, run-varying 90 |
| `measured-stars-tuning` | passed / passed | 40 / 43 | 1,985 | declared-added 62, declared-changed 1, identity 1, run-varying 73 |
| `optical-calibration-tuning` | passed / passed | 46 / 43 | 12,260 | declared-added 1, identity 1, run-varying 39 |
| `astrometric-uncertainty-tuning` | passed / passed | 76 / 79 | 2,419 | declared-added 2, identity 1, run-varying 387 |
| `actual-pixels-blind-warm-readouts` | passed / passed | 143 / 161 | 1,406,999 | declared-added 10,973, declared-changed 2, identity 1, run-varying 270 |
| `actual-configured-resolution-resources` | passed / passed | 64 / 72 | 2,662 | declared-added 1,208, identity 2, run-varying 733 |
| `measured-stars-held-out` | passed / passed | 113 / 117 | 5,717 | declared-added 174, declared-changed 1, identity 1, run-varying 207, scorer-by-design 38 |
| `optical-calibration-held-out` | passed / passed | 129 / 128 | 36,421 | declared-added 1, identity 1, run-varying 114 |
| `astrometric-uncertainty-held-out` | passed / passed | 376 / 382 | 14,843 | declared-added 2, identity 1, run-varying 2,857 |

- **`pixels-tuning`.** Base and head each record exactly the pinned deterministic failures:
  - `02-mono-roi-bin2-0: Ambiguous: multiple independently verified orientations`
  - `02-mono-roi-bin2-1: expected Warm solve`
- **Two conditions set before this run reported:**
  - **List, don't absorb.** The verdict lists every resource failure by pair, with its message and both revisions'
    values, even though none fails continuity.
  - **Allocation tripwire.** A head-only allocation miss, with the base within budget, stops the run for review. It
    does not pass as a timing difference. Working-set and time misses are listed.
- **Resource failures listed.** The only one in the pack is `02-mono-roi-bin2-1: solve exceeded declared cold/warm
  time budget` in `pixels-tuning`. It appears on both base and head, so it creates no differing leaf. No pair has a
  `resource-outcome` leaf or a recorded reason, and the allocation tripwire was not triggered.

### Tuning result

All 29 tuning processes completed with their reports. Every pixel, measured-star and calibration process passed.
The three provisional uncertainty processes failed as described under [uncertainty floors](#uncertainty-floors),
and they are not outcomes. The declared floors were committed at `350044f8`, before the held-out pack.

### Held-out pixels

Each pixel case is one blind or warm solve; the blind count is in brackets. Variant cells render `mono-native` and
the four `cfa-*` views.

| Cell | Views rendered | Cases (blind) | Accepted | Rejected | Worst pose error | Worst focal relative error | Worst withheld p95 | Max round trip (px) |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| equidistant 1 s | 10 | 90 (30) | 90 | 0 | 0.0038° | 5.3e-5 | 0.022 px | 5.65e-6 |
| equidistant 20 s | 10 | 90 (30) | 90 | 0 | 0.0032° | 4.3e-5 | 0.016 px | 1.096e-5 |
| equidistant 60 s | 10 | 90 (30) | 90 | 0 | 0.0034° | 4.2e-5 | 0.018 px | 1.096e-5 |
| rectilinear 1 s | 6 | 54 (18) | 54 | 0 | 0.0025° | 5.7e-5 | 0.034 px | 7.9e-12 |
| rectilinear 20 s | 6 | 54 (18) | 54 | 0 | 0.0015° | 1.3e-5 | 0.021 px | 7.9e-12 |
| rectilinear 60 s | 2 of 6 | 18 (6) | 18 | 0 | 0.0022° | 6.0e-6 | 0.021 px | 3.7e-12 |
| `product-depth` 20 s | 5 | 45 (21) | 36 | 9 | 0.0015° | 2.1e-5 | 0.032 px | 2.0e-10 |
| `product-depth` 60 s | 5 | 45 (37) | 12 | 33 | 0.0008° | 7.6e-6 | 0.016 px | 1.7e-10 |
| `bright-background` 20 s | 5 | 45 (15) | 45 | 0 | 0.0013° | 1.3e-5 | 0.013 px | 1.096e-5 |
| `bright-background` 60 s | 5 | 45 (39) | 9 | 36 | 0.0012° | 1.3e-5 | 0.010 px | 1.8e-12 |
| `clouds-partial` 20 s | 4 of 5 | 36 (12) | 36 | 0 | 0.0010° | 6.3e-6 | 0.015 px | 1.096e-5 |
| `clouds-partial` 60 s | 4 of 5 | 36 (12) | 36 | 0 | 0.0014° | 2.5e-5 | 0.037 px | 1.9e-10 |
| `overcast` 20 s | 4 of 5 | 36 (36) | 0 | 36 | — | — | — | — |
| `overcast` 60 s | 4 of 5 | 36 (36) | 0 | 36 | — | — | — | — |

**Every cell:**
- met its expected outcome, and recorded no failure;
- association precision 1, no missing mapping and a midpoint delta of 0 ticks;
- ambiguity rate 0, rejected-underconstrained rate 0 and no unmodeled-source association.

**Rejections and refusals.** Every rejection is `rejected-acquisition-or-quality-failed`. The refusals occurred
exactly as declared, and nowhere else:
- **Rectilinear 60 s.** `mono-native`, `mono-bin2`, `mono-mirror` and `mono-roll` were refused at capture with
  `stellar-exposure-temporal-budget-exceeded`. That is 3 cases × 3 months × 4 views, 36 refusals.
- **Cloud variants.** `mono-native` was refused at initialize with `NotSupportedException`, once per month in each of
  the four cells.

**Accuracy.** The worst accepted pose error, 0.0038°, is 16 times inside the 0.06° tolerance. The worst focal
relative error, 5.7e-5, is 17 times inside 0.001.

**Variant outcomes by view.** Each entry is accepted cases out of 9. "Fail-closed" means at least one case rejected
and none failed.

| Variant | Exposure | `mono-native` | `cfa-native` and each `cfa-derived-phase-*` view |
| --- | --- | --- | --- |
| `product-depth` | 20 s | fail-closed, 0/9 | accepted, 9/9 |
| `product-depth` | 60 s | fail-closed, 0/9 | fail-closed, 3/9 |
| `bright-background` | 20 s | accepted, 9/9 | accepted, 9/9 |
| `bright-background` | 60 s | accepted, 9/9 | fail-closed, 0/9 |
| `clouds-partial` | 20 s, 60 s | refused | accepted, 9/9 |
| `overcast` | 20 s, 60 s | refused | fail-closed, 0/9 |

**Round trips.**
- The report records 202 per-destination maxima. 172 of them are at most 3.49e-10 px.
- The other 30 are each one ulp of Up on the optical axis, as [derived above](#tolerances):
  - 17 at f 379.32 px/rad, 5.65e-6 px;
  - 13 at f 735.55 px/rad, 1.096e-5 px.
- The largest is 9 times inside the 1e-4 px bound.

**Trails, truncations and exclusions.** Exclusions are the measurer's excluded candidates by reason, summed over the
cell's cases. Trail lengths are each case's median truth trail, then the longest truth trail; both are maxima over the
cell's cases. Associations count the solver's associations whose detection is flagged saturated or trailed.

| Cell | Trail median / max (px) | Truncated | Max midpoint bias (px) | Saturated / trailed associations | Saturated-excessive | Trail-too-long | Crowded | Blended | Low SNR | Hot pixel or cosmic ray | Masked or edge | Other |
| --- | --- | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| equidistant 1 s | 0.05 / 0.07 | 0 | 1.3e-6 | 271 / 1,151 | 8 | 4 | 9,013 | 1,463 | 9,754 | 45,935 | 1,099 | 0 |
| equidistant 20 s | 1.06 / 1.33 | 50 | 1.3e-4 | 14,964 / 0 | 852 | 48 | 9,715 | 2,893 | 2,518 | 12,334 | 871 | 12 |
| equidistant 60 s | 3.19 / 4.00 | 114 | 1.2e-3 | 31,096 / 11,938 | 6,221 | 18 | 9,688 | 2,398 | 2,230 | 11,624 | 926 | 0 |
| rectilinear 1 s | 0.08 / 0.15 | 0 | 3.4e-6 | 33 / 248 | 0 | 0 | 173 | 6 | 4 | 44 | 131 | 0 |
| rectilinear 20 s | 1.67 / 3.00 | 17 | 4.1e-4 | 1,103 / 2 | 49 | 0 | 218 | 51 | 13 | 60 | 164 | 0 |
| rectilinear 60 s | 4.49 / 6.58 | 16 | 2.0e-3 | 590 / 917 | 76 | 0 | 88 | 20 | 0 | 6 | 58 | 0 |
| `product-depth` 20 s | 1.06 / 1.35 | 139 | 1.3e-4 | 8,350 / 956 | 743 | 440 | 39,903 | 13,959 | 1,926 | 9,749 | 1,451 | 263 |
| `product-depth` 60 s | 3.17 / 4.04 | 359 | 1.2e-3 | 4,255 / 2,778 | 5,880 | 971 | 39,620 | 14,163 | 1,630 | 8,856 | 1,527 | 519 |
| `bright-background` 20 s | 1.06 / 1.33 | 26 | 1.3e-4 | 14,703 / 8 | 939 | 12 | 2,442 | 731 | 1,723 | 9,058 | 232 | 0 |
| `bright-background` 60 s | 3.19 / 4.00 | 52 | 1.2e-3 | 3,614 / 0 | 8,695 | 0 | 2,023 | 225 | 830 | 5,211 | 235 | 2 |
| `clouds-partial` 20 s | 1.06 / 1.33 | 22 | 1.3e-4 | 8,114 / 55 | 488 | 14 | 1,659 | 508 | 1,688 | 9,009 | 317 | 68 |
| `clouds-partial` 60 s | 3.19 / 4.00 | 40 | 1.2e-3 | 15,012 / 11,724 | 3,428 | 0 | 1,468 | 268 | 1,275 | 6,127 | 238 | 64 |
| `overcast` 20 s | 1.06 / 1.33 | 22 | 1.3e-4 | — | 12 | 0 | 198 | 81 | 8,336 | 39,371 | 1,039 | 0 |
| `overcast` 60 s | 3.19 / 4.00 | 40 | 1.2e-3 | — | 132 | 11 | 479 | 66 | 6,426 | 30,182 | 771 | 270 |

- "Other" is too-broad, unconverged and extended.
- The midpoint bias is the largest distance, over whole truth trails, between a trail's mid-exposure position and
  the mean of its rendered samples. That is the bias an integrated centroid carries relative to the midpoint the
  solver binds. At most 2.0e-3 px, it is negligible against the 0.75 px mapping tolerance.
- Truncated trails and associations are summed over the cell's cases.

### Negative control: start-UTC binding

The pixel harness takes the accepted blind case of `mono-native`, `mono-roi` and `cfa-native`, where rendered, and
solves it a second time with its frame context bound to the exposure start instead of the midpoint. That gives 61
control cases. The table gives one row per cell, with each value's range across the cell's control cases.

| Cell | Cases | Predicted half-exposure rotation | Midpoint ground pose error, max | Start-UTC ground pose error | Start-time sky through the midpoint solve, RMS (px) | Truth half-exposure motion, median (px) |
| --- | ---: | ---: | ---: | --- | --- | --- |
| equidistant 1 s | 9 | 0.0018° | 0.0016° | 0.0012–0.0025° | 0.014–0.028 | 0.013–0.027 |
| equidistant 20 s | 9 | 0.0364° | 0.0010° | 0.0356–0.0368° | 0.246–0.503 | 0.256–0.531 |
| equidistant 60 s | 9 | 0.1093° | 0.0013° | 0.1080–0.1092° | 0.739–1.506 | 0.768–1.593 |
| rectilinear 1 s | 6 | 0.0018° | 0.0011° | 0.0013–0.0027° | 0.028–0.051 | 0.035–0.041 |
| rectilinear 20 s | 6 | 0.0364° | 0.0012° | 0.0363–0.0367° | 0.708–0.819 | 0.692–0.812 |
| rectilinear 60 s | 3 | 0.1093° | 0.0013° | 0.1092–0.1095° | 2.116–2.268 | 2.075–2.245 |
| `product-depth` 20 s | 3 | 0.0364° | 0.0011° | 0.0364–0.0371° | 0.483–0.489 | 0.503–0.531 |
| `product-depth` 60 s | 1 | 0.1093° | 0.0008° | 0.1099° | 1.472 | 1.569 |
| `bright-background` 20 s | 6 | 0.0364° | 0.0010° | 0.0358–0.0368° | 0.249–0.501 | 0.259–0.531 |
| `bright-background` 60 s | 3 | 0.1093° | 0.0011° | 0.1082–0.1093° | 0.743–0.752 | 0.778–0.800 |
| `clouds-partial` 20 s | 3 | 0.0364° | 0.0007° | 0.0360–0.0363° | 0.492–0.505 | 0.503–0.531 |
| `clouds-partial` 60 s | 3 | 0.1093° | 0.0012° | 0.1087–0.1093° | 1.473–1.512 | 1.509–1.593 |

**What the solver does with start-UTC.**
- All 61 start-UTC solves are still `Accepted`, and each one's sky pose equals the midpoint solve's to within
  1.1e-11°.
- Its ground pose is wrong by the predicted half-exposure rotation: 0.036° at 20 s and 0.109° at 60 s. Bound to the
  midpoint, the ground pose error is at most 0.0016°.
- The mapping test takes each star's horizontal position at the exposure start, converts it to sky coordinates at
  the midpoint and maps it through the midpoint solve. It lands about as far from the trail midpoint as the star's
  true half-exposure motion: up to 2.3 px RMS at rectilinear 60 s.

The solver cannot detect the wrong reference time from the pixels. Binding the midpoint is therefore the consumer's
obligation: #1169 first, as stated under [reference time](#reference-time-the-exposure-midpoint), with these numbers
as the size of the mistake.

### Held-out measured stars

| Cell | Cases | v1 compared | Min v2 recall | Min v1 recall | Worst v2 centroid p95 (px) | False associations | Truncated | Missed eligible stars | Trail metadata |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | --- | --- |
| equidistant 1 s | 21 | yes | 0.992 | 0.809 | 0.170 | 0 | 0 | crowded 9, masked 1, saturated-excessive 1 | not applicable |
| equidistant 20 s | 21 | no | 0.960 | 0.451 | 0.125 | 0 | 12 | crowded 49, edge 2, no-candidate 21, saturated-excessive 49 | not applicable |
| equidistant 60 s | 21 | no | 0.804 | 0 | 0.139 | 0 | 17 | crowded 62, edge 1, no-candidate 57, saturated-excessive 445 | **not met** |
| rectilinear 1 s | 18 | yes | 1.000 | 0.791 | 0.175 | 0 | 0 | none | not applicable |
| rectilinear 20 s | 18 | no | 0.983 | 0.769 | 0.050 | 0 | 6 | no-candidate 10, saturated-excessive 4 | not applicable |

- Every measured-stars process passed. At 20 s and 60 s v1 is reported, not compared.
- v1 is not trail-aware, and it loses the trailed stars: its minimum recall falls to 0.451 at 20 s and 0 at 60 s.
  The trail-aware v2 measurer keeps at least 0.804.

**Trail metadata.** Only equidistant 60 s has scored trails, on CFA; its 18 mono cases have none. All three CFA
cases miss the length row:

| Case | Scored trails | Reported | Recovered without metadata | Not recovered | Normalized length error p95 | Angle error p95 | Status |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| `01-cfa-native` | 382 | 307 | 0 | 75 | 1.197 | 2.91° | not met |
| `05-cfa-native` | 345 | 263 | 1 | 81 | 1.117 | 3.00° | not met |
| `09-cfa-native` | 313 | 256 | 0 | 57 | 1.216 | 2.93° | not met |

- **What is scored.** Truth trails of at least 3 px. The length error is `|reported − truth| / max(0.5, 0.15 ×
  truth)`.
- **When the row is met.** Every recovered trail reports metadata, the length p95 is at most 1 and the angle p95 is
  at most 10°.
- **What it gates.** Only the trail-metadata envelope row. Solving is never gated on it.

### Held-out session optical calibration

Every calibration process passed. Each cell has 3 cases, all `Accepted`, and 9 withheld-frame scores.

| Cell | Worst withheld pose error | Worst withheld p95 |
| --- | ---: | ---: |
| equidistant 1 s | 0.0040° | 0.019 px |
| equidistant 20 s | 0.0037° | 0.017 px |
| equidistant 60 s | 0.0046° | 0.022 px |
| rectilinear 1 s | 0.0029° | 0.051 px |
| rectilinear 20 s | 0.0014° | 0.029 px |

### Held-out astrometric uncertainty

Held-out estimated at the declared floor for its exposure in every cell, using the
`enu-rotation-vector-radians-then-log-focal-scale` convention:

| Index | Parameter |
| --- | --- |
| 0 | ENU rotation vector, east component |
| 1 | ENU rotation vector, north component |
| 2 | ENU rotation vector, up component |
| 3 | log focal scale |

| Cell | Status | Floors | Frames | Released | Withheld | Mean χ²/dof | Held-out χ²/dof | Pooled ellipsoid coverage at 0.6827 / 0.95 / 0.99 | Failures |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| equidistant 1 s | passed | `declared-1s` | 420 | 420 | 0 | 0.972 | 0.977 | 0.707 / 0.943 / 0.986 | 0 |
| equidistant 20 s | **failed** | `declared-20s` | 420 | 359 | 61 | 1.014 | 1.042 | 0.616 / 0.944 / 0.983 | 7 |
| equidistant 60 s | **failed** | `declared-60s` | 420 | 292 | 128 | 0.990 | 1.052 | 0.589 / 0.935 / 0.976 | 18 |
| rectilinear 1 s | passed | `declared-1s` | 360 | 360 | 0 | 0.971 | 0.967 | 0.681 / 0.956 / 0.992 | 0 |
| rectilinear 20 s | **failed** | `declared-20s` | 360 | 339 | 21 | 0.898 | 0.964 | 0.720 / 0.950 / 0.991 | 5 |

**Released and withheld.**
- A frame is *released* when the estimator's own validity gate marks its covariance `available`. A *withheld* frame
  carries `model-invalid-residual-excess` or `model-invalid-held-out-prediction`.
- Coverage is computed over released frames only.

The harness turns withholding into a failure per profile and pooled:

```csharp
private const double MaximumFalseWithholdFraction = .02;
if (withheld > MaximumFalseWithholdFraction * all.Length + 1)
    failures.Add($"{label}: {withheld} of {all.Length} accepted frames withheld: …");
```

At 60 frames a profile fails at 3 withheld. Pooled, it fails at 10 of 420 or 9 of 360.

It fails a coverage level when the coverage of released frames misses that level by more than:

```csharp
var bound = CoverageToleranceSigma * Math.Sqrt(Levels[level] * (1 - Levels[level]) / n);
```

That uses `CoverageToleranceSigma = 3.5` and `Levels = [.6827, .95, .99]`, over `n` released frames.

**The 30 failures:**

- **Equidistant 20 s: 7 failures, all withholding.**
  - Profiles over the limit: `mono-native` 15 of 60, `mono-roi` 15, `mono-mirror` 13, `mono-roll` 14 and
    `cfa-native` 4.
  - Pooled: 61 of 420.
  - Session chain: one total withheld, `05-session-roi: total withheld model-invalid-residual-excess
    model-invalid-residual-excess`.
  - `mono-bin2` and `mono-roi-bin2` withheld none.
- **Equidistant 60 s: 18 failures.**
  - Withholding, 12 failures:
    - profiles: `mono-native` 23 of 60, `mono-roi` 18, `mono-bin2` 8, `mono-roi-bin2` 8, `mono-mirror` 22,
      `mono-roll` 24 and `cfa-native` 25;
    - pooled: 128 of 420;
    - session chain: 4 totals withheld, native and ROI for months 01 and 09.
  - Log-focal-scale (parameter 3) interval coverage below its level, 6 failures:

    | Profile | Coverage | Level | Bound | Released frames (n) |
    | --- | ---: | ---: | ---: | ---: |
    | `mono-roi` | 0.810 | 0.95 | 0.118 | 42 |
    | `mono-mirror` | 0.895 | 0.99 | 0.056 | 38 |
    | `mono-roll` | 0.778 | 0.95 | 0.127 | 36 |
    | `mono-roll` | 0.889 | 0.99 | 0.058 | 36 |
    | pooled | 0.880 | 0.95 | 0.045 | 292 |
    | pooled | 0.949 | 0.99 | 0.020 | 292 |

- **Rectilinear 20 s: 5 failures.**
  - Withholding: `mono-native` 10 of 60, `mono-roi` 3, `mono-mirror` 6, and pooled 21 of 360. `mono-roll` withheld
    2, within the limit; `mono-bin2` and `mono-roi-bin2` withheld none.
  - One coverage failure: on `mono-bin2`, the up-component (parameter 2) interval coverage is 0.917 at 0.6827
    (bound 0.210, n 60, none withheld).

**The estimator released overconfident covariance at 60 s.** The 60 s coverage failures are on *released* frames:
- pooled n 292 = 420 − 128 withheld;
- `mono-roi` 42, `mono-mirror` 38, `mono-roll` 36.

The estimator's validity gate passed those frames and released log-focal-scale intervals that are too narrow. Pooled,
0.880 of the 0.95 intervals contain the truth; the harness accepts no fewer than 0.905 = 0.95 − 0.045. At 0.99,
0.949 do, against at least 0.970. Withholding did not contain the miscalibration at 60 s.

The pooled 60 s ellipsoid coverage at 0.6827 is also low: 0.589, inside its bound of 0.095 (n 292) by 0.002. It is
not a failure.

**The rectilinear 20 s miss is over-coverage.** The `mono-bin2` up-component intervals are too wide: 0.917 contain
the truth at 0.6827. That is a conservative miscalibration, not overconfidence.

**What is withheld.** By the manifest's held-out rule, a held-out coverage miss withholds covariance for that
exposure. Two misses apply:
- 60 s: the equidistant overconfidence.
- 20 s: the rectilinear 20 s over-coverage, which withholds 20 s for every family. Equidistant 20 s failed on
  withholding alone.

So covariance is withheld at 20 s and 60 s in every family, and uncertainty is supported at 1 s only. Nothing was
retuned. [#1179](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1179) tracks requalification and whether
the product should gate covariance release by exposure.

### Held-out resources

All 10 resource processes passed, in the declared order, at concurrency 1:
- 30 measured operations after 5 warm-up operations;
- route `existing-production-through-qualified-pixel-harness`;
- backlog not applicable: bounded stateless capture and measurement, no queue.

The working-set figures are the process working set and cumulative process peak, not memory retained by the
operation.

| Order | Cell | Capture p95 (ms) | Detection median (ms) | Solve median / p95 (ms) | CPU median (ms) | Allocated median (MiB) | Peak working set (MiB) |
| ---: | --- | ---: | ---: | --- | ---: | ---: | ---: |
| 1 | equidistant 1 s mono | 160 | 144 | 6.3 / 6.9 | 316 | 103 | 343 |
| 1 | equidistant 1 s CFA | 924 | 1,023 | 7.2 / 10.5 | 1,955 | 540 | 1,382 |
| 2 | equidistant 20 s mono | 163 | 162 | 6.2 / 6.9 | 338 | 105 | 435 |
| 2 | equidistant 20 s CFA | 848 | 1,009 | 7.1 / 10.6 | 1,880 | 541 | 1,377 |
| 3 | equidistant 60 s mono | 195 | 178 | 6.0 / 6.8 | 388 | 109 | 365 |
| 3 | equidistant 60 s CFA | 1,137 | 1,125 | 7.2 / 9.7 | 2,194 | 554 | 1,378 |
| 4 | equidistant 60 s mono | 197 | 179 | 6.1 / 6.7 | 388 | 109 | 439 |
| 4 | equidistant 60 s CFA | 945 | 1,029 | 6.6 / 9.2 | 1,994 | 554 | 1,376 |
| 5 | equidistant 20 s mono | 158 | 157 | 5.9 / 6.6 | 328 | 105 | 337 |
| 5 | equidistant 20 s CFA | 838 | 997 | 7.8 / 11.3 | 1,834 | 541 | 1,380 |
| 6 | equidistant 1 s mono | 191 | 176 | 7.6 / 8.4 | 380 | 103 | 402 |
| 6 | equidistant 1 s CFA | 880 | 980 | 6.9 / 10.3 | 1,877 | 540 | 1,386 |
| 7 | rectilinear 1 s | 197 | 240 | 1.4 / 3.6 | 452 | 72 | 958 |
| 8 | rectilinear 20 s | 188 | 256 | 1.4 / 1.5 | 459 | 74 | 1,003 |
| 9 | rectilinear 20 s | 200 | 263 | 1.4 / 1.5 | 477 | 74 | 985 |
| 10 | rectilinear 1 s | 204 | 244 | 1.5 / 2.0 | 466 | 72 | 984 |

**Against budget.**
- The largest capture p95 is 1,137 ms (equidistant 60 s CFA), against 40 s. The largest mono capture p95 is 204 ms,
  against 10 s.
- The largest solve p95 is 11.3 ms, against the 500 ms warm-solve budget.
- Equidistant allocation grows with exposure: mono from 103 to 109 MiB (6%), CFA from 540 to 554 MiB (3%).
- Solve medians stay between 5.9 and 7.8 ms on equidistant and between 1.4 and 1.5 ms on rectilinear, at every
  exposure.

### Renderer capacity

The renderer-capacity process passed in 8 s with no failures. On rectilinear `mono-native`:
- fastest source speed 0.1688246568 px/s;
- 64 slots at 56 s, 65 at 57 s and 68 at 60 s, against at most 64 slots of 0.15 px;
- derived maximum exposure 56.8637 s;
- last rendered exposure 56 s and first refused 57 s.

**Probes that rendered, each in 64 slots:**

| Probe | Max motion per slot (px) |
| --- | ---: |
| Rectilinear boundary that fits | 0.1477 |
| Rectilinear 60 s `mono-roi` and `mono-roi-bin2` | 0.1494 |
| Equidistant 60 s `mono-native` | 0.1460 |
| Equidistant 60 s `cfa-native` | 0.1493 |
| Equidistant clouds `cfa-native` | 0.1493 |

**Probes refused as declared:**
- the rectilinear boundary that exceeds;
- rectilinear 60 s `mono-native`, `mono-bin2`, `mono-mirror` and `mono-roll`;
- equidistant clouds `mono-native`.

## Supported envelope

This table is the single statement of which exposures the virtual milestone supports. Every row is virtual-only and
makes no physical claim. Every row is subject to the [revision rerun](#revision-rerun): support stands only for a
cell that also passes in the rerun pack.

| Family | Exposure | Supported | Not supported or limited |
| --- | --- | --- | --- |
| equidistant, mono and CFA | 1 s | Blind and warm acquisition, readouts, configured-resolution resources, measured stars, optical calibration and uncertainty, as the [v2](virtual-astrometry-qualification-v2.md) equidistant row, with the trail-aware v2 measurer | As v2 |
| equidistant, mono and CFA | 20 s | Blind and warm acquisition, readouts, resources, measured stars and optical calibration | **Covariance withheld.** Withholding at the declared floors fails on four mono profiles and on CFA, and the rectilinear 20 s coverage miss withholds 20 s for every family |
| equidistant, mono and CFA | 60 s | Blind and warm acquisition, readouts, resources, measured stars and optical calibration | **Covariance withheld.** The estimator released overconfident log-focal-scale intervals: pooled 0.880 at 0.95. **Trail metadata not supported** on CFA: length p95 1.12–1.22 against 1 |
| rectilinear 6 mm, mono | 1 s | As the v2 rectilinear row | As v2 |
| rectilinear 6 mm, mono | 20 s | Blind and warm acquisition, readouts, resources, measured stars and optical calibration, inside the v2 distortion domain | **Covariance withheld.** `mono-bin2` up-component over-coverage, and withholding on three profiles |
| rectilinear 6 mm, mono | 60 s | Blind and warm acquisition on `mono-roi` and `mono-roi-bin2` only | **Full-frame views refused** at capture: the renderer's limit is 56 s. Measured stars, calibration and uncertainty are not run; resources are not measured at 60 s |
| equidistant `product-depth`, mag 6.5 render | 20 s, 60 s | Fail-closed: nothing accepted wrongly. CFA accepts 9 of 9 at 20 s | `mono-native` rejects every case at 20 s and 60 s. CFA accepts 3 of 9 per view at 60 s |
| equidistant `bright-background`, 20 e⁻/s | 20 s, 60 s | All views accept at 20 s; `mono-native` accepts at 60 s | CFA rejects every case at 60 s (fail-closed) |
| equidistant `clouds-partial` | 20 s, 60 s | CFA views accept every case | **Mono refused** at initialize: the native mono readout does not support clouds |
| equidistant `overcast` | 20 s, 60 s | Rejected, as declared | Mono refused, as for `clouds-partial` |
| other v2 families | 20 s, 60 s | — | Not qualified for long exposure |

**Consumer constraints**, first for #1169 (registered stacking):
- **Covariance.** Treat astrometric covariance above 1 s as unsupported, even when the estimator marks it
  `available`. At 60 s the estimator released overconfident intervals, so its validity gate is not sufficient.
- **Reference time.** Bind the solve and every cross-frame mapping to the exposure midpoint, not the `CameraFrame`
  timestamp. A start-UTC binding is accepted silently and shifts the ground pose by 0.036° at 20 s and 0.109° at
  60 s.

Neither constraint blocks #1168, which changes no product code.
[#1179](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1179) tracks requalifying long-exposure uncertainty
and every limit in this table.

## Observations

- The equidistant projector's θ = acos(Up) loses about 1.5e-8 rad on the optical axis, where atan2(hypot(x, y), z)
  would not. The product impact is at most about 1e-5 px, 75,000 times below the mapping tolerance. The product is
  unchanged and no issue is filed.

## Not claimed

- Physical accuracy, or any physical lens.
- A deeper solver catalog. #1167 owns solver catalog depth; #1168 owns render depth and exposure.
- Long exposure for the equisolid, stereographic, orthographic and rectilinear 8 mm families.
- Gains other than 150.
