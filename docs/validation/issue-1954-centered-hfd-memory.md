# Centered HFD allocation and standalone memory evidence

## HFD allocation

The centered measurement recipe gives eligible detections first use of its 32-sample limit. Within
each eligibility group it uses the detector's flux ranking and stable peak-position tie break.
Saturation, an out-of-bounds focus window, and a trail that fails the applicable shape check are
known before the full HFD measurement. Bayer detections with a trail flag can qualify through
the existing core check. A selected aperture can still fail the full measurement, which retains
its rejection reason. The recipe attempts at most 32 full HFD measurements.

When slots remain, the record keeps the brightest preflight rejections with their reasons.
Samples appear in detection-index order and the omitted count is the number of detections without
a sample entry. The `eligible-first-v1` recipe parameter binds the allocation to the output identity.
The `exposure-measurement-v1` payload still records the same sample and proof fields. Retained
output identities select their original brightness-first recipe on replay, including the previous
Bayer core check when that recipe used it. [Issue and acceptance](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1954)

The retained reader checks allocation against the recorded saturation and trail flags and the
focus-window geometry. For an in-bounds, trail-flagged Bayer detection, eligibility also requires
the pixel-based core check. The record stores that proof for valid samples. For an omitted flagged
detection, allocation order cannot be fully verified from the record alone. Replay repeats the
core check from the retained pixels.

The failing-first fixtures had 48 detections in each frame. For both Mono16 and Bayer, the prior
sampler returned 20 valid samples when 36 stars were eligible and zero when only four were eligible.
The eligible-first sampler returned 32 and four respectively, with 16 omitted detections in each
case. The tests check which detections were kept, deterministic payloads, bounded rejection
accounting, rejection of forged allocation and ordering, and byte-identical replay under retained
recipe identities for the full 48-detection fixtures. Reader tampering for incomplete accounting,
saturation flags and a diameter on a trailed detection is checked under both policies.

The CameraAgent replay fixture commits a live Mono16 measurement under the historical plain-input
options, reopens the journal with the normal executor, and checks the original artifact ID, output
identity and payload SHA-256. It also checks the dependent association and rejected astrometry
outputs.

