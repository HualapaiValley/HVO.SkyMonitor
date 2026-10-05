# Hourly and daily time-lapses

Issue #1130 adds genuine H.264/MP4 products on both hosts. CameraAgent keeps a
small viewing copy locally; it never sends videos to durable ingress or the
upload outbox. LogicHost reconstructs its own larger videos from received raw
captures. Each host assembles its own daily video from its compatible hourly
products without encoding those hours again.

## Display and timeline

The default is 180× real time: one source hour occupies 20 seconds. The other
supported speed is 300×: one hour occupies 12 seconds. This changes presentation
timestamps, not the rate of celestial motion in the source images. A 24-hour
window occupies eight minutes at 180× or 4.8 minutes at 300×. Sunrise reporting
periods can differ from 24 hours; their actual UTC bounds determine the duration.

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
StackCount=1 disables stacking. Daytime uses the fixed display transfer, selected
at solar altitude at least −6 degrees. Exposure normalization/deflicker and
arbitrary processing-graph filter adoption are not part of this first preset.

Selected cardinal directions, four corner metadata blocks and the image circle
are composited through the existing typed presentation renderer. Text scales
with the image, the corner plates meet the left/right margins, and provenance
and burned metadata distinguish source dimensions, video dimensions, exposure,
stack size and total integration. Non-square cameras retain their aspect ratio:
1936×1216 becomes 1210×760 under the edge limit. CameraAgent defaults to a 1280
maximum dimension; LogicHost preserves native dimensions up to 4096, with the
existing 16-million-pixel Bayer reconstruction limit. There is no
upscaling.

## Execution boundary

`HVO.SkyMonitor.Processing` owns pure timeline, stack traversal and display logic.
`HVO.SkyMonitor.Video.FFmpeg` is the optional infrastructure adapter; it owns
processes and temporary files. AgentCore remains transport neutral. Neither host
references the other host or its persistence contracts. Shared sunrise/site-time
logic lives in Astronomy.

The qualified boundary is Linux, FFmpeg/ffprobe 6.1.x with libx264, MP4, the
concat demuxer, microsecond time base and setts bitstream filter. It requires
`prlimit`, `taskset`, `nice`, `ionice` and `setpriv` at `/usr/bin`. A real two-color
canary checks encoding, exact packet timing, decoding and decoded color order.
Both executable SHA-256 values are pinned and rechecked before work. Replacing
either binary requires restarting the worker. Missing capabilities return an
explicit unavailable state; they never terminate acquisition or ingest.

The closed software profile is libx264 veryfast, CRF 23, one thread, no B frames,
yuv420p, VFR and Lanczos final resize. LogicHost can select NVENC p6/constant QP18
when the actual hardware canary succeeds; otherwise it selects software. The
Pi 5 uses software encoding. Listing an encoder is not proof that its device or
driver works. Video and provenance retain the actual profile, binary identities,
prepared-image checksums, packet checksums and timing. Every output is probed
and fully decoded before publication. Daily assembly additionally proves ordered
packet-payload equality with its hourly inputs.

