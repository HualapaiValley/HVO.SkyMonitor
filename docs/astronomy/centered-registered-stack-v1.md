# Centered registered stack v1

`centered-registered-stack` is an opt-in Processing window recipe, separate
from `rolling-mean` and historical replay. Its caller supplies exactly five
ordered slots (`−2` through `+2`), including reasons for holes and the exact
reference capture and qualified linear artifact at offset zero. The recipe
does not choose a cadence, extend a deadline, find a current calibration, or
replace the reference. Registration does not publish a live view.

## Alignment and pixels

- Coordinates are delivered-image pixel edges, with the center of pixel
  `(i,j)` at `(i+0.5,j+0.5)`. Use each member's own observation midpoint for
  sky mapping. The center exposure is sampled at identity. For a neighbor,
  evaluate the inverse direct map on each reference pixel center, then use
  only positive-weight bilinear taps. An out-of-range or invalid tap excludes
  that source at that point. The stencil does not extrapolate.
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
