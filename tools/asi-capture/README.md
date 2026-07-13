# ASI RAW16 Capture

`asi_capture.cpp` is a dependency-minimal ZWO SDK utility for hardware and
profile characterization. Each run writes a machine-readable SDK profile with
camera properties, supported bins and formats, and all control capability
ranges. Captures write untouched SDK RAW16 bytes and a JSON sidecar with the
profile reference, requested and actual controls, layout, timing, temperature,
and non-destructive sample statistics.

The utility intentionally does not debayer, stretch, stack, compress, or convert
the camera buffer. The sidecar interprets each two-byte sample as little-endian
only for statistics; the raw file remains byte-for-byte SDK output so packing can
be verified independently.

Build against the official ZWO ASI Camera SDK V1.41 ARMv8 library. The tested
layout places `ASICamera2.h` under `include/` and `libASICamera2.so` under
`lib/`, both beside the utility. The native library requires the platform
`libusb-1.0.so.0` runtime.

Tested SDK artifacts:

- `ASICamera2.h`: SHA-256 `af6ab82e66905b3a0f3313e1e82ccb4fedde272eaa64744588a1be8103f9cf0c`
- ARMv8 `libASICamera2.so`: SHA-256 `3ecf511979ed571131e7d7f4a467112aba21940b4dc91d63bd109b9683bf67c4`
- ZWO SDK license: SHA-256 `98ad1c18048bfdabc8463740ac36a8d8cd710bdc3102ac6c22978ec50056e5a2`

SDK source: https://www.zwoastro.com/software/product-sdk/

```bash
g++ -std=c++20 -O2 -Wall -Wextra -Werror \
  -Iinclude asi_capture.cpp -Llib -lASICamera2 \
  -Wl,-rpath,'$ORIGIN/lib' -o asi-capture
```

Example:

```bash
./asi-capture --output-dir captures --exposure-us 20000000 \
  --gain 150 --offset 10 --count 4
```

Probe a camera without capturing a frame:

```bash
./asi-capture --output-dir captures/profile --profile-only
```

Capture an advertised binned mode while retaining the color-camera Bayer grid:

```bash
./asi-capture --output-dir captures/profile --exposure-us 100000 \
  --gain 150 --offset 10 --bin 2 --count 2
```

Capture the SDK's mono-bin mode for comparison. Mono-bin intentionally loses
the color filter-array layout and is only accepted with a bin factor above one:

```bash
./asi-capture --output-dir captures/profile --exposure-us 100000 \
  --gain 150 --offset 10 --bin 2 --mono-bin --count 2
```

The ASI178MC also exposes a separate hardware-bin-2 control. It is never implied
by `--bin 2`; request it explicitly for paired characterization:

```bash
./asi-capture --output-dir captures/profile --exposure-us 100000 \
  --gain 150 --offset 10 --bin 2 --hardware-bin --count 2
```

Capability metadata is not proof that a mode is operational. SDK 1.41 reports
the ASI178MC hardware-bin control as writable, but the tested still/RAW16 path
rejects enabling it. The utility fails without writing a frame in that case so
the unsupported combination cannot be mistaken for hardware-binned evidence.

For maximum-coverage binning, the utility trims a calculated odd post-bin row or
column before requesting the ROI. The ASI178MC specifically rejected its odd
bin-3 height; odd-width rejection has not been established. The returned ROI,
plus the derived packed RAW16 row stride and byte count in the sidecar, are
authoritative for the captured file. Parity means record the four 2x2 sample
positions so that Bayer-preserving and mono-bin output can be distinguished
without modifying the raw frame.

The SDK profile is capability evidence, not a calibrated virtual sensor or lens
model. Manufacturer values, measured buffer behavior, sensor response fits, and
installed-rig optics must retain separate provenance and verification status.

Profile documents use `hvo-asi-sdk-profile-v1`; capture sidecars use
`hvo-asi-raw16-sidecar-v2`. Their repository schemas are
`asi-sdk-profile-v1.schema.json` and `asi-raw16-sidecar-v2.schema.json`. Version
2 keeps the original numeric `bayerPattern` field and adds
`bayerPatternName`, requested-versus-applied mode controls, layout, and timing.

Use `docs/calibration/asi174mm-characterization.md` for the full calibration
matrix. ASI178MC measurements provide useful SDK and sensor-behavior evidence,
but its Sony IMX178 response must not be substituted for ASI174MM calibration.
