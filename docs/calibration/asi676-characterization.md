# ASI676MM and ASI676MC Characterization

This document records the initial virtual profiles for the ZWO ASI676MM and
ASI676MC with their supplied 2.5 mm all-sky lenses. The source values are the
operator's 2026-07-21 consolidation of the current ZWO MM and MC manuals and a
measured full-frame sample. Published, sample-derived, and assumed values are
kept separate so that this provisional profile cannot be mistaken for a
physical-camera calibration.

## Shared Camera Specification

| Property | Value | Evidence |
| --- | ---: | --- |
| Sensor | Sony IMX676 STARVIS 2, back illuminated | ZWO manuals |
| Active image | 3552 x 3552, 12.61 MP | ZWO manuals |
| Sensor size | 7.104 x 7.104 mm, 10.04 mm diagonal | ZWO manuals |
| Pixel pitch | 2.0 x 2.0 um | ZWO manuals |
| Shutter | Rolling | ZWO manuals |
| Peak QE | Approximately 83% | ZWO manuals |
| Read noise | 0.56 to 2.9 e- | ZWO manuals |
| Example read noise | 1.8 e- at gain 82 (8.2 dB) | ZWO manuals |
| Full well | 10.55 ke- | ZWO manuals |
| ADC | 12-bit and 10-bit modes | ZWO manuals |
| HCG activation | Gain 180 | ZWO manuals |
| HCG dynamic range | Close to 11 stops | ZWO manuals |
| Exposure range | 32 us to 2000 s | ZWO manuals |
| Amp glow | None | ZWO manuals |
| Internal buffer | 256 MB DDR3 | ZWO manuals |
| Host interface | USB 3.0/2.0 Type-B | ZWO manuals |
| Maximum stated USB power | 1.36 W | ZWO manuals; model-name typo noted in power section |

The ZWO manuals describe both cameras as uncooled and list the same electronics,
body, and readout modes. The MM is monochrome with a 21 mm diameter, 1.1 mm thick broadband
AR-coated window. The MC has a color-filter array and a 21 mm diameter, 1.1 mm
thick UV/IR-cut window. The installed MC CFA phase must be verified through the
SDK and a parity capture; the executable V1 virtual profile provisionally uses
RGGB because that is the raw CFA contract currently supported by the system.

## Published Frame Rates

| Resolution | USB 3 RAW16 | USB 3 RAW8 | USB 2 RAW16 | USB 2 RAW8 |
| --- | ---: | ---: | ---: | ---: |
| 3552 x 3552 | 15.6 fps | 31.2 fps | 1.7 fps | 3.4 fps |
| 1920 x 1080 | 95 fps | 99.7 fps | 10.4 fps | 20.9 fps |
| 1280 x 720 | 145.7 fps | 145.7 fps | 23.5 fps | 47 fps |
| 640 x 480 | 210.4 fps | 210.4 fps | 70.5 fps | 141.2 fps |
| 320 x 240 | 379 fps | 379 fps | 283 fps | 347 fps |

RAW16 carries the 12-bit ADC result in a 16-bit container; it does not increase
sensor measurement depth. ZWO permits higher rates through ROI. The intended
approximately 3328-square ROI, 2x2 binning, and RAW8/RAW16 rate must be measured
through the SDK rather than inferred from this table.

## Supplied Lens

The removable manual-focus lens is marked `2.5 mm`, `f/1.2`, `3 MP`, and `IR`.
Its intended use is all-sky/fisheye imaging. ZWO separately lists a 2.5 mm,
170-degree lens for a 1/2-inch sensor but does not establish that it is
optically identical to the packaged ASI676 lens. The virtual profile therefore
records the field as approximately 170 degrees pending calibration.

One operator-measured full-frame sample yields the following estimates. The
source image and checksum are not retained in this repository, so these values
are useful placeholders rather than reproducible calibration evidence:

| Derived property | Provisional value |
| --- | ---: |
| Illuminated-circle diameter | 3240 to 3270 px |
| Physical image-circle diameter | Approximately 6.5 mm |
| Sensor-width coverage | Approximately 91 to 92% |
| Pixels within sky circle | Approximately 8.3 to 8.4 MP |
| Side padding | Approximately 140 to 155 px per side |
| Practical square ROI | Approximately 3328 x 3328 |
| 2x2 binned ROI | Approximately 1664 x 1664 |
| Binned sky-circle diameter | Approximately 1620 to 1640 px |

The virtual profiles use the midpoint 1627.5 px image-circle radius and the
geometric sensor center `(1776,1776)`. Equidistant projection and a 170-degree
field imply a synthetic scale of 1097.0456 px/radian. These are simulation
placeholders, not physical calibration results. Each camera still requires a
star-field fit for projection family, principal point, boresight, roll,
distortion, image circle, lens centering, tilt, and horizontal parity.

## Provisional Response Model

The shared virtual response is `zwo-asi676-12bit-published-envelope-v1`. It is
bounded to gains 0 through 180 because the supplied evidence does not define a
complete post-HCG gain curve. It derives gain-zero conversion from the
published 10,550 e- full well spanning 4095 native ADU and applies ZWO's 0.1 dB
gain convention:

