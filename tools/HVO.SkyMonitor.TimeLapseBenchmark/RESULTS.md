# Issue 1130 performance and visual investigation

Measured 2026-10-05 at base `7cc97ec3969ae53aacab20baac90c47d14c99a7c`.
This checkpoint informs the requested feature discussion. It is not production
acceptance, an installer change, or a completed time-lapse implementation.

## Environment and method

- Pi: `hvo-edge-01`, Raspberry Pi 5 / 8 GiB / four cores, Debian 13.6;
  isolated Docker volume on NVMe/XFS under `/var/lib/docker`.
- Local: `hvo-dev-03`, x64 KVM, eight exposed Intel Core Ultra 9 285H CPUs,
  32 GiB, virtual SSD. This VM has no GPU passthrough.
- Both: FFmpeg `6.1.1-3ubuntu5`, libx264, CRF 23, yuv420p. One encoder,
  nice 19, idle I/O priority; Docker CPU quota and explicit encoder threads.
  Pi CPU limits work; its memory cgroup controller is absent. RSS is measured,
  not an enforced Pi memory ceiling.
- Pi 5 has no H.264/H.265 hardware encoder. An actual exposed-device
  `h264_v4l2m2m` attempt failed to find a valid device. See the
  [Raspberry Pi camera documentation](https://www.raspberrypi.com/documentation/computers/camera_software.html).
- Source generation, video verification and application publication are outside
  timed encoding. Times end at output close to the OS cache, excluding durable
  fsync publication. Long cases below are single trials, not percentiles.

The native motion cases contain 180 independently rendered images across one
actual hour at 20-second cadence. A 60× timeline produces a 60-second MP4 with
181 VFR packets, including the terminal hold. Every source hash/time and output
PTS/count/duration/dimension is checked; the entire output is decoded.
There is no repeated source strip in these continuous-hour results.

## Genuine continuous-hour results

| Native source | Profile / CPU quota | Pi seconds | Local seconds | Pi encoder peak RSS |
| --- | --- | ---: | ---: | ---: |
| 1280×1280 color night | fast / 1 | 39.780 | 13.257 | 238 MiB |
| 3552×3552 mono night | veryfast / 1 | 97.082 | 31.513 | 677 MiB |
| 3552×3552 color night | veryfast / 1 | 91.132 | 28.715 | 674 MiB |
| 3552×3552 color day, 20 s cadence | veryfast / 1 | 91.774 | 27.413 | 674 MiB |
| 3552×3552 color night | veryfast / 2 | 47.063 | 15.625 | 769 MiB |
| 3552×3552 color night | fast / 1 | 176.088 | 57.297 | 1379 MiB |

Local native mono/day `fast` took 63.873/73.512 seconds, respectively.
Pi native color output was 146.3 MB (`veryfast`) versus 169.3 MB (`fast`).
A faster preset is not guaranteed to create a larger file at the same CRF;
these content-specific sizes do not establish equal visual quality.

The source-file cache check fsynced and advised eviction of only the task's
180 native color JPEGs, after checksum verification. The subsequent Pi encode
recorded 475,200 input blocks and took 90.978 seconds, versus 91.132 warm.
It produced an identical MP4 SHA-256. No global cache flush was used.
This confirms actual source reads without attributing all speed to OS caching.
Inter-frame compression remains normal encoder behavior.

Eighteen measured stars move about 280 native pixels across the 3552-pixel hour,
or 101 pixels at native 1280. Decoded Pi color retains 280.397 pixels median
movement; maximum measured centroid error is 0.126 pixel. Different noise or
file hashes alone were not accepted as proof of moving stars.

## Capture timing and five-second daytime preference

The user selected roughly one daytime image every five seconds. At 60×, that is
12 new observations per video second; at a 20-second night cadence it is three.
Exposure and cadence are separate: a 5 ms exposure captured every five seconds
still occupies five seconds on the source timeline.

A 20-second interval becomes 1/3 second of video, and a five-second interval
becomes 1/12 second. VFR encodes a timed hold instead of copying an image 20 or
five times at 60 fps. CFR 30 requires cumulative rounding for five-second
intervals (2.5 frames), not independent truncation. Dense captures require an
explicit output sampling grid and recorded selection, not unlimited encoding.

The new daytime test uses 60 genuinely fresh native images at five-second
cadence, spanning five source minutes. Its stable solar-background target gives
37.514 ms exposures. Hourly numbers derived from it must be labeled projections.
Measured one-CPU veryfast encoding: **17.746 s Pi / 4.932 s local**, all 61
packets/timestamps/duration/full-decode checks pass. A simple 12× estimate is
3.55 minutes Pi / 59.2 seconds local per source hour; these are projections
from a five-minute scene, not measured full-hour results at this cadence.

## Image-processing interference

Five warmups, then 30 arrivals at five-second spacing per condition. The path
reads a native 3552 Bayer raw file, runs production fixed display transfer and
Bayer reconstruction, and uses equivalent benchmark Skia JPEG92 packing.
It does not run the entire CameraAgent capture/durable-ingress graph or the
production JPEG wrapper. Continuous encoder load reuses the qualified short
full-resolution source sequence. The output of every foreground operation is
hash-checked.

| Host / encoder load | Median response | p95 response | Maximum | 5 s deadline misses |
| --- | ---: | ---: | ---: | ---: |
| Pi / baseline | 2.626 s | 2.746 s | 2.755 s | 0 / 30 |
| Pi / 1 CPU | 2.643 s | 2.773 s | 2.825 s | 0 / 30 |
| Pi / 2 CPUs | 2.672 s | 2.788 s | 2.860 s | 0 / 30 |
| Local / baseline | 1.034 s | 1.120 s | 1.133 s | 0 / 30 |
| Local / 1 CPU | 1.043 s | 1.296 s | 1.363 s | 0 / 30 |
| Local / 2 CPUs | 1.071 s | 1.216 s | 1.320 s | 0 / 30 |

All 90 outputs per host match the expected JPEG checksum. Pi p95 change is below
2%. Local one-CPU p95 rises 15.7%, exceeding the predeclared 10% discussion
threshold despite no deadline misses. It is retained as a flagged result,
not dismissed or claimed as full acquisition qualification. Raw per-operation
latency, CPU, allocations and working-set records remain in the evidence bundle.
Pi thermal observations reported no throttling.

## Hourly reuse

Four **different** adjacent quarter-hour intervals were independently encoded,
then stream-copied into one 60-second video. Assembly took 0.114 seconds locally
and 0.164 seconds on Pi, producing 12.13 MB / 184 packets. Every compressed
packet payload, exact PTS/DTS sequence, duration and full decode passed.
The extra packet per segment is its terminal hold; no original observation is
lost or invented at a join.

An earlier local 24-copy assembly microbenchmark produced 24 minutes / 422 MB
in 0.566 seconds. It repeated a fixture and is not a newly captured daily video.
The distinct-segment check supports reusing compatible hourly outputs, but does
not qualify different dimensions, codecs, display/overlay recipes, missing
hours, sunrise clipping or partial products. Those need production policy.

## Visibility, catalog and flicker

The original display used fixed black64/white4095/gamma2.2. It is a nonlinear
fixed transfer, not the normal per-image percentile/asinh preview stretch.
Its stars are too faint for the user's desired viewing result, especially in
the smaller 1280 viewing copies. Plain encoding timings remain a baseline;
they do not imply acceptance of the displayed image.

The database contains 119,625 HYG objects. Original clips use a magnitude-6.5
ceiling followed by the actual sensor/horizon/SNR visibility model, initially
admitting 1,352 mono / 1,344 color stars at 3552. A separate depth 16 / cap 100000
request increased candidates from 8,920 to 32,950 mono / 62,274 color, but kept
identical raw pixels and admitted-star counts in that exposure. The deeper
candidates failed the sensor admission rule. Calling the original image a
render of every catalog row would be incorrect.

Native still comparisons now include the original fixed transfer, production
percentile/asinh stretch, and production three-frame unaligned arithmetic mean
followed by that stretch. Original/raw replay hashes match. Averaging is not
registration: a short stack can reduce noise but broaden moving stars. Its
60 seconds of integration must not replace the observation timestamp or triple
the video hold. All 180 moving-comparison raw hashes match the original color hour. Full-hour
one-CPU local encodes and matched 15-frame/five-minute Pi encodes are:

| Display source | Local measured hour | Local hour file | Pi measured five minutes | Pi five-minute file |
| --- | ---: | ---: | ---: | ---: |
| Original fixed transfer | 28.715 s | 146.3 MB | 7.878 s | 12.37 MB |
| Single-frame percentile/asinh stretch | 49.335 s | 445.2 MB | 12.785 s | 38.47 MB |
| Rolling-three mean + same stretch | 41.923 s | 288.0 MB | 11.233 s | 25.97 MB |

All use VFR / veryfast / one CPU and pass exact media/decode verification. Pi 15-frame
cases each produce 16 packets / five seconds. The local hour cases each produce 181
packets / 60 seconds. These times **exclude creating the stretched/stacked source
previews**; they are not end-to-end stack-processing measurements. Reusing
already-produced immutable previews could avoid that extra work in the video
job, subject to the agreed production source/recipe contract.

The stronger stretch exposes both stars and noise, increasing encoder work and
file size. Stacking reduces that noise/encoding cost but is not a free substitute
for a single frame. The stack's first two outputs contain only one/two exposures,
and the encoded mean brightness initially changes by 4.00/255. After the third
frame, its maximum adjacent change is 0.485/255, versus 0.669 for stretched singles.
This is a real warm-up requirement, not an encoder flicker correction. Use a
retained populated stack or explicitly handle incomplete windows; do not silently
repeat or discard observations to hide it. The preview keeps the startup visible.

The initial daytime stress fixture alternated 1/5 ms exposures with fixed tone,
causing source brightness jumps of 59.30 levels on a 0–255 scale; encoded jumps
were 59.68. Flicker was already in the inputs. In the genuine steady-exposure
hour, maximum adjacent average-brightness change is 0.021 in the source and
0.034 in Pi video. Means use area-averaged full-range RGB inside the image circle,
not an invalid comparison of full-range JPEG Y and limited-range video Y.
Steady output has a roughly 1.33-level codec brightness bias.

The operator selected the stretched three-frame stack for nighttime use and
accepted either 180× or 300×. The selected starting default is **180×**, retaining
300× as an option: one hour in 20 seconds, or a complete 24-hour day in eight
minutes. This retains more viewing time and lowers playback bandwidth relative
to 300×. At the selected five-second daytime cadence, preserving every update
requires 36 updates per video second at 180×, versus 60 at 300×; this is a playback
requirement, not a new measured daytime encode.

Initially the gallery only changed browser playback rate, leaving every download
at 60×. The corrected gallery provides actual independently timed 60×, 120×,
180× and 300× files for the preferred stack, at native3552 and preview1280 sizes.
The six new files preserve all 181 compressed packet hashes and divide PTS, DTS
and duration exactly, with zero measured timestamp error and complete hard-fail
decode. Their durations are 30, 20 and 12 seconds. Each native file is still
287,954,113 bytes; each preview is 52,295,847 bytes. Browser metadata confirms all
three durations and its file selector updates both download URLs at playbackRate1.
The 300× preview reached the end without a decode error; buffering and dropped
display frames occurred, so this is not a smooth-playback qualification.

Faster timing alone does not reduce source decode/filter/encode work or file size.
Skipping observations would change cost and requires a separate declared policy.
Stacked/unstacked videos are separate encodes of different sources; the speed
variants are stream-copied versions of the same encoded images.

## Selected-overlay follow-up

The accepted color stack now has a full-hour overlay candidate using production
`JpegImageCodec`, `PresentationLayerProducers` and `PresentationLayerCompositor`.
Cardinals and the circle use the original rig geometry. Corner text follows each
source row's UTC, exposure, stack count and summed integration. All 180 raw,
combined and original-JPEG identities and source timestamps remain unchanged;
all 180 new overlay JPEGs are unique. The separate predicted scene supplies
geometry only, with explicit synthetic benchmark capture/artifact IDs and no
star annotations. Additional filters were not selected.

This route starts from the saved stack JPEG, so it adds one JPEG92 round trip.
It is not a lossless overlay comparison or the final production pipeline.
The overlay sample itself remains a 60× file, matching its burned-in text;
the separately timed plain-stack downloads are used for pace selection.

Incremental processing uses the same 15 native images, five warmups and 30
operations per condition, one CPU, nice19 and idle I/O priority. Control reads,
decodes and re-encodes JPEG; treatment adds fresh typed layers and composition.

| Host | JPEG control median / p95 | With overlays median / p95 | Median overlay stage |
| --- | ---: | ---: | ---: |
| Local | 0.251 / 0.278 s | 0.290 / 0.353 s | 0.026 s |
| Pi NVMe | 0.342 / 0.383 s | 0.432 / 0.544 s | 0.079 s |

The full-operation median increment is 39 ms locally and 90 ms on Pi. Individual
operation timing excludes output hashing and disk writes; batch CPU/allocation
totals include hash bookkeeping. Thirty-operation CPU totals are 7.649/8.839 s
local and 10.426/13.498 s Pi (control/overlay). Allocations are about 4.315/5.476 GB
per batch on both hosts. Process high-water RSS reaches 850 MiB local and 856 MiB
Pi; these are cumulative in-process peaks, not independently measured mode RSS.
The Pi cgroup proves `cpu.max=100000 100000`; no memory controller is present.
All repeated hashes are stable within each host. JPEG-control hashes also agree
between hosts; composed-output hashes differ, so cross-architecture pixel identity
is not claimed. A separate first-image diagnostic confirms identical typed layer
payloads and source identity. Decoding both results on the same local FFmpeg
gives PSNR 65.89 dB, mean absolute RGB-channel difference 0.0153/255, and 1.246%
changed pixels, with no differences in the defined central sky region. This is
a bounded comparison of one image, not a general renderer-equivalence guarantee.
The diagnostic CLI was added after timings; original measured binaries remain
frozen separately.

Using separately prepared overlay sources, local encoding of the actual hour
took **50.754 s**, producing 278.5 MB / 181 packets / 60 seconds. Pi encoding of
the matched five-minute prefix took **11.182 s**, producing 25.39 MB / 16 packets /
five seconds. Both are VFR/veryfast/one CPU with exact timing and full decode
checks. Encoding excludes overlay generation. The corresponding earlier plain
stack measurements were 41.923 s local-hour and 11.233 s Pi-prefix; these are
single trials, and the extra JPEG round trip prevents attributing their size
or encoder-time differences solely to drawing overlays. No combined live-capture
contention qualification is claimed for this new path. Pi temperature ended at
45°C with throttled0x0.

Candidate D is hosted with full-screen viewing and native/preview downloads at
the same LAN review URL. The preview passed 181 decoded frames / 60 seconds,
complete hard-fail decode, and browser metadata/dimension checks.

## Revised overlays and native ASI174MM

The operator requested larger cardinal labels, corner blocks against the image
margins, and a rectangular camera example. The benchmark-only layout now uses
an 18-pixel compass font per 630 pixels of image height, proportionally sized
badges, and padded corner backplates within one pixel of the image edges.
The one-pixel inset absorbs fractional font-bound rounding; all four cardinals
and backplates are checked for clipping for every generated image. The earlier
layout-v2 rectangular attempt failed this invariant and was retained as rejected.
The review page also now sizes the video element to its image aspect ratio,
removing the artificial side gutters around square previews. The operator
confirmed that both cardinals and corner text look better.

The ASI174MM sequence is a fresh hour at native **1936×1216**, using the actual
VirtualSky ASI174 sensor-response implementation, 5.86 µm pixels, gain 0,
20-second exposures/cadence, rolling-three mean and existing display stretch.
All 180 raw, combined and JPEG hashes are distinct. The source manifest records
the actual one/two/three-member startup and each stack's contributing indices.
The 119,625-row catalog is followed by physical admission (596 stars initially,
589 finally). Eighteen measured stars move a median 95.456 native pixels;
the decoded video gives 95.486 pixels, with maximum centroid error 1.158 pixels.
This is a virtual camera, not physical-camera qualification.

All following rows encode **one genuine source hour**, VFR/veryfast/one CPU,
from separately prepared v3 overlay JPEGs. Generation/stacking/stretch/composition
are excluded from encode timing. Native files remain 60 seconds at 60× before
lossless timing conversion to the downloadable 180×/300× variants.

| Host / source | Encode time | CPU time | Peak process RSS | Native file |
| --- | ---: | ---: | ---: | ---: |
| Local, 3552-square color | 43.011 s | 42.89 s | 721.6 MiB | 275.1 MB |
| Local, ASI174MM 1936×1216 | 5.835 s | 5.78 s | 184.4 MiB | 20.80 MB |
| Pi NVMe, same ASI174MM inputs | 15.842 s | 15.80 s | 174.2 MiB | 20.77 MB |

These are single trials, not percentile estimates or isolated comparisons of
sensor size. Square and rectangular cases differ in sensor response and source
pixels. The original small-font overlay processing measurements above remain
separate; no new-layout concurrent acquisition qualification is claimed.
Pi finished at 42.8°C, throttled0x0, with only its two existing BuildKit containers.

Both geometries have native and smaller full-field **180×/20-second** and
**300×/12-second** downloads at
[the server review page](http://192.168.1.14:8096/?layout=3#layouts).
Preview dimensions are 1280×1280 and 1210×760 respectively. All eight files
preserve every encoded packet through retiming, have exact PTS/DTS/durations,
pass full hard-fail decode, and return verified byte ranges with attachment
headers. Browser metadata and source selectors match both speeds at playback
rate 1. The rectangular 300× sample reached the end without a decode error,
but recorded 11 dropped display frames out of 181 and 11 buffering events;
downloaded playback is preferable for judging motion. This is not universal
browser or smooth-streaming qualification.

## NVIDIA encoding on home-dev-01

The existing **RTX 5070**, driver **580.178.04**, and native FFmpeg
**6.1.1-3ubuntu5** successfully encode 3552×3552 H.264. No installation or driver,
Docker runtime, CameraAgent, or release-tooling change was needed. The host
exposes twelve i9-14900K CPUs; its filesystem is ext4 on a virtual disk, not
qualified as NVMe. User-systemd scopes enforce one CPU, 3 GiB host RAM and
128 tasks. Each encode uses nice19 and idle I/O priority. JPEG decoding stays
on the CPU; NVIDIA samples show encoder activity and zero decoder activity.
The interface is described in [NVIDIA's FFmpeg documentation](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/ffmpeg-with-nvidia-gpu/index.html).

The matched full-hour source is the already accepted **plain stretched
rolling-three color sequence**, with all 180 source checksums verified. Both
trials run on the same host. They are independent encodes, not a retiming trick.

| Profile | Encode time | CPU time | Peak process RSS | File | PSNR / SSIM |
| --- | ---: | ---: | ---: | ---: | ---: |
| x264 veryfast, CRF23 | 40.129 s | 40.04 s | 723.7 MiB | 288.0 MB | 46.152 dB / 0.980146 |
| NVENC p6/hq, constant QP18 | 8.645 s | 8.09 s | 1451.2 MiB | 335.2 MB | 46.549 dB / 0.981567 |

In these single full-hour trials NVENC is **4.64× faster**, uses **79.8% less
CPU time**, and makes a **16.4% larger file**. The slightly higher global
reference scores do not establish perceptual equivalence for every faint star.
PSNR/SSIM compare all 180 unique source frames, excluding the terminal hold;
JPEG full-range YUV is converted to limited-range YUV420 and compared with the
decoded H.264 frames. Exact frame timestamps/count, dimensions, 60-second
duration and complete hard-fail decode pass for both. The software MP4 checksum
also matches the earlier accepted local x64 encode.

An untimed NVENC 180× stream-copy conversion also passes unchanged packet hashes,
exact 20-second duration and full decode. Native-size detail PNGs retain two
preselected 768-pixel regions from frame 90 for source/software/GPU comparison.
Visual inspection of the lower region preserves visible star positions and
small points without obvious blocking; this limited inspection is not operator
acceptance of the complete GPU sequence. Native GPU media remains in the private
home-dev-01 task directory; the hosted layout candidates use software encoding.

NVENC's 44 utilization samples peak at 44% encoder activity, 3% general GPU
activity, 1068 MiB VRAM and 36°C. These are sampled peaks, not continuous
maximum-resource guarantees. GPU memory is separate from the 3 GiB host-RAM
limit. The comparison intentionally limits CPU and concurrency; it does not
measure maximum GPU throughput or simultaneous live processing on this host.
Dataset transfer, source processing, verification and quality assessment are
excluded. The one-time 433.9 MB source transfer achieved about 0.96 MB/s on the
tested SSH route; central encoding should preferably use images already present
there. This is not a production-network capacity measurement.

The retained short screening trials explain profile selection. A 15-image
prefix took 3.623 s in x264, 1.116 s in NVENC p4/CQ23 and 1.568 s in p6/CQ23,
but the GPU reference scores were substantially worse. Changing p6 CQ23 to CQ15
produced identical bytes on this installed FFmpeg/driver path; its cause remains
unqualified, so that path is not selected. Explicit p6 constant QP20 responded
(1.618 s, PSNR44.455/SSIM0.970960) and justified trying QP18 over the genuine
full hour. CRF, CQ and QP numbers must not be treated as equivalent quality.

The measured result supports NVENC as an optional central-host profile, with a
bounded software profile on Pi. It does not establish a deployment boundary,
GPU-host live-capture contention behavior, or mixed CPU/GPU hourly stream-copy
compatibility. Production must pin/validate codec parameters across reusable
segments and qualify any fallback that changes the encoder profile.

## Host-specific outputs and direct smaller encoding

The operator clarified the architecture after the GPU comparison: **CameraAgent
keeps a smaller local-only timelapse and never uploads video**. **LogicHost
independently generates its larger video from received source images**, using
NVIDIA hardware when available and qualified. Each host reuses its own compatible
hourly segments for its own daily product. The earlier output-selection question
in `layout-gpu-results.json` is superseded by this decision; the immutable
measurement checkpoint itself remains unchanged. This avoids video transfer
rather than requiring a central copy of the edge video. It does not authorize
moving hardware or modifying installed drivers/runtimes.

An additional bounded trial measures native source JPEG decode, Lanczos resize
and x264 veryfast CRF23 encoding directly to the smaller output. The inputs
already contain the accepted stack/stretch/v3 overlays. Source preparation is
excluded; resizing is included. This is different from resizing an already
encoded video to create the earlier CRF18 review copies.

| Host / native input → output | Actual source span | Encode time | CPU time | Peak RSS | File |
| --- | ---: | ---: | ---: | ---: | ---: |
| Local color, 3552² → 1280² | One hour | 15.479 s | 15.40 s | 179.8 MiB | 21.98 MB |
| Pi color, 3552² → 1280² | Five minutes / 15 images | 2.969 s | 2.91 s | 170.8 MiB | 2.10 MB |
| Pi ASI174MM, 1936×1216 → 1210×760 | One hour | 7.728 s | 7.67 s | 107.7 MiB | 4.97 MB |

All three use one encoder CPU and pass exact dimensions, PTS/count, duration
and complete hard-fail decode. These are single trials. The Pi color prefix is
not a measured full hour and must not be described as one. The smaller file
sizes do not prove identical visual quality to the prior higher-quality review
copies. The native aspect ratio is preserved exactly; these pixel dimensions
are candidate settings, not a finalized product limit.

The complete color hour is available as an actual 180×/20-second
[smaller-output candidate](http://192.168.1.14:8096/?layout=3#edge-small), with
full-screen viewing and a 21.98 MB download. Retiming preserves all 181 compressed
packets and passes exact timing/full decode; HTTP attachment and range bytes
match the original file. Its decoded midpoint was visually checked for the
complete field, visible stars and readable corrected overlays. The hosted
full-hour sample was generated locally; the Pi performance sample is the
separately identified five-minute prefix.
Its precomposed corner data still reports the original 3552×3552 image size,
disclosed on the page; production overlays must label source and output
dimensions distinctly. This candidate is genuinely 1280×1280.

The decision and additional commands/results are retained in
[`edge-small-results.json`](edge-small-results.json). Combined live processing
with the final selected recipe, retention, output pixel limits and gap behavior
remain production design/qualification work. Technical prerequisite issues
#993, #1135 and #1136 are closed as of this checkpoint; their contracts must be
read and reused during implementation rather than assuming the old issue-body
dependency wording is current.

## Preliminary screening and boundaries

Forty verified short clips per host cover 640/1280/3552 mono/color, 1936×1216
mono, 3096×2080 color, day and twilight. These use 24 sources over five minutes.
Pi 1280 color `fast`: VFR 3.92 s, CFR30 9.18 s, CFR60 14.09 s, legacy60 15.94 s.
Pi native 3552 color: VFR 20.75 s, CFR30 67.78 s, CFR60 109.95 s, legacy60 122.78 s.
The VFR profile disables B frames for correct terminal timing; CFR keeps them,
so this is a practical profile comparison rather than pure frame-count isolation.

Local five-run shortlist: 1280 VFR median 1.27 s (1.22–1.32), CFR30 2.72 s
(2.52–2.82); 3552 color VFR 6.59 s (6.39–7.14). These are medians/ranges,
not p95 estimates. Earlier hour/day replays remain labeled microbenchmarks.
Rejected terminal-duration and automatic Annex-B conversion trials are retained,
excluded from accepted measurements, and explained by the qualified commands.

Selected cardinal/corner-data/image-circle overlays now have the bounded evidence
above. Optional filters, combined live acquisition, real weather
variability, all browser/device targets and a genuine new 24-hour observation
are not qualified by this checkpoint. Proposed production starting point:
one low-priority encoder, one CPU, bounded memory/scratch/time, VFR veryfast,
immutable source/display identity, hourly reuse, and explicit gap behavior.

Private raw evidence: `/home/roys/.cache/hvo/1130-f6f3f27f`, and matching Pi
volume `hvo-1130-f6f3f27f`. Exact commands/probes/hashes live beside each output;
`pi-final-evidence.json` retains retrieved Pi results, pixel checks and foreground
records. Source generation uses SDK 10.0.401 and the repository algorithms.
`pi-additional-evidence.json` retains the later day5/display measurements.
The final Pi observation was 45.0°C, throttled 0x0, 883 GiB free; only the two
pre-existing BuildKit containers remained running. Task evidence uses 2.5 GiB
on Pi and 6.4 GiB locally, retained for review.
Catalog SHA256: `b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2`.

Compact machine-readable commands, hashes, resources, source-manifest identities
and accepted result records are retained in [`results.json`](results.json).
The later overlay and speed-download evidence is in
[`overlay-speed-results.json`](overlay-speed-results.json); the earlier checkpoint
and its source fingerprints remain unchanged.
The subsequent rectangular/layout and matched GPU trials are retained separately
in [`layout-gpu-results.json`](layout-gpu-results.json), with frozen source and
binary identities, resource/quality summaries, media checks and review evidence.

The shared browser loaded both brighter previews with 60-second duration and
60×/180×/300× controls. The operator took over playback during the single-frame
check; a complete automated browser playback pass for both new variants is not
claimed. Native outputs passed complete hard-fail FFmpeg decode; viewing copies passed
ffprobe decoding/count and duration checks.
