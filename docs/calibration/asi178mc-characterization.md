# ASI178MC Characterization

This document defines the evidence hierarchy and initial virtual profile for the
development ASI178MC. It is backed by `asi178mc-sdk-profile-v1.json` and
`asi178mc-session-20260713.json` and must be updated as controlled bias, dark,
flat, and astrometric lens measurements become available.

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
| Gain-zero conversion | about 0.916 e-/native ADU | ZWO published graph |
| Gain-zero full well | about 15 ke- | ZWO graph |
| Gain-zero read noise | about 2.25 e- RMS | ZWO graph |
| Minimum read noise | about 1.35 e- RMS | ZWO graph |

Sources:

- https://i.zwoastro.com/zwo-website/manuals/ASI178_Manual_EN_V1.3.pdf
- https://www.zwoastro.com/software/product-sdk/
- https://web.archive.org/web/20240509064023id_/https://astronomy-imaging-camera.com/product/asi178mc-color/

## Verification Status

The current profile is intentionally provisional. Hardware and SDK evidence do
not imply that the virtual response or installed optics are calibrated.

The production physical declaration is
`cameraagent.zwo-asi178mc.sample.json`: full-frame 3096 x 2080 bin-1 color RAW16,
14-bit ADC depth in a 16-bit little-endian container, 6192-byte stride, RGGB at
origin `(0,0)`, and `OpaqueContainerV1` with `StoredContainer` levels 0 through
65535. It contains no `simulationResponse`. The committed candidate Fujinon
optics remain provisional calibration metadata; `horizontalFlip: true` records
that optical calibration while SDK/native flip remains disabled. This physical
declaration does not alter `virtual-asi178mc.full.json`, whose
`FullRangeScaledV1` mapping is a simulation model rather than a claim about
native SDK container encoding.

| Component | Status | Evidence |
| --- | --- | --- |
| SDK identity and capabilities | Hardware verified | SDK 1.41 profile probe on serial `350f500522000900` |
| Native RAW16 layout | Hardware verified | Untouched `3096 x 2080`, 12,879,360-byte captures |
| RGGB phase | Hardware verified | SDK Bayer code plus measured four-position parity response |
| Binned dimensions and packing | Hardware verified | Normal SDK bin 1/2/3/4 capture matrix |
| Mono-bin behavior | Partially hardware verified | Bin 2/4 flatten parity; bin 3 retains a residual pattern |
| Hardware-bin-2 control | Reported, not operationally verified | SDK reports writable; tested RAW16 still path rejects enablement |
| Electron/noise response | Provisional | Published curves and open-sky samples; no controlled photon-transfer fit |
| Lens identity and projection | Unverified | Candidate geometry only; no barrel inspection or multi-star fit |
| Installed orientation | Provisional | Landmark observations without a synchronized multi-star fit |
| Bias, dark, flat, and defect maps | Uncalibrated | Controlled datasets have not been collected |

## Linux x64 Functional Acceptance

The 2026-08-03 functional acceptance used SDK V1.41 on Ubuntu 24.04 x64 with
the camera attached alone on a 5 Gbit/s USB 3 bus. The operator-installed x64
library SHA-256 was
`d1de4a5ab85c8cafbddfad9c593bbba515890d3adf20c1ca44dafcf15f2775ce`.
The private runtime selected the camera by serial without placing that value in
configuration, logs, telemetry, or committed evidence.

The ordinary standalone CameraAgent path retained 25 contiguous full-frame
RAW16 captures and v2 sidecars. Every payload was 12,879,360 bytes, all 25
SHA-256 values were distinct, and every sidecar checksum matched its immutable
payload. Sidecars reported 3096 x 2080, 6192-byte stride, RGGB at `(0,0)`,
14-bit samples in a 16-bit little-endian byte-aligned container, and
`OpaqueContainerV1`/`StoredContainer`. Observed SDK exposure completion was
20.550-20.557 seconds, readout was 0.004-0.021 seconds, durable ingress after
readout was 0.080-0.301 seconds, and the uncooled sensor reported
30.3-32.8 C in uncontrolled indoor conditions.

