# 2026-07-22 ASI676 Hardware Session

This document records the initial operator-verified hardware and SDK evidence
for the installed ASI676MC and ASI676MM. It separates SDK-reported capability,
operationally verified still/RAW16 modes, and behavior that still requires
controlled calibration. The machine-readable session summary and per-capture
evidence are under
[`asi676/evidence/2026-07-22`](asi676/evidence/2026-07-22).

## Profile

| Property | ASI676MC | ASI676MM | Evidence |
| --- | --- | --- | --- |
| Serial | `290750012a020900` | `1624cd1226020900` | SDK V1.41 |
| Active image | 3552 x 3552 | 3552 x 3552 | SDK and RAW16 captures |
| Pixel pitch | 2.0 um | 2.0 um | SDK V1.41 |
| Reported ADC depth | 12 bit | 12 bit | SDK V1.41 |
| Native still formats | RAW8, RGB24, Y8, RAW16 | RAW8, RAW16 | SDK V1.41 |
| CFA | RGGB | None | SDK report; parity structure observed in RAW16 |
| Supported bins | 1, 2, 3, 4 | 1, 2, 3, 4 | SDK and operational captures |
| Gain range | 0-600 | 0-600 | SDK V1.41 |
| Exposure range | 32 us-2000 s | 32 us-2000 s | SDK V1.41 |
| Offset range | 0-200 | 0-200 | SDK V1.41 |
| Cooler/shutter | None | None | SDK V1.41 |

The SDK's `ElecPerADU` values are retained as manufacturer/SDK provenance and
are not a measured conversion gain. The capture utility used for this session
read camera properties before applying requested controls, so the value in each
retained sidecar describes the prior camera state and must not be associated
with that sidecar's gain. The reviewed successor utility refreshes the property
after applying controls; controlled photon-transfer measurements are still
required for either installed camera.

## Verification Status

| Component | Status | Evidence |
| --- | --- | --- |
| SDK identity and capability | Hardware verified | SDK 1.41 probes keyed by physical serial |
| Full-frame dimensions and packing | Hardware verified | 3552 x 3552, 7104-byte rows, 25,233,408-byte payloads |
| ASI676MM full-frame container mapping | Provisional operator-verified evidence | Tested bin-1 samples are exact 12-bit values shifted left four bits |
| ASI676MC full-frame container mapping | Unresolved | Tested bin-1 samples occupy every low-nibble residue and are not an exact left shift |
| ASI676MC CFA structure | SDK reported, parity structure observed | SDK reports RGGB; uncontrolled captures cannot independently assign parity positions to colors |
| Normal bin dimensions | Hardware verified | Bins 2/3/4 return 1776/1184/888 square tightly packed RAW16 frames |
| Normal bin aggregation | Partially hardware verified | Output levels remain comparable across bins; exact averaging and noise behavior need flats |
| ASI676MC mono-bin | Operationally verified at bin 2 | Applied control reports `1`, layout becomes Mono16, and parity response is flat |
| Hardware-bin-2 | Operationally verified on both cameras | Applied control reports `1`; timing and levels differ from normal bin 2 |
| Gain 179/180 behavior | Operationally verified only | Four frames per gain and camera; no controlled transition or conversion-gain fit |
| Bias, dark, flat, and defect maps | Uncalibrated | Covered and uniform-source datasets have not been collected |
| Installed optics and orientation | Unverified | No focus, dark-sky, flat, or astrometric dataset |

Capability metadata is not treated as operational proof. In particular,
normal binning, MC mono-bin, and hardware-bin-2 remain separate modes in the
evidence and must remain separate in a future acquisition adapter.

## 2026-07-22 Session

The standalone `tools/asi-capture` utility and official ZWO ASI Camera SDK
V1.41 ARMv8 library were staged under `~/asi-capture-v1.41` on
`allsky01`. The utility source deployed for the evidence session had SHA-256
`da2ab5b4c2562469889d39d8e7dc067862bcd872d517cea4c696d47a002b0bdc`.
The binary SHA-256 is
`9c263e3fbfd6bb4f9f476771158bb57e4ebf5d7c7a2a8b5d4888d98da1c19872`.
The post-session source fix that refreshes gain-dependent camera properties has
SHA-256 `a7924216da89ce3b15d15100f527c4809f02512b7c4f1afe4e482808747a138d`.
Its ARM64 validation binary has SHA-256
`aa2a5d63871ac35d77a843f7f018603e5066e8859fdd6d0c7411bccd5b4ee1e1`.
Sequential gain-0 and gain-82 validation captures reported `ElecPerADU` as
2.59 and 1.0076 respectively, matching the newly applied control rather than
the prior run. Their profiles and sidecars are retained under
[`successor-validation`](asi676/evidence/2026-07-22/successor-validation), and
`SHA256SUMS.successor-raw16` binds the two host-retained raw inputs.
`get_throttled` remained `0x0` after this successor check.

