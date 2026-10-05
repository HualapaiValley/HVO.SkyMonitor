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
becomes 1280×860 under the edge limit. CameraAgent defaults to a 1280
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

Software fallback occurs during capability qualification. A qualified NVENC
profile remains pinned for the process lifetime; a later device/driver failure
ends the affected job as Unavailable without switching its retained profile.
After repairing the device, restart LogicHost and increment the target Generation
to rediscover failed retained hours. To recover using software, set PreferNvidia
to false, increment Generation and restart. Existing produced hours remain intact;
new daily products use only compatible hours from the new generation. A later
qualification success can also select a different profile for a new scan and
create additional hourly/daily revisions; earlier immutable products are retained.
Use PreferNvidia=false when a consistently available software profile is wanted.

Only one encoder runs per host process/scratch lease. Edge has an exclusive
worker file lease; central SQL leases fence the lane across replicas. Child
processes run on one allowed CPU at nice 19 and idle I/O priority, with file-size,
CPU-time, address-space and descriptor limits; core dumps are disabled. RSS, temporary bytes, pipe output,
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
This guarantee assumes compatible planner, renderer and encoding contract
versions; it does not provide the algorithm-upgrade support described below.
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

An hour rejected during discovery has no request row to show in the UI. Warning
event 11313 (`Central time-lapse hour rejected`) identifies the device, UTC window
and failure type; the daily request is withheld while that child is absent.
Later hours and reporting periods are still discovered. Each scan retries the
omitted hour while its reporting date remains within CatchUpDays (supported range
1–7 days). Repair the invalid source/descriptor or configuration within that span;
the next scan can enqueue the missing hour and daily without a Generation bump.
The Generation recovery above applies when a retained terminal request also
needs rebuilding. Once the date leaves the configured catch-up span, discovery
does not retry it automatically. Check event 11313 when a daily has no request,
as well as visible failed requests when an enqueued daily cannot finish.

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

There is no supported upgrade from time-lapse products created by earlier
pre-merge algorithm versions. Read validation currently rebuilds an hourly
timeline with the current planner; changing that planner's version can make
older products unreadable, including an edge date listing containing one such
product. Central discovery also does not automatically create new jobs merely
because a planner or renderer version changed. Incrementing Generation can
create new central requests within the catch-up span, but does not restore old
products' readability or repair their links.

Use a separate disposable instance with fresh storage for experiments across
these pre-release versions, preserving existing data and evidence. A supported
future algorithm upgrade must explicitly preserve old product validation and
reads, version the central discovery contract, and qualify hourly/daily reuse
across that boundary before adoption. Neither a Generation bump nor a routine
binary replacement supplies that compatibility in this initial implementation.