Graceful cancellation during an active exposure exited in 535 ms without a
partial or quarantined artifact, and a separate SDK probe reopened the camera.
Process restart reconciled all committed records before the next capture. USB
unbind/rebind produced the expected failed-exposure status on the stale native
session; a clean CameraAgent restart then reopened the device and captured
successfully. Final health was healthy, every required local lane was drained,
the durable journal contained 25 completed assignments, central HTTP attempts
were zero, and OTLP logs, metrics, and capture/raw-ingress/lane/processing
traces were observed. Sustained cadence, throughput, thermal, USB saturation,
and resource characterization remain deferred to issue #268.

The machine-readable SDK evidence is
[`asi178mc-sdk-profile-v1.json`](asi178mc-sdk-profile-v1.json). The SDK-reported
`ElecPerADU` value is retained as provenance but is not treated as a calibrated
conversion gain because it disagrees with the published gain-zero curve and has
not been tied to an SDK gain setting.
Commands, hashes, host state, measurements, and durable source paths for the
cloudy session are recorded in
[`asi178mc-session-20260713.json`](asi178mc-session-20260713.json).

## RAW16 Mapping

The virtual response model uses:

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

## 2026-07-12 Open-Sky Matrix

The ZWO ASI Camera SDK V1.41 ARMv8 library and repository
`tools/asi-capture` utility were deployed natively to `allskycamera01` under
`/home/roys/asi-capture`. No system packages, Docker images, services, or global
libraries were installed.

The camera was serial `350f500522000900`, attached as a USB3 camera to a USB3
host, at approximately 35 C. Offset was 10, high-speed mode was disabled, and
USB bandwidth control was 40. Two full-resolution untouched RAW16 frames were
captured for every exposure/gain pair:

| Exposure | Gains |
| --- | --- |
| 0.1 s | 0, 150, 300 |
| 1 s | 0, 150, 300 |
| 20 s | 0, 150, 300 |

The retained Pi source path is
`/home/roys/asi-capture/captures/matrix-20260712T0830Z`. The downloaded analysis
working path was `/tmp/opencode/asi178mc-matrix/matrix-20260712T0830Z`; it was
temporary and is not a repository artifact. Representative gain-150 display
derivatives and sidecars were also organized under the external path
`data/calibration/asi178mc/2026-07-12`.

Every raw file was 12,879,360 bytes. Adjacent JSON recorded camera properties,
requested and actual controls, timing, temperature, and basic statistics.

| Exposure | Gain-150 median container ADU | Gain-150 mean container ADU |
| ---: | ---: | ---: |
| 0.1 s | 78 | 83.5 |
| 1 s | 84 | 92.9 |
| 20 s | 598 | 580.7 |

Twenty-second medians at gains 0, 150, and 300 were approximately 152, 601, and
3227 container ADU. The observed floor was 4 and ceiling was 65534. All low-byte
values occurred, with a strong modulo-four code bias. Near-container clipping
was sparse even at 20 seconds and gain 300. Long exposures showed persistent
spatial glow, edge/optical obstruction, fixed-pattern structure, and hot pixels.
Repeated short high-gain sequences also contained occasional whole-frame level
excursions that were not consistently first-frame settling.

These images combine sky signal, dark current, bias, fixed-pattern response,
amp/dome/lens glow, obstructions, hot pixels, and stars. They cannot isolate any
one sensor parameter. Preview JPEGs are display derivatives only; quantitative
analysis uses untouched RAW16 inputs.

The original capture pattern can be repeated with a new named output directory:

```bash
ssh roys@192.168.1.5 \
  "/home/roys/asi-capture/asi-capture \
    --output-dir /home/roys/asi-capture/captures/session-name \
    --exposure-us 20000000 --gain 150 --offset 10 --count 4"
```

