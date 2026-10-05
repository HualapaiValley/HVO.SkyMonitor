# Production-path measurements for #1130

Measured 2026-10-05 after the operator accepted the moving full-catalog examples
in [RESULTS.md](RESULTS.md). [Machine-readable evidence](production-results.json)
retains exact commands, binary identities, input-manifest hashes, media checksums,
CPU, allocations, sampled I/O and individual foreground timings. The
[runbook](../../docs/runbooks/time-lapse-generation.md) describes configuration.

## Inputs and boundary

Each trial uses five genuine source minutes from the full 119,625-row HYG catalog:
15 fresh night images at 20-second cadence, or 60 daytime images at five-second
cadence with 37.514 ms exposures. ASI174MM uses its 1936×1216 rig and 5.86 µm sensor
model. Source/celestial clocks and raw hashes advance independently. Output is
180×, with rolling-three mean, stretch/fixed daytime transfer and the accepted
cardinal, corner-data and image-circle overlays.

Timing includes verified raw reads, production rendering, bounded FFmpeg,
packet/full-decode verification, durable MP4 publication and output SHA.
Qualification is included below and also recorded separately. Capture, host job
databases and central chunk publication are excluded; separate native integration
tests exercise those publication authorities and authenticated media routes.

Hosts are the NVMe Pi5/8 GiB and local eight-core x64 VM from the investigation.
Docker CPU quotas are three and four respectively; managed processor count is
two. Encoder children use one allowed CPU, nice 19 and idle I/O priority. Pi has
no memory cgroup controller; aggregate RSS is measured, while production child
address-space/RSS bounds are enforced independently. Trials use warm filesystem
caches without global cache flushing. Single trials are not percentiles.

## Rendering, encoding and publication

| Five-minute source / output | Pi wall seconds | Local wall seconds | Pi aggregate peak RSS |
| --- | ---: | ---: | ---: |
| 3552² mono → 1280² | 17.856 | 10.837 | 1.124 GiB |
| 3552² color → 1280² | 28.889 | 13.039 | 1.090 GiB |
| 3552² daytime → 1280², optimized | 51.371 | 21.658 | 1.274 GiB |
| ASI174MM 1936×1216 → 1210×760 | 5.016 | 2.808 | 0.387 GiB |
| 3552² mono → native | — | 15.845 | — |
| 3552² color → native | — | 18.463 | — |

Daytime initially took 197.756 seconds/2.781 GiB on Pi and 73.230 seconds/2.930 GiB
locally. Fixed conversion allocated multiple full-image double planes. Its new
integer-black path uses exact bilinear quarter-sample sums and a bounded transfer
table; fractional black retains the scientific path. Independent equivalence
tests cover padded strides, odd dimensions and different transfer policies.
Both hosts produced **identical prepared JPEG hashes and identical MP4 SHA-256
before and after**. Local allocation fell from 39.59 GiB to 15.64 GiB over 60 images;
cumulative allocation is distinct from peak memory.

A 12× projection is about 10.3 minutes per source hour on Pi, within the edge's
15-minute encode deadline. This is a sizing projection, not a measured full-hour
production run. Full-hour encoder-only results and accepted visual examples
remain separately identified in RESULTS.md; neither substitutes for a host soak.

## Concurrent actual image processing

Five warmups, then 30 actual native ProcessingRecipeExecutor encoded-preview
operations at five-second arrivals: verified raw read, stretch/demosaic and JPEG.
The background continuously runs serial production videos in the same process.
Repeated short inputs are load only, not a longer observing period. This excludes
the complete acquisition/ingress/outbox path.

| Host / condition | Median response | p95 response | Maximum | 5 s misses |
| --- | ---: | ---: | ---: | ---: |
| Pi / baseline | 1.367 s | 1.391 s | 1.404 s | 0/30 |
| Pi / video active | 1.327 s | 1.359 s | 1.390 s | 0/30 |
| Local / baseline | 0.533 s | 0.583 s | 0.586 s | 0/30 |
| Local / video active | 0.606 s | 0.713 s | 0.742 s | 0/30 |

