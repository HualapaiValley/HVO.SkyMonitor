# Sunrise-to-sunrise product POC (#1134)

This deliberately isolated harness consumes actual VirtualSky output. It is not
a production scheduler, calendar migration, storage contract or encoder adapter.
The operator review checkpoint remains open until disposition is recorded.

The rendering input for the first complete run is #1131 prototype
`37338b3866248200b3aa0751c3457177766ca823`, based on target
`cb5d7e3f63c69e85f0f079ef63c687fbb7e5f818`. `Pinned/` contains byte-for-byte pure
composer sources from #993 checkpoint
`75185e7e794b1028f28ac3f09e7125c14a1b4e98`; its scheduling and product-store code
are not used. The keogram wrapper samples one source column at a time and places
it on a planned sunrise-period axis. The trail folds one source at a time.

The fixed scenario is October 12, 2026, Hualapai coordinates 35.347 N / 113.878 W,
elevation 0 m, America/Phoenix. `AstronomyEngineSolarEventCalculator` resolves
both sunrises. The period is 24 hours 50.2821409 seconds. The starting-sunrise
date labels it October 12. Historical archive compatibility is not implemented.

Acquisition timestamps use the actual run clock. Each independent camera module
receives an explicit celestial start through the supported fixed-scene option;
those starts advance across the sequence, and stars move during each exposure.
The recorded `VirtualExposure` retains both clock intervals. Reconstruct pixels
using the exact per-source configuration, exposure, gain, seed and source UTC;
acquisition/projection timing fields are expected to differ on repeat runs.

The 640-square RGGB sensor uses the provisional ASI676 12-bit response. It is a
reduced virtual rig, not a physical sensor characterization. Display is linear
Bayer reconstruction, black 64, white 4095, and fixed gamma 2.2; matched linear
PNGs use gamma 1. No image uses percentile auto-stretch. The explicit open-loop
sequence exposure policy is `clamp(2500 / scalarSkyRate, 1 microsecond, 20 seconds)`
at gain 0. The recipe is not a delivered automatic-exposure feature.

One slot per minute spans the entire `[start,end)` period. The first 15 slots
are intentionally unscheduled; 30 minutes of captures beginning at site 01:30
are deliberately omitted; the final 15 minutes lack captures. There are 1,381
real sources among 1,441 slots. The final slot represents 50.282 seconds. Gray
keogram patterns and black video slates are missing-data annotations, never
invented sky pixels. This completed-period example has no quality exclusions or
unelapsed slots; those states are explicitly distinct from its missing data.

Only exposures fully contained in the astronomical dark window, with recorded
midpoint Sun altitude <= -18 degrees, enter the trail. The first run selects
557 frames and 11,140 exposure seconds. One-minute cadence with at most 20-second
exposures leaves unsampled time even outside the deliberate outage. No missing
arcs are interpolated.

## Reproduce the revised sample set

Use SDK 10.0.401. The full catalog must already be available and pass the normal
read-only snapshot resolver (119,625 rows, database SHA-256
`b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2`). No installer
or installed-instance mutation is involved. Use a new output root for each run.

```bash
dotnet run --project tools/HVO.SkyMonitor.VirtualSkyPoc --configuration Release -- samples /private/evidence/samples /verified/catalog/root
dotnet run --project tools/HVO.SkyMonitor.VirtualSkyPoc --configuration Release -- day /private/evidence/day /verified/catalog/root
dotnet run --project tools/HVO.SkyMonitor.VirtualSkyPoc --configuration Release -- details /private/evidence/details /verified/catalog/root
python tools/HVO.SkyMonitor.VirtualSkyPoc/verify.py /private/evidence/day /private/evidence/samples --details /private/evidence/details
python tools/HVO.SkyMonitor.VirtualSkyPoc/present.py /private/evidence/day /private/evidence/samples /public/review /private/encoder/bin --details /private/evidence/details
python tools/HVO.SkyMonitor.VirtualSkyPoc/serve.py /public/review --bind 192.168.2.45 --port 8094
```

Presentation dependencies used for the first run are Pillow 12.3.0, matplotlib
3.11.2 and numpy 2.5.3 in an isolated Python environment. FFmpeg and ffprobe come
from the Linux static-build provider linked by the official FFmpeg download
page. The retained archive is `ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz`, verified
against the release asset SHA-256
`72509b592457c9dca4aeaa59cee3b8ddca11ba2d71d70800bb2ad0f4cfe129a2`.
The actual encoder reports `n9.0.2-22-g46d8f462ee-20261001`; binary hashes, complete
version output, commands, ffprobe metadata and full-decode results accompany the
videos. This is POC-only encoder qualification, not #1130 production delivery.

