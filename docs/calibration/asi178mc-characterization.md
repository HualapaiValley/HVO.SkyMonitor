# ASI178MC Characterization

This document defines the evidence hierarchy and initial virtual profile for the
development ASI178MC. It complements the session record in
`asi178mc-pi-smoke-test.md` and must be updated as controlled bias, dark, flat,
and astrometric lens measurements become available.

## Profile

| Property | Value | Evidence |
| --- | ---: | --- |
| Sensor | Sony IMX178 color | ZWO product/manual |
| Active image | 3096 x 2080 | ZWO and SDK V1.41 |
| Pixel pitch | 2.4 um | ZWO and SDK V1.41 |
| CFA | RGGB | SDK `ASI_BAYER_RG` and RAW16 capture |
| Physical ADC | 14 bit | ZWO and SDK V1.41 |
| Transport | little-endian RAW16 | measured capture |
| Exposure range | 32 us to 1000 s | ZWO manual |
| Gain units | 0.1 dB | ZWO SDK convention |
| Gain-zero conversion | about 0.916 e-/native ADU | ZWO graph/SDK |
| Gain-zero full well | about 15 ke- | ZWO graph |
| Gain-zero read noise | about 2.25 e- RMS | ZWO graph |
| Minimum read noise | about 1.35 e- RMS | ZWO graph |

Sources:

- https://i.zwoastro.com/zwo-website/manuals/ASI178_Manual_EN_V1.3.pdf
- https://www.zwoastro.com/software/product-sdk/
- https://web.archive.org/web/20240509064023id_/https://astronomy-imaging-camera.com/product/asi178mc-color/

## RAW16 Mapping

The physical sensor model uses:

```text
native_e_per_adu(gain) = 0.916 * 10^(-gain / 200)
container_e_per_adu = native_e_per_adu / 4
adc_limited_electrons = min(15000, 16383 * native_e_per_adu)
```

The SDK capture uses the full 16-bit container, with observed values from 4 to
65534 and all low-byte values represented. It is neither right-aligned 14-bit
nor an exact left shift by two. The virtual profile therefore maps the physical
14-bit response continuously into a 16-bit container but does not claim
bit-exact reproduction of the SDK's low-bit transfer behavior.

Display derivatives use one global RAW16 percentile/asinh transfer followed by
bilinear RGGB interpolation into RGB24. Demosaicing never modifies or replaces
the Bayer raw artifact. The same conversion is used for actual and virtual
comparison JPEGs so color differences are not caused by separate preview
recipes.

Read noise is piecewise-linearly interpolated through the sourced points:

```text
(0,2.25), (50,1.92), (100,1.72), (150,1.57),
(200,1.44), (270,1.37), (300,1.37), (400,1.35)
```

## Initial Optical Calibration

The installed lens is provisionally identified as the Fujinon FE185C057HA-1.
Its nominal 5.7 mm image circle is the only candidate consistent with the
7.43 x 4.99 mm IMX178 sensor: it is wider than the sensor height, so the top and
bottom are clipped, but narrower than the sensor width, so both lateral edges
remain visible. The FE185C046HA-1 4.6 mm circle should fit vertically, while the
FE185C086HA-1 8.6 mm circle should exceed the sensor width.

The candidate lens is represented as a 185-degree equidistant fisheye with a
1.8 mm nominal focal length, 1187.5 px image-circle radius, 735.55 px projection
scale, and principal point `(1548,1040)`. The exact model remains unconfirmed
until its barrel marking is inspected or a multi-star distortion fit is made.

Observed landmarks place north near image-up and southeast in the lower-left.
The camera therefore uses a horizontal flip: east is image-left and west is
image-right. A previous three-star fit used an incorrect Altair correspondence
and was rejected. More matched stars are required to solve principal point,
boresight tilt, roll, radial distortion, and projection family robustly.
Obstructions, vignetting, and dome/lens reflections still require flat fields.

The initial magnitude-zero system rate is 18,000 electrons/second. The reduced
development fixture explicitly uses 1.05 background electrons/second/photosite
so downscaled geometry does not incorrectly behave like 4x4 hardware binning.
The full profile derives approximately the same Bortle-3 background from the
full-resolution pixel solid angle. These values combine lens throughput,
atmosphere, passband, and assumed absolute QE and are not sensor specifications.

## Measured Open-Sky Baseline

Representative gain-150 display derivatives and sidecars are under:

`data/calibration/asi178mc/2026-07-12`

| Exposure | Median container ADU | Mean container ADU |
| ---: | ---: | ---: |
| 0.1 s | 78 | 83.5 |
| 1 s | 84 | 92.9 |
| 20 s | 598 | 580.7 |

These images include sky signal, dark current, bias, fixed-pattern response,
amp/dome/lens glow, obstructions, hot pixels, and stars. They cannot isolate any
one sensor parameter.

## Calibration Process

Future camera/lens onboarding should produce five versioned datasets:

1. Layout: SDK format, dimensions, stride, byte order, CFA phase, effective ADC
   depth, container mapping, and clipping behavior.
2. Bias: covered minimum exposures across gain and offset, using differenced
   pairs for temporal read noise and averages for fixed-pattern bias.
3. Photon transfer: uniform flat pairs across gain and signal level to measure
   conversion gain, linearity, PRNU, and saturation.
4. Dark response: covered exposure and temperature matrix for dark current,
   DSNU, hot pixels, and glow.
5. Optics/sky: astrometrically matched stars plus flats and SQM readings for
   projection, distortion, PSF, vignetting, obstructions, throughput, and sky
   calibration.

Raw inputs remain immutable. Preview JPEGs, debayered images, stacks, and model
fits are derivative artifacts with explicit recipes and source identifiers.

## Binned Mono Capability

The SDK reports supported bin factors 1, 2, 3, and 4 and exposes the
`ASI_MONO_BIN` control. This confirms hardware capability but not its exact ROI,
packing, black level, or response. A virtual mono-bin profile will be added only
after paired RAW16 captures establish those semantics.