All foreground hashes match. Local p95 increased **22.3% (130 ms)**, exceeding
the investigation's 10% discussion threshold. This is a retained cost, not zero
interference: background work now includes managed native rendering/allocation
in the same process. Child-inclusive CPU rose from 19.8 to 166.8 seconds locally.
The measured five-second cadence has substantial headroom, but full acquisition
still needs deployment qualification. Pi aggregate RSS was 1.499 GiB under load;
temperature ended at 48.3°C with throttled flags zero. Its slightly lower latency
under load is trial variation, not an acceleration claim.

The first local load attempt exposed successful process exit racing the RSS
read; that failed trial is excluded. The corrected monitor tolerates an exited
process, still verifies status/drains pipes, and passes repeated short-process
tests plus the replacement load run. Linux parent-death cancellation retains a
dedicated creating thread until child exit.

## NVIDIA host

Existing home-dev-01 RTX5070/12 GiB, driver 580.178.04, native FFmpeg 6.1.x.
No packages, drivers, GPU configuration or Docker runtime were changed.

| Native 3552² color encoder | Wall incl. qualification | Work excl. qualification | Managed render | Output bytes |
| --- | ---: | ---: | ---: | ---: |
| x264 veryfast CRF23 | 15.670 s | 14.753 s | 10.055 s | 16,419,501 |
| NVENC p6 QP18 | 15.674 s | 14.280 s | 9.875 s | 32,061,881 |

The accepted NVENC profile favors faint-star quality and produces a larger file;
QP is not equivalent to CRF. Rendering dominates this short bounded sample, so
GPU selection is not claimed to halve total time. Prior full-hour encoder and
quality comparisons remain in RESULTS.md. NVENC initially failed CUDA startup
at 8 GiB virtual address space; 32 GiB succeeds with the same 2 GiB child RSS
limit. Packet order, timing and full decode pass for both profiles.

Each host assembles its own compatible hourly packets into daily output.
CameraAgent videos remain local; LogicHost recreates its larger version from
received raw sources. Storage sizing must account for the documented quota and
absence of automatic video expiration. Runtime adoption remains behind the
operator's existing installer/live-upgrade hold.

## Mixed-cadence daily compatibility

A separate synthetic 512×512 red/green/blue canary uses the production FFmpeg
arguments on `home-dev-01` (FFmpeg 6.1.1, RTX 5070). Three captures at 20-second
cadence and twelve captures at five-second cadence represent adjacent minutes
at 180×. Both libx264 and NVENC retained matching stream configuration across
the two segments, then stream-copied all 15 packets with identical payload
hashes and exact presentation/duration values into a 666666-microsecond video.
Full decode passed. This checks the protocol, not sky appearance or performance.

Reproduce on an already qualified NVIDIA host with
`python3 mixed_cadence.py <new-empty-output-directory>`. Each subprocess is
time-limited and pinned to one CPU. Commands, individual probes and joined
probes are retained in that directory; [mixed-cadence-results.json](mixed-cadence-results.json)
records the accepted result. No runtime or driver installation is performed.

## Production browser playback

The actual production mono1280, daytime1280, ASI174 1210×760, software color3552
and NVIDIA color3552 MP4 files played through their 1.666667-second duration in
T3/Electron 44.4.2 / Chrome 152 on the operator's Mac. The smaller outputs had
zero dropped display frames; the initial native software pass reported two, and
the native NVIDIA pass reported none. All completed without a media error.
Native clips needed more buffering than the initial three-second probe allowed.
This is qualification of that browser instance, not every browser or network.
The [browser result](production-browser-results.json) retains dimensions, completion,
frame counters and limitations separately from FFmpeg's exact packet/decode proof.
