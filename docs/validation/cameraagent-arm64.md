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
