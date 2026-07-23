# Reference Calibration v1

`reference-calibration` is the host-neutral linear correction recipe for explicit
immutable bias, dark, flat, and defect references. The initial producer is
VirtualSky software evidence; it is not a physical sensor or optical calibration.

## Profile

The canonical `reference-calibration-profile-v1` JSON records:

- opaque profile ID and version, source, and half-open effective UTC interval;
- width, height, Mono16 or RGGB16 format, and positive flat normalization ADU;
- inclusive gain and optional temperature applicability;
- exactly one bias, dark, flat, and defect descriptor;
- each reference artifact ID, payload SHA-256, exposure, gain, and temperature.

The canonical profile SHA-256 becomes the calibrated product's calibration
compatibility identity. The defect payload SHA-256 becomes its mask identity.
Artifact auxiliary names are `bias-reference`, `dark-reference`,
`flat-reference`, and `defect-reference`; the JSON auxiliary is
`calibration-profile`.

## Arithmetic

All source samples are unsigned little-endian 16-bit integers. RGGB samples are
corrected at their original photosites and are never demosaiced.

For each usable sample:

1. `darkSignal = max(dark - bias, 0)`.
2. Scale `darkSignal` by `lightExposure / darkExposure` and subtract it and bias
   from the light, clamping the intermediate signal at zero.
3. Subtract bias and dark scaled by `flatExposure / darkExposure` from the flat.
   A non-positive usable flat denominator is terminal.
4. Multiply the light signal by `flatNormalizationAdu`, divide by the corrected
   flat, round midpoint away from zero, and clamp to `[0, 65535]`.
5. Replace marked defects with the rounded mean of non-defect orthogonal
   neighbors. Mono uses distance one; RGGB uses distance two to preserve the CFA
   lane. No compatible neighbor is terminal.

Inputs are borrowed and unchanged. The only full-frame recipe allocation is the
packed output. Complexity is O(width x height).

## Outcomes

Missing profile/reference inputs skip without publication. Invalid, stale,
ambiguous, corrupt, layout-mismatched, conditions-mismatched, invalid-flat, or
unrepairable-defect inputs are terminal with stable `calibration.*` reason codes. Cancellation throws
and no partial product is returned. Output lineage is ordered raw, bias, dark,
flat, defect, and the output checksum covers exact corrected bytes.

## Synthetic References

`synthetic-calibration-model-v1` deterministically generates spatial bias, dark
fixed pattern, pixel-response variation, radial vignetting, and configured fixed
defects. CameraAgent materializes four manifest-v2 reference artifacts under
`calibration/synthetic/<MODEL_SHA256>/` using atomic immutable publication and
retention holds. The canonical profile is the bundle commit marker: once present,
any missing or changed member fails closed instead of being regenerated. The raw
capture records the exact model identity, and the processing step rejects a
different reference-generation model. VirtualSky applies the same configured model to ordinary light
frames, but the correction recipe receives only persisted reference bytes and
the canonical profile; it receives no ideal pixels or simulator truth. Reference
artifacts use deterministic sequences in the reserved upper half of the positive
64-bit range, away from practical acquisition sequences. Central scheduling and
UI integration remain outside this contract.
