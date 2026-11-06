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
`calibration/synthetic/<PUBLICATION_SHA256>/` using atomic immutable publication and
retention holds. `calibration-library-bundle.json` records those direct references
as `synthetic-references-v1` masters with empty source lineage, and
`reference-calibration-profile.json` is the final commit marker. Once the marker
is present, any missing or changed member fails closed instead of being
regenerated. The publication identity binds the model to layout and rig/sensor
profile hashes, while the bundle and raw capture retain the model-only identity
used for source compatibility. The processing step rejects a
different reference-generation model. VirtualSky applies the same configured model to ordinary light
frames, but the correction recipe receives only persisted reference bytes and
the canonical profile; it receives no ideal pixels or simulator truth. Reference
artifacts use deterministic sequences in the reserved upper half of the positive
64-bit range, away from practical acquisition sequences. Central scheduling and
UI integration remain outside this contract.

## Additive Local Library Envelope

`calibration-library-bundle-v1` indexes the profile and manifest-v2 artifacts. A
bundle records exact agent/rig and sensor-profile identities, input and normalized
linear-16 output layouts, gain/offset/exposure/temperature applicability, effective
UTC interval, simulator acquisition-model identity, and source/master artifacts.

`synthetic-references-v1` bundles contain the four deterministic direct references
as masters and leave source-frame lineage, offset, and light-exposure facts
explicitly absent. Missing facts are never synthesized during adoption.
`virtual-acquisition-v1` bundles contain three immutable source frames per kind;
bias, dark, and flat masters use `calibration-median-v1`, while defect masks use
`calibration-bitwise-or-v1`. Master lineage lists source artifacts in acquisition
order.

Initial selection is exact-readout only. No crop/bin transform or nearest-match
ranking is defined. A selector must match identity, native/readout geometry, CFA
and origin, sample/container/packing/stored-code semantics, gain, offset, light
exposure, temperature, and effective interval, then produce exactly one active
bundle. Missing, stale, corrupt, incomplete, ambiguous, incompatible, inactive,
publication-conflict, acquisition-failure, and master-build-failure outcomes use
stable `calibration.library.*` reasons.

## Mixed-Gain Calibration Families

The opt-in `mixed-gain-normalized-mean` recipe starts from raw frames and uses an
explicitly activated, immutable `calibration-family-v1`, with exact gain-indexed
bias, dark, flat and defect masters. Every selected light binds its own member,
master IDs/checksums and exposure facts in `hvo-normalization-plan-v1`. The family
identity replaces only the per-gain calibration axis; it does not relax layout,
mask, offset, exposure, temperature setpoint, rig/sensor, location or processing
profile matching. Members and retained masters are pinned by frozen plans while
products still reference them, including completed executions.

Missing members/masters, incompatible identities and gains outside the validated
response or 4:1 coefficient bound are excluded with stable reasons. No raw,
nearest-gain or guessed master is substituted. Missing/corrupt already-frozen
master evidence fails closed rather than reselecting from the current library.
The execution detail exposes the frozen candidate exclusions; a failed selection
before freeze exposes its stage reason. Saturation is a per-photosite exclusion
before correction, with per-light counts in the diagnostic companion. Defects and
invalid flat responses also have zero weight; a no-coverage output zero is not a
zero-flux measurement. Versioned row-major UInt8 Metadata rasters retain valid
contributor counts (0–128) and no-coverage flags (1 when count is zero), with
original geometry, exact byte bounds and lineage/checksum bindings in the small
diagnostic companion. These support rasters use normal raster persistence and
archived replay; operator execution details do not read their full payloads.
The output uses fixed gain 0 and ×16 scaling, with signed
calibration followed by equal valid-sample weights and one final clipping step.
See [the processing contract](processing-recipes-v1.md#mixed-gain-normalized-mean).

Gain responses for VirtualSky ASI174MM and configured virtual ASI178MC/ASI676
profiles are pinned simulation truth, not measured physical response. Automatic
night gain control is verified using rendered VirtualSky measurements. Physical
mixed-gain mode remains unavailable until controlled gain-response and stored-code
mapping evidence is accepted; sensors without supported gain data retain the
ordinary identical-gain rule. Full-range-scaled readouts are not accepted by the
current right-aligned mixed-gain plan. Mixed exposures, inverse-variance weighting,
registration and physical sensitivity claims remain outside this contract.
