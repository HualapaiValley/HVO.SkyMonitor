# CameraAgent ARM64 Validation

## Intended Host Preflight

On 2026-07-13, `allskycamera01` (`192.168.1.5`) was confirmed reachable by
SSH and Docker context. The host reported:

- Linux `aarch64`, four CPUs, and 3,980,189,696 bytes of memory;
- Docker Engine `29.6.1`;
- approximately 51 GB free on the root filesystem;
- no existing containers; and
- the ZWO ASI178MC attached on USB 3.

No CameraAgent image was built or deployed during this preflight. The idle host
temperature was approximately 84.7 C, `vcgencmd get_throttled` returned
`0xe0008`, and the ARM clock was approximately 600 MHz despite a configured
1.5 GHz frequency. This indicates an active soft temperature limit and prior
frequency-capping/throttling, so deployment and performance observations would
have added thermal load and produced misleading results.

Repeat the intended-host validation only after cooling and power are stable.
Record image build/start time, health, restart recovery, graceful shutdown,
capture cadence, CPU, memory, storage growth, temperature, and throttle flags.
Do not derive acceptance thresholds from the thermally constrained preflight.

## Development Fallback

The `devpi5` Docker context may be used for ARM64 VirtualSky container startup
and functional testing. It is not a substitute for final performance and
physical-camera characterization on `allskycamera01`.

### 2026-07-13 VirtualSky Run

`devpi5` reported Linux `aarch64`, four CPUs, 17,006,903,296 bytes of memory,
Docker Engine `29.5.0`, no throttle flags, and approximately 63 C before the
validation run. Its root filesystem was initially full; pruning only inactive
Docker build cache recovered approximately 56 GB without stopping its existing
containers.

An on-device SDK build reached `dotnet publish` but then lost SSH connectivity.
The host recorded an orderly shutdown and reboot at that time, retained no
persistent prior-boot journal, and reported no throttle flags after restart. Do
not attribute that reboot to temperature, memory, or power without additional
host evidence. To avoid repeating the heavy build, the application was then
published locally for `linux-arm64` as framework-dependent output and copied
into the ARM64 ASP.NET runtime image on `devpi5`.

The isolated validation container used `cameraagent.sample.json`, dedicated
named volumes, and host port `15130`. LogicHost was intentionally unavailable
to exercise durable outbox retention. Observations were:

- the image reported `arm64` and 287,643,745 bytes;
- `/health` returned healthy for self, identity database, camera configuration,
  and disk pressure;
- ten captures followed the configured 25-second cadence without drift in the
  recorded timestamps;
- each storage root indexed 40 artifacts: raw, combined, preview, and annotated
  preview for every capture;
- the unavailable central endpoint left all 40 upload manifests durable in the
  local outbox;
- after 3 minutes 52 seconds, `docker top` reported 5.7 percent process CPU and
  250,912 KiB RSS;
- temperature was 63.1 C, the ARM clock was approximately 2.4 GHz, and
  `get_throttled` remained `0x0`;
- graceful stop logged `Capture processing channel drained` and exited with
  code 0; and
- restart preserved the 40 prior index/outbox entries, returned healthy, and
  increased both to 48 after two more captures.

The validation container, image, and dedicated volumes were removed afterward.
These are descriptive development-host observations, not acceptance thresholds
and not final full-resolution or physical-camera performance evidence.