Daily playback uses 24 fps (nominal 1440x), hourly 6 fps (360x). Missing slots
keep the same playback duration as captured slots. `source-index.csv` maps each
slot to exact source time, acquisition time, exposure and daily playback index;
`video-evidence.json` supplies the hourly offset. The civil 18:00–19:00 sample
contains the sunrise-anchored captures at :23.1586721 each minute.

## Evidence and bounds

Samples retain raw files privately. Each sequence raw frame is hashed, measured,
converted and released; PNGs and settings remain on disk for verification and
video encoding. The harness holds one current camera frame plus bounded product
buffers and the metadata index, never a full day's image stack. Raw resource
records report elapsed time, process CPU, allocations and peak working set.
This is a bounded sample workload, not a canonical camera throughput claim.

`verify.py` independently checks raw checksums, active-aperture means/clipping,
PNG pixel hashes, clock separation, planned-period slots, dark eligibility,
the complete streaming maximum trail and five bilinearly sampled keogram
columns. Presentation performs ffprobe frame-count/codec/dimension checks and a
complete FFmpeg decode. The no-login LAN server serves only exact manifest paths,
supports video byte ranges, and has no directory browsing or runtime-state root.

The first incomplete sample attempt is retained separately: a fractional
duration round-trip check rejected the following-sunrise capture. The rendering
prototype fixes that check without changing the exposure ticks. The replacement
sample batch includes all 66 matched cases. Rejected samples remain evidence.

## Operator-requested revision

The operator accepted the concept with visual changes: correct East/West, add
true-scale Sun and Moon/phase, and demonstrate a per-image panorama. On
2026-10-02 the operator [accepted the revised POC and date label](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1134#issuecomment-5959367671)
at http://192.168.2.45:8095/. October 12 means sunrise October 12 through
sunrise October 13. Review-v1 is retained unchanged. Visual acceptance does not
replace renderer qualification, independent review or production adoption.

The revised renderer is pinned to
`33712845b6b31c9382b24bb8b0eb8ac63723b486`. Revised matched samples use the
upward-looking rig (`horizontalFlip: true`), with North up, East left and West
right. `renderSolarSystemDisks: true` adds actual Sun/Moon light before exposure,
noise, gain and clipping. The geometric disks use shared Astronomy topocentric
position/distance, true angular scale, and geocentric lunar phase; the bright
limb points toward the Sun. Positions advance with the source clock. Disk
geometry is sampled at the exposure midpoint; no within-exposure disk trail,
texture, refraction, occultation, eclipse, flare or bloom is claimed.

`details` produces four additional 1.5-degree perspective-field sensor captures:
the Sun, the observing-period crescent, and separately dated later lunar phases.
They are separate VirtualSky captures, not enlarged disks pasted into all-sky
sources. Exposure targets 1,500 electrons per illuminated projected disk pixel
and is capped at 1 second to respect the existing stellar temporal budget.
Failed longer-exposure/over-budget attempts remain excluded evidence.

```bash
dotnet run --project tools/HVO.SkyMonitor.VirtualSkyPoc --configuration Release -- details /private/evidence/details /verified/catalog/root
python tools/HVO.SkyMonitor.VirtualSkyPoc/verify.py /private/evidence/day /private/evidence/samples --details /private/evidence/details
python tools/HVO.SkyMonitor.VirtualSkyPoc/present.py /private/evidence/day /private/evidence/samples /public/review-v2 /private/encoder/bin --details /private/evidence/details
```

Every policy sample also emits a 1440×360 panorama and mapping record. It uses
the shared calibrated projector to map azimuth 0–360 degrees and altitude 90–0
degrees into the actual source, then bilinearly resamples its declared fixed
transfer display. Four valid source samples are required; unavailable samples
are black, without extrapolation. The axes are N–E–S–W–N and zenith-to-horizon;
zenith stretching is inherent to this projection. Independent verification
checks 108 panorama pixels against the pinned equidistant calibration.

Revised runs retain launch-time harness source, assembly SHA-256 values and
embedded build identities automatically. Use a new empty directory for each
run. Build to completion before launching a run; interrupted preparation runs
are preserved and excluded rather than represented as complete sequences.
`measure` compares legacy illumination, SolarDriven illumination without disks,
and SolarDriven with disks, using five warm-up and thirty measured captures per
configuration at day and night. CPU, elapsed/p95, allocation, peak working set,
raw output bytes, disk writes and backlog boundaries are explicit. Measurements
on a shared host are workload evidence, not a hardware qualification.
