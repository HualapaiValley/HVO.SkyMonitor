# ASI174MM Characterization

This procedure captures the measurements needed to replace provisional virtual
camera parameters with values from the installed ASI174MM, lens, and camera
settings. Preserve native SDK `RAW16` bytes; do not apply debayering, stretching,
gamma, white balance, stacking, or image compression.

## Published Baseline

The initial model uses the documented 1936 x 1216 Sony IMX174 geometry, 5.86 um
pixels, 12-bit normal mode, approximately 32.4 ke- gain-zero saturation, 7.9
e-/ADU at gain zero, and the ZWO gain convention of 0.1 dB per control unit.
Read noise is interpolated from the ZWO performance graph from approximately
5.9 e- at gain zero to 3.6 e- at high gain.

Sources:

- https://www.zwoastro.com/product/asi174mm-mono/
- https://i.zwoastro.com/zwo-website/manuals/ASI174_Manual_EN_V1.5.pdf
- https://i.zwoastro.com/wp-content/uploads/2022/07/5dd84cfadc280ae67776a78c8b21a40c.jpg
- https://bbs.zwoastro.com/d/6154-camera-gain-values

The camera offset, dark current, native `RAW16` bit alignment, fixed-pattern
noise, and installed-lens throughput remain unmeasured.

## Capture Manifest

Write one JSON manifest beside every raw frame with:

```json
{
  "cameraModel": "ASI174MM",
  "cameraSerial": "...",
  "sdkVersion": "...",
  "pixelFormat": "RAW16",
  "adcModeBits": 12,
  "width": 1936,
  "height": 1216,
  "strideBytes": 3872,
  "exposureMicroseconds": 1000000,
  "gainControl": 150,
  "offsetControl": 10,
  "sensorTemperatureC": 25.0,
  "highSpeedMode": false,
  "timestampUtc": "2026-07-12T00:00:00Z",
  "frameKind": "bias|dark|flat|sky",
  "sequence": 1
}
```

Record ambient temperature, lens, aperture, filter/window, sky conditions,
SQM reading when available, and whether the lens cap or a uniform source was
used in a session-level manifest.

## Capture Matrix

1. Packing: uniformly illuminated exposure ramps at gains 0, 150, 189, and 300;
   record observed minimum, maximum, byte order, active bits, and RAW8 relation.
2. Bias/read noise: 64 minimum-exposure covered frames at gains 0, 50, 100,
   150, 189, 200, 250, 300, and 400 for each tested offset.
3. Photon transfer: at least 16 pairs of uniform flats at each gain and at 10-15
   signal levels from bias through saturation.
4. Linearity/saturation: exposure ramp with a stable flat source through the
   native maximum code, retaining clipped-pixel counts.
5. Dark current: 16 covered frames at 1, 5, 20, 60, and 300 seconds over the
   available sensor-temperature range.
6. Sky throughput: untracked clear-sky frames at 1, 5, 10, 20, and 30 seconds,
   gains 0, 100, 150, and 189, with the production lens at its production
   aperture. Record SQM or calibrated zenith surface brightness if available.
7. Fixed pattern: retain all individual bias, dark, and flat frames so PRNU,
   DSNU, hot pixels, column offsets, and amp glow can be measured separately.

## Analysis Outputs

The analysis should produce versioned machine-readable values for:

- native RAW16 packing and white level;
- bias ADU as a function of offset and gain;
- electrons/ADU and read-noise electrons as functions of gain;
- input-referred saturation and linearity residuals;
- dark-current median, DSNU, and hot-pixel distribution versus temperature;
- stable spatial bias/column structure and PRNU;
- magnitude-zero system electron rate and sky electron rate for the installed
  lens, passband, and site.

Use differenced frame pairs for temporal variance so fixed-pattern structure is
not incorrectly counted as read or photon noise. Do not average the source
frames before analysis.
