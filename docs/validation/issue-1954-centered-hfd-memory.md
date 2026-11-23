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

The failing-first fixtures had 48 detections in each frame. For both Mono16 and Bayer, the prior
sampler returned 20 valid samples when 36 stars were eligible and zero when only four were eligible.
The eligible-first sampler returned 32 and four respectively, with 16 omitted detections in each
case. The tests check which detections were kept, deterministic payloads, bounded rejection
accounting, rejection of forged allocation and ordering, and byte-identical replay under retained
recipe identities. The November Mono16 report counted 24 valid HFD measurements among the first
32 of 1,108 detections. [Original allocation finding](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1952#issuecomment-7066223900)

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
this report. Each run started with fresh identity, keys, configuration and ext4 storage. The host
ran in a private network namespace with its HTTP listener on loopback, central integration off,
and upload off. The process used the published runtime defaults for GC.

The cgroup used the split-host settings of one CPU and 1 GiB (`cpu.max` of `100000 100000` and
`memory.max` of `1073741824` bytes). `memory.swap.max` was zero. Each run used a 156-second
observation window followed by normal shutdown when the process was still running. GNU time and
`/proc` supplied process RSS. The kernel's `memory.peak` supplied the cgroup high-water mark, and
headroom is `memory.max − memory.peak`. `memory.events` tracks encounters with the limit and OOM
events. [Split-host settings](../../deploy/split-host/compose.cameraagent.yml)

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
run produced 96 detections and 32 valid HFD samples. [Historical baseline](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1954)

### Observations

Both runs used 20-second nominal slots. A missed admission is an offered slot without a committed
raw capture. The terminal unused reservation for the next slot stayed outside the observation
window. An exposure whose shutter is outside the window tolerance can still have a committed
raw capture and a published single view.

With W2 noise, all eight admissions committed. Inter-shutter intervals were 20.009–20.044 s and
shutter-to-durable-ingress times were 6.309–7.725 s. Each raw frame held 12,879,360 bytes, for
103,034,880 raw bytes total. The standard lane completed all eight captures. The eight
measurements recorded `candidate-budget-exceeded`. Eight single views and 16 revisions committed.
The first shutter was outside window tolerance and appeared in three overlapping windows. The
journal recorded five subsequent registration dispositions with single-exposure output.

The quieter control also committed all eight admissions. Inter-shutter intervals were
19.980–20.020 s and shutter-to-durable-ingress times were 5.544–6.589 s. The standard lane completed
all eight captures. The measurement records retained 669–678 detections per exposure, with 31–32
valid HFDs and 637–646 omissions. Eight single views and 16 revisions committed. Its six closed
windows had one excluded startup reference, three single-exposure dispositions, and two terminal
registration failures recorded as `processing.execution-failed`. The failure code covers multiple
exception types, so the receipt leaves the cause unresolved. The focus measurement for capture
seven retained one `no-star` result among its 32 sample entries.

| Measurement | W2 noise | Quieter control |
| --- | ---: | ---: |
| Peak process RSS via `VmHWM` | 1,048.3 MiB | 1,046.4 MiB |
| Peak cgroup charge | 1,024 MiB | 1,024 MiB |
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
user plus system CPU time, respectively. Both processes exited cleanly. RSS measures resident
process mappings. The cgroup figure measures memory charged to the group, including file cache.

After shutdown, SQLite `quick_check` passed on both journals. All 16 committed raw files and all
16 exposure-measurement payloads matched their journal SHA-256 values. Each single view reopened
its 2048 × 1375 JPEG from the exact manifest artifact and matched its recorded digest. The noisy
run logged one startup replay claim before the raw journal schema initialized. Capture and single
publication continued after initialization.

The selected limit sustained single-view publication and admission at this cadence in both short
runs, with zero cgroup headroom at peak. Successful full-resolution registered-stack publication
and longer-run capacity at this setting are unverified.
