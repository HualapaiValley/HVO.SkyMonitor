# ZWO ASI CameraAgent Runtime

The `ZwoAsi` CameraAgent module uses the official ZWO ASI Camera SDK V1.41 at
runtime. The repository contains C ABI declarations derived from the V1.41
header, but no vendor library, header, udev rule, installer, or other vendor
binary. Obtain V1.41 from the official ZWO Product SDK page:
https://www.zwoastro.com/software/product-sdk/.

## Verified Artifacts

| Artifact | Platform | SHA-256 | Status |
| --- | --- | --- | --- |
| `ASI_Camera_SDK.zip` | official V1.41 download | `c6d59bf2de5b807f13d60d10b035e10dff31aeaa43391c6cd4782242126775ec` | downloaded and verified; not redistributed |
| `ASI_linux_mac_SDK_V1.41.tar.bz2` | nested Linux/macOS archive | `488f5df7476494faa41ecbc1c6399f13cb5f7495a2e34814c098094261ee6f03` | extracted and verified; not redistributed |
| `ASICamera2.h` | platform-neutral V1.41 header | `af6ab82e66905b3a0f3313e1e82ccb4fedde272eaa64744588a1be8103f9cf0c` | pinned |
| `libASICamera2.so.1.41` | Linux ARM64 | `3ecf511979ed571131e7d7f4a467112aba21940b4dc91d63bd109b9683bf67c4` | installed and verified |
| `libASICamera2.so.1.41` | Linux x64 | `d1de4a5ab85c8cafbddfad9c593bbba515890d3adf20c1ca44dafcf15f2775ce` | installed and verified |
| ZWO SDK license | V1.41 distribution | `98ad1c18048bfdabc8463740ac36a8d8cd710bdc3102ac6c22978ec50056e5a2` | pinned |
| `99-asi.rules` | installed Linux udev rules | `a64c24317f154d5b074e9fac9249f4a5d905153b22f1e74aac4c5f8fe1443f36` | installed and verified |

The first physical deployment was verified on Debian 13 ARM64 with Docker
29.6.2. Three public capability profiles were observed: ASI676MC 3552x3552
color RGGB, ASI676MM 3552x3552 mono, and ASI120MM Mini 1280x960 mono. All report
12-bit sensors. The ASI676 cameras advertise bins 1/2/3/4; the ASI120MM Mini
advertises bins 1/2. Separate retained ARM64 evidence establishes the ASI178MC
profile below. Linux x64 was subsequently verified with the separately hashed
V1.41 x64 library on Ubuntu 24.04. The vendor SDK remains operator-supplied and
is not committed to or redistributed by this repository.

The production adapter has exactly two supported physical modes:

| Expected model | Full-frame bin-1 RAW16 | Pixel pitch | ADC depth | Stride | Stored declaration |
| --- | --- | ---: | ---: | ---: | --- |
| `ASI676MC` | 3552 x 3552 color RGGB | 2.0 um | 12 bit | 7104 bytes | `OpaqueContainerV1`, levels 0..65535 |
| `ASI178MC` | 3096 x 2080 color RGGB | 2.4 um | 14 bit | 6192 bytes | `OpaqueContainerV1`, levels 0..65535 |

Both are tightly packed, little-endian, full-frame, bin-1 profiles with RGGB
origin `(0,0)`. Other models, ROI/bin modes, mono modes, and decoded container
mappings are not supported by this adapter release.

The committed physical samples select an explicit empty v2 processing graph.
Raw ingress and manifest lineage remain durable, but no processing recipe is
allowed to interpret `OpaqueContainerV1` until a decoded stored-code transfer is
established. Add only processing steps that explicitly support that declaration.

## Host Installation

Install the operator-reviewed SDK files rather than downloading them during an
application or image build:

```bash
sudo install -o root -g root -m 0644 ASICamera2.h /usr/local/include/ASICamera2.h
sudo install -o root -g root -m 0755 libASICamera2.so.1.41 /usr/local/lib/libASICamera2.so.1.41
sudo ln -sfn libASICamera2.so.1.41 /usr/local/lib/libASICamera2.so
sudo install -o root -g root -m 0644 99-asi.rules /etc/udev/rules.d/99-asi.rules
sudo ldconfig
sudo udevadm control --reload-rules
sudo udevadm trigger
```

Verify the reviewed artifacts before starting CameraAgent:

```bash
sha256sum /usr/local/include/ASICamera2.h \
  /usr/local/lib/libASICamera2.so.1.41 \
  /etc/udev/rules.d/99-asi.rules
ldd /usr/local/lib/libASICamera2.so.1.41
```

Install `libusb-1.0-0`; the CameraAgent container installs this runtime package
but deliberately does not install the SDK. Ensure the service account is in the
group selected by the reviewed vendor udev rule, then log out and back in after
changing group membership. Keep Linux USBFS memory at 200 MB for the verified
multi-camera host and confirm the active value:

