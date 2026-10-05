# Issue 1130 performance evidence

This isolated harness generates actual native-resolution VirtualSky images,
compares H.264 encoding profiles, measures interference with image processing,
and checks hourly stream-copy assembly. The operator accepted the three-frame
stack, proportional overlays, 180×/300× playback and independent edge/central
outputs. Historical investigation results and limitations are in `RESULTS.md`;
production-path measurements and concurrency limits are in [PRODUCTION.md](PRODUCTION.md).
the production path is configured through the [runbook](../../docs/runbooks/time-lapse-generation.md).

## Production-path measurements

Build with the repository's pinned SDK, for the measured host architecture:

```bash
dotnet publish tools/HVO.SkyMonitor.TimeLapseBenchmark/HVO.SkyMonitor.TimeLapseBenchmark.csproj \
  -c Release -r linux-arm64 --self-contained true -o /task/bin
```

`production-inputs OUTPUT CATALOG CASE` creates five source minutes of verified
raw captures. Cases are `mono3552`, `color3552`, `day3552` and `mono174` (the real
ASI174MM rig model). Every night sequence has 15 fresh 20-second exposures;
daytime has 60 fresh 37.514 ms exposures at five-second cadence. The full local
catalog, rig, actual celestial clocks, raw checksums and payload-free source
descriptions are recorded in `production.json`. Do not replace advancing images
with repeated copies when generating visual or throughput evidence.

`production INPUT OUTPUT MAX_DIMENSION Software|Nvidia 180|300 1|3` uses the
production rolling-stack traversal, raw SHA verification, display transfer and
overlays, bounded FFmpeg adapter, packet/full-decode checks, durable MP4
publication and output SHA verification. Results separate source reads,
rendering, qualification, total work, managed CPU, allocation and working set.
They exclude capture, host job databases and central object-store publication.
Use a fresh output directory for every trial.

`production-contend INPUT OUTPUT true|false` runs the real foreground
`ProcessingRecipeExecutor` encoded-preview recipe at five-second spacing,
after five warmups, for 30 operations. The background condition continuously
executes serial production videos in the same process. These repeated short
jobs are load only; they are not a longer observing period. Every foreground
JPEG must retain its checksum. This test excludes the complete CameraAgent
ingress/outbox and physical acquisition.

`production_runs.py INPUT_ROOT BINARY OUTPUT --mode edge|native|gpu|daytime|contention`
runs serial fresh trials and records child-inclusive CPU, aggregate process-group
RSS at 200 ms, sampled I/O, CPU quota and exact commands. `contention-video`
reruns only the background condition after a correction. Input subdirectories
are `mono3552`, `color3552`, `day3552` and `asi174-v2`. Keep builds, generation and
transfers off the measured host during trials; declare cache state. A successful
five-minute trial is not a measured full hour, and this harness is not full host
qualification.

## Inputs and clocks

`Program.cs generate OUTPUT CATALOG` renders 24 distinct images per sequence:
twelve 5-second capture intervals followed by twelve 20-second intervals. Each
sequence covers 300 source seconds and becomes a 5-second video at 60×.
Night exposures equal the intervals; daytime exposures are 1/5 ms and twilight
exposures are 20/100 ms. Exposure and capture cadence are independent facts.
Source/celestial/acquisition clocks, configurations, catalog and image checksums
are retained in the manifests. Synthetic geometry is not physical-camera
qualification. Monochrome pixels use an RGB JPEG carrier in this harness.

The operator's subsequent host split is explicit: CameraAgent creates a smaller
local-only video and never uploads timelapse videos. LogicHost independently
creates a larger video from received source images, using NVIDIA encoding when
available and qualified. Each host reuses its own compatible hourly segments
for daily output. The accepted defaults are 1280 maximum dimension on CameraAgent
and native resolution up to 4096 on LogicHost, with the existing Bayer pixel bound.

The fixed display transfer is black 64 / white 4095 / gamma 2.2. The preview is
encoded as JPEG quality 92 without resizing. Native inputs include 640-square,
1280-square and 3552-square mono/color, 1936×1216 mono and 3096×2080 color.

`Program.cs motion OUTPUT CATALOG` generates four genuine continuous-hour
sequences, each 180 independent 20-second-cadence sources: 1280 color, 3552 mono,
3552 color, and 3552 daytime with the existing solar-background exposure target.
`day5` generates 60 fresh 3552 daytime sources at the user's selected five-second
cadence. These steady-exposure samples replace the visibly flickering 1/5 ms
alternating stress fixture in the review gallery. Millisecond exposure is not
used as the source timeline weight.