The checksummed November Mono16 corpus reproduced 24 of 32 valid HFD samples on the
1,108-detection frame. Eligible-first yielded 32 of 32 with 1,076 detections omitted. Two
companion frames went from 23 to 32 valid samples (1,112 detections) and from 18 to 32
(1,100 detections). The raw brightness oracles passed and the detection lists matched under
both policies for all three frames.
[Original allocation finding](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1952#issuecomment-7066223900)

The Bayer trail label is a conservative HFD exclusion. The core calculation gives an upper bound.
A bound at or above the threshold can reflect uncertainty about roundness. A separate confirmed-trail
label would require independent positive evidence of elongation and a versioned interpretation of it.
The record uses the existing conservative reason and threshold. In the repository's round σ1.5
Bayer fixture, six detections flagged as trailed passed the core check, with zero trail exclusions
across those 64 seeds. The independent review observed seven exclusions before the correction and
three among 63 evaluable round σ1.5 stars afterward. Its σ1.2 cohort went from one exclusion to
zero, all 55 ordinary Bayer VirtualSky anchors had a valid HFD, and the flagged genuine 3, 3.5,
4 and 6 pixel controls stayed excluded. These counts describe their respective cohorts.
[Independent trail finding](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1952#issuecomment-7067245176) · [Issue clarification](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1954#issuecomment-7067250405)

## Standalone memory envelope

The local Linux x64 runs used the framework-dependent Release CameraAgent built from `8b6efe989`
with SDK 10.0.402 and run on .NET 10.0.14. That commit contains the sampler and reader used by
this report. The later retained-identity correction is specific to replay. Each run started with
fresh identity, keys, configuration and ext4 storage. The host ran in a private network namespace
with its HTTP listener on loopback, central integration off, and upload off. The process used the
published runtime defaults for GC.

The cgroup used the split-host CPU quota (`cpu.max` of `100000 100000`). The selected split-host
memory setting is 1 GiB (`memory.max` of `1073741824` bytes). `memory.swap.max` was zero in all
trials. Each run used a 156-second observation window followed by normal shutdown when the
process was still running. GNU time and `/proc` supplied process RSS. The kernel's
`memory.peak` supplied the cgroup high-water mark, and headroom is
`memory.max − memory.peak`. `memory.events` tracks encounters with the limit and OOM events.
[Split-host settings](../../deploy/split-host/compose.cameraagent.yml)

The disposable camera profile started from `virtual-asi178mc.full.json` with the centered
execution profile enabled. It delivered 3096 × 2080, 14-bit RGGB in a 16-bit little-endian
container, with native ROI, 1 × 1 binning, CFA origin `(0,0)` and stored black and white levels
of 64 and 65,535. Both runs used ControlledNight, seed 2025, a virtual timeline beginning
2026-11-19 04:00 UTC, five-second exposures at gain 150 and a 20-second minimum start interval.
The Production HYG 4.2 snapshot held 119,625 rows. Its database SHA-256 was
`b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2`.

The graph durably published qualified normalization (`Calibration` with strategy `None`, variant
`qualified-linear`) and `ProjectedScene` from raw. `LocalStorage` used the same root as raw ingress,
retained data for seven days and updated the latest frame. The normal centered workers measured,
closed windows and published views. The first run used the W2 sensor noise settings at magnitude
6.5. The quieter control set magnitude 5, zero background electrons, disabled shot and dark
noise, and used 0.01 electron of read noise at every gain point. The control reduced the source
and noise load while preserving full-frame rendering, normalization, measurement and publication.

The 20-second interval comes from the #1974 qualification workload. This run covers a short
local filesystem workload. #1928 supplied the six-exposure ordinary publication sequence and the
distinction between a missed admission, a shutter outside window tolerance, and an excluded
registration contributor. Its Linux browser result verified five-contributor Mono16 and Bayer
stacks and archive reopening. This run checks the live journal and retained files from the
constrained process. [#1928 ordinary sequence](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1928#issuecomment-7057393832) · [#1974 workload](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1974)

The historical recipe-only `b2ebaec5` workload used a 3096 × 2080 Bayer frame with 96 synthetic
sources. On four logical CPUs with workstation GC, normalization peaked at 144 MiB process RSS,
and normalization with measurement peaked at 1,204 MiB. With one CPU and a 768 MiB GC hard
limit, the measurement process peaked at about 767 MiB with identical canonical results. That
run produced 96 detections and 32 valid HFD samples. [PR #1952 F4](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1952#issuecomment-7066223900)

### Initial 1 GiB runs

Both runs used 20-second nominal slots. A missed admission is an offered slot without a committed
raw capture. The terminal unused reservation for the next slot stayed outside the observation
window. An exposure whose shutter is outside the window tolerance can still have a committed
raw capture and a published single view.

With W2 noise, all eight admissions committed. Inter-shutter intervals were 20.009–20.044 s and
shutter-to-durable-ingress times were 6.309–7.725 s. Each raw frame held 12,879,360 bytes, for
103,034,880 raw bytes total. The standard lane completed all eight captures. The eight
measurements found 5,089–5,195 candidates and recorded `candidate-budget-exceeded` before HFD.
Eight single views and 16 revisions committed.
The first shutter was outside window tolerance and appeared in three overlapping windows. The
journal recorded five subsequent registration dispositions with single-exposure output.

The quieter control also committed all eight admissions. Inter-shutter intervals were
19.980–20.020 s and shutter-to-durable-ingress times were 5.544–6.589 s. The standard lane completed
all eight captures. The measurement records retained 669–678 detections per exposure, with 31–32
valid HFDs and 637–646 omissions. Eight single views and 16 revisions committed. Its six closed
windows had one excluded startup reference, three single-exposure dispositions, and two terminal
registration failures recorded as `processing.execution-failed`. The focus measurement for capture
seven retained one `no-star` result among its 32 sample entries.

| Measurement | W2 noise | Quieter control |
| --- | ---: | ---: |
| Peak process RSS via `VmHWM` | 1,048.3 MiB | 1,046.4 MiB |
| Peak cgroup charge | 1,024 MiB | 1,024 MiB |
| Anonymous charge at a sampled 1,024 MiB | 788.7 MiB | 814.1 MiB |
| File charge at the same sample | 226.4 MiB | 201.9 MiB |
| Cgroup headroom at peak | 0 MiB | 0 MiB |
| Cgroup limit events | 957 | 748 |
| Kernel OOM events and kills | 0 / 0 | 0 / 0 |
| Committed raw admissions / offered slots | 8 / 8 | 8 / 8 |
| Missed admissions | 0 | 0 |
| Raw bytes retained | 103,034,880 | 103,034,880 |
| Cgroup write bytes | 289,529,856 | 250,392,576 |
| Local state bytes at shutdown | 237,037,604 | 217,618,034 |
| Shutter to first single-view publication | 6.650–8.683 s | 6.055–9.891 s |

GNU time separately reported 1,048.3 MiB and 1,045.0 MiB maximum RSS and 58.40 s and 42.93 s of
user plus system CPU time, respectively. Both processes exited cleanly. RSS includes the process's
resident mappings, including shared mappings whose pages can be charged to a different cgroup.
The cgroup figure includes file cache charged to the group. The anonymous and file values come
from `memory.stat` at the highest sampled `memory.current`. The kernel high-water mark does not
record its own component breakdown.

After shutdown, SQLite `quick_check` passed on both journals. All 16 committed raw files and all
16 exposure-measurement payloads matched their journal SHA-256 values. Each single view reopened
its 2048 × 1375 JPEG from the exact manifest artifact and matched its recorded digest. The noisy
run logged a failed startup replay claim with `InvalidOperationException` and the message
"Raw ingress schema 17 must be initialized before capture processing schema 12." Capture and
single publication continued after initialization. The Kepler follow-up is to make the replay
worker wait for raw journal initialization before claiming work on an empty standalone root.

### Registration failure diagnosis

The original quieter run recorded the generic `processing.execution-failed` code for reference
captures 2 and 5. A repeat of that profile at 1 GiB logged first-chance exceptions locally and
reproduced the code for reference captures 2 and 3. In each case the first throw was
`System.OutOfMemoryException` in `LinearRegisteredMean`, when `CenteredRegisteredStackRecipe`
allocated its full-frame RGB sum array (154,552,320 bytes). The runtime reported 805,306,368 bytes
available to the managed heap. The repeat reached 1,024 MiB cgroup charge with 926 limit events
and zero kernel OOM events or kills. The exception trace attributes the repeated failures to
managed memory pressure. The original run's generic receipts contain no exception type.

The completed single-exposure dispositions in the control also show a separate registration
limit. The centroid matcher recorded `AmbiguityCheckLimitExceeded` for neighboring inputs when
their eligible source field exceeded its 192-source limit.

### Matched registered-stack trials

These trials kept the W2 sensor's shot, dark and read noise, the five-second exposures at gain
150, and the same 20-second cadence, resolution, graph and storage. They fixed the VirtualSky
scene at the sequence anchor, used magnitude 5 and set the renderer's candidate cap to 64
(`OperatorSceneObjectLimit` with `maximumResults: 64`). This field keeps the centroid matcher
within its source limit and exercises HFD and registered stacking with the same camera noise.
All eight raw payload SHA-256 values matched by capture order across the limits. Each input was
12,879,360 bytes, for 103,034,880 raw bytes per trial. The 1 GiB trial used the first-chance
exception trace from the diagnostic. The higher limits used the published host directly.

| Measurement | 1 GiB | 2 GiB | 3 GiB |
| --- | ---: | ---: | ---: |
| Peak process RSS via `VmHWM` | 1,097.9 MiB | 1,837.7 MiB | 1,860.7 MiB |
| Peak cgroup charge | 1,024 MiB | 2,048 MiB | 2,111.6 MiB |
| Highest sampled cgroup charge | 1,024.0 MiB | 2,047.4 MiB | 2,099.1 MiB |
| Anonymous charge at that sample | 771.0 MiB | 1,613.1 MiB | 1,635.8 MiB |
| File charge at that sample | 244.0 MiB | 417.9 MiB | 446.3 MiB |
| Cgroup headroom at peak | 0 MiB | 0 MiB | 960.4 MiB |
| Cgroup limit events through shutdown | 2,532 | 80 | 0 |
| Kernel OOM events and kills | 0 / 0 | 0 / 0 | 0 / 0 |
| Committed raw admissions / offered slots | 8 / 8 | 8 / 8 | 8 / 8 |
| Missed admissions | 0 | 0 | 0 |
| HFD measurements with 32 valid samples | 7 / 8 | 8 / 8 | 8 / 8 |
| Registered stacks committed | 4 | 5 | 5 |
| Registered stack views published | 3 | 4 | 5 |
| Process user plus system CPU time | 63.34 s | 67.13 s | 44.78 s |
| Cgroup write bytes | 414,175,232 | 475,017,216 | 475,963,392 |
| Local state bytes at shutdown | 381,134,890 | 439,824,105 | 439,947,941 |
| Inter-shutter interval | 20.010–20.017 s | 20.011–20.016 s | 20.010–20.017 s |
| Shutter to durable raw ingress | 5.970–6.528 s | 5.904–6.369 s | 5.770–6.005 s |
| Reference shutter to first stack view revision | 52.776–54.773 s | 52.553–54.648 s | 52.368–52.418 s |

At 1 GiB the worker committed four sets of registered 3096 × 2080 RGB and support planes and
published three registered views with three or four contributors. First-chance traces captured
`System.OutOfMemoryException` allocating the Bayer decoder's color planes during the registration
for reference capture 4. A second managed OOM occurred in `PackRows` while processing capture 7.
Its raw admission committed, its single view used the raw fallback, and its HFD record reported
`linear-input-unavailable`. The journal recorded both failures as `processing.execution-failed`.

At 2 GiB all eight normalizations and measurements completed. The HFD inputs had 93–107
detections per exposure, with 32 valid samples in each. Five registered stacks committed,
including three with five contributors. Four stack views published before the observation ended.
The registered views centered on captures 4 and 5 each had all five contributors. There were
zero registration or normalization execution failures. The cgroup still reached its limit.

At 3 GiB all eight normalizations and HFD measurements completed. All five registered stacks
published their views. The stacks for reference captures 4, 5 and 6 each included five
contributors. There were zero registration or normalization execution failures. This trial
left 960.4 MiB of cgroup headroom and recorded zero limit events.

Each trial published eight single views. The startup shutter fell outside tolerance in each
trial and still committed as a raw admission. At 1 GiB and 2 GiB the stack for reference capture 6
committed its planes and receipt, with the view pending at shutdown. The startup replay ordering
error also appeared at 2 GiB and 3 GiB. Capture, measurement and registration proceeded after raw
ingress initialization.

All three journals passed SQLite `quick_check`. Retained raw files and all available processing
outputs matched their payload digests. The registered previews decoded as 2048 × 1375 JPEGs.
The previews for reference captures 2 and 3 matched across all three limits. The five-contributor
previews for captures 4 and 5 matched between 2 GiB and 3 GiB. The 3 GiB preview for reference
capture 6 had SHA-256 `6d34417a1589cd6fb9a6569454f94877b6952dfc3949989c320b741c24ac1c1a`.

The selected 1 GiB limit sustained raw admissions and some registered publication in this
workload, with zero headroom and managed allocation failures. At 2 GiB the full registration
path completed and the cgroup reached its limit. The 3 GiB result establishes measured
headroom for this short standalone workload. Steady-state capacity over longer retention and
other camera profiles remains unmeasured.