```bash
cat /sys/module/usbcore/parameters/usbfs_memory_mb
```

Set `usbcore.usbfs_memory_mb=200` through the host's persistent kernel command
line or bootloader configuration when the value is lower, then reboot. Do not
replace the reviewed udev file with an unverified permissive rule.

## Configuration

Use `cameraagent.zwo-asi676mc.sample.json` or
`cameraagent.zwo-asi178mc.sample.json` as the matching private runtime
configuration basis. The committed samples store only
`libraryPathEnvironmentVariable` and `cameraSerialEnvironmentVariable` names.
The named runtime values are sensitive identifiers: never place a physical
serial or private deployment path in Git, logs, telemetry labels, exception
text, screenshots, or shell history. Existing repository hardware evidence
predates this environment indirection and may contain serial evidence; do not
describe repository history as serial-free.

The library environment variable must resolve to an absolute V1.41 library path,
and the serial environment variable must resolve to exactly 16 hexadecimal
characters. The module performs no environment reads, SDK loading, USB
enumeration, or camera opening during construction or configuration preflight.
Preflight first resolves the exact configured `ExpectedModel` to one of the two
profiles above and rejects unsupported or cross-paired model/layout declarations.
Initialization resolves the variables, loads the library with
`NativeLibrary.Load`, resolves every required export before use, checks V1.41
major/minor compatibility, discovers by private serial, normalizes only an exact
native `ZWO ` model prefix when comparing against the selected profile, verifies
the native camera against that profile, then opens and initializes it.

The SDK writes RAW16 directly into CameraFrame-owned memory. The adapter does
not transform, shift, debayer, or otherwise touch those bytes. Both physical
rigs declare `OpaqueContainerV1` with `StoredContainer` levels 0 through 65535
because neither ADC-to-16-bit-container mapping is established for production
decoding. Processing that
requires decoded sample codes rejects this transform; raw persistence and
reconstruction preserve the exact container bytes. The 2026-07-22 uncontrolled
hardware evidence shows all low-nibble residues and does not establish left- or
right-alignment. See
`docs/calibration/asi676-hardware-session-20260722.md`; controlled RAW8/RAW16
packing evidence is still required before promoting that declaration. Existing
ASI178 observations likewise do not justify replacing its physical opaque
declaration with the virtual profile's `FullRangeScaledV1` simulation mapping.

Acquisition timestamps are module-observed command and status boundaries. They
do not claim exact sensor start/end timing. `ASIGetDataAfterExp` is synchronous;
the module retains exclusive camera and destination-buffer ownership until the
native call returns and cannot safely cancel an in-flight call.

## Docker

The container needs the USB bus read/write and the reviewed library mounted at
the absolute path supplied by the named environment variable. A direct run
equivalent using a mode-0600, history-safe environment file is:

```bash
docker run --rm \
  --env-file /path/to/private-zwo.env \
  --mount type=bind,source=/dev/bus/usb,target=/dev/bus/usb \
  --mount type=bind,source=/usr/local/lib/libASICamera2.so.1.41,target=/usr/local/lib/libASICamera2.so.1.41,readonly \
  --mount type=bind,source=/path/to/private-cameraagent.json,target=/app/cameraagent.json,readonly \
  hvo-cameraagent:latest
```

Use equivalent Compose bind mounts in production. Do not use `--privileged`;
the reviewed udev/group permissions and `/dev/bus/usb` bind are sufficient. A
container image can be shared across hosts because the SDK and serial-bearing
environment file remain host runtime inputs; committed configuration contains
only the environment-variable names.

## Hardware Smoke

The hardware test is excluded from normal Unit and Integration selection. Run
it only after independent review. Avoid inline assignments because they persist
in shell history. Use a root-readable environment file loaded by the service
manager, or enter the serial without echo and export it for the current shell:

```bash
export HVO_ZWO_SDK_LIBRARY=/usr/local/lib/libASICamera2.so.1.41
read -r -s -p 'ZWO camera serial: ' HVO_ZWO_CAMERA_SERIAL; printf '\n'
export HVO_ZWO_CAMERA_SERIAL
# Select exactly ASI676MC or ASI178MC for the attached camera.
export HVO_ZWO_EXPECTED_MODEL=ASI676MC
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release --filter 'TestCategory=Hardware'
unset HVO_ZWO_CAMERA_SERIAL
```

It selects the complete matching profile from `HVO_ZWO_EXPECTED_MODEL`, performs
one 100 ms full-frame RAW16 light capture, verifies model-specific dimensions,
stride, byte length, ADC depth, and opaque-container semantics, then disposes the
camera. The suite retains one discovered Hardware case. If any opt-in variable
is absent, MSTest reports the test inconclusive.
