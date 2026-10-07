# Virtual all-sky astrometry qualification — projection families (v2)

> **Long exposures.** This envelope was measured on 1 s frames. Issue
> [#1168](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1168) states which longer exposures are supported, for
> equidistant and rectilinear 6 mm, in [virtual-astrometry-long-exposure-v1](virtual-astrometry-long-exposure-v1.md).

Issue [#1126](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1126), under epic
[#520](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/520), extends the #1106 virtual envelope
([v1](virtual-astrometry-qualification-v1.md)) from equidistant fisheye to the other projection families that the
shared radial model supports. The #1102, #1103, #1104 and #1105 held-out harnesses run unchanged over each family,
using the [#1126 manifest](../validation/issue-1126-qualification-manifest.json) and runner. This report is the
single place that states which projection families the virtual milestone supports. v1 remains the record of the
#1106 measurement.

**Status: measured at `276f9307`.** The family/profile matrix, partitions, tolerances, budgets and fallback
below were declared before any tuning or held-out run and are unchanged. The defect sections, results,
dispositions and the [supported envelope](#supported-envelope) were added after measurement.

This is a virtual qualification only. It makes no claim about physical accuracy, and no profile here is a
physical lens certification.

## Frozen family/profile matrix

Every family uses the virtual ASI174 mono sensor: 1936×1216, 5.86 µm pixels, Mono16. Each runs the same six
readouts as the v1 mono rig:
- native
- ROI (240, 96, 1440, 1024)
- 2×2 digital average
- ROI+2×2
- mirror (horizontal flip inverted)
- roll (orientation 82°, 137°, −57°)

The other readouts use orientation 82°, 137°, 13°. Native focal lengths are in native-sensor pixels. Every
family is declared in `VirtualAstrometryFixture.Families` and selected per process by `HVO_PROJECTION_FAMILY`.

| Family | Projection | Aperture | Field | Native focal (px) | Nominal start | Calibration version | Lens parameters |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `equidistant` | Equidistant fisheye | Circle 595.84 px | 180° | 379.32 | truth ÷ 1.037 | `virtual-fisheye-180-equidistant-v1` | v1 regression baseline, unchanged. Also runs `cfa-native` (ASI178MC, Fujinon FE185C057HA-1 candidate). |
| `equisolid` | Equisolid fisheye | Circle 595.84 px | 180° | 421.32 | truth ÷ 1.037 | `virtual-asi174-equisolid-180-substitution-v1` | **Synthetic substitution.** Ideal 180° equisolid lens filling the v1 image circle. Not a named commercial lens. |
| `stereographic` | Stereographic fisheye | Circle 595.84 px | 180° | 297.92 | truth ÷ 1.037 | `virtual-asi174-stereographic-180-substitution-v1` | **Synthetic substitution.** Ideal 180° stereographic lens filling the v1 image circle. Not a named commercial lens. |
| `orthographic` | Orthographic fisheye | Circle 595.84 px | 160° | 605.03 | truth × 1.037 | `virtual-asi174-orthographic-160-substitution-v1` | **Synthetic substitution.** Ideal 160° orthographic lens filling the v1 image circle. Not a named commercial lens. |
| `rectilinear` | Perspective | Sensor rectangle | 86.8° × 61.4° (96.3° diagonal) | 1023.89 | truth ÷ 1.037 | `virtual-asi174-rectilinear-6mm-substitution-v1` | **Synthetic substitution at a published focal length:** ideal pinhole at the Kowa LM6HC 6 mm focal length (provenance below). |
| `rectilinear-8mm` | Perspective | Sensor rectangle | 70.7° × 48.0° (79.9° diagonal) | 1365.19 | truth ÷ 1.037 | `virtual-asi174-rectilinear-8mm-substitution-v1` | **Declared fallback only.** Synthetic ideal pinhole at 8 mm. Not a named commercial lens. |

Notes on the matrix:
- **Truth optics.** Every family starts from the v1 `virtual-asi174.full.json` optics: k1 = 0, principal point
  (968, 608) and horizontal flip. Each fisheye family keeps the 595.84 px image circle. Only the projection,
  field, focal length and calibration version change, so the families differ from the baseline in projection
  alone.
- **Existing 50 mm sample rig.** `virtual-asi174-rectilinear.full.json` (50 mm, 12.9° field) is not used. A
  magnitude-5 selection puts too few stars in that field to solve blind, so it would test catalog depth rather
  than the projection.
- **Rectilinear focal length.** The rectilinear profiles set `FocalLengthMillimeters`, so the native focal is
  focal length ÷ pixel pitch. They declare `LensKind.Rectilinear` and no image circle. The aperture is the sensor
  rectangle.
- **Nominal start (blind and warm harness).** v1 starts calibration from a focal 1.037 below truth. The new
  families keep that offset except orthographic. An orthographic projection cannot reach a 595.84 px circle from
  a focal below the circle radius, and 605.03 ÷ 1.037 = 583.44 px is below it, so the nominal would be an invalid
  optic. Orthographic therefore starts 1.037 *above* truth (627.42 px), the same relative offset in the valid
  direction.
- **CFA.** CFA runs only for the equidistant baseline. CFA combined with any new family is untested and outside
  the envelope.
- **Uncertainty floors.** The #1105 systematic floors are keyed by readout (0.042 px mono unbinned, 0.028 px mono
  2×2). They model centroid sampling error, not projection, and are reused unchanged for every family.

### Kowa LM6HC provenance (rectilinear)

- **Lens.** Kowa LM6HC: 6 mm focal length, 1″ maximum sensor format (16 mm diagonal image circle), C-mount.
- **Sources** (both retrieved 2026-10-06):
  - STEMMER IMAGING product page: <https://www.stemmer-imaging.com/en/kowa-lm6hc>, SKU 18077. It gives focal
    length 6 mm, maximum sensor format 1″ and sensor diagonal 16 mm.
  - 1stVision specification page: <https://1stvision.com/lens/spec/Kowa/LM6HC>. It gives the same focal length
    and format, and a distortion of −0.2 %.
- **Unusable source.** The Baumer datasheet URL `https://media.baumer.com/Baumer_ZVL-LM6HC_EN_20260331_DS.pdf`
  returned an HTML page rather than a PDF, so it is not used.
- **Coverage.** The IMX174 diagonal is 13.40 mm, inside the 16 mm image circle, so the lens covers the full
  sensor and the aperture is the sensor rectangle.
- **What is modelled.** The profile uses the published focal length only, as an ideal pinhole. The published
  −0.2 % distortion and any vignetting are not modelled. The profile is a synthetic substitution at a published
  parameter, not a model of the physical lens.

## Frozen partitions, tolerances and budgets

- **Partitions, seeds and tolerances.** Sky times, seeds, readouts and tolerances are those compiled into the
  #1102, #1103, #1104 and #1105 harnesses. This issue does not change them:
  - tuning: 10 February, seeds 110220 (pixels and measured stars), 110420 (calibration) and 110520 (uncertainty)
  - held-out: 15 January, 15 May and 15 September (seeds 1102xx, 1104xx, 1105xx)
- **Budgets.** Resource budgets are the v1 budgets. For a non-circular aperture, the "interior" used by the
  scorers is the full sensor rectangle.
- **Harness changes are structural only.** The harnesses learn the family and aperture. They change no tolerance
  or budget, and their equidistant arithmetic is bit-identical:
  - The truth reference implements every family's ideal radius and its inverse, including k1. Unit tests pin it
    to the production projectors on every frozen profile (`VirtualProjectionFamilyReferenceTests`).
  - Scorers judge interiors by the profile's aperture.
  - Rectangular apertures replace the circle-edge "unsupported point" requirement with four outside-frame probes,
    which both the truth reference and `PixelToSky` must reject.
- **Solver.** The solver and its options are unchanged at the frozen revision. An algorithmic defect found on
  tuning may be fixed only within the limits the coordinator set, and only after messaging the coordinator:
  - the fix must be bounded
  - no tolerance changes and no added model terms
  - equidistant results stay exact, or any change is explained numerically
- **Declared fallback.** If the 6 mm rectilinear profile fails any tuning harness, a commit made before any
  held-out run replaces `rectilinear` with `rectilinear-8mm` in the final family list and records the failure. A
  held-out failure never triggers the fallback.
- **Held-out failures.** A family that fails held-out becomes a named software gap with an *unsupported*
  disposition. It is not retried or tuned against.

## Solver defect found on tuning

Tuning found one algorithmic solver defect, in the perspective catalog index. It was fixed under the
coordinator's limits, and the solver version is now `spherical-triangle-astrometry-v2`.

- **Defect.** Solver v1 indexed every above-horizon training star for a perspective (narrow) field. That suits
  a telescope field, but a wide rectilinear lens has hundreds of training stars, and the index exceeds its
  2,000,000-triangle bound. At `f0634c45` the 6 mm and 8 mm tuning cells rejected all 18/18 blind solves
  fail-closed before forming any hypothesis: "Catalog triangle index exceeds its 2 million-entry bound".
- **First attempt, superseded (`a475ee47`).** This attempt indexed the brightest ⌈2 · DetectionTriangleStars ·
  2π / ω⌉ training stars, where ω is the pinhole solid angle of the field. It cleared 6 mm full frame, but the
  count it requests grows as the field narrows. The 6 mm 1440×1024 ROI and every 8 mm readout therefore still
  exceeded the bound.
- **Fix (`4d3a6ee6`).**
  - Order the training stars by magnitude, which is the fisheye branch's ordering.
  - Count the index triangles each star closes with the brighter stars already admitted, using the index build's
    unchanged acceptance rule (`IsIndexTriangle`).
  - If the complete training set fits the bound, index it in training order, bit-identical to solver v1.
  - Otherwise, index the largest magnitude-ordered prefix that fits, so the count stops at the bound.

  The fix changes no bound, tolerance, model term or projection API.
- **Bit-identity.**
  - Fisheye selection is unchanged.
  - Every fisheye tuning report at `3e6e4f44` matches `f0634c45` in every measured, fitted, residual and coverage
    value. Only the revision, solver version, identity hashes and resource/timing fields differ.
  - `WideFieldIndexTests` pins a field that fits the bound to solver v1's index, triangle and hypothesis counts and
    to the IEEE bits of its solution.
- **Result.** At `3e6e4f44` every rectilinear blind solve is accepted, 12 of 12:

  | Profile | Index stars | Index triangles | Cold solve |
  | --- | --- | --- | --- |
  | 6 mm | 290–324 | 1.986–1.997 M | 1.6–2.2 s |
  | 8 mm | 330–389 | 1.980–1.998 M | 1.6–2.1 s |
- **Defect record.** The pre-fix and superseded-fix tuning packs are kept as the defect record:
  - `tuning-f0634c45-rectilinear-2`
  - `tuning-f0634c45-rectilinear-8mm`
  - `tuning-a475ee47-rectilinear`
  - `tuning-a475ee47-rectilinear-8mm`

  They are listed with hashes in the evidence index.

## Harness defect found on tuning

Tuning also found one defect in this issue's own truth reference. It was corrected by test-only commit
`276f9307` before any held-out run, with coordinator approval.

- **How it was found.** At `3e6e4f44` the rectilinear optical-calibration tuning cell failed. Every fitted value
  was accurate: focal 3.5 × 10⁻⁶ relative, principal point 0.026 px, k1 8.95 × 10⁻⁶, withheld RMS
  0.006–0.010 px. The scorer still reported:
  - 6 mm: a 13.85 px mapping error, and 5, 3 and 4 "missing" withheld stars (native, ROI, ROI+2×2)
  - 8 mm: 3, 1 and 4 missing withheld stars
- **Cause.**
  - The tuning truth has k1 = −0.007. For a perspective lens, g = tan θ, and the one-term distortion
    d = g(1 + k1 g²) folds back toward the axis past θ = 81.75°.
  - Production documents its supported domain as a radial derivative 1 + 3 k1 g² ≥ ½ (`RadialDistortion`), and
    neither renders nor maps rays beyond it.
  - The family-general truth reference had no such limit, so stars 85–90° off-axis folded back into the frame in
    the reference only.
  - Those are exactly the "missing" withheld stars. The mapping-error sample at θ = 85° accounts for the whole
    error: f g³ Δk1 = 1034 × 1493 × 8.95 × 10⁻⁶ ≈ 13.8 px.
- **Correction.**
  - The reference states the domain independently (`IsSupportedIdealRadius`), and `Project` returns nothing
    outside it.
  - The mapping-error sweep skips a sample only when the truth's own domain excludes it. Inside the truth domain,
    every sample is still compared.
  - No production, tolerance, bound, model or partition change.
- **Check.** A labelled diagnostic of the same patch on `3e6e4f44` showed that the 6 mm mapping error falls from
  13.85 px to 0.0386 px, and no withheld star is missing in either profile:

  | Profile | Withheld stars before (native, ROI, ROI+2×2) | After |
  | --- | --- | --- |
  | 6 mm | 15, 11, 22 | 10, 8, 18 |
  | 8 mm | 9, 5, 12 | 6, 4, 8 |

  The removed rows are exactly the previously missing ones, and the residuals of every remaining row are
  unchanged to the bit. The diagnostic pack (`diag-domain-3e6e4f44`) is kept in the evidence index, labelled as
  not evidence.
- **Envelope limit.** Rectilinear support ends at the documented distortion domain:
  - tuning k1 −0.007: θ ≤ 78.42°
  - held-out k1 −0.008: θ ≤ 77.64°
  - k1 ≥ 0: no limit short of 90°

  Inside each frozen frame the excluded rays exist only on the folded, non-physical branch, so no in-frame
  physical coverage is lost. Fisheye families never reach the domain edge for any frozen k1.
- **Rectilinear mapping-score domain.** The calibration reports carry `mappingDomainZenithDegrees` = 85. For a
  perspective profile with negative k1, the mapping score actually covers only the k1 distortion domain:
  - tuning (k1 −0.007): θ ≤ 78.42°
  - held-out (k1 −0.008): θ ≤ 77.64°

  The rectilinear mapping claim does not extend to 85°. The fisheye scores are not limited by the distortion
  domain. They cover the 85° sweep wherever the truth pixel lies inside the image circle, which for orthographic's
  160° field ends at 80°.

## Pre-existing finding against the v1 equidistant envelope

#1106 never ran the tuning partition (10 February, seed 110220); it measured held-out only. #1126 runs tuning for
the first time, and one equidistant pixels case fails there identically at the base revision `b13f0d0e` and on
this branch:

- **Case.** `02-mono-roi-bin2-0`, the blind solve of the mono ROI+2×2 readout (418 detections).
- **Outcome.** Rejected fail-closed as `ambiguous`, "multiple independently verified orientations", after
  2,384 ms. No wrong pose is accepted. The next cold solve (`roi-bin2-1`) and the warm solve after it
  (`roi-bin2-2`) are accepted.
- **Tuning rate.** 1 of 30 blind tuning cases. The rejected source has no cross-frame grid, so the grid count is
  725 of 750, and the frozen harness cell fails on that count.
- **Identical at base.** The case sequence and every detection count match `b13f0d0e` exactly. This is not a
  #1126 regression, and equidistant solver behaviour is unchanged by this issue.
- **Why it surfaced now.** Before this issue's reporting-only guard, the pixel harness's incompatible-prior probe
  ran with a null prior after a rejected blind solve. Its assertion stopped the run and masked the failure list.
  Evidence: the pristine base run, and the base run with the guard applied as an uncommitted diagnostic. Both are
  listed with hashes in the evidence index.

The v1 held-out claim (30/30 blind, 90/90 rows) stands as measured. The supported envelope now also states:

- equidistant blind acquisition can fail closed with `ambiguous`
- the observed tuning rate for that is 1/30 blind cases, on mono ROI+2×2
- warm continuity and the next cold solve recover from it

The tuning cell stays failed as frozen; its assertion is not changed.

## Tuning results

Tuning ran at `276f9307` from a clean tree on hvo-dev-02: pack `tuning-276f9307-all` (all families) and pack
`tuning-276f9307-rectilinear-8mm` (the declared fallback, measured for the record). The 6 mm rectilinear profile
passed every tuning cell, so the fallback was not needed and `rectilinear` (6 mm) stayed in the final family list.

| Family | Pixels | Measured stars | Calibration | Uncertainty |
| --- | --- | --- | --- | --- |
| equidistant | fail, [pre-existing](#pre-existing-finding-against-the-v1-equidistant-envelope) | pass | pass | pass |
| equisolid | pass | pass | pass | pass |
| stereographic | pass | pass | pass | pass |
| orthographic | pass | pass | pass | fail, [coverage](#orthographic-uncertainty-tuning-miss) |
| rectilinear 6 mm | pass | pass | pass | pass |
| rectilinear 8 mm (fallback, not final) | pass | pass | pass | pass |

### Orthographic uncertainty tuning miss

The orthographic uncertainty tuning cell failed two coverage checks, both on mono 2×2 at the 0.99 level: the
ellipsoid coverage and the parameter-2 interval coverage were each 0.857, one frame of seven outside. The shortfall,
0.133, exceeds the 0.132 bound for n = 7. One of the profile's eight frames was withheld, which left n = 7.

Nothing was tuned in response. Held-out ran as frozen, and the held-out orthographic uncertainty cell passed
(below). The tuning miss is recorded here and in the envelope; the held-out pass does not offset it.

## Held-out results

Held-out ran once at `276f9307`, from a clean tree on hvo-dev-02, with no family list:
`issue-1126-qualification.sh final`, pack `final-276f9307`, from 2026-10-06T23:28:06Z to 2026-10-07T00:15:33Z,
on an Intel Core Ultra 9 285H with 8 logical processors. Nothing was re-run and no tolerance, partition or model
changed afterwards. The pack index records `status: failed`, and it stays failed.

hvo-dev-02 dropped off the network for about a minute at 2026-10-07T00:18Z, after the pack finished. The host did
not reboot and the pack hashes are unchanged. The pack is held in two places with the same per-pack manifest,
`final-276f9307.sha256.txt` (sha256 `e910f90357dc175b08801b6e3499b53d92566781dc16e9decfc6531e1bebb37f`, index
`513d4200b9425cf040eed6fd888abdd39f4722ae30f20b8b4f94518b20118563`):

- primary: `hvo-dev-02:~/development-state/HVO.SkyMonitor/1126/final-276f9307`
- copy: `home-dev-02:~/development-state/HVO.SkyMonitor/evidence/1126/mirror-hvo-dev-02/final-276f9307`

The pack was produced at `276f9307`. Its metrics were extracted with `b29583f2`, which changes only
`docs/validation/issue-1126-metrics.sh` so that the extractor passes undefined (`NaN`) values through. No product
source changed after `276f9307`.

| Family | Pixels | Resources | Measured stars | Calibration | Uncertainty |
| --- | --- | --- | --- | --- | --- |
| equidistant | pass | pass | **fail: time ratio** | pass | pass |
| equisolid | pass | pass | **fail: time ratio** | pass | pass |
| stereographic | pass | pass | **fail: ambiguous solve** | pass | pass |
| orthographic | pass | pass | pass | pass | pass |
| rectilinear 6 mm | pass | pass | pass | pass | pass |

Headline values (maxima over the held-out cases):

| Family | Blind accepted | Blind pose error | Withheld p95 blind / warm | Calibration mapping error | Uncertainty frames withheld |
| --- | --- | --- | --- | --- | --- |
| equidistant | 30/30 | 0.0103° | 0.037 / 0.056 px | 0.014 px | 0 |
| equisolid | 18/18 | 0.0048° | 0.032 / 0.047 px | 0.021 px | 1 (0.28 %) |
| stereographic | 18/18 | 0.0082° | 0.047 / 0.049 px | 0.019 px | 0 |
| orthographic | 18/18 | 0.0086° | 0.050 / 0.047 px | 0.016 px | 1 (0.28 %) |
| rectilinear 6 mm | 18/18 | 0.0059° | 0.084 / 0.052 px | 0.061 px | 0 |

The rectilinear calibration mapping errors are 0.038, 0.061 and 0.036 px (cases 01, 05, 09), scored over the
held-out distortion domain θ ≤ 77.64° (see [Harness defect found on tuning](#harness-defect-found-on-tuning)).

### Equidistant regression against #1106

Every equidistant held-out report was compared field by field with the #1106 held-out metrics
(`issue-1106-qualification-evidence.json`, revision `9061c8f7`, solver v1). No measured, fitted, residual or
coverage value differs. The differences are only:
- resource and timing fields
- identity, revision and family fields, including the calibration `fitIdentitySha256` of all three cases. The
  fit identity includes the solver identity, and this issue bumps `AstrometricConventions.SolverVersion` to
  `spherical-triangle-astrometry-v2`; the truth, errors and diagnostics of every fit are identical.
- values derived from resources: calibration wall and allocation headroom and limits, the uncertainty
  estimator-to-frame time ratio, and the measured-stars failure count (the time-ratio miss below)

Only equidistant has a solver-v1 held-out baseline. Equisolid, stereographic and orthographic are new coverage
with no earlier measurement to compare against. Their claim to the v1-identical solve path rests on the index
selection: for every non-perspective model, v1 and v2 both index the brightest 120 training stars, and the reports
record at most 120 index stars and 260,572 triangles, well under the 2,000,000-triangle bound. The largest-prefix
and full-training-set selection applies only to perspective (rectilinear) fields.

### Measured-stars dispositions

- **Time ratio, equidistant and equisolid: recorded resource miss.** The v2/v1 measurement time exceeded the
  frozen ratio of 4 on three ROI+2×2 cases:

  | Family | Case | v1 | v2 | Ratio |
  | --- | --- | --- | --- | --- |
  | equidistant | `01-mono-roi-bin2` | 11.4 ms | 45.9 ms | 4.03 |
  | equisolid | `01-mono-roi-bin2` | 9.7 ms | 45.1 ms | 4.64 |
  | equisolid | `09-mono-roi-bin2` | 9.1 ms | 38.6 ms | 4.25 |

  Every accuracy gate passed in both families: no false associations, recall and centroid RMS inside their
  regression limits, and every v2 solve accepted. The cells are recorded as failed, the measurement was not
  repeated, and the measured-stars time-ratio claim (`maximumMeasurementTimeRatio` 4) is not met for ROI+2×2 in
  these two families.

  Context, analysis only:
  - The timed region is the v2 measurer alone, and this issue does not change it. The product source diff is the
    solver core and contracts only.
  - On these frames the v1 time is about 9–11 ms, so a few milliseconds move the ratio across the limit.
  - The #1106 figures ran on a different host. One control run of the equidistant measured-stars held-out cell
    at the base revision `b13f0d0e`, on the same host as this issue's pack, separates host from branch. The
    control ran only the #1106 manifest entry `measured-stars-held-out`, with the project, filter and
    environment (`DOTNET_TieredCompilation=0`, `HVO_EVIDENCE_REVISION`) that `issue-1106-qualification.sh`
    uses for it. The script itself was not used because it always runs the whole manifest and its build starts
    shared build servers. The control was built with `MSBUILDDISABLENODEREUSE=1`,
    `-p:UseSharedCompilation=false` and `-p:UseRazorBuildServer=false` under the host's heavy-job lock, from a
    clean tree. This PR leaves the script unchanged. Pack `control-b13f0d0e-measured-stars-held-out-equidistant`
    ran from 2026-10-07T00:25:43Z to 00:27:35Z. It is a control for this attribution only and changes no status
    or disposition. v1 / v2 time and ratio for the ROI+2×2 cases:

    | Case | #1106 (`9061c8f7`, i9-14900K, 12 logical) | Control (`b13f0d0e`, Core Ultra 9 285H, 8 logical) | #1126 equidistant (`276f9307`, Core Ultra 9 285H, 8 logical) |
    | --- | --- | --- | --- |
    | `01-mono-roi-bin2` | 11.5 / 33.9 ms, 2.94 | 10.5 / 35.5 ms, 3.38 | 11.4 / 45.9 ms, 4.03 |
    | `05-mono-roi-bin2` | 9.7 / 34.3 ms, 3.53 | 10.7 / 34.7 ms, 3.26 | 11.9 / 36.4 ms, 3.06 |
    | `09-mono-roi-bin2` | 11.6 / 33.9 ms, 2.92 | 11.3 / 46.1 ms, 4.09 | 9.8 / 34.5 ms, 3.53 |

  - **Attribution.** The same-host base run (`b13f0d0e`) also exceeds 4× on an ROI+2×2 case
    (`09-mono-roi-bin2`, 4.09), so its own cell fails the frozen gate. The over-limit case varies between runs on
    a ~10 ms v1 baseline. The ratio miss is attributed to host and timing variance, not to the #1126 change. The
    cells stay failed, nothing was re-run, and the control does not turn them into passes.
  - **Observation.** The #1106 ratio gate of 4× does not hold at the base revision on the Core Ultra 9 285H. The
    measured-stars qualification is `TestCategory("Manual")`, so this gate is not a CI gate. No issue is filed for
    it here; it is carried to the software envelope of epic #520 at close-out.
- **Ambiguous solve, stereographic: named unsupported gap.** On `05-mono-roi-bin2` the v2 measurer recovered all
  164 eligible stars, but the v2 solve was rejected fail-closed as `ambiguous` ("multiple independently verified
  orientations"). The v1 detector path on the same frame was accepted. This is not caused by the solver change:
  a fisheye field takes the unchanged brightest-120 index selection in both solver versions (the held-out index
  peaks at 120 stars and 260,572 triangles), and the changed prefix selection applies only to perspective fields.
  It is the same failure class as the
  [equidistant ROI+2×2 tuning finding](#pre-existing-finding-against-the-v1-equidistant-envelope). Named gap:
  **stereographic ROI+2×2 measured-star blind solve can fail closed as ambiguous** (1 of 18 held-out cases). No
  model term or tolerance was changed.

### Measured-stars time-ratio record

Every measured-stars run of this issue that produced a report is listed here, without selection. Each row is one
run of the whole cell; the range is the v2/v1 measurement time over its cases, and the limit is 4. Two packs
produced no measured-stars report and are not rows: `tuning-a813c6ec-rectilinear` (the build failed on an analyzer
error) and `tuning-f0634c45-rectilinear` (the catalog root was not set up as a symbolic link).

| Revision | Partition | Family | Cases | v2/v1 range | Failures | Outcome |
| --- | --- | --- | --- | --- | --- | --- |
| `f0634c45` | tuning | equidistant | 7 | 1.47–3.07 | 0 | pass |
| `f0634c45` | tuning | equisolid | 6 | 2.42–3.00 | 0 | pass |
| `f0634c45` | tuning | stereographic | 6 | 2.51–3.56 | 0 | pass |
| `f0634c45` | tuning | orthographic | 6 | 2.09–2.89 | 0 | pass |
| `f0634c45` | tuning | rectilinear 6 mm | 6 | 2.31–3.19 | 6 | solver index bound (defect record) |
| `f0634c45` | tuning | rectilinear 8 mm | 6 | 2.34–2.86 | 6 | solver index bound (defect record) |
| `a475ee47` | tuning | rectilinear 6 mm | 6 | 2.28–4.23 | 3 | one ratio miss (`02-mono-native`, 477 vs 113 ms); two solver index bound |
| `a475ee47` | tuning | rectilinear 8 mm | 6 | 2.35–2.70 | 6 | solver index bound (defect record) |
| `3e6e4f44` | tuning | equidistant | 7 | 1.45–3.71 | 0 | pass |
| `3e6e4f44` | tuning | equisolid | 6 | 2.39–3.69 | 0 | pass |
| `3e6e4f44` | tuning | stereographic | 6 | 2.51–2.84 | 0 | pass |
| `3e6e4f44` | tuning | orthographic | 6 | 2.29–3.12 | 0 | pass |
| `3e6e4f44` | tuning | rectilinear 6 mm | 6 | 2.27–2.88 | 0 | pass |
| `3e6e4f44` | tuning | rectilinear 8 mm | 6 | 2.34–2.76 | 0 | pass |
| `276f9307` | tuning | equidistant | 7 | 1.44–3.53 | 0 | pass |
| `276f9307` | tuning | equisolid | 6 | 2.12–3.27 | 0 | pass |
| `276f9307` | tuning | stereographic | 6 | 2.35–3.05 | 0 | pass |
| `276f9307` | tuning | orthographic | 6 | 2.29–3.08 | 0 | pass |
| `276f9307` | tuning | rectilinear 6 mm | 6 | 2.31–2.75 | 0 | pass |
| `276f9307` | tuning | rectilinear 8 mm | 6 | 2.36–2.75 | 0 | pass |
| `276f9307` | held-out | equidistant | 21 | 1.20–4.03 | 1 | ratio miss |
| `276f9307` | held-out | equisolid | 18 | 1.75–4.64 | 2 | ratio miss |
| `276f9307` | held-out | stereographic | 18 | 1.74–3.75 | 1 | ambiguous solve |
| `276f9307` | held-out | orthographic | 18 | 2.17–3.42 | 0 | pass |
| `276f9307` | held-out | rectilinear 6 mm | 18 | 2.34–2.84 | 0 | pass |
| `b13f0d0e` (base, control) | held-out | equidistant | 21 | 1.46–4.09 | 1 | ratio miss; control for attribution only, not evidence |

## Supported envelope

This table is the single statement of which projection families the virtual milestone supports. Every row is
virtual-only and makes no physical-lens claim.

| Family | Profile | Supported | Not supported or limited |
| --- | --- | --- | --- |
| equidistant | v1 baseline, mono and CFA | Blind and warm acquisition, configured-resolution resources, measured-stars accuracy, optical calibration and uncertainty, as #1106 | Blind acquisition can fail closed as `ambiguous` on mono ROI+2×2 (tuning rate 1/30; recovered by the next cold or warm solve). Measured-stars time ratio not met on held-out ROI+2×2: 4.03 at `276f9307` (285H) against 2.94 at #1106 (i9-14900K); the same-host base run also exceeds 4× (4.09), so the miss is attributed to host and timing variance. |
| equisolid | Synthetic 180° substitution | Blind and warm acquisition, resources, measured-stars accuracy, optical calibration, uncertainty | Measured-stars time ratio not met on held-out ROI+2×2 (4.64, 4.25); attributed to host and timing variance as for equidistant. |
| stereographic | Synthetic 180° substitution | Blind and warm acquisition, resources, optical calibration, uncertainty; measured stars on native, ROI, 2×2, mirror and roll | Measured-star blind solve on ROI+2×2 can fail closed as `ambiguous` (held-out 1/18). |
| orthographic | Synthetic 160° substitution | Blind and warm acquisition, resources, measured stars, optical calibration; uncertainty passed held-out | Uncertainty coverage missed on tuning mono 2×2 (0.857 at 0.99, n = 7, bound 0.132). Mapping scored to θ ≤ 80°, the image-circle edge. |
| rectilinear | Synthetic pinhole at the Kowa LM6HC 6 mm focal length | Blind and warm acquisition, resources, measured stars, optical calibration, uncertainty | Supported only inside the distortion domain: θ ≤ 77.64° at the held-out k1 −0.008, θ ≤ 78.42° at the tuning k1 −0.007; the reports' 85° mapping domain does not apply. Requires solver v2 (`spherical-triangle-astrometry-v2`). |
| any new family | CFA | — | Untested; outside the envelope. |
| rectilinear 8 mm | Declared fallback | — | Measured on tuning only, for the record; not part of the envelope. |