The host was Debian 13 ARM64 on a four-core Cortex-A76 with 17,006,182,400
bytes of memory. The two ASI676 cameras were each attached directly through a
different 5 Gbit/s USB 3 root controller. The ASI120MM Mini observed during
inventory remained on USB 2 and was not captured as part of this session.

The operator identified the installed supply as 5 V / 6 A. Before reboot,
`get_throttled=0x50000` retained historical under-voltage/throttle flags while
all current-state bits were clear. The host booted at `2026-07-22 12:29:11`
local time. Every post-reboot checkpoint, including after all captures, returned
`get_throttled=0x0`. Host temperature was 41.7-42.8 C at recorded checkpoints;
capture sidecars report sensor temperatures of 36.0-36.8 C. These observations
are clean bounded-session evidence, not sustained power or thermal acceptance.

The session captured 43 untouched RAW16 frames between
`2026-07-22T19:50:03.525Z` and `2026-07-22T19:54:36.036Z`:

- one full-frame gain-zero smoke frame per camera;
- four full frames per camera at gains 0, 82, 179, and 180;
- one normal frame per camera at bins 2, 3, and 4, gain 82;
- one ASI676MC bin-2 mono-bin frame; and
- one hardware-bin-2 frame per camera.

All captures used RAW16, the 32 us minimum exposure, offset 1, flip 0, and USB
bandwidth control 40. The 21 ASI676MM frames total 445,965,440 bytes and the 22
ASI676MC frames total 452,273,792 bytes. All 898,239,232 source bytes remain at
`~/asi-capture-v1.41/captures/asi676-profile-20260722T194734Z`.
They are checksum-bound on the capture host but are not in a durable shared
archive, so raw-derived conclusions in this document are operator-verified and
cannot be independently reproduced from the repository alone.

## Gain Observations

The following ranges include the initial gain-zero smoke frame. They describe
the uncontrolled scene and camera behavior only.

| Camera | Gain | Frames | Mean container ADU | Median container ADU | Observed maximum |
| --- | ---: | ---: | ---: | ---: | ---: |
| ASI676MC | 0 | 5 | 236.60-245.84 | 162 | 18,560 |
| ASI676MC | 82 | 4 | 518.17-526.14 | 324 | 47,216 |
| ASI676MC | 179 | 4 | 1373.38-1422.43 | 756-800 | 65,534 |
| ASI676MC | 180 | 4 | 1369.28-1395.49 | 688-729 | 65,534 |
| ASI676MM | 0 | 5 | 390.16-395.09 | 176 | 14,512 |
| ASI676MM | 82 | 4 | 907.42-923.25 | 352 | 36,976 |
| ASI676MM | 179 | 4 | 2382.34-2410.27 | 880-912 | 65,520 |
| ASI676MM | 180 | 4 | 2424.13-2442.08 | 880 | 65,520 |

The MC gain-179 and gain-180 groups contain sparse samples in the top four
container codes. The MM groups reach 65,520, which is the 12-bit maximum shifted
left four bits. These high-gain open-scene frames therefore cannot establish an
unclipped response curve. The measurements do not establish a high-conversion-
gain transition; covered pairs and uniform flats are required.

## RAW16 Mapping

The raw byte-order and low-bit analysis used untouched little-endian SDK buffers
and the checksum-reporting `tools/asi-capture/analyze_raw16.py` script. Exact
source-relative paths and hashes are recorded in `session.json`. The analyzed
ASI676MM bin-1 frames at gains 0 and 180 contain only low-nibble residue zero.
The named gain-180 frame contains exactly 4096 distinct container codes,
consistent with:

```text
container_adu = native_12_bit_adu << 4
```

This is provisional for the tested still/RAW16 path and settings, not a claim
about every camera mode.

The named ASI676MC bin-1 frames at gains 0 and 180 contain every low-nibble
residue. The gain-180 frame contains 7811 distinct container codes, so a
simple 12-bit left shift would be incorrect. The MC mapping requires controlled
packing and RAW8-relation measurements before a virtual or physical adapter
assigns an effective ADC transfer.

