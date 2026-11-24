# Hourly and daily time-lapses

CameraAgent generates local H.264/MP4 viewing copies (#1130) and assembles each
daily video from compatible hourly products without encoding them again.
Videos stay in the local time-lapse store, outside durable ingress and the
upload outbox.

> Historical configuration, storage, and restart procedures for retired
> LogicHost time-lapses are in the [pre-retirement runbook](https://github.com/HualapaiValley/HVO.SkyMonitor/blob/66e6aa237b40c70342815af6e2511122daa56bb6/docs/runbooks/time-lapse-generation.md#logichost-configuration-and-storage).

## Display and timeline

The default is 180× real time: one source hour occupies 20 seconds. The other
supported speed is 300×: one hour occupies 12 seconds. This changes presentation
timestamps, not the rate of celestial motion in the source images. A 24-hour
window occupies eight minutes at 180× or 4.8 minutes at 300×. Sunrise reporting
periods can differ from 24 hours; their actual UTC bounds determine the duration.
Partial hours at sunrise are retained even below one 60-fps frame. Boundaries
round to media microseconds; a positive sub-microsecond window uses one tick.
Daily duration is the sum of these independently rounded hourly durations.

Capture start timestamps determine each image's hold. Exposure and stack
integration remain separate recorded facts. A 20-second cadence gets four times
the hold of a five-second cadence. A one-millisecond exposure captured every five
seconds still represents five seconds of the timeline. Up to 60 unique images
per output second are selected by deterministic time bins; every candidate and
sampled-out ordinal remains in provenance. At the chosen daytime cadence of
five seconds, 180× uses 36 images per output second and 300× uses 60.

A hold ends at the next capture, capped at 60 seconds by default. The final image
uses the median of the last 31 cadence intervals, or five seconds when there is
no cadence history. Leading, internal and trailing gaps are visible NO IMAGE
slates. Rejected saturated captures have their own gap intervals. Empty hours
are NoSources; they acquire internal gap segments only when assembling a daily
video containing other usable hours. An entirely empty daily period stays
NoSources. Failed hours block daily assembly rather than masquerading as missing
sky observations.

The default night display uses a rolling unaligned three-frame arithmetic mean
and the existing linear stretch/demosaic algorithms. The first two images record
one- and two-frame warmup. Changes to layout, exposure/gain compatibility,
location, day/night regime or a cadence gap over one minute reset the stack.
StackCount=1 disables stacking. Daytime always uses one source frame and the fixed
display transfer, selected
at solar altitude at least −6 degrees. Exposure normalization/deflicker and
arbitrary processing-graph filter adoption are not part of this first preset.

Selected cardinal directions, four corner metadata blocks and the image circle
are composited through the existing typed presentation renderer. Text scales
with the image, the corner plates meet the left/right margins, and provenance
and burned metadata distinguish source dimensions, video dimensions, exposure,
stack size and total integration. Non-square cameras use proportional sizing
rounded to the nearest even pixel: 1936×1216 becomes 1280×804 and 3096×2080
becomes 1280×860 under CameraAgent's default maximum dimension of 1280.
Output dimensions are capped without upscaling.

## Execution boundary

`HVO.SkyMonitor.Processing` owns pure timeline, stack traversal and display logic.
`HVO.SkyMonitor.Video.FFmpeg` is the optional infrastructure adapter; it owns
processes and temporary files. AgentCore remains transport neutral. Shared
sunrise/site-time logic lives in Astronomy.

The qualified boundary is Linux, FFmpeg/ffprobe 6.1.x with libx264, MP4, the
concat demuxer, microsecond time base and setts bitstream filter. It requires
`prlimit`, `taskset`, `nice`, `ionice` and `setpriv` at `/usr/bin`. A real two-color
canary checks encoding, exact packet timing, decoding and decoded color order.
Both executable SHA-256 values are pinned and rechecked before work. Replacing
either binary requires restarting CameraAgent. Missing capabilities return an
explicit unavailable state while acquisition continues.

The CameraAgent software profile is libx264 veryfast, CRF 23, one thread, no B
frames, yuv420p, VFR and Lanczos final resize. Video and provenance retain the
actual profile, binary identities, prepared-image checksums, packet checksums
and timing. Every output is probed and fully decoded before publication. Daily
assembly additionally proves ordered packet-payload equality with its hourly inputs.

CameraAgent holds an exclusive worker file lease so one encoder uses the scratch
directory at a time. Child processes run on one allowed CPU at nice 19 and idle
I/O priority, with file-size, CPU-time, address-space and descriptor limits. The
worker disables core dumps. RSS, temporary bytes, pipe output,
wall time and cancellation are bounded. Cancellation kills and reaps the child
before reusing the slot. A dedicated creating thread remains alive until child
exit because [Linux parent-death signals track that thread](https://man7.org/linux/man-pages/man2/PR_SET_PDEATHSIG.2const.html). Managed preparation is a single sequential worker with
at most three raw images cached; it shares the host process and is not a separate
cgroup. Its impact must be assessed with the concurrent-processing benchmark.

The default per-encode limits are 15 minutes, 8 GiB scratch, 2 GiB output,
8 GiB child address space, and 2 GiB child RSS. A whole job has a one-hour
deadline, including daily assembly and preparation. CameraAgent checks free
storage before allocating:
keep at least 2 GiB or the existing acquisition disk-pressure reserve, whichever
is larger. Its total publication quota is 32 GiB. Hitting the quota records a
failure and leaves acquisition running. This release does not automatically
expire videos or their immutable metadata; provision an appropriate quota and
storage before enabling sustained generation.

## CameraAgent configuration

The feature is disabled by default. Configure the `CameraAgent:TimeLapses`
section alongside the existing capture configuration, for example:

```json
{
  "CameraAgent": {
    "TimeLapses": {
      "Enabled": true,
      "RigProfileSha256": "<exact configured rig SHA-256>",
      "MaximumDimension": 1280,
      "Timing": { "Compression": 180, "MaximumHoldSeconds": 60, "FallbackCadenceSeconds": 5 },
      "Frames": { "StackCount": 3, "CardinalDirections": true, "CornerMetadata": true, "ImageCircle": true },
      "Encoder": { "ExecutablePath": "/usr/bin/ffmpeg", "ProbePath": "/usr/bin/ffprobe" }
    }
  }
}
```

Use the existing local automation registry's `TimeLapseGeneration` descriptor
and its advertised `time-lapse:<preset SHA-256>` target. Register SourceWindowClosed
occurrences for CompletedCivilHour and SunriseDay, with actual-source selection
and the chosen processing allowance, using the [automation workflow](cameraagent-local-automation.md).
Do not calculate or hard-code a different target after changing the preset. A
changed preset advertises a new target, while retained jobs keep their original
settings and sources. A daily occurrence creates its exact clipped hourly
partition and reuses already published matching hours where available.

Storage is `<RawIngressRoot>/.time-lapses`: FULL/WAL SQLite authority, immutable
MP4/JSON products, and a confined encoder scratch directory. Pending source plans
hold raw inputs against retention. Interrupted file publications have durable
intents; recovery removes only their recorded unpublished files. Retry from the
observing-day card requires the canonical local owner and operations-mutate
permission, is revision-bound and audited, and retains the original inputs.
There are at most 64 pending requests, 8192 candidates per hourly window, 50
hourly children per daily request and ten explicit retries per job.

The real observing-day/calendar and product library show queued, working,
produced, no-source, unavailable and failed states. Product detail has actual
playback/fullscreen, download and provenance links. The authenticated
`/api/v1/operations/time-lapses` endpoints expose video bytes with SHA ETags and byte ranges;
metadata readiness is separate from retained-file availability.

## Qualification and adoption

The [benchmark evidence](../../tools/HVO.SkyMonitor.TimeLapseBenchmark/README.md)
separates initial encode-only trials, accepted moving full-catalog examples and
production rendering/encoding measurements. Do not extrapolate replayed inputs
as an entire moving night or confuse retimed playback with an encoding speedup.
Native Manual tests require the recorded FFmpeg boundary. Unit tests remain
Docker-free.

This change does not install FFmpeg, update a live CameraAgent, change GPU
drivers/container runtimes, move hardware or alter installer/release tooling.
Those operational adoption steps remain behind the operator's existing hold.
Enabling a host requires an already qualified boundary and sufficient storage.

There is no supported upgrade from time-lapse products created by earlier
pre-merge algorithm versions. Read validation currently rebuilds an hourly
timeline with the current planner; changing that planner's version can make
older products unreadable, including a date listing containing one such product.

Use a separate disposable instance with fresh storage for experiments across
these pre-release versions, preserving existing data and evidence. A supported
future algorithm upgrade must explicitly preserve old product validation and
reads and qualify hourly/daily reuse across that boundary before adoption.
