# Session optical calibration — v1

Issue #1104 (V5 of #1098) adds a reusable optical calibration fitted jointly over several frames. Each frame
keeps its own pose, so pose is never folded into the optics. The result is one immutable native-sensor model,
and cropped or binned readouts are views derived from it. It builds on the
[shared astrometry library](shared-astrometry-v1.md) and the
[measured-star path](measured-star-diagnostics-v1.md).

**Not included:**
- writes to physical rigs
- per-frame lens models
- projection math inside hosts
- FITS/WCS export
- physical lens or sensor qualification, which stays with #1107/#523

## Ownership

- **`HVO.SkyMonitor.Astronomy`**
  - Owns the radial model: `RadialDistortion` and `ProjectionContext.RadialDistortionK1`.
  - Owns the fitter: `OpticalCalibrationSession.Fit`, with `OpticalCalibrationOptions` and
    `OpticalCalibrationResult` (`optical-calibration-result-v1`, fitter `session-optical-calibration-v1`).
  - Owns the readout and activation helpers: `RigProjectionContextFactory.CreateReadoutView` and
    `CreateCalibratedOptics`.
- **`HVO.SkyMonitor.AgentCore`**
  - `OpticsProfile.RadialDistortionK1` is the persisted declaration.
  - It is omitted from JSON when zero, so existing rigs, scenes and identities serialize byte-for-byte as before.
- **`HVO.SkyMonitor.CameraAgent.Common`**
  - `VirtualOpticalCalibrationReviewService` owns review, accept and reject for virtual profiles only.
  - Activation and persistence stay with the host's existing named-rig lifecycle.

## Radial model

- The distortion is radial about the principal point, applied to the family's normalized ideal radius:
  `r_d = r (1 + k1 r²)`.
  - For fisheye families, `r` is the family's ideal radial function of the off-axis angle.
  - For perspective, `r` is `tan θ`.
- **Every family supports it**: equidistant, equisolid, orthographic and perspective. The VirtualSky renderer
  projects through the same `ProjectionContext`, so emulator support follows from the rig declaration.
- **Invertibility is a validation rule, not a hope.**
  - `|k1| ≤ 0.5`.
  - The radial derivative `1 + 3 k1 r²` must stay at or above 0.5 across the whole declared aperture.
  - A circular image circle or rectangular sensor corner that reaches beyond that domain is rejected with
    `ArgumentOutOfRangeException`. It is never clamped.
  - `Undistort` inverts by bracketed Newton iteration within the supported domain. The round trip is exact to
    1e-12, and it returns NaN outside the domain.
- **Readout views.** `CreateReadoutView(native, readout)` maps the principal point, focal lengths and aperture
  through ROI and binning, and carries k1 unchanged because k1 is in normalized units. It rejects a readout whose
  native geometry differs from the model, that falls outside the sensor, that is not divisible by its binning,
  or that is empty.

## Fit

**Parameters.** Every member of `OpticalCalibrationOptions` is part of the options identity.

| Parameter | Fitted | Default bound |
| --- | --- | --- |
| Focal scale (one shared scale on fx and fy; log-parameterized) | Always | ±5% of nominal |
| Principal point x, y (native photosites) | Always | ±40 px of nominal |
| Radial k1 | Only when `FitRadialDistortion` | ±0.02 |

Family, parity, aperture, image-circle radius and orientation are never fitted. The nominal value is kept for
each.

**Stages.**
1. **Acquisition.** Each fit frame is blind-solved with the ordinary bounded acquisition at nominal optics, in
   its own readout view. A frame whose best competing candidate differs by more than 2° yet matches 80% as many
   stars is `ambiguous`. No frame may fail or be ambiguous.
2. **Joint fit.** Damped Gauss–Newton runs over the shared optics plus one rotation per frame.
   - Shared-optics derivatives are finite differences. A backward step is used where the forward step would
     leave the supported domain or the bound.
   - Associations use the solver's nearest-with-1.5×-margin, one-to-one rule. The radius shrinks from 7.5 to 1.5
     px in stages, with Huber weighting.
   - Convergence is declared only at the final radius.
3. **Evaluation.** The gates are listed below.
4. **Validation.** Each withheld frame is solved independently by the ordinary single-frame solver through the
   candidate's derived readout view. Every frame must yield a measured mapping with focal scale within 0.003 of
   1.