Normal ASI676MM bin 2 uses low-nibble residues 0, 4, 8, and 12; hardware-bin-2
returns only residue zero. MC normal, mono-bin, and hardware-bin-2 captures each
use all low-nibble residues, with different distributions. Binning mode is
therefore part of sample-layout provenance.

## Binning Observations

| Camera/mode | Dimensions | Bytes | Mean ADU | Median ADU | Exposure status | Download |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| MC normal bin 2 | 1776 x 1776 | 6,308,352 | 517.82 | 330 | 418.81 ms | 37.90 ms |
| MC normal bin 3 | 1184 x 1184 | 2,803,712 | 519.80 | 330 | 418.85 ms | 31.24 ms |
| MC normal bin 4 | 888 x 888 | 1,577,088 | 521.05 | 332 | 418.88 ms | 29.91 ms |
| MC mono-bin 2 | 1776 x 1776 | 6,308,352 | 519.48 | 288 | 419.12 ms | 49.02 ms |
| MC hardware-bin 2 | 1776 x 1776 | 6,308,352 | 430.95 | 324 | 286.05 ms | 4.70 ms |
| MM normal bin 2 | 1776 x 1776 | 6,308,352 | 921.03 | 344 | 418.87 ms | 37.59 ms |
| MM normal bin 3 | 1184 x 1184 | 2,803,712 | 922.04 | 343 | 418.79 ms | 29.21 ms |
| MM normal bin 4 | 888 x 888 | 1,577,088 | 913.67 | 338 | 418.83 ms | 23.34 ms |
| MM hardware-bin 2 | 1776 x 1776 | 6,308,352 | 662.86 | 304 | 291.18 ms | 4.37 ms |

The per-capture sidecar is authoritative for every row stride and byte count.
Normal binning retained approximately comparable levels across factors rather
than scaling by bin area. Hardware-bin-2 changed both level and still-mode
timing. Single uncontrolled frames cannot establish whether a mode sums,
averages, clips, or applies another transfer.

The ASI676MC mono-bin parity means were `519.482, 519.543, 519.390, 519.521`,
while normal and hardware binning retained a strong four-position pattern.
This verifies the SDK control's intended CFA-loss behavior for bin 2 without
calibrating channel response.

## Evidence and Reproduction

The repository retains all 22 original SDK profiles and 43 original sidecars,
plus two profiles and two sidecars from successor validation. They validate
against `hvo-asi-sdk-profile-v1` and `hvo-asi-raw16-sidecar-v2`, respectively.
[`SHA256SUMS.raw16`](asi676/evidence/2026-07-22/SHA256SUMS.raw16) binds each
original raw file to its source-relative path under the retained session root;
`SHA256SUMS.successor-raw16` binds the two successor checks. The checksum
manifests do not make the machine-local files immutable or independently
available. `SHA256SUMS.metadata` binds the committed profiles, sidecars, and
session summary. Raw payloads are intentionally not stored in Git.

The full camera serials are intentionally retained because they are the stable
physical-binding keys for this evidence. Hostname and relative source location
are retained for reproducibility. The evidence contains no credentials, tokens,
email addresses, private keys, or IP addresses.

Camera indices in these commands were discovery-session observations, not
physical identity. Re-discover the index and bind evidence by serial before a
future run.

```bash
~/asi-capture-v1.41/asi-capture \
  --output-dir ~/asi-capture-v1.41/captures/session-name \
  --camera-index INDEX --exposure-us 32 --gain 82 --offset 1 \
  --bin 2 --count 1
```

Add `--mono-bin` or `--hardware-bin` only as separate, explicitly identified
experiments.

## Remaining Calibration

1. Capture covered minimum-exposure pairs across gain and offset for bias,
   fixed-pattern structure, effective packing, and temporal read noise.
2. Capture stable uniform flat pairs across signal levels for conversion gain,
   linearity, PRNU, saturation, and exact normal/mono/hardware bin semantics.
3. Capture covered exposure and temperature series for dark current, DSNU, hot
   pixels, and glow.
4. Identify and focus each installed lens, then collect flats and astrometrically
   matched stars for vignetting, obstruction, projection, orientation, and PSF.
5. Measure sustained acquisition through the future production `ICameraModule`;
   this short standalone session is not CameraAgent throughput acceptance.