Only one encoder runs per host process/scratch lease. Edge has an exclusive
worker file lease; central SQL leases fence the lane across replicas. Child
processes run on one allowed CPU at nice 19 and idle I/O priority, with file-size,
CPU-time, address-space and descriptor limits. RSS, temporary bytes, pipe output,
wall time and cancellation are bounded. Cancellation kills and reaps the child
before reusing the slot. A dedicated creating thread remains alive until child
exit because [Linux parent-death signals track that thread](https://man7.org/linux/man-pages/man2/PR_SET_PDEATHSIG.2const.html). Managed preparation is a single sequential worker with
at most three raw images cached; it shares the host process and is not a separate
cgroup. Its impact must be assessed with the concurrent-processing benchmark.

Defaults per edge encode: 15-minute deadline, 8 GiB scratch, 2 GiB output,
8 GiB child address space and 2 GiB child RSS. Central defaults: 30 minutes,
48 GiB scratch and 16 GiB output. Software children retain the edge memory limits;
NVENC has a 32 GiB virtual-address allowance for CUDA initialization while retaining
the 2 GiB resident limit. A whole job
has a one-hour deadline, including daily assembly and preparation. These are
ceilings, not reservations of RAM. Edge checks free storage before allocating:
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

## LogicHost configuration and storage

Configure `CentralTimeLapses` independently. Targets name a received rig and an
acknowledged deployment location version; current device settings are never used
to guess historical geometry or site.

```json
{
  "CentralTimeLapses": {
    "Enabled": true,
    "Targets": [{
      "DevicePublicId": "<device UUID>",
      "RigProfileSha256": "<received rig SHA-256>",
      "LocationId": "<acknowledged location ID>",
      "LocationVersion": 1,
      "Generation": 1
    }],
    "MaximumDimension": 4096,
    "PreferNvidia": true,
    "SettleAllowance": "00:10:00",
    "CatchUpDays": 2,
    "Timing": { "Compression": 180 },
    "Frames": { "StackCount": 3, "CardinalDirections": true, "CornerMetadata": true, "ImageCircle": true },
    "Encoder": { "ScratchDirectory": "/var/lib/hvo/time-lapse-scratch" }
  }
}
```

Discovery visits explicitly configured targets, freezes closed hours and builds
a daily request from exact hourly job IDs. Late arrivals within the configured
catch-up span create immutable successors; older products and links remain.
A small capture-count/reference-completion/latest-receipt check reuses unchanged
requests without rereading full source descriptors. References arriving after a
raw image trigger a successor when that image becomes reconstructable. Raw retention alone does not replace a finished
hour with an empty successor. Later settings do not change existing requests. Source availability and quality
exclusions are recorded. Retrying/rebuilding outside that bounded discovery span
is not automatic. Restart recovery has three fenced attempts; failed terminal
requests remain visible. To explicitly rebuild a target after repairing unavailable inputs, increase its
Generation value and restart with the updated configuration. This creates new
requests from currently available inputs within the catch-up span and preserves
the failed requests; it is not a mutation of their frozen input plans. The initial
release has no central manual-retry UI.

SQL owns the requests, leases, retention holds, dependencies and publications.
An active lease renews every 20 seconds and expires after two minutes. At most
256 requests are pending. A committed video uses 32 MiB immutable chunks through
the existing object-store API, with a maximum 1024 chunks and no provider-limit
increase. A seekable verified stream supports browser ranges and full downloads.
Publication attempt prefixes isolate interrupted outputs: older attempts cannot
delete newer attempts or committed hourly products. Failed cleanup during a
storage outage is retained for bounded recovery rather than reported as success.

`/app/time-lapses` lists authorized requests by reporting date and links actual
videos. `/api/v1.0/time-lapses/{id}/video` supports GET/HEAD, byte ranges and
attachment downloads; `/provenance` supplies the retained metadata. Every read
checks the current human credential and observatory membership. System/mixed
credentials and another observatory's users cannot retrieve these private
videos. Missing bytes return 410; storage faults return a sanitized 503.

## Qualification and adoption

The [benchmark evidence](../../tools/HVO.SkyMonitor.TimeLapseBenchmark/README.md)
separates initial encode-only trials, accepted moving full-catalog examples and
production rendering/encoding measurements. Do not extrapolate replayed inputs
as an entire moving night or confuse retimed playback with an encoding speedup.
Native Manual tests require the recorded FFmpeg boundary; Unit tests remain
Docker-free. Central Integration tests use owned Testcontainers for SQL/storage
and do not require a physical camera.

This change does not install FFmpeg, update a live CameraAgent, change GPU
drivers/container runtimes, move hardware or alter installer/release tooling.
Those operational adoption steps remain behind the operator's existing hold.
Enabling a host requires an already qualified boundary and sufficient storage.