Controlled flat or covered bias/dark pairs are required for photon-transfer or
read-noise measurements. The open-sky matrix characterizes operational behavior
and system structure only.

## Cloudy-Sky SDK and Binning Session

On 2026-07-13, the standalone C++ utility probed SDK 1.41 and captured short
RAW16 pairs at gain 150, offset 10, and a requested 0.1-second exposure. The
camera reported RAW8, RGB24, Y8, and RAW16 formats; bins 1, 2, 3, and 4; gain
`0-510`; exposure `32-2000000000 us`; offset `0-600`; and controls reported as
writable for hardware-bin and mono-bin.

| Bin | Returned dimensions | Row bytes | Frame bytes |
| ---: | ---: | ---: | ---: |
| 1 | 3096 x 2080 | 6192 | 12,879,360 |
| 2 | 1548 x 1040 | 3096 | 3,219,840 |
| 3 | 1032 x 692 | 2064 | 1,428,288 |
| 4 | 774 x 520 | 1548 | 804,960 |

The nominal bin-3 height is 693, but SDK error 8 rejected the odd ROI. Trimming
one post-bin row to 692 produced the largest tested top-left ROI. It covers
2,076 of 2,080 physical sensor rows, omitting four source rows. This is measured
SDK behavior and must not be inferred from the advertised bin list alone; odd-
width rejection was not tested.
Normal bin-2 captures report hardware-bin disabled. The final utility configures
a valid bin-2 ROI first; enabling the distinct hardware-bin control then causes
SDK error 16 and writes no frame. An earlier development-order experiment also
received error 8 before ROI configuration, but that path is not part of the
reproducible workflow. Hardware binning therefore remains unsupported for this
tested still/RAW16 combination despite the writable capability report.

Bin-1 parity means from a representative cloudy frame were approximately
`70.30, 50.67, 51.83, 88.35` ADU for even/even, even/odd, odd/even, and odd/odd
samples. Bin-2 with mono-bin disabled retained the same strong four-position
pattern. At bin 2 with mono-bin enabled, the four means were `83.011, 83.030,
83.006, 82.977` ADU, and bin 4 was similarly flat. Bin 3 mono-bin retained an
elevated odd/odd mean (`89.30` versus approximately `81.1-81.3` elsewhere).
Normal binned RAW16 therefore retains color-grid behavior, while SDK mono-bin
strongly suppresses it at bins 2 and 4 but is not parity-uniform at bin 3. It
does not calibrate channel gains or establish a generic mono-bin response
because cloud illumination and lens transmission were not controlled.

Requested 0.1-second still exposures reached SDK success in approximately
394-405 ms at every bin factor, followed by approximately 12-84 ms for
`ASIGetDataAfterExp`. Sensor temperature was 39.6-39.7 C. The Raspberry Pi was
73.5-74 C with no current throttle bits and a roughly 1.5 GHz ARM clock, but
`get_throttled=0xe0000` retained earlier frequency-cap, throttle, and soft-
temperature history. These are characterization observations, not a clean
thermal acceptance run.

Session frames ranged between whole-frame levels near 65 and 85 mean ADU. Cloud
changes prevent attributing that variation to the camera, although earlier
short high-gain sequences independently recorded occasional level excursions.
Stationary read-noise work still requires controlled covered pairs and explicit
outlier handling. Cloud cover prevented any astrometric lens or orientation
claim.

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
`ASI_MONO_BIN` control. Physical captures now establish the returned dimensions,
tightly packed little-endian RAW16 layout, odd bin-3 row constraint, and color-
grid versus mono-bin distinction. Black level, signal aggregation, conversion
gain, and noise response remain uncalibrated. A virtual mono-bin response profile
must not be added until controlled bias and flat pairs establish those semantics.