`visual` compares native raw fixed transfer, existing percentile/asinh stretch,
and the existing rolling-three arithmetic mean followed by stretch. It also checks
a deeper catalog query while retaining physical visibility admission.
`display-motion` regenerates the full native color hour and emits single-frame
stretch and rolling-three stretch sequences; require raw hashes to match the
plain-hour inputs. The initial stack has one/two members before reaching three.
Do not equate summed integration with video duration or claim star registration.

`overlay-inputs SOURCE OUTPUT CATALOG` composites existing typed circle, cardinal
and corner-data layers onto the accepted stacked JPEG sequence. The rig geometry
is unchanged and corner facts follow each row's UTC, exposure, stack count and
integration. This comparison adds one JPEG92 round trip; it is not lossless.
`overlay-bench SOURCE GEOMETRY_JSON OUTPUT` compares JPEG read/decode/encode with
and without those layers, using five warmups and 30 operations across 15 sources.
Per-operation timing excludes output hashing and disk writes. Batch CPU/allocation
totals include hash bookkeeping; RSS is a cumulative process high-water mark.
`overlay-sample SOURCE GEOMETRY_JSON OUTPUT_JPEG` writes one untimed diagnostic
image and its exact typed layer payloads for cross-host numerical comparison.

`asi174-motion OUTPUT CATALOG` generates a genuine native 1936×1216 monochrome
hour using the production ASI174MM sensor model, 5.86 µm pixels, 20-second
exposures and a rolling-three mean followed by stretch. The source manifest
records every contributing index and measured star centroid. `overlay-layout`
and `overlay-layout-sample` take the same arguments as `overlay-inputs` and
`overlay-sample`, but apply the benchmark-only proportional compass style and
corner backplates within one pixel of the image edges. Both square and rectangular
cases retain their full field and aspect ratio. These are visual candidates,
not changes to the application's presentation defaults.

`gpu_encode.py SOURCE OUTPUT --encoder libx264|h264_nvenc` is the native-host
hardware comparison. Its NVENC controls are `--preset p4|p6`, `--rate-control
cq|qp` and `--quality N`. The selected full-hour comparison used `p6`, `qp`, `18`
against x264 veryfast CRF23. Quality numbers are not equivalent across encoders.
The helper retains exact media timing, full decode, per-source-frame PSNR/SSIM,
process resources and 200 ms NVIDIA utilization samples. JPEG decoding remains
software. Run inside the declared one-CPU/3 GiB user scope; no Docker NVIDIA
runtime, package or driver installation was needed on the tested home-dev-01.
This follows the available [NVIDIA FFmpeg encoding interface](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/ffmpeg-with-nvidia-gpu/index.html).
Transfer, source generation, verification and quality measurement are outside
the reported encoding interval. The result is not a maximum GPU throughput test.

`verify_motion.py` checks independent clocks/hashes and measured native star
centroids. `inspect_media.py` measures those stars after decoding and compares
source/encoded daytime brightness using the same full-range RGB conversion.
Neither hash uniqueness alone nor repeated noise establishes celestial motion.

`encode.py` maps capture intervals to playback durations using a factor of 60.
The default concat image time base would quantize timestamps at 25 fps; each
input instead declares `option framerate 60`. The VFR profile disables B frames
and supplies a terminal hold packet to preserve exact MP4 duration. CFR 30/60
profiles retain their default B frames, so these are practical profile
comparisons, not an isolated experiment on frame count alone. At 30 fps,
five source seconds are 2.5 frames; cumulative rounding preserves total time.

`legacy60` lists an image once per source second, matching the old ImageSaveService
1 Hz sampling and 60 fps video timeline. It reuses references rather than copying
JPEG files, which understates the historical filesystem cost.

`encode.py --output-size WIDTH HEIGHT` measures native JPEG decode, Lanczos resize
and encoding directly to the smaller output. Both dimensions must be even,
preserve the exact source aspect ratio and be no larger than the input. Timing
includes resizing. The earlier frozen `encode.py` and measurements are preserved;
this option was added only after the host-specific output decision.

