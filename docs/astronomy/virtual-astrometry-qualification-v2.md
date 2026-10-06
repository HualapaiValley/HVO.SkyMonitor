# Virtual all-sky astrometry qualification — projection families (v2)

Issue [#1126](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1126), under epic
[#520](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/520), extends the #1106 virtual envelope
([v1](virtual-astrometry-qualification-v1.md)) from equidistant fisheye to the other projection families that the
shared radial model supports. The #1102, #1103, #1104 and #1105 held-out harnesses run unchanged over each family,
using the [#1126 manifest](../validation/issue-1126-qualification-manifest.json) and runner. This report is the
single place that states which projection families the virtual milestone supports. v1 remains the record of the
#1106 measurement.

**Status: matrix frozen, measurement pending.** This revision declares the family/profile matrix, partitions,
tolerances, budgets and fallback before any tuning or held-out run. Results, dispositions and the envelope table
are added once the held-out pack exists. Nothing below is changed after measurement except to add those results.

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
