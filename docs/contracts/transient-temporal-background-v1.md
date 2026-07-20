# Transient Temporal Background V1

## Purpose

This contract defines host-neutral whole-frame compatibility, masks, and temporal
background products before transient candidate extraction. Processing owns
window validation, outcomes, and lineage. Imaging owns pure detector-pixel mask
and background arithmetic. Hosts remain responsible for loading artifacts,
waiting for future inputs, and applying deadlines.

V1 does not define streams, chunks, video decoding, candidate extraction,
classification, event persistence, or host scheduling.

`TransientTemporalWindowActivation` is the shared allocation boundary for the
four detector modes. `Off` returns without invoking request/window construction;
edge, central, and hybrid runtime wiring remains owned by their host issues.

## Window Semantics

`TransientTemporalBackgroundRequest` identifies the target as position `N` and
supplies already resolved context positions. The products use these exact
ordered sources:

| Product | Background sources |
| --- | --- |
| `CausalProvisional` | `N-2,N-1` |
| `CenteredFinal` | `N-2,N-1,N+1,N+2` |

`N` is never a background source. Capture sequence must equal the target
sequence plus the declared position. Actual UTC exposure starts and ends are
authoritative. They must be strictly ordered, non-overlapping, and within the
request's explicit maximum adjacent-start interval. Sequence gaps and temporal
gaps have different reason codes.

Known event-bearing context is supplied by evidence identity and excluded from
the mean. Discovering event-bearing frames belongs to candidate extraction.
Every target, included source, and known-event exclusion remains in ordered
lineage. A window with no remaining context produces `NoUsableContext`.

## Compatibility And Normalization

Detector dimensions, stride, Mono16 layout, source representation, conversion,
source-to-detector transform, linear levels, rig, orientation, calibration,
mask profile, sensor, and processing profile must match exactly. Setpoint regime
may differ only when every source supplies the same calibrated linear response
identity and an explicit positive rational sensitivity.

The normalization factor is target sensitivity divided by source sensitivity,
reduced to an exact unsigned rational. The Imaging algorithm applies it to the
black-subtracted signed sample, rounds midpoint values away from zero, restores
the target black level, and excludes a sample if normalization would leave the
unsigned 16-bit range. Opaque gain values or setpoint strings are never
interpreted as response ratios.

## Masks

`Linear16PixelMask` is detector-coordinate, row-major, LSB-first, one bit per
pixel. A set bit means excluded. The final byte's unused high bits must be zero.
Mask identity covers kind, algorithm, geometry, and exact bytes.

The persistent component set is exactly sky, image circle, horizon, obstruction,
bad pixel, and star. Every component kind and identity must match across the
applicable window. Saturation is intrinsic to detector-input construction and
may vary by source. Components compose with bitwise OR while their individual
identities remain in source lineage. RGGB source masks use any-photosite
semantics: if any member of a 2x2 RGGB cell is excluded or saturated, the
derived detector cell is excluded even when cell averaging hides the clipped
photosite.

The exact intrinsic saturation-mask checksum is part of the detector-input
descriptor and therefore its canonical identity. Runtime mask bytes must match
that checksum before window processing.

The V1 star strategy is a persistent mask formed from the union of
catalog-projected PSF support at every timestamp available to that product:
`N-2,N-1,N` for causal provisional processing and `N-2..N+2` for centered final
processing. The support is transformed into detector coordinates before
rasterization. VirtualSky labels and private truth masks are not production
inputs.

## Background Arithmetic

`Linear16TemporalBackground` performs one scan over borrowed source frames. For
each detector pixel it excludes masked samples, normalizes remaining samples,
and emits the rounded unsigned arithmetic mean. If no source supports a pixel,
the output contains the target black level and the no-support mask excludes the
pixel. The effective product mask is the target mask unioned with no-support.

The algorithm allocates one packed background and bit-packed masks. It does not
clone source frames or allocate a full-frame accumulator plane.

## Outcomes And Lineage

Outcomes are `Produced`, `Missing`, `TimedOut`, or `Incompatible`. Stable reason
codes distinguish missing source, deadline timeout, invalid request, sequence
gap, temporal gap, incompatible layout/profile/response/mask, and no usable
context.

`TransientTemporalBackgroundJson` persists a strict bounded outcome descriptor
for both success and failure. It carries reason-coded lineage and checksums but
never embeds full-frame background or mask bytes.

A produced descriptor records:

- target detector-input identity;
- exact ordered source positions, sequences, evidence identities, and exposure
  intervals;
- included, target, and known-event dispositions;
- source sensitivity and applied normalization rational;
- component mask identities;
- output layout and background/effective-mask checksums;
- background and mask algorithm identities;
- a stable descriptor identity.

Equivalent in-memory edge inputs and reconstructed central inputs must produce
byte-identical backgrounds, masks, checksums, lineage, and descriptor identity.

## Star Strategy Evidence

The pre-coding W1/W2 measurement is recorded on issue #115 and reproduced by
`TransientStarMaskStrategyTests`. At 25-second cadence and 20-second exposure,
p95 projected motion across the 100-second window was about 3.2 detector
pixels. The persistent mask removed 100% of measured supra-threshold star
residual count and energy. It used 18.515% of the valid W1 image circle and
1.189% of W2.

Nonlinear fisheye registration is deferred because it adds resampling and
full-frame scratch without a measured V1 correctness benefit. Reconsider it if
physical/catalog evidence exceeds 20% valid-area masking or leaves more than 1%
of star residual count or energy.
