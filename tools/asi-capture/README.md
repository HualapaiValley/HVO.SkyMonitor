# ASI RAW16 Capture

`asi_capture.cpp` is a dependency-minimal ZWO SDK utility for hardware
characterization. It writes untouched SDK RAW16 bytes and a JSON sidecar with
camera properties, requested and actual controls, timing, temperature, and
non-destructive sample statistics.

The utility intentionally does not debayer, stretch, stack, compress, or convert
the camera buffer. The sidecar interprets each two-byte sample as little-endian
only for statistics; the raw file remains byte-for-byte SDK output so packing can
be verified independently.

Build against the official ZWO ASI Camera SDK V1.41 ARMv8 library:

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

Use `docs/calibration/asi174mm-characterization.md` for the full calibration
matrix. ASI178MC measurements provide useful SDK and sensor-behavior evidence,
but its Sony IMX178 response must not be substituted for ASI174MM calibration.