**Gates.** Every failure is a reason-coded rejection, and only an `Accepted` result carries `CalibratedNative`.

| Gate | Default | Rejection code |
| --- | --- | --- |
| Fit frames | 3–24 | `insufficient-frames` |
| Withheld frames | ≥ 1 | `insufficient-validation-frames` |
| Sky rotation across fit frames (circular LMST span) | ≥ 15° | `insufficient-sky-rotation` |
| Per-frame fitting stars | ≥ 12 | `insufficient-associations`, `frame-support:<capture>` |
| Radial support: bins of the native aperture radius with ≥ 6 stars | ≥ 4 of 5 | `insufficient-radial-support` |
| Azimuthal support | ≥ 6 of 8 | `insufficient-azimuth-support` |
| Verification stars (held-out catalog subset, isolated) | ≥ 12 | `insufficient-verification-stars` |
| Fitting / verification RMS | ≤ 0.4 / 0.5 readout px | `fitting-residual`, `verification-residual` |
| Parameter at its declared bound | none | `parameter-bound:<name>` |
| Correlation-matrix condition number of the marginal shared-optics covariance | ≤ 1000 | `ill-conditioned` |
| Marginal standard error: focal scale, principal point, k1 | ≤ 0.002, 1.5 px, 0.002 | `underconstrained:<name>` |
| Signed radial bias per supported radius bin | not both > 0.2 px and > 3σ | `radial-bias:<bin>` |
| Convergence | within 40 iterations | `not-converged` |
| Cooperative budget | 120 s | `time-budget` (`BudgetExceeded`) |
| Catalog completeness or coordinate model | declared | `catalog-incomplete`, `coordinate-model-unsupported` (`Unavailable`) |

**What the gates mean.**
- The marginal covariance is the Schur complement of the shared optics after every per-frame pose is
  eliminated. It is scaled by the unweighted residual variance. It is a conditional least-squares uncertainty,
  not a calibrated probability.
- The radial-bias gate is how an omitted distortion term or a wrong projection family is detected: either one
  leaves a systematic signed radial residual that pose and focal scale cannot absorb.
- A failed session proposes no optics, so the previously accepted calibration stays authoritative.

## Host review and activation (virtual profiles only)

`VirtualOpticalCalibrationReviewService` reviews an `OpticalCalibrationResult` against the active named rig.

**Accept is blocked when:**
- the rig is physical (any module other than `VirtualSky`)
- a rig change is already pending
- the result is not accepted
- the result's nominal native model differs from the active rig's
- the calibration cannot be represented as an `OpticsProfile`

**Representation.** `CreateCalibratedOptics` replaces only the principal point, focal lengths and k1, plus the
calibration version `session-optical-calibration-v1:<result identity>`. It throws unless the new profile
reproduces the calibrated native model exactly.

**Decisions.**
- **Accept.**
  - Re-reviews first, and refuses a stale review (changed rig or optics revision).
  - Writes one immutable optics revision and composes one draft rig revision.
  - Never stages or activates. Activation follows the existing preview, stage and restart path.
  - Accepting the same result again returns the existing revision.
- **Reject.** Writes nothing. Reject decisions are not durably journaled in v1.

Both keep the previously active revision and its optics unchanged.

## Evidence

**Unit**
- `RadialDistortionTests` (5) cover:
  - inversion
  - every family's round trip against the closed form
  - noninvertible and out-of-range rejection
  - zero-coefficient serialization identity
  - readout-view mapping
- `OpticalCalibrationSessionTests` (10) cover:
  - recovery within tolerance, with pose kept per frame and out of the session optics
  - ROI/bin views derived from one native calibration
  - determinism and option identity
  - omitted distortion
  - wrong family (equisolid truth against an equidistant nominal)
  - insufficient sky rotation, frames or radial support
  - ambiguity (a rotationally symmetric sky)
  - incomplete catalog and invalid inputs
  - exhausted budget
- `VirtualOpticalCalibrationReviewServiceTests` (4) cover:
  - accept, reject, retention and idempotency
  - stale reviews
  - physical-rig and foreign-nominal blocks
  - `CreateCalibratedOptics` refusals