`retime.py SOURCE OUTPUT --factor 2|3|5` creates standalone 120×/180×/300×
downloads from the qualified 60× video. FFmpeg stream-copy plus the
[`setts` bitstream filter](https://ffmpeg.org/ffmpeg-bitstream-filters.html#setts)
divides PTS, DTS and packet duration without changing compressed image packets.
The helper verifies every packet checksum, timestamp, total duration and a full
decode. The 1/1800000 time base exactly represents these divisions. This changes
playback pace, not image count, file size or source-processing cost.

`timing_cases.py` uses explicitly retimed source pixels to verify selecting
30/60 output frames from 1,200 timestamps at 50 ms cadence. Missing coverage
stays explicit; a user-visible gap policy is not implemented here. `dense_day.py`
replays actual daytime images at one changing source per second to demonstrate
the dense-input cost. Neither retimed fixture claims new sky observations.

## Reproduction

Build using SDK 10.0.401 and the repository's central packages:

```bash
dotnet publish tools/HVO.SkyMonitor.TimeLapseBenchmark -c Release \
  -r linux-arm64 --self-contained false -m:2 -warnaserror -o /task/bin/linux-arm64
docker build -t hvo/timelapse-benchmark:1130-f6f3f27f \
  tools/HVO.SkyMonitor.TimeLapseBenchmark
```

The recorded run used the pinned SDK Docker image because the host did not have
10.0.401 installed. Build both RIDs and generate inputs before timed encoding.
Run data and binaries inside an isolated task-owned mount at `/data`. On the Pi,
the named volume is on NVMe under `/var/lib/docker`; home and root are microSD.
Leave installed applications and unrelated BuildKit containers intact.

```bash
docker run --rm --network none --cpus 1 --cpu-shares 128 --pids-limit 128 \
  -v hvo-1130-f6f3f27f:/data hvo/timelapse-benchmark:1130-f6f3f27f \
  python3 /data/sweep.py /data/results/pi-screen
```

Exact FFmpeg commands, source mappings, process resources and media probes are
saved beside each output. Encoding uses nice 19, idle I/O priority, one process
at a time, and 1/2 CPU quotas. The Pi lacks a cgroup memory controller; its CPU
limit is enforced, but no Docker RAM limit is claimed. Measured RSS is retained.
No global host cache flush is performed. The separately recorded `--cold-sources`
check uses fsync/POSIX_FADV_DONTNEED only on private immutable JPEGs after
checksum validation, then requires nonzero input I/O and an identical output hash. Encoding time ends at output close to the OS
cache; it excludes input generation, container startup, verification and durable
application publication. Shortlist repetitions report median/min/max over five
invocations, not p95 from five samples.

`contend.py` runs five warmups and 30 arrivals at five-second spacing using a
full-resolution raw Bayer input, first alone, then with sustained encoder work
at 1/2 CPUs. It executes production FixedDisplayTransfer/Bayer reconstruction
plus equivalent Skia JPEG packing in the benchmark helper. It does not invoke
the complete CameraAgent capture/durable-ingress pipeline or the production
JPEG wrapper. Median/p95 response, service time, deadline misses, allocations,
working set and exact output checksums are retained. A greater-than-10% median
or p95 change is flagged for discussion even when no deadline is missed.

`motion_runs.sh` runs the genuine continuous-hour Pi cases.
`followups.sh` is the older microbenchmark driver: selected repeats, preset comparisons, a one-hour fixture
(60-second video from 12 declared source replays), and daily assembly from 24
compatible copies. `stitch.py --segments ...` also qualifies distinct adjacent
segments. The real quarter-hour experiment verifies four different clips.
`stitch.py` disables concat's automatic bitstream conversion
for these strictly identical profiles and checks every compressed packet hash,
PTS/DTS boundary, total duration and complete decoding. It does not qualify
mixed sizes/codecs/recipes, missing hours or changed overlays.

## Investigation boundaries

The fixed-size input cases are bounded. Python child-process timeouts in the
encoding helper are not the production process-tree cancellation design.
`--skip-verify` is used only for repeated foreground-load encodes whose profile
was already qualified; those files are not counted as verified media trials.
This tool is not registered as a runtime service or supported distribution.
It does not change an installed CameraAgent, installer, release pipeline or
production configuration. Selected overlays now have bounded processing evidence;
optional filters and complete acquisition with the combined path remain unqualified.

The report includes the operator-requested five-second daytime cadence and
brighter single/stacked-source follow-ups. `results.json` retains the accepted
commands, resources and source/output identities. Viewing-copy conversion and
source processing are excluded from encoding costs. The LAN review gallery and
its explicit allowlist are private evidence, not a production web endpoint.

## Candidate review workflow

Use the T3 browser for initial checks. When requesting operator feedback, provide
an ordinary server-hosted review URL with full-screen playback and explicit
original-file downloads for local viewing. The current private review deployment
is `http://192.168.1.14:8096/` on hvo-dev-03, managed independently of T3 by the
user service `hvo-1130-review-f6f3f27f.service`. It serves an exact file allowlist,
range requests and attachment responses; native MP4 bytes remain unchanged.
The current service is transient and must be started again after a host reboot.
The `#speeds` section offers separate 60×/120×/180×/300× files at both resolutions;
its file selector updates the player and download links together. All those files
play at their stated pace with a local player's speed set to 1×. Visual-comparison
controls below it explicitly change browser-only playback of the 60× files.