```text
gain_zero_e_per_adu = 10550 / 4095
e_per_adu(gain) = gain_zero_e_per_adu / 10^(gain / 200)
usable_signal_codes = 4095 - black_level_adu
adc_limited_full_well = min(10550, usable_signal_codes * e_per_adu)
```

Read noise is linearly bounded by the published 2.9 e- at gain zero and 1.8 e-
at gain 82, then held at 1.8 e- until the gain-180 HCG transition. The
provisional HCG endpoint is 0.65 e-, selected to approximate the published
11-stop dynamic range without smoothing across the discontinuity. This model
exists only to make deterministic virtual frames useful before controlled
characterization. It does not replace bias, dark, flat, photon-transfer,
linearity, or gain-step measurements.

The provisional RAW16 black level is 64 native ADU. Reported usable full well
accounts for the signal codes above that pedestal. The renderer emits values
from 0 through 4095 in a little-endian 16-bit container. MM and MC currently
share the electrical response; MC uses neutral channel weighting because its
spectral response and the MM/MC window transmission differences remain
uncalibrated.

## Virtual Profiles

- `virtual-asi676mm.full.json` emits full-frame 3552-square `Mono16`, uses a
  nominal gain of 82 and a 32 ms short exposure, and runs at a conservative 1 Hz
  CameraAgent cadence. VirtualSky remains a still-frame module and does not
  claim the intended 30 fps video rate.
- `virtual-asi676mc.full.json` emits full-frame 3552-square provisional RGGB
  RAW16 with a 12-bit white level. It uses a 20 s night exposure and 25 s
  cadence for long-exposure color context.

The current rig contract has no acquisition-bin or hardware-ROI capability
matrix, and its optical crop is not an acquisition ROI. The intended MM mode
(approximately 3328-square ROI, 2x2 binning to approximately 1664 square,
likely RAW8 near 30 fps with RAW16 evaluated separately) must remain hardware
evidence until those contracts and the physical ASI module exist.

## Initial Virtual Performance

The x64 Release acceptance workload uses an empty deterministic catalog, five
warmups, and 30 measured full-resolution captures per shipped profile. This
measures virtual projection/rendering and buffer behavior, not USB acquisition,
physical sensitivity, or Raspberry Pi performance.

| Profile | Median / p95 | Throughput | Allocated/frame | Maximum sampled process RSS | Final SHA-256 |
| --- | ---: | ---: | ---: | ---: | --- |
| ASI676MM Mono16 | 273.116 / 310.405 ms | 3.596 frames/s | 126.2 MB | 917.0 MB | `3FC06CC08F58E148CA03A14ADDC17FDF28E52FE5ADC14380AC04C09E9FA901E8` |
| ASI676MC RGGB16 | 1140.901 / 1153.280 ms | 0.875 frames/s | 328.1 MB | 1155.0 MB | `B126CB15DA8FCD21D6F0F77EC47BDA0F925D9AFC0F6158055D57F3180F532C69` |

Both outputs are exactly 25,233,408 bytes and remain bounded to native 12-bit
samples. The MC renderer retains three full color planes and is intentionally
the larger memory workload. Its measured rate remains well above the configured
one-frame-per-25-second context cadence. The full-frame MM virtual profile is a
still-render correctness fixture; it does not claim the physical 1664-square
30 fps mode.

## Mechanical and Environmental Reference

| Property | Value |
| --- | ---: |
| Body diameter | 62 mm |
| Body height excluding lens | 37.1 mm |
| Main body section | 28 mm |
| Front thread | M42 x 0.75 |
| Front flange diameter | 50.8 mm |
| Back focus | 12.5 mm |
| Camera mass | 126 g |
| Tripod mount | 1/4-inch thread |
| Working environment | -5 C to 50 C; 20% to 80% RH |
| Storage environment | -20 C to 60 C; 20% to 95% RH |

The enclosure must keep the camera body below 50 C. The package also includes
a protective cover, 2 m USB 3.0 cable, ST-4 cable, 1.25-inch nosepiece, and quick
guide.

## Required Hardware Evidence

Before removing `provisional` from either profile:

1. Capture one SDK capability profile per physical camera, including serial,
   CFA code, bins, formats, controls, and exact exposure/gain limits.
2. Verify untouched RAW8 and RAW16 dimensions, stride, byte order, white level,
   CFA parity, and ROI origin/alignment rules.
3. Benchmark the intended full-frame and 3328-square 2x2 modes over USB 3 on the
   target Pi 5, including sustained frame rate, drops, CPU, RSS, temperature,
   throttling, and storage throughput.
4. Collect controlled bias, dark, flat, linearity, photon-transfer, and defect
   datasets for both cameras and both protective windows.
5. Fit each installed lens from synchronized star fields rather than assuming a
   nominal fisheye projection.

The native probe workflow is documented in `tools/asi-capture/README.md`.
