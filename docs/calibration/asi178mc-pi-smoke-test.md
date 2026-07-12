# ASI178MC Raspberry Pi Smoke Test

On 2026-07-12, the ZWO ASI Camera SDK V1.41 ARMv8 library and the repository's
`tools/asi-capture` utility were deployed natively to `allskycamera01` under
`/home/roys/asi-capture`. No system packages, Docker images, services, or global
libraries were installed.

## Camera

- Model: ZWO ASI178MC
- Serial: `350f500522000900`
- USB: USB3 camera on a USB3 host
- Native image: 3096 x 2080, RG Bayer, RAW16
- SDK-reported sensor depth: 14 bits
- Pixel size: 2.4 um
- Test temperature: approximately 35 C
- Offset: 10
- High-speed mode: disabled
- USB bandwidth control: 40

The test camera is not an ASI174MM and its IMX178 calibration values must not be
used as ASI174 sensor constants.

## Capture Matrix

Two full-resolution untouched RAW16 frames were collected for each combination:

| Exposure | Gains |
| --- | --- |
| 0.1 s | 0, 150, 300 |
| 1 s | 0, 150, 300 |
| 20 s | 0, 150, 300 |

Pi source data:

`/home/roys/asi-capture/captures/matrix-20260712T0830Z`

Downloaded working copy:

`/tmp/opencode/asi178mc-matrix/matrix-20260712T0830Z`

Every `.raw16` file is 12,879,360 bytes. The adjacent JSON records camera
properties, requested and actual controls, timing, temperature, and basic
statistics.

## Initial Findings

- RAW16 is little-endian and occupies the full 16-bit container range. It is not
  a simple right-aligned 14-bit value or an exact `value << 2` encoding.
- All low-byte values occur, although a strong modulo-four code bias remains.
- The observed floor is 4 and observed ceiling is 65534.
- Median background increases with both exposure and gain as expected.
- Twenty-second medians at gains 0, 150, and 300 were approximately 152, 601,
  and 3227 container ADU respectively.
- Near-container clipping was sparse even at 20 seconds and gain 300.
- Long exposures reveal persistent spatial glow, edge/optical obstruction,
  fixed-pattern structure, and hot pixels in addition to the star field.
- Repeated short high-gain sequences contain occasional large whole-frame level
  excursions. They are not consistently first-frame settling and should be
  investigated before estimating stationary read noise from those frames.

Preview JPEGs are display derivatives only. All quantitative analysis must use
the untouched RAW16 files.

## Repeating A Capture

```bash
ssh roys@192.168.1.5 \
  "/home/roys/asi-capture/asi-capture \
    --output-dir /home/roys/asi-capture/captures/session-name \
    --exposure-us 20000000 --gain 150 --offset 10 --count 4"
```

For photon-transfer or read-noise measurements, use controlled flat or covered
bias/dark pairs. The current open-sky frames characterize operational behavior
and system structure but cannot isolate sensor conversion gain or read noise.