**HYG qualification (Manual): `VirtualOpticalCalibrationQualificationTests`**
- **Rendering.** The real `VirtualSkyCameraModule` renders the ASI174 mono profile with truth optics: perturbed
  focal scale, principal point and k1, with a truth pose.
- **Fitter inputs.** The fitter sees only final pixels, measured by `linear-stellar-local-v2`, and the
  *nominal* native model.
- **Fit frames.** native at 05:00Z, ROI at 06:30, native at 08:00, bin2 at 09:00 UTC.
- **Withheld frames.** native and ROI at 07:15, ROI+bin2 at 09:30.
- **Independent scoring.** Each withheld frame is re-measured and solved through the calibrated readout view,
  then scored by `VirtualAstrometryReference`. That reference implements the equidistant projection with k1
  independently of production projectors and must give:
  - association precision of 1
  - withheld mapping RMS ≤ 0.5 px and p95 ≤ 0.75 px
  - pose error ≤ 0.06°
  - focal error ≤ 0.001
- **Omitted-distortion control.** Each partition is refit with `FitRadialDistortion: false`. It must be rejected
  when truth k1 ≠ 0 and accepted when k1 = 0.
- **Activation.** The calibrated optics must reproduce the calibration exactly and initialize a VirtualSky module.
- **Output.** `virtual-optical-calibration.json` (`virtual-optical-calibration-v1`).

**Tolerances** were predeclared on #1104 before any held-out run:

| Gate | Limit |
| --- | --- |
| Calibration | Accepted |
| Focal-scale relative error | ≤ 0.001 |
| Principal-point error | ≤ 1 native px |
| k1 error | ≤ 0.0015 |
| Native intrinsic-map error over zenith angle ≤ 85° (reference equations) | ≤ 0.5 px |
| Withheld validation verification RMS | ≤ 0.5 readout px |
| Session fit wall time | ≤ min(60 s, 1.25 × solves × single cold solve + 2 s) |
| Session fit allocation | ≤ 1.25 × solves × single cold solve + 256 MiB |
| Process peak working set | ≤ 2 GiB |

**Resource baseline.** The baseline is one ordinary cold single-frame solve of the first native fit frame at
nominal optics, measured in the same process after the fits. `solves` is the 4 fit plus 3 withheld frames. The
session's fixed cost is one blind acquisition per fit frame and one ordinary solve per withheld frame, so the
bound says the joint fit adds only bounded overhead to that unavoidable work. The harness requires
`DOTNET_TieredCompilation=0`. Run one partition at a time:

```bash
HVO_EVIDENCE_REVISION=<committed sha> DOTNET_TieredCompilation=0 HVO_ASTROMETRY_CATALOG_ROOT=<catalog> \
  dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests -c Release \
  --filter "FullyQualifiedName~VirtualOpticalCalibrationQualificationTests.HeldOutOpticalCalibration"
```

### Tuning partition (Feb 10, seed 110420; not final evidence)

**Truth:** focal ×1.010, principal point (+5, −4) px, k1 −0.007, pose 82/137/13.

**Accuracy.** Accepted.

| Error | Value | Limit |
| --- | --- | --- |
| Focal | 2.0e-5 | 0.001 |
| Principal point | 0.004 px | 1 px |
| k1 | 1.0e-5 | 0.0015 |
| Intrinsic map | 0.008 px | 0.5 px |

**Withheld frames.** Independent RMS was 0.012 px (native), 0.010 px (ROI) and 0.005 px (ROI+bin2).

**Omitted-distortion control.** Rejected with `fitting-residual`, `verification-residual` and `radial-bias` in
all five radius bins.

**Cost.**

| Run | Wall time | Allocated | Peak working set |
| --- | --- | --- | --- |
| Cold session | 34.1 s (acquisition 19.0 s, joint fit 0.24 s, validation 14.9 s) | 17.6 GiB | 456 MiB |
| Single cold solve (baseline) | 5.8 s | 3.4 GiB | — |

- The joint fit is under 1% of the session.
- The session costs less than its seven constituent solves.
- The solver's 3.4 GiB per cold solve predates this change. It is recorded for #1106's resource gate.

No setting was changed after tuning.

### Held-out partition

Recorded after the held-out run on the committed revision.
