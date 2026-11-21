# Centered registered stack

`centered-registered-stack` is an opt-in Processing window recipe, separate
from `rolling-mean` and historical replay. Its caller supplies exactly five
ordered slots (`−2` through `+2`), including reasons for holes and the exact
reference capture and qualified linear artifact at offset zero. The recipe
does not choose a cadence, extend a deadline, find a current calibration, or
replace the reference. Registration does not publish a live view.

## Closure and readback

At close, construct artifact pins with `CenteredStackArtifactPin.FromHeader`
from committed descriptor and journal fields plus the committed payload checksum.
Use `CenteredStackRetainedInputReader.BindCommittedMeasurement` on the exact
cutoff-visible step-three settlement and the pinned rig geometry. Pass the
result and the artifact pins to `CenteredStackExposurePin.FromCommitted`.
This pins the measurement reason, delivered linear layout and complete artifact
header without opening pixel files. A plan missing an envelope digest or its
linear layout requires reconstruction from verified committed metadata.

For exposures with a complete valid lattice, `CenteredStackValidityMask.PinAllValid`
streams the checksum without reading or allocating a pixel plane during closure.
Use `OpenAllValid` against the frozen pin after acquiring the raw and linear
custody. A nonuniform mask must have its own committed payload and checksum.
The same canonical artifact headers must be used at close and reopen.

Reopen the evidence by its committed identity and plan digest. Its reader binds
the base variant, channel order and dimensions to the frozen publication.
The reader also requires every contributor's retained map and verification
report to agree with its registration method. `VerifyPlanes` checks the
recipe, exact layout, payload and lineage of each plane. It counts each
slot's support bits, including pixels with just one bit set.

New execution uses `centered-direct-registration-v2` with
`direct-centroid-similarity-v4` and the reference-aperture footprint policy.
The producer version and normalized values change the execution and output
identities for the same frozen plan. The outer evidence schema and input
validity-mask identity stay the same. The reader reopens valid v1 products by
their original identities and validates the persisted planes. The writer
requires the current recipe identity bound to the request's selector and
plan. Re-executing a plan with the current producer yields v2 identities.

## Alignment and pixels

- Coordinates are delivered-image pixel edges, with the center of pixel
  `(i,j)` at `(i+0.5,j+0.5)`. Use each member's own observation midpoint for
  sky mapping. The center exposure is sampled at identity. For a neighbor,
  evaluate the inverse direct map on each reference pixel center, then use
  only positive-weight bilinear taps. An out-of-range or invalid tap excludes
  that source at that point. The stencil does not extrapolate. All maps are
  restricted to the reference's calibrated geometric footprint, regardless
  of whether the center sample is clipped or masked at an interior pixel.
- With a compatible, verified retained plate solution on each endpoint,
  map through their common sky coordinates and check the pair against
  retained centroids. With an absent or valid rejected solution, a direct
  orientation-preserving similarity fit may be used instead. The fit must
  pass distinct source and reference holdouts, field coverage, residual and
  ambiguity checks. A similarity is not an absolute sky solution. No map is
  borrowed from an adjacent capture. Invalid retained evidence fails closed.
- Only matching, supported Mono16 or Bayer RGGB16 readouts with known stored-code
  mapping and black/white levels are eligible. Geometry, linear response,
  recipe and actual integration, gain and offset must agree within a window.
  The recipe excludes sensor-white and corrected-white values independently.
  Normalization-only and reference-calibrated products retain different
  provenance; they are not mixed. A Bayer sample is reconstructed on its
  declared local CFA phase, at full delivered resolution, before resampling.
  Its 1-pixel border and invalid 3×3 neighborhoods do not contribute.

The relative-map ambiguity check searches every eligible centroid in both
fields after the disjoint training and holdout checks. An alternate must have
at least eight matches, the combined minimum for the fit and holdout sets, and
evidence inconsistent with the selected map. A sufficiently spread subset
that meets the same residual bounds can veto even when a looser search match
falls outside those bounds. The ambiguity pass can only reject
the selected map. A field over 192 eligible centroids on either side, or a
search that uses its 250 million spatial probe budget, yields
`AmbiguityCheckLimitExceeded`. The recipe records a registration rejection
for that neighbor. The verified plate-pair path does not use this relative-fit
limit.

## Result and limits

The result is an arithmetic mean in the qualified input's **stored linear
code units**, with unit contribution weights, one final rounding, no white
balance/stretch, and no pixel-area correction. It preserves neither an
absolute photometric calibration nor guaranteed aperture-integrated flux
under arbitrary projection and clipping. Alignment cannot undo motion blur
inside an exposure.

Reopen the evidence together with its complete set of `mono` or `red`,
`green`, `blue` UInt16 planes and the `support` UInt8 plane. Support bit `i`
identifies slot `i` in plan order. A display-valid pixel has at least two bits
set. Its effective integration is the sum of those slots' actual exposures,
not the window span. Whole-product integration and time range describe all
actual contributors; the reference midpoint describes output geometry. A
qualified center without a publishable stack remains a single exposure. With
no usable center, there is no replacement stack or drawable neighbor.

Inputs are bounded to 6.5 million delivered pixels per exposure, 40 artifacts
and 224 MiB of combined payload for one call. The recipe processes one source's
decoded/reconstructed scratch at a time and keeps one accumulation grid.
Those are hard input limits, not a statement about worker concurrency or an
exposure-duration qualification. A missing pinned Ready input is retryable;
it is not permission to change a frozen window's coverage. Use the evidence
reader to verify layout, checksums and contributor lineage when reopening, and
retain both endpoints' plate dependencies if a published trail will use their
map. Physical-camera accuracy is not qualified by the virtual checks.

## VirtualSky evidence envelope

The actual sensor-pixel comparison uses a verified equidistant VirtualSky
catalog and retained plate pairs. The sampled cells include 1 s native
Mono16 at 60 s spacing with three exposures, 20 s native Mono16 at 120 s
spacing with three exposures, and 1 s native RGGB16 at 30 s spacing with
five exposures. The native Bayer comparison scores red, green and blue
separately. One-second Mono16 mirror and roll use the same three-exposure
comparison. Mono16 ROI with 2×2 digital-average binning uses an independent
delivered-coordinate grid and coverage checks at 120 s spacing.

The 20 s, 60 s spacing controls exercise a partially registered Mono16
window and a Bayer single-exposure fallback. Those controls cover rejection
and independently computed geometry where a verified map is available. The
positive sharpness and aperture-code comparisons use isolated apertures that have
all contributors and at least 3 px between the first and last predicted
placements. A positive 20 s native Bayer stack, 60 s stacked exposures and
physical-camera accuracy are outside the measured envelope. Aperture-code
ratios characterize the selected VirtualSky cells. Integrated flux and
automatic mixing across acquisition settings require a defined product
policy and a separately validated recipe.
