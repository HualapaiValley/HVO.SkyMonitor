# Retained satellite exposure presentation

These examples use the actual virtual-camera/sensor outputs already retained by
[#529](../orbital-prediction-v1/virtual-camera.md), with a separate
`PredictedSatelliteTracks` layer from their canonical exposure products. This
exercise does not acquire another snapshot, propagate another orbit, rerun the
detector or publish a stored capture. Synthetic brightness remains an explicit
input to the original simulation, independent of orbital elements.

| Example | Exposure UTC, 2026-10-20 | Prediction image | Canonical source |
| --- | --- | --- | --- |
| ISS / NORAD 25544, Seattle | 13:03:42.287674–13:03:47.287674 | [PNG](iss/predicted.png) / [SVG](iss/predicted.svg) | [source envelope](iss/source-facts.json) / [validation](iss/validation.json) |
| Starlinks / NORAD 56838 and 65283, HVO fixture | 04:00:10–04:00:15 | [PNG](starlinks/predicted.png) / [SVG](starlinks/predicted.svg) | [source envelope](starlinks/source-facts.json) / [validation](starlinks/validation.json) |

The short paths cover five-second exposures. Full-pass diagrams are a separate
planning product. Optional object labels may be omitted when they do not fit;
mandatory prediction/uncertainty and applicable source/geometry/stale/zero-EOP
qualifications remain visible, along with the canonical facts. The HVO
scenario uses its documented nominal configured rig and fixture location,
35.347 degrees north, 113.878 degrees west, assumed ellipsoidal height 0 m.
This is not measured camera calibration or a verified HVO elevation.

## Reproduce and verify

From the repository root, use SDK 10.0.402 and the commands in
[the POC guide](../../../tools/HVO.SkyMonitor.VirtualSkyPoc/SATELLITE-POC.md#retained-exposure-presentation-example-1393).
The retained input PNG hashes are:

```text
docs/validation/orbital-prediction-v1/samples/iss-injected-03.png
7E30DF3A26A65FC7C80585A2A5F0392E25F8A80167AE5A84AB42EB40BD07B94C
docs/validation/orbital-prediction-v1/samples/constellation-injected-03.png
64B0459DD9271AECC55F8156A693143B6BF3DCCD9B53C1226D5A44B53A3E6411
```

The command produces the original base, a disabled-layer raster, predicted
raster/SVG, strict layer payload, source envelope, standalone manifest,
interactive HTML comparison, input/product checksums and assembly provenance.
This directory retains the new presentation products; unchanged bases and
canonical orbital tracks remain in their original #529 directories. The
standalone manifest records caller-supplied pairing and coordinate provenance;
it is not an authenticated stored-capture manifest.

Root validation checked:

- Every selected portable file against [checksums.json](checksums.json).
- The exact source-envelope SHA-256 against the payload source identity.
- Disabled composition equal to every decoded base RGB byte, while enabling
  the prediction changes the display output.
- The canonical track identities and original snapshot/record/exposure lineage.
- The generated SVG comparison in the native browser: both images load, the
  prediction checkbox hides its overlay while retaining the base, and no console
  error is reported.

The examples were regenerated from the Release CLI at source commit
`0a249abd5eb772709d08ee45a010ac983dce6f92`, using one-pixel track strokes and
two-pixel-radius endpoint rings. Base image and canonical track bytes are
unchanged; mandatory source-validity/configured-geometry text remains visible
even when every optional object label is omitted. Each
validation file pins its exact assembly identity and SHA-256. These are output
checks and component integration evidence, not a new numerical oracle,
performance baseline, live cadence result or measured satellite identification.

## Attribution and limits

The source images and these derivatives contain a transformed subset of HYG 4.2
by David Nash (Astronexus), under CC BY-SA 4.0. Preserve the complete
[attribution](ATTRIBUTION-HYG.md) and [license](LICENSE-HYG.md) with redistribution.
HVO supplies the projection, sensor simulation, synthetic satellite stimuli and
prediction presentation; upstream authors do not endorse those changes.

No Sun/shadow/brightness or physical uncertainty is inferred from elements.
Raw/calibrated detector pixels receive no annotations. Runtime snapshot/time
composition, retained capture prediction publication, measured association and
complete five-second pipeline qualification retain their separately documented
dependencies.
