# Celestial image pipeline: dataflow, cost and acceleration (#1170)

This is the #1170 whole-pipeline performance record under epic #520. It maps the composed CameraAgent
celestial path as shipped, and records how it was measured. It ranks where the cost goes and dispositions
each candidate, including managed SIMD, native code and GPU acceleration.

The first PR (`Refs #1170`) delivers:

- the manifest and runner;
- a fresh CPU baseline and the after evidence;
- the dataflow map;
- three byte-exact, evidence-backed changes: two Imaging kernels and processing-store connection pooling;
- the analysis tooling that compares the arms.

Later sibling integrations refresh the evidence at the composition checkpoints below. The final
`Closes #1170` PR carries the last refresh.

Raw packs stay outside the repository under the evidence root named in [Retained evidence](#retained-evidence).
Only summaries and hashes are committed.

**Status.**
- Primary interleaved matrix: A `7b6b3bc9`, B `d1c3ffb1`, product base `b13f0d0e`.
- Post-sync confirmation: A′ `7a2af5b7`, B′ `8f3f1fdb`, base `0639e27d`.
- Every pack's archive hash is in [Retained evidence](#retained-evidence).

## Scope and honesty boundaries

- **Measured:** the shipped CameraAgent composition, from VirtualSky capture through raw ingress, lane claim,
  the configured processing graph and completion, on development host `home-dev-01`. The harness is
  `tests/HVO.SkyMonitor.CameraAgent.Tests/Issue1170ComposedPipelineEvidenceTests.cs`, which drives the real
  host services, not a library-only reconstruction.
- **Owned by open siblings: measured, not changed.**
  - #518: the VirtualSky renderer and scene schema.
  - #1126: projection and the astrometry solver. It merged into `development/v1` (#1174) after the primary
    matrix was measured, so it is in the post-sync base `0639e27d` and is covered only by the post-sync
    confirmation.
  - #526: calibration.

  Their costs appear in the attribution, and their candidates are reported for the owning issue or the batched
  follow-up below. Nothing in those areas is edited by #1170 until they merge and a refresh
  shows a Tier C, byte-exact or version-tolerance change.
- **Not available:**
  - The original #1098, #1106 and #522 raw evidence packs do not exist; the operator confirmed that. This record makes no
    claim about their timings. S1 re-runs the committed #1106 manifest as a new generation instead.
  - `perf` is unavailable on the measurement host (`kernel.perf_event_paranoid=4`; no sysctl was changed),
    so native frames inside SQLite and the runtime are attributed through EventPipe only. They show as unresolved
    native time where EventPipe cannot name them.
- **Not run:**
  - Raspberry Pi execution, which #1171 owns.
  - Any GPU prototype, which needs operator approval.
  - Physical camera capture, owned by #288 and #1107.

  Host results here are never Pi speedups.

## Method

The manifest is `docs/validation/issue-1170-pipeline-manifest.json`. It was declared before
measurement and fixes the cells, trial counts, collectors and regression rule. The runner is
`docs/validation/issue-1170-pipeline.sh`. `docs/validation/issue-1170-metrics.sh` summarizes a pack and
applies the rule to a baseline/after pair. `docs/validation/issue-1170-oncpu.py` splits EventPipe thread-time
samples into running, waiting, I/O, GC-suspension and unresolved-native time.

- **Sections.**
  - S1 is the unchanged #1106 manifest with `DOTNET_TieredCompilation=0`, kept comparable with #1106.
  - S2 is the steady state per workload: W1 night long/short, day and dense; W2 CFA; W6 large ASI676; and the sample night.
  - S3 is cold start.
  - S4 is saturation and drain.
  - S2 to S4 run with production runtime defaults (tiered compilation and dynamic PGO on), and every
    `DOTNET_*` variable is recorded.
  - The `tc0-side-cell` profile is labelled and is never production latency.
- **Trials.** Each S2/S4 trial is a separate process with 5 warm-up and 30 measured operations, 5 trials per cell.
  Trial medians are reported as median/min/max across trials. p95 is reported per trial and pooled over all
  measured operations, never from trial medians.
- **Per-stage evidence.** Each sample records:
  - wall time, thread CPU time and precise allocated bytes;
  - GC counts;
  - `/proc/self/io` deltas for capture, publish, claim, handle and complete.

  Each trial also records peak RSS (`VmHWM`), LOH size and JIT time.
- **Attribution.** Attribution trials attach `dotnet-trace` 10.0.745401 through a PID handshake. The harness waits
  until the session is live and stops it with an in-process `MeasurementComplete` event, so the rundown is not
  truncated. Collector overhead is the attribution trial against the measurement median of the same cell.
- **Host discipline.**
  - Heavy work runs under `flock -o` on a dedicated lock (#520 addendum 003), with MSBuild node reuse and
    the compiler and Razor build servers disabled.
  - The run never starts a build server of its own, so nothing of its own outlives it.
  - Method amendment, recorded in the manifest's `methodAmendments`:
    - The baseline S1 and measure packs at `d942bd86` ran `dotnet build-server shutdown` before S1 and
      before every trial, as #520 addendum 003 required.
    - Addendum 004 withdrew that on every host because it stops other sessions' servers too.
    - Later packs run without it. A W2 control pack ran at scratch revision `7b6b3bc9`: the baseline plus
      only that runner change, with product source byte-identical to `b13f0d0e`.
    - The control came out above the baseline trial spread. Processing was +6.1%, foreground +7.4% and
      service +6.3%, with allocations identical (-0.0015%) and peak RSS -0.4%. The cause is not established.
      Session-to-session host drift fits the data, but it is a hypothesis, not a finding.
  - **Finding: timings drift between sessions.** On `home-dev-01`, timings drift about 6% between sessions about
    an hour apart, so comparisons across sessions are invalid for #1170. The `d942bd86` baseline measure pack
    is superseded for comparison for that reason. It is retained, and its S1 pack is still the output-identity
    reference.
  - Each trial waits for the one-minute load average to fall below 0.5.
  - Tenants are recorded before and after every trial, not paused.
  - **Hypothesis: the guests share a host.** `home-dev-01` and `home-dev-02` are both 12-vCPU KVM guests on an
    i9-14900K, and they probably share one physical host.
    - With `home-dev-02` at load 31, `home-dev-01` showed about 7 steal ticks per 10 s while idle.
    - Contention between the guests is therefore a candidate cause of the drift. It is a hypothesis, not a
      finding.
    - The primary matrix did not record steal or the load on `home-dev-02`. The post-sync confirmation records
      both, and it runs inside a quiet-neighbour window: its arms run while this session holds
      `home-dev-02`'s heavy lock.
- **On-CPU classifier amendment.** Commit `d710f5c1` changes `issue-1170-oncpu.py`, which is analysis only and
  not part of the measured path:
  - The VSTest message loop in the test host blocks in `Socket.Poll`. Its native interval ends in
    `SocketPal.Poll` with no interop frame, so the first version counted it as running native time. That was
    about a third of the W2 and W6 running totals. It is now a wait.
  - Equal-time frames rank by name, so a re-run gives the same output.
  - Re-running the first version on copies of the baseline traces reproduces the recorded values exactly.
  - Both arms' traces are reclassified with the amended script on `home-dev-02`, outside the retained packs,
    so `home-dev-01` stays quiet during measurement.
- **Interleaved comparison.** Baseline and after are measured as adjacent pairs, one cell at a time:
  - Arm A is the scratch revision `7b6b3bc9`, the baseline product with the amended runner. Arm B is the after
    head `d1c3ffb1`. Both use the same runner, lock, load gate, counts and trials.
  - Each arm is one unchanged runner invocation restricted to the cell with `HVO_1170_CELLS='^<cell>$'`.
  - Order is counterbalanced. Odd manifest cells run A then B, and even cells run B then A.
  - `docs/validation/issue-1170-interleaved.py` compares each cell only against its adjacent pair.
  - The comparison counts only when coverage is complete:
    - every slot holds exactly one A pack and one B pack;
    - both arms are measure packs on the recorded manifest, with trials 1 to 5 and the same single cell;
    - each indexed trial is its own evidence document: the runner's `<cell>-t<trial>` name, the indexed
      `evidenceSha256`, and the document's own trial, scenario, workload, runtime profile and revision. An arm
      holds no unindexed evidence, and both metrics and output identity read exactly the indexed documents;
    - the cell is a manifest cell, and its scenario, workload and runtime profile are the ones the index rows,
      the evidence documents and the metrics key all carry, so one cell's evidence cannot be credited to another;
    - no cell appears in two slots;
    - the measured cells equal the expected cell list, a required argument, so a missing slot is a gap.
    Any gap makes the comparison incomplete and fails it.
- **Timing verdict** (per cell, on trial medians):

  | Verdict | When |
  |---|---|
  | Regressed | B's fastest trial is slower than A's slowest. Blocks merge unless explained. |
  | Improved | B's slowest trial beats A's fastest. Only these cells support an improvement claim. |
  | No resolvable change | Anything in between |

- **Output identity.**
  - S1 at the after head is compared leaf by leaf against the `d942bd86` S1 pack, ignoring only a narrow,
    recorded set of timing, resource and provenance keys.
  - Every interleaved pair compares each sample's raw hash, size and measured flag, plus each pipeline node's
    status, outcome and output count. It also compares each output's payload hash, size, recipe identity and
    output identity. A node with no outputs is still compared.
  - A value that varies between A's own trials is excluded only for that record. Only recipe and output
    identities may vary; a payload, size, status or outcome that varies is a difference. B must equal A on every
    other value.
  - Any output difference stops the run.
- **Resource rule.** This is the manifest rule, applied within each pair:

  | Metric | Regression when |
  |---|---|
  | Allocated bytes | Above 1% |
  | Full-frame copy count | Any increase |
  | Peak RSS | Above max(5%, 32 MiB) |

  The manifest's timing rule (above max(5%, the baseline trial-median spread)) assumed one session. The
  interleaved verdict replaces it for timings, including the S4 drain. Unexplained regression blocks merge.

**Measurement host.** Every pack carries this record.

| Field | Value |
|---|---|
| Host | `home-dev-01`, a KVM guest on an Intel Core i9-14900K |
| Logical processors | 12 |
| Memory | 15.0 GiB (`MemTotal` 15,707,108 kB) and 16 GiB swap. The #520 assignment stated 20 GiB; the recorded value is the actual one. |
| Kernel | Linux 6.8.0-142-generic. CPU governor not exposed in the VM. |
| SDK | .NET SDK 10.0.401 |
| Tracing | `dotnet-trace` 10.0.745401 |
| `perf` | Unavailable (`perf_event_paranoid=4`) |
| `DOTNET_*` | No caller `DOTNET_*` variables set. Each cell's runtime variables are recorded per trial. |
| Manifest SHA-256 | Baseline packs at `d942bd86`: `f5ed9eaf1658791f129305ede009e7fe9647f8eccb01514c41452de47889d58d`. Interleaved arms, the `7b6b3bc9` attribution and the after packs: `ca0f1ba779010c60ce3542b79bc37b763379f161869be170f6a03c2c3809f840`. The only change is the build-server method amendment. |

The host also served other sessions during measurement. They were recorded before and after each pack, and
the load gate held every trial until the one-minute load average was below 0.5.
- The longest wait across all retained packs was 170 s, well inside the 1,800 s limit. No trial started at a
  load of 0.5 or above. These figures cover all 235 runner-recorded trials in the 39 runner indexes on
  `home-dev-01`, read from each index's `load` (`postsync-analysis/r1-correction/gate-waits-home-dev-01.tsv`). The
  170 s wait is `7b6b3bc9/control-w2`. The post-sync and interleaved mirrors on `home-dev-02` hold 152 of those
  trials (110 interleaved, 40 post-sync measure and 2 S1), and their longest wait is 150 s.
- The runner now fails closed: an expired or unreadable gate stops the run before the next trial and writes no
  index. Before review r0 an expired gate let the trial start, but that never happened in the retained evidence.

## Composed path and ownership

One frame of the shipped graph, in order. The owner column names the assembly, plus the open sibling issue
where one is integrating there.

| Stage | What runs | Owner |
|---|---|---|
| Capture | VirtualSky scene, stellar exposure plan, PSF rasterization, sensor plane render and quantize | CameraAgent.Common `Modules/VirtualSky`, Imaging, Astronomy (#518 renderer/scene schema) |
| Publish | Raw SHA-256, raw `.bin` write-through, fsync, rename and directory fsync; journal reservation and commit | CameraAgent.Common `RawIngress` |
| Claim | Lane claim (SQLite) and existence checks | CameraAgent.Common `Capture/Distribution` |
| Handle: raw load | Read the published raw file back and verify its checksum | CameraAgent.Common `Capture/FrameProcessingWorker`, AgentCore `FrameReconstructor` |
| Handle: calibration | Calibration recipe (strategy None packs rows, then the product SHA) | Processing (#526 calibration) |
| Handle: rolling combination | Restores the window's earlier outputs from disk, then the Linear16 arithmetic mean | CameraAgent.Common `Capture/Processing`, Imaging |
| Handle: previews | Mono16 percentile/asinh display stretch to Mono8 | Imaging |
| Handle: annotation | Annotation render on Mono8 | Processing, Imaging |
| Handle: projected scene | Scene JSON staging, hashing and persistence | CameraAgent.Common `Modules/VirtualSky` (#1126 projection) |
| Handle: persistence | Each product written through with fsync, rename and directory fsync, then read back and stream-hashed; sidecars and index appended | CameraAgent.Common `Storage`, `Capture/Processing` |
| Handle: local and archive storage | Selected products written again to the agent and archive roots; latest-frame snapshots | CameraAgent.Common `Capture/Processing`, `Frames` |
| Handle: node state | Execution lease, node attempt and output rows (SQLite, `synchronous=FULL`) | CameraAgent.Common `Capture/Processing` |
| Complete | Lane completion (SQLite) | CameraAgent.Common `Capture/Distribution` |

## Data movement per frame

These figures are for a steady-state sample-night frame: Mono16 484×304, 294,272 bytes. They come from reading
the code along the harness graph, with a full RollingCombination window and automatic publication allowed. All
products use the packed encoding, so no JPEG encode runs.

| Category | Count per frame | Bytes |
|---|---|---|
| Fresh full-frame buffers (all LOH) | 19: 2 in capture (#518), 17 in handle | about 5.44 MB |
| Pure memory copies | 9 | 1,912,768 |
| In-memory SHA-256 passes | 11 | about 2.80 MB |
| Stream SHA-256 read-backs of files just written | 10 | about 1.91 MB |
| Full-frame files written (each with fsync, rename and directory fsync) | 11 | 2,207,040 |
| Full-frame files read | 15: raw 1, window restore 4, post-write verify 10 | 3,384,128 |
| Full-frame `FileStream`s opened | about 26 | |

This PR does not change any of these counts. Its three changes affect compute and connection lifetime, not
buffers or files. Copy-count candidates are in the findings table.

## Results

### Primary interleaved matrix

All 22 interleaved arms passed with 5 trials each. Every value below is the median of trial medians, with the
trial-median range in brackets. Each B arm is judged only against its adjacent A arm. Product base for every
number is `b13f0d0e`, before #1126; see [Composition checkpoints](#composition-checkpoints).

| Pair | Cell | Order | Metric (ms) | A `7b6b3bc9` | B `d1c3ffb1` | Median change | Verdict |
|---|---|---|---|---|---|---|---|
| 01 | s2-sample-night | AB | Processing | 170 [160–204] | 149 [131–151] | -12.6% | **improved** |
|  |  |  | Service | 282 [260–354] | 305 [244–327] | +8.3% | no resolvable change |
|  |  |  | Foreground | 108 [103–147] | 166 [112–177] | +54.2% | no resolvable change |
| 02 | s2-w1-night-long | BA | Processing | 278 [272–321] | 219 [211–247] | -21.0% | **improved** |
|  |  |  | Service | 479 [476–555] | 428 [416–491] | -10.7% | no resolvable change |
|  |  |  | Foreground | 205 [200–218] | 212 [206–244] | +3.1% | no resolvable change |
| 03 | s2-w1-night-short | AB | Processing | 265 [263–267] | 194 [188–200] | -26.7% | **improved** |
|  |  |  | Service | 337 [336–340] | 263 [258–273] | -22.0% | **improved** |
|  |  |  | Foreground | 74 [73–75] | 72 [70–74] | -2.6% | no resolvable change |
| 04 | s2-w1-day | BA | Processing | 251 [239–284] | 173 [167–181] | -30.9% | **improved** |
|  |  |  | Service | 409 [389–502] | 325 [316–338] | -20.5% | **improved** |
|  |  |  | Foreground | 152 [148–180] | 151 [146–159] | -0.4% | no resolvable change |
| 05 | s2-w1-dense | AB | Processing | 284 [255–345] | 259 [240–275] | -9.0% | no resolvable change |
|  |  |  | Service | 501 [460–604] | 496 [467–565] | -0.9% | no resolvable change |
|  |  |  | Foreground | 216 [203–262] | 241 [218–299] | +11.3% | no resolvable change |
| 06 | s2-w2-night | BA | Processing | 1,542 [1,438–1,772] | 1,267 [1,236–1,678] | -17.8% | no resolvable change |
|  |  |  | Service | 2,598 [2,449–2,948] | 2,233 [2,179–2,832] | -14.0% | no resolvable change |
|  |  |  | Foreground | 1,057 [1,008–1,108] | 961 [942–1,123] | -9.1% | no resolvable change |
| 07 | s2-w6-night | AB | Processing | 1,729 [1,654–1,916] | 1,473 [1,407–1,487] | -14.8% | **improved** |
|  |  |  | Service | 2,808 [2,670–3,065] | 2,491 [2,415–2,539] | -11.3% | **improved** |
|  |  |  | Foreground | 1,091 [1,019–1,114] | 1,024 [1,006–1,028] | -6.1% | no resolvable change |
| 08 | s2-w1-night-long-tc0 | BA | Processing | 371 [326–472] | 289 [246–328] | -21.9% | no resolvable change |
|  |  |  | Service | 651 [593–860] | 593 [501–623] | -8.9% | no resolvable change |
|  |  |  | Foreground | 275 [267–384] | 282 [255–286] | +2.5% | no resolvable change |
| 09 | s3-sample-cold | AB | Foreground | 617 [600–687] | 745 [671–1,108] | +20.8% | no resolvable change |
|  |  |  | First-operation service | 1,087 [1,071–1,185] | 1,386 [1,172–1,755] | +27.5% | no resolvable change |
| 10 | s3-w1-cold | BA | Foreground | 849 [797–878] | 924 [805–1,079] | +8.9% | no resolvable change |
|  |  |  | First-operation service | 1,456 [1,389–1,493] | 1,607 [1,349–1,722] | +10.3% | no resolvable change |
| 11 | s4-w1-saturation | AB | Foreground | 252 [232–260] | 250 [235–259] | -0.8% | no resolvable change |
|  |  |  | Drain after last accept | 2,099 [1,821–3,089] | 343 [199–550] | -83.7% | **improved** |

| Pair | Cell | Allocated bytes per operation, A → B | Change | Peak RSS change | Output values compared | Outputs |
|---|---|---|---|---|---|---|
| 01 | s2-sample-night | 202.3 MiB → 200.7 MiB | -0.84% | +0.8% | 1,750 | identical |
| 02 | s2-w1-night-long | 323.6 MiB → 305.0 MiB | -5.73% | -9.0% | 1,750 | identical |
| 03 | s2-w1-night-short | 129.6 MiB → 111.2 MiB | -14.24% | +1.2% | 1,750 | identical |
| 04 | s2-w1-day | 275.7 MiB → 257.2 MiB | -6.71% | +0.6% | 1,750 | identical |
| 05 | s2-w1-dense | 323.5 MiB → 305.0 MiB | -5.73% | -8.3% | 1,750 | identical |
| 06 | s2-w2-night | 1,839.9 MiB → 1,790.2 MiB | -2.70% | +0.4% | 1,750 | identical |
| 07 | s2-w6-night | 1,173.2 MiB → 1,076.4 MiB | -8.25% | -1.1% | 1,750 | identical |
| 08 | s2-w1-night-long-tc0 | 326.6 MiB → 308.1 MiB | -5.67% | -8.9% | 1,750 | identical |
| 09 | s3-sample-cold | 214.1 MiB → 213.0 MiB | -0.52% | +0.8% | 50 | identical |
| 10 | s3-w1-cold | 320.1 MiB → 302.0 MiB | -5.64% | -4.2% | 50 | identical |
| 11 | s4-w1-saturation | 295.0 MiB → 304.8 MiB | +3.31% (rule trip, see below) | -8.1% | 1,750 | identical |

**Output identity.**
- Every pair is identical on every stable output value: 1,750 per steady or saturation pair and 50 per cold
  pair, under the comparator as corrected in review r0 (see [Comparator corrections](#comparator-corrections-reviews-r0-to-r2)).
  - Per capture that is 3 sample values, 27 node values (9 nodes) and 20 output values. A steady pair has
    35 captures and a cold pair has 1.
  - The values excluded as varying between A's own trials are the recipe and output identities of the
    ProjectedScene and Annotation outputs. That is 4 per capture, 140 per steady pair and 4 per cold pair.
- S1 at `d1c3ffb1` is identical to the `d942bd86` S1 pack across all 16 runs and 17 reports, with 0 differences. The 118
  ignored leaf keys were audited, and each is a timing, allocation, working-set, throughput or revision field.

**S4 allocation rule trip, explained.**
- In saturation a sample's allocated bytes are the process-wide `GC.GetTotalAllocatedBytes` delta over that
  frame's foreground window, while a concurrent drain processes earlier frames. Drain allocation is counted
  only when it falls inside a foreground window.
- The decomposition below splits each trial's whole-run allocation into four parts: setup, the five serial
  warmup operations, the in-window sum of the 30 measured foreground deltas, and the residual. The residual is
  drain work after the last accept, the gaps between windows and the final output read. Share is the
  residual's share of the saturation phase (whole run minus setup and warmup). GB are 10^9 bytes.
- The residual is defined by the sum identity whole = setup + warmup + in-window + residual, so the four parts
  always add up to the recorded whole-run total exactly; for A trial 1 that total is 11,948,142,600 bytes.

| Arm | Trial | Whole run (GB) | Setup | Warmup | In-window | Residual | Share | Whole ÷ 30 (MB) | Backlog peak | Drain (s) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| A | 1 | 11.948 | 0.064 | 1.678 | 9.194 | 1.012 | 9.9% | 398.3 | 7 | 1.82 |
| A | 2 | 11.950 | 0.064 | 1.678 | 9.059 | 1.150 | 11.3% | 398.3 | 8 | 3.09 |
| A | 3 | 11.945 | 0.064 | 1.679 | 9.083 | 1.119 | 11.0% | 398.2 | 8 | 2.25 |
| A | 4 | 11.950 | 0.064 | 1.680 | 9.178 | 1.029 | 10.1% | 398.3 | 8 | 2.10 |
| A | 5 | 11.950 | 0.064 | 1.680 | 9.133 | 1.073 | 10.5% | 398.3 | 8 | 2.04 |
| B | 1 | 11.277 | 0.064 | 1.583 | 9.417 | 0.213 | 2.2% | 375.9 | 2 | 0.34 |
| B | 2 | 11.278 | 0.064 | 1.583 | 9.401 | 0.230 | 2.4% | 375.9 | 3 | 0.44 |
| B | 3 | 11.283 | 0.064 | 1.583 | 9.324 | 0.312 | 3.2% | 376.1 | 3 | 0.55 |
| B | 4 | 11.273 | 0.064 | 1.583 | 9.476 | 0.150 | 1.6% | 375.8 | 2 | 0.21 |
| B | 5 | 11.265 | 0.064 | 1.580 | 9.478 | 0.143 | 1.5% | 375.5 | 2 | 0.20 |

- The hypothesis predicted two things, and both hold:
  - A's out-of-window share is clearly larger: 9.9–11.3% against 1.5–3.2%. It tracks drain time.
  - Whole run ÷ 30 operations is 398.2–398.3 MB for A and 375.5–376.1 MB for B, −5.6% with disjoint
    ranges, which matches steady-state W1's −5.7%. With warmup and setup excluded, the saturation phase alone
    (in-window + residual) ÷ 30 is 340.1–340.3 MB against 320.7–321.2 MB, also −5.6% and disjoint.
- The +3.3% per-window figure therefore comes from where the drain's allocation is attributed, not from
  extra allocation.
- The in-window sum rises (9.06–9.19 GB to 9.32–9.48 GB) only because B's drain finishes most of its work
  while frames are still being accepted. Gen0 counts are equal (900–913), all 30 operations complete in
  both arms, and peak RSS is −8.1%.
- The harness metric is unchanged in this PR. The decomposition was computed from the existing packs by
  `docs/validation/issue-1170-s4-allocation.py`; the coordinator re-checked it and accepted the trip as explained.

**Disclosed, not resolved.** Neither of these is a regression under the timing verdict, and both causes are
hypotheses.
- **Sample-night foreground (+54% at the median).**
  - The whole difference is in the `capture` stage, which is bimodal in both arms: high mode 137–163 ms, low
    mode 88–112 ms. A was in the high mode in 1 of 5 trials, B in 3 of 5.
  - High-mode trials also use more capture CPU (172–200 ms against 114–138 ms) at equal allocation, and they
    compile more methods.
  - No code changed between the arms is on the capture path. Per-process tiered/PGO code-generation variance
    fits.
- **Cold-start medians (+27% sample, +10% W1 first-operation service).**
  - The slow B trials form one window, 02:48:03–02:49:06Z: all of pair 09's B arm and the first three trials
    of pair 10's.
  - In those trials the unchanged `capture`, `claim` and `complete` stages were slower too, and JIT time was
    1,020–1,342 ms against 745–846 ms for about 1% more compiled IL.
  - B's two trials outside the window match A on JIT time and on every stage.
  - Steal time was not recorded for the primary matrix. A host-level slowdown during that minute fits. The
    synced-head re-run of both cold cells recorded steal and shows no regression; see
    [Post-sync confirmation](#post-sync-confirmation).
- **Within-pair drift.** W2's B arm ran its first two trials at 1,575–1,682 ms processing and its last three
  at 1,238–1,269 ms. The host shifts within a pair as well as between sessions, which is why most W2 and
  foreground comparisons here do not resolve.

### Post-sync confirmation

After #1174 merged #1126 into `development/v1`, the branch was synchronized to `0639e27d`. Four pairs were
re-run there with the same runner, method and verdict rule.
- **Arms.** A′ = `7a2af5b7` is a scratch commit whose src is byte-identical to `0639e27d`, and it is not
  proposed for merge. `git diff 0639e27d 7a2af5b7 -- src` is empty. B′ = `8f3f1fdb` is the merged branch head.
  - The runner's manifest still names product base `b13f0d0e`. Its `armAProductSourceUnchangedFromBase` field
    is therefore false for A′, and the diff above is the identity proof instead.
- **Verdict weight.** These pairs bear verdicts, and a regression would block.
- **Quiet neighbour.**
  - The `home-dev-02` heavy lock was held from 03:36:45Z to 04:14:10Z (holder pid 244494, 75-minute cap). It
    was released early, after the last timing trial (04:01:37Z), while S1 ran. S1 is output identity only.
  - The `home-dev-02` sampler over the window, 03:36:46–04:14:08Z (449 samples): busy median 1.1%, p95 2.6%,
    max 10.7%; load1 median 0.16. load1 stayed above 2 only until 03:37:51Z, the decay of a gate that exited
    just before the lock was taken.
- **Steal.** `home-dev-01` steal ticks were sampled once a second (`clk_tck` 100, 12 vCPUs) and attributed to
  each trial:
  - the two steady and saturation pairs: 5–12 ticks per trial;
  - the cold pairs: 0–3 ticks.
  - The most any trial lost is 0.12 CPU-seconds. Every trial finished while the neighbour was quiet.

| Pair | Cell | Order | Metric (ms) | A′ `7a2af5b7` | B′ `8f3f1fdb` | Median change | Synced-head verdict | Primary-matrix verdict |
|---|---|---|---|---|---|---|---|---|
| 01 | s2-w1-night-short | AB | Processing | 260 [258–265] | 191 [189–199] | -26.5% | **improved** | **improved** (pair 03) |
|  |  |  | Service | 331 [329–335] | 260 [257–267] | -21.5% | **improved** | **improved** |
|  |  |  | Foreground | 71 [70–72] | 69 [68–72] | -3.3% | no resolvable change | no resolvable change |
| 02 | s4-w1-saturation | AB | Foreground | 224 [221–228] | 228 [226–236] | +2.0% | no resolvable change | no resolvable change (pair 11) |
|  |  |  | Drain after last accept | 2,322 [2,260–2,492] | 438 [264–486] | -81.1% | **improved** | **improved** |
| 03 | s3-sample-cold | BA | Foreground | 576 [574–578] | 578 [571–589] | +0.3% | no resolvable change | no resolvable change (pair 09) |
|  |  |  | First-operation service | 1,006 [998–1,016] | 968 [962–980] | -3.8% | **improved** | no resolvable change |
| 04 | s3-w1-cold | AB | Foreground | 790 [782–853] | 842 [796–863] | +6.5% | no resolvable change | no resolvable change (pair 10) |
|  |  |  | First-operation service | 1,375 [1,359–1,475] | 1,401 [1,314–1,425] | +1.9% | no resolvable change | no resolvable change |

| Pair | Cell | Allocated bytes per operation, A′ → B′ | Change | Peak RSS change | Output values compared | Outputs |
|---|---|---|---|---|---|---|
| 01 | s2-w1-night-short | 129.6 MiB → 111.2 MiB | -14.21% | +0.8% | 1,750 | identical |
| 02 | s4-w1-saturation | 289.2 MiB → 303.9 MiB | +5.10% (rule trip, explained below) | -10.2% | 1,750 | identical |
| 03 | s3-sample-cold | 214.2 MiB → 212.9 MiB | -0.62% | -0.3% | 50 | identical |
| 04 | s3-w1-cold | 320.4 MiB → 302.1 MiB | -5.70% | -4.2% | 50 | identical |

- **Result.** No pair regressed and every output is identical.
  - Both W1 night-short improvements and the S4 drain improvement from the primary matrix are confirmed at the
    synced head.
  - The comparator's rc=1 has one cause, the S4 allocation rule, which the decomposition below explains.
- **Cold start.**
  - Neither cold cell regresses at the synced head, and the sample-night first-operation service improved
    −3.8%. That improvement rests on this one pair and is stated at that strength.
  - The primary matrix's slow cold window is consistent with a host-level slowdown, but it is not attributed;
    see the disclosure above.
- **S4 at the synced head.** The decomposition gives the same explanation as before:

  | Arm | Residual share | Whole run ÷ 30 (MB) | Saturation phase ÷ 30 (MB) | Backlog peak | Drain (s) |
  |---|---|---|---|---|---|
  | A′ | 11.8–12.8% | 398.2–398.8 | 340.1–340.7 | 9–10 | 2.26–2.49 |
  | B′ | 2.0–3.2% | 375.9–376.4 | 321.1–321.6 | 2–3 | 0.26–0.49 |

  - Both per-operation figures fall −5.6%, with disjoint ranges.
  - Gen0 counts are equal (903–915 against 906–914), and all 30 operations complete in both arms.
- **S1 at the synced head.** S1 ran at both arms after the timing pairs: A′ 04:01:37–04:21:24Z, B′
  04:22:17–04:42:41Z. S1 is output identity only; its timings are not evidence.
  - A′ against B′ is **identical**: all 16 runs and 17 reports, 0 differences. The 121 ignored leaf keys were
    audited, and each is a timing, allocation, working-set, throughput or revision field.
  - Against the primary matrix's after pack (`d1c3ffb1`, base `b13f0d0e`), B′ differs in 3,062 leaves. They
    are in 5 reports, and they come from #1126's solver v2, which #1174 brought into the synced base:

    | Leaf (index-free) | Count | Change |
    |---|---:|---|
    | `reports/[]/assessment/solverVersion` | 90 | `spherical-triangle-astrometry-v1` → `-v2` |
    | `projectionFamily` | 5 | absent → `equidistant` |
    | `sourceCoverage/[]/outsideFrameProbes` | 90 | absent → `0` |
    | Identity hashes derived from those: source geometry, frames, assessments, diagnostics, sessions, shared calibration, fits | 2,877 | changed |

  - No measured value differs, and no catalog or provenance field differs.
  - Since A′ equals B′, this PR contributes none of the 3,062.
  - The solver v2 identity change was expected when #1126 merged; see
    [Composition checkpoints](#composition-checkpoints).


### Comparator corrections (reviews r0 to r2)

Review r0 found that the comparator, the S4 decomposition and the runner's load gate could pass on evidence
they should reject. Review r1 found that the comparator still accepted an arm whose index named one trial's
evidence twice. Review r2 found that it still credited one cell's evidence to another cell relabelled in the
index. All are corrected, and each correction rejects every constructed failure case. The earlier
tools accepted each of those cases, except a stray `B-*` directory, on which the 71592ad4 S4 decomposition
crashed:

| Case | 71592ad4 | Corrected |
|---|---|---|
| Empty root | complete, rc=0 | incomplete, rc=1 |
| Expected cell not measured | complete, rc=0 | incomplete, rc=1 |
| No expected cell list, or an empty one | complete, rc=0 | usage error rc=2, or incomplete rc=1 |
| Two A packs in a slot | complete, rc=0 | incomplete, rc=1 |
| B arm missing its last trial | complete, rc=0 | incomplete, rc=1 |
| Same cell in two slots | complete, rc=0 | incomplete, rc=1 |
| A node with no outputs changes Completed to Skipped | identical, rc=0 | difference, rc=1 |
| A stable Annotation record changes | identical, rc=0 | difference, rc=1 |
| A payload varies between A trials | identical, rc=0 | difference, rc=1 |
| An index row names another trial's evidence, with or without that trial's own evidence present (review r1, at 11d405b3) | complete, rc=0 | incomplete, rc=1 |
| A trial's evidence is replaced by another trial's, with or without its indexed hash updated (review r1, at 11d405b3) | complete, rc=0 | incomplete, rc=1 |
| Unindexed evidence, or an attribution run in a measure arm (review r1, at 11d405b3) | complete, rc=0 | incomplete, rc=1 |
| Both arms relabelled as another manifest cell, or as a cell the manifest does not list (review r2, at 615909f0) | complete, rc=0 | incomplete, rc=1 |
| Index rows claim another runtime profile (review r2, at 615909f0) | complete, rc=0 | incomplete, rc=1 |
| Load gate times out, or reads an empty, unreadable or non-numeric load | trial runs | run stops, no index |
| S4 decomposition: two A packs and no B pack, a stray `B-*` directory, or a B pack indexed as steady | rc=0, or a crash | rc=2 with a reason |

The recorded verdicts do not change when the corrected tools re-run the retained evidence:
- **Timing and resources.** Every timing, resource and regression verdict, every order and every arm is
  identical to the recorded primary (`interleaved-comparison.json`) and post-sync (`postsync-comparison.json`)
  comparisons.
- **Coverage.** Coverage is complete in both, with no gaps.
- **Outputs.** Every output is identical. The 140 excluded values per steady pair are the same records as
  before, so the narrower exclusion hid no stable value.
- **rc=1.** The comparator's rc=1 still has one cause in each, the S4 allocation rule.
- **S4 decomposition.** The corrected post-sync decomposition is byte-identical to the recorded one.

The node and output-identity cases above edit evidence without its indexed hash, so the corrected comparator
now rejects them as incomplete before comparing outputs. Copies with every indexed hash recomputed still report
a difference, so the identity rules stay exercised behind the binding.

The odd-stride equivalence rows added in review r0 fail when either kernel is mutated to start rows on even
byte offsets, while the other 13 cases still pass.

## Attribution

One attribution trial per cell and collector, at `7b6b3bc9` (baseline) and `d1c3ffb1` (after). Every number
below comes from the amended classifier unless a row says otherwise.

**Session boundary.** The two packs ran in different sessions:
- baseline 00:19:57–00:36:32Z;
- after 03:01:52–03:18:48Z.

Other sessions were active on `home-dev-02` throughout the after pack, and from 03:09:53Z that included this
issue's own Tier C gate. Steal time was not recorded for either pack. Absolute times are therefore not
comparable between the two packs, and no timing claim rests on them; timing verdicts come only from the
interleaved matrix. Attribution supports two things:
- each pack's own frame shares;
- per-frame changes too large to be drift, measured against untargeted frames in the same trace.

**On-CPU split** (sampled thread milliseconds over the whole trial, 35 operations plus setup):

| Cell | Arm | Running | Managed | Native | GC suspension | I/O | Unresolved native | Wait |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| s2-sample-night | A | 8,734 | 5,399 | 3,335 | 16,140 | 1,967 | 16,684 | 164,059 |
|  | B | 7,255 | 5,537 | 1,718 | 17,126 | 2,031 | 15,887 | 155,992 |
| s2-w1-night-long | A | 15,683 | 11,478 | 4,205 | 25,484 | 3,200 | 24,913 | 247,661 |
|  | B | 18,534 | 15,290 | 3,244 | 30,989 | 3,583 | 28,869 | 286,066 |
| s2-w2-night | A | 70,670 | 59,267 | 11,403 | 103,619 | 6,228 | 96,283 | 953,308 |
|  | B | 78,167 | 67,525 | 10,641 | 125,183 | 6,777 | 106,554 | 1,058,690 |
| s2-w6-night | A | 84,896 | 74,858 | 10,038 | 96,163 | 9,767 | 97,959 | 970,194 |
|  | B | 92,756 | 83,860 | 8,895 | 110,502 | 11,408 | 106,753 | 1,101,726 |
| s4-w1-saturation | A | 15,778 | 11,500 | 4,278 | 19,578 | 3,151 | 17,888 | 177,924 |
|  | B | 19,198 | 15,971 | 3,227 | 19,737 | 3,037 | 19,617 | 195,456 |

- GC suspension and unresolved native are sampled thread time, which includes parked threads, so they exceed
  running time in every cell. They show allocation pressure and the `perf` gap; they are not CPU.
- **Old and new classifier.** The amendment moves only the VSTest `SocketPal.Poll` loop from native running
  to wait. Only W2 and W6 move:

  | Cell | Arm | Running, first version | Running, amended | Native, first version | Native, amended | Moved to wait |
  |---|---|---:|---:|---:|---:|---:|
  | s2-w2-night | A | 106,253 | 70,670 | 46,986 | 11,403 | 35,583 |
  |  | B | 124,027 | 78,167 | 56,501 | 10,641 | 45,860 |
  | s2-w6-night | A | 122,178 | 84,896 | 47,320 | 10,038 | 37,282 |
  |  | B | 139,017 | 92,756 | 55,156 | 8,895 | 46,261 |

  The sample-night, W1-long and S4 totals are the same under both versions. The first version's totals equal
  the totals recorded in the packs for all ten traces.

**Ranked frames.** These are the baseline's largest exclusive frames by running share, with owners. The full top
200 for each trace is in `oncpu.json`. The findings table below dispositions them.

| Cell | 1 | 2 | 3 | 4 |
|---|---|---|---|---|
| s2-sample-night | SQLite `PrepareAndEnumerateStatements` 17.4% (CameraAgent.Common) | SQLite `NextResult` 9.5% | `StellarExposureRenderPlan.Prepare` 5.5% (#518) | `Rasterize` 4.9% (#518) |
| s2-w1-night-long | `RenderSensorPlane` 11.4% (#518) | SQLite prepare 10.0% | `Rasterize` 6.3% (#518) | SQLite `NextResult` 5.9% |
| s2-w2-night | `RenderSensorPlane` 20.8% (#518) | `DemosaicToRgb24` 12.9% (Imaging) | `Rasterize` 8.1% (#518) | `Memmove` 6.7% |
| s2-w6-night | `RenderSensorPlane` 29.1% (#518) | `DemosaicToRgb24` 20.9% (Imaging) | stream SHA-256 read-back 5.3% (Storage) | `Mono16DisplayStretch.Apply` 5.1% (Imaging) |
| s4-w1-saturation | `RenderSensorPlane` 11.1% (#518) | SQLite prepare 9.4% | SQLite `NextResult` 6.2% | `Rasterize` 5.9% (#518) |

**Targeted frames, baseline → after** (exclusive running milliseconds per trace):

| Frame | sample-night | W1-long | W2 | W6 | S4 |
|---|---:|---:|---:|---:|---:|
| `Linear16ArithmeticMean.Compute` (change 1) | 42 → 8 | 667 → 7 | 1,868 → 7 | 3,600 → 5 | 666 → 11 |
| `Mono16DisplayStretch.Apply` (change 2) | 41 → 45 | 630 → 660 | 2,257 → 1,560 | 4,354 → 3,141 | 688 → 696 |
| SQLite prepare: `PrepareAndEnumerateStatements` + `sqlite3_prepare_v2` (change 3) | 1,811 → 267 | 1,745 → 381 | 1,713 → 336 | 1,666 → 296 | 1,804 → 464 |
| Untargeted reference: `RenderSensorPlane` | 88 → 95 | 1,780 → 2,471 | 14,677 → 17,442 | 24,675 → 30,355 | 1,751 → 2,362 |
| Untargeted reference: `Rasterize` | 432 → 435 | 988 → 1,605 | 5,721 → 7,121 | 699 → 819 | 934 → 1,717 |

- Untargeted frames rose 0–84% in the after pack, which fits the session boundary above.
- The mean fell by 81–99.9% and SQLite prepare by 74–85% despite that.
- The stretch fell 28–31% in W2 and W6. It shows no change in sample-night, W1 or S4, where it is at most
  4.4% of running time and session noise is larger than the effect. The stretch's timing benefit is not
  separately resolved there.
- W6's baseline managed `PrepareAndEnumerateStatements` frame falls below the top-200 cut, so its row counts
  only `sqlite3_prepare_v2` and understates the baseline.

**Allocation shares.**
- The resource rule uses the allocation figures in the interleaved matrix, which are precise per-stage
  `GC.GetAllocatedBytesForCurrentThread` and process-wide deltas.
- The `gc-verbose` trials are kept for allocation-site stacks.
  - Their `topN` reports weight the stacks of the trace's stacked events. Those are dominated by
    `GCAllocationTick`, roughly one event per 100 KB allocated, so the shares indicate where volume is
    allocated rather than measure it.
  - In W2 the top site is `GC.AllocateUninitializedArray`, the full-frame buffers: 20.9% at baseline and 25.3%
    after. Next come JSON (de)serialization and buffered file-stream read and flush state, at about 26–28%
    inclusive.
  - These are the inputs to findings 5 and 10. No allocation-site change is proposed in this PR.

**Collector overhead.** This is each attribution trial's median against the same cell's interleaved arm median
(processing; foreground for S4):

| Cell | Baseline `cpu` | Baseline `gc-verbose` | After `cpu` | After `gc-verbose` |
|---|---:|---:|---:|---:|
| s2-sample-night | +4.8% | +9.9% | -5.4% | -22.2% |
| s2-w1-night-long | +5.4% | +4.9% | +35.3% | +24.4% |
| s2-w2-night | -2.6% | -2.6% | +12.4% | +38.9% |
| s2-w6-night | -6.2% | -7.2% | +11.1% | +11.4% |
| s4-w1-saturation | -8.3% | -8.3% | +20.9% | +20.8% |

These single trials come from a different session than the arms they are compared with, so the overhead is
not resolved. Baseline collector overhead falls inside about ±10% of the session-to-session drift. The after
figures include the co-tenant load described above.

## Findings and dispositions

Shares are of the baseline (`7b6b3bc9`) running time in the attribution cell named, from the amended
classifier. Frames owned by an open sibling are measured and reported here, not changed.

| # | Candidate | Owner | Measured share (baseline) | Expected benefit | Uncertainty | Burden | Disposition |
|---|---|---|---|---|---|---|---|
| 1 | `Mono16SceneRenderer.RenderSensorPlane` (VirtualSky sensor plane) | Imaging, #518 renderer | W6 29.1%, W2 20.8%, W1 11.4% | Largest single frame in capture; vectorizing the per-pixel plane render | Virtual capture only; physical cameras do not run it | Byte-exact rewrite inside #518's area | **Report to #518**, batched #520 follow-up. Measure-only here. |
| 2 | `BayerRggb16Demosaicer.DemosaicToRgb24` | Imaging | W6 20.9%, W2 12.9% (CFA only) | Managed SIMD over the bilinear RGGB kernel | Gain bounded by memory bandwidth at RGB24 output | Byte-exact kernel plus an equivalence test, as for changes 1 and 2 | **Next #1170 candidate** for a later refresh; not in this PR. |
| 3 | SQLite statement prepare and step (`PrepareAndEnumerateStatements`, `NextResult`, `sqlite3_prepare_v2`) | CameraAgent.Common processing and lane stores | sample-night 17.4% + 9.5% + 3.3%, W1 10.0% + 5.9% | Change 3 removed the per-open schema re-parse; prepared-statement reuse would remove the rest | Per-statement cost is spread over many call sites | Statement-cache plumbing across the store | **Partly addressed** (change 3). Remainder to the batched follow-up. |
| 4 | `TemporalPointSpreadRaster.Rasterize` | Imaging, #518 renderer | W2 8.1%, W1 6.3%, sample-night 4.9% | PSF tiling and SIMD | Depends on #518's scene schema | Renderer area | **Report to #518.** |
| 5 | `Buffer.Memmove` (full-frame copies) | Several; 9 pure copies per frame | W2 6.7%, W1 3.5%, W6 2.7% | Removing copies also removes LOH buffers (19 per frame) | Each copy has its own owner and lifetime reason | Cross-stage buffer ownership | **Batched follow-up**: copy-count and LOH pooling plan. |
| 6 | Stream SHA-256 read-back after write (`LiteHashProvider.ProcessStreamAsync`) | CameraAgent.Common storage | W6 5.3%, W2 3.3% | 10 read-backs per frame could hash the in-memory bytes instead | Read-back is a durability check; dropping it needs a policy decision | Storage contract | **Batched follow-up**; needs an owner decision. |
| 7 | `Mono16DisplayStretch.Apply` | Imaging | W6 5.1%, W1 4.0% | Lookup-table transfer | Low | Small | **Adopted** (change 2). |
| 8 | `Linear16ArithmeticMean.Compute` | Imaging | W1 4.3%, W6 4.2% | Narrow vectorized accumulator | Low | Small | **Adopted** (change 1). |
| 9 | `StellarExposureRenderPlan.Prepare` and `ComputePredictionsSha256` | Imaging, #518 | sample-night 5.5% + 3.8% | Cache the plan across frames with an unchanged scene | Scene changes every frame at real cadence | Renderer area | **Report to #518.** |
| 10 | GC suspension and allocation | Pipeline-wide | Sampled GC-suspension thread time exceeds running time in every cell; 19 LOH buffers per frame | Pooling full-frame buffers | Sampled thread time includes parked threads, so it overstates CPU | Cross-stage ownership | **Batched follow-up**, with candidate 5. |
| 11 | Catalog load at cold start (#521 details and alias validation) | Catalog.Sqlite | Not in the primary matrix (base `b13f0d0e`) | n/a | Measured only by the post-sync cold pairs | n/a | **Measure at refresh**; see the post-sync confirmation. |

## Changes in this PR

1. **Linear16 arithmetic mean** (`src/HVO.SkyMonitor.Imaging/Linear16ArithmeticMean.cs`).
   - **Accumulator.** Per row it accumulates into a narrow `uint` row, widened with `Vector<T>`, whenever the
     frame count cannot overflow it (at most 65,537 frames on a little-endian platform). Otherwise it uses a
     pooled `ulong` row.
   - **Exactness.** The divide and truncation are unchanged, so output is byte-identical.
     `tests/HVO.SkyMonitor.Imaging.Tests/Linear16KernelEquivalenceTests.cs` pins it against a frozen copy of the
     previous per-pixel code, including padded and odd strides and the saturated 65,537-frame limit.
2. **Mono16 display stretch** (`src/HVO.SkyMonitor.Imaging/Mono16DisplayStretch.cs`).
   - **Tables.** The histogram is pooled and the per-pixel `Math.Asinh` is replaced by a transfer lookup table
     computed with the same expression for every sample value in the black/white range.
   - **Exactness.** Output is byte-identical for every policy, which the same test class pins against the
     previous transfer.
   - **Identity.** Algorithm versions and recipe identities are unchanged because every output byte is.
3. **Processing store connection pooling**
   (`src/HVO.SkyMonitor.CameraAgent.Common/Capture/Processing/SqliteCaptureProcessingStore.cs`).
   - **What it fixes.** Each unpooled open re-read and re-parsed the whole `raw-ingress.db` schema before its
     first PRAGMA. The private cache from #698 is kept.
   - **No state crosses callers.**
     - The gallery rank function is re-registered per open.
     - The window-candidate temp table is emptied before each use.
     - The migration temp tables are created and dropped inside the migration transaction.
   - **Unchanged.** The read-only inspection connection that disables checkpoint-on-close stays unpooled.
   - **Inode pinning.** A pooled connection keeps its inode open, as the raw journal, lane store and admission
     coordinator already do for the same file. Nothing in the product replaces `raw-ingress.db` while the agent runs.
4. **Analysis only** (not on the measured path):
   - The on-CPU split classifies GC-driven `SharedArrayPool` trimming anywhere on the stack as GC suspension.
   - It classifies asynchronous file writes, which end in `RandomAccess.WriteAtOffset` without an interop
     frame, as I/O.
   - Commit `d710f5c1` adds the VSTest `SocketPal.Poll` wait and deterministic tie ranking (see Method).
   - `docs/validation/issue-1170-interleaved.py` compares interleaved pairs and S1 packs. It fails a pair
     comparison whose coverage is incomplete, whose indexed trials are not each their own evidence document, or
     whose evidence is not the manifest cell it is credited to (see Method).
   - `docs/validation/issue-1170-s4-allocation.py` decomposes S4 allocation. It requires exactly one A pack and
     one B pack, both passed saturation measure packs of the same cell.
   - `docs/validation/issue-1170-pipeline.sh`: the load gate fails closed.

   Baseline and after are analysed with the same scripts.

**Per-change evidence.** The three changes ship together, so the interleaved matrix measures their sum.
Attribution separates them by frame.

| Change | Exactness | Frame evidence (attribution) | Timing and resource evidence (interleaved, `b13f0d0e`) |
|---|---|---|---|
| 1. Mean | `Linear16KernelEquivalenceTests` against the frozen previous code; every pair output-identical; S1 identical | Mean frame 81–99.9% lower in every cell | The sum of all three: processing improved in sample-night and W1 night long, night short and day, and processing and service improved in W6. Allocated bytes per operation are lower in every steady and cold cell (-0.5% to -14.2%). The matrix does not split the timing by change. |
| 2. Stretch | Same test class, every display policy; every pair output-identical | Stretch frame 28–31% lower in W2 and W6; not resolved in W1, S4 or sample-night | Included in the sum above; not separately resolved. |
| 3. Pooling | Store tests unchanged and passing; no state crosses callers (see above); every pair output-identical | SQLite prepare 74–85% lower in every cell | Included in the sum above. The S4 drain after the last accept improved -83.7% (2,099 to 343 ms at the median, disjoint) and the backlog peak fell from 7–8 to 2–3. |

## Acceleration dispositions

| Candidate | Evidence | Disposition |
|---|---|---|
| Managed SIMD (`Vector<T>`) | `Vector<T>` is 256-bit AVX2 on the measurement hosts, which expose no AVX-512, and 128-bit NEON on Cortex-A76 (Pi 5). The #1170 kernels are width-agnostic. | Adopted where evidence supports it (mean). Pi benefit is a #1171 hypothesis. |
| Native kernel (C/C++/Rust) | Every hot kernel in the attribution is managed and still scalar except changes 1 and 2. `Vector<T>` already reaches the hardware SIMD width on both targets (AVX2, NEON). A native kernel adds a per-RID native asset build for linux-x64 and linux-arm64, P/Invoke marshaling of LOH buffers, and a second implementation to keep byte-exact. | **Not adopted.** No candidate exists where managed SIMD has been tried and fallen short. Re-evaluate under #1171 if a kernel still dominates on the Pi after managed SIMD. |
| NVIDIA GPU (RTX 5070 on home-dev-01) | Capability probe: 12,227 MiB, driver 580.178.04, CUDA 13.0 (V13.0.88); link idles at PCIe gen1 x16 and is capable of gen5 x16. No GPU package referenced; no prototype run. Cost model below. | **Not adopted; no prototype.** Data movement would not be the obstacle (cost model below). The obstacles are that the deployment target (#1171, Raspberry Pi 5) has no CUDA device, that the two largest kernels sit in #518's renderer, and that managed SIMD has not been tried on them yet. A prototype needs operator approval. |
| Intel GPU | None on the measurement hosts. | Unavailable; not evaluated. |
| Pi 5 GPU, scaler or ISP | Not probed. Pi execution is #1171. | Deferred to #1171. |
| linux-arm64 publish | CameraAgent (framework-dependent), ProcessingRunner and ReplayRunner (self-contained) publish for linux-arm64 with `-warnaserror` and no warnings. Every shipped native asset (`libSkiaSharp.so`, `libe_sqlite3.so`, CoreCLR) is ELF aarch64. | Build and publish ready. Load and run qualification is #1171. |
| ReadyToRun or Native AOT | No project enables ReadyToRun, trimming or AOT. | Hypothesis for S3 and Pi cold start; measure under #1171. |

**GPU data-movement cost model.**
- Compute per frame is the baseline attribution's sampled running time divided by the 35 operations in the
  trace (5 warm-up plus 30 measured). That figure includes setup and the collector, so it is an upper bound.
- Transfer bytes are the frame in and the product out. The link rates are assumptions, not measurements:
  4 GB/s is about gen1 x16, the rate the idle link reports, and 25 GB/s is an assumed pageable-copy rate on
  the gen5 x16 link under load.

| Kernel | Workload | CPU per frame | Bytes in + out | Transfer at 4 GB/s | Transfer at 25 GB/s |
|---|---|---:|---:|---:|---:|
| `RenderSensorPlane` | W6 (25,233,408-byte Mono16) | about 705 ms | scene (KB) + 25.2 MB | about 6 ms | about 1 ms |
| `DemosaicToRgb24` | W6 | about 507 ms | 25.2 MB + 37.9 MB (RGB24) | about 16 ms | about 2.5 ms |
| `RenderSensorPlane` | W2 (12,879,360-byte Mono16) | about 419 ms | scene (KB) + 12.9 MB | about 3 ms | about 0.5 ms |
| `DemosaicToRgb24` | W2 | about 260 ms | 12.9 MB + 19.3 MB | about 8 ms | about 1.3 ms |

- Every product returns to host memory, because each one is hashed and written through on the CPU. A GPU
  path would therefore pay both directions on every frame, and that is included above.
- Even at the pessimistic link rate, transfer is under 4% of the CPU compute it would replace for these
  kernels. The disposition is decided by deployment target, ownership and byte-exactness, not by data
  movement.

## Composition checkpoints

Each sibling merge triggers a `cp-<issue>` refresh. The refresh re-runs the manifest at the new
`development/v1` head and records identity changes the sibling intends. The manifest that measured this
PR's packs stays unchanged here, so its recorded SHA-256 still matches. Checkpoint entries go into the
manifest when a refresh re-measures.

| Checkpoint | Trigger | Expected identity change | Status |
|---|---|---|---|
| `pr1` | This PR. Primary matrix at `productBase` `b13f0d0e`; post-sync confirmation at `0639e27d` | None. The kernels and pooling are byte-exact and identity-neutral. | This record |
| `cp-1126` | #1126 merges | `AstrometricConventions.SolverVersion` moves from `spherical-triangle-astrometry-v1` to `spherical-triangle-astrometry-v2` (bounded wide-field solver fix approved by the #520 coordinator). Equidistant and the other fisheye families stay bit-identical. This is an expected identity change, not a regression. | #1126 merged into the post-sync base `0639e27d`. Its identities are in both post-sync arms, so the confirmation compares like with like. A full refresh follows the remaining sibling merges. |
| `cp-518` | #518 merges | Whatever the renderer or scene-schema versions declare. The renderer candidates below are re-measured. | Pending |
| `cp-526` | #526 merges | Calibration recipe versions it declares. The calibration `PackRows` and SHA candidates are re-measured. | Pending |
| `final` | Last sibling refresh | Carries any Tier C, byte-exact sibling-area optimizations. `Closes #1170`. | Pending |

## Reproduce

Run from a clean worktree at the measured revision. The runner refuses a dirty tree and takes the heavy lock itself.

```bash
# A local catalog copy whose `current` is a symlink to the version directory.
export HVO_ASTROMETRY_CATALOG_ROOT=<catalog root>

docs/validation/issue-1170-pipeline.sh s1 <pack>/s1                # S1: unchanged #1106 manifest, TC=0
docs/validation/issue-1170-pipeline.sh measure <pack>/measure      # S2-S4: production defaults, no collectors
docs/validation/issue-1170-pipeline.sh attribute <pack>/attribute  # dotnet-trace sample-cpu and allocation profiles

docs/validation/issue-1170-metrics.sh <pack>/measure                         # summarize one pack
docs/validation/issue-1170-metrics.sh <baseline>/measure <after>/measure     # single-session regression rule

# Interleaved pairs: <root>/<NN>-<cell>/A-<revision> and B-<revision>, each made by
#   HVO_1170_CELLS='^<cell>$' docs/validation/issue-1170-pipeline.sh measure <dir>
# The third argument is the comma-separated manifest cell ids the root must cover exactly.
python3 -I docs/validation/issue-1170-interleaved.py pairs <root> <comparison.json> <expected cell ids>
python3 -I docs/validation/issue-1170-interleaved.py s1 <baseline>/s1/s1 <after>/s1/s1 <s1-identity.json>

# S4 allocation decomposition for one interleaved slot.
python3 -I docs/validation/issue-1170-s4-allocation.py <root>/<NN>-s4-w1-saturation <s4-allocation.json>

# Re-run the on-CPU split for one attribution trace.
python3 -I docs/validation/issue-1170-oncpu.py <trial>/trace.speedscope.json <trial>/oncpu.json
```

Attribution needs `dotnet-trace` installed outside the repository tool manifest, in the directory named by
`HVO_1170_TOOLS` (default `~/.local/share/hvo-1170-tools`):
`dotnet tool install dotnet-trace --version 10.0.745401 --tool-path ~/.local/share/hvo-1170-tools`.

## Retained evidence

Evidence root on `home-dev-01`: `~/development-state/HVO.SkyMonitor/evidence/1170/`. Each pack directory has
an `index.json`, a `SHA256SUMS` and a sibling `<pack>.tar.zst` with its `.sha256`. Archive hashes:

| Pack | Role | `.tar.zst` SHA-256 |
|---|---|---|
| `d942bd86/baseline/s1` | S1 output-identity reference for the primary matrix | `68ecdba9430ef3f7b068d56a5d6c4130bae09d301cef6578e39062a9eff586e8` |
| `d942bd86/baseline/measure` | Superseded for comparison (session drift); retained | `49d1372212ab58959a453c26011e130a430711801b4846453bc1bad1eaed8159` |
| `7b6b3bc9/control-w2/measure` | Addendum 004 W2 control | `75aa7c26ca6ff4a58794eca1253c2b13d58e40de9c5bac706feab419856a3221` |
| `7b6b3bc9/baseline/attribute` | Baseline attribution | `dd9bfc1174121a322b733671a509ce82eeaecb737b6b9bc385331a4ed2979dff` |
| `d1c3ffb1/after/s1` | S1 at the after head | `03acbfdbee3af88c0c4aae6defd9ccb90fe9c3cb402a408f84423b30f3efd869` |
| `d1c3ffb1/after/attribute` | After attribution | `aca78eaa25d0f58197f5c47b0a73efb823d99a5ba09e18f74de612f08dd37427` |
| `interleaved/01-s2-sample-night/A-7b6b3bc9` | Pair 01, arm A | `e3a9b6df5c44decc067240b16574c4319ef63446f848303e20807e44189f6341` |
| `interleaved/01-s2-sample-night/B-d1c3ffb1` | Pair 01, arm B | `4db7d3941656633c954f566f2aeb8168b7e3d264a4c03d9a8e4137dd99443518` |
| `interleaved/02-s2-w1-night-long/A-7b6b3bc9` | Pair 02, arm A | `c11ddfb6f60ad2e6419dc373257ba82574bc39588a65ca6d3553028613a99663` |
| `interleaved/02-s2-w1-night-long/B-d1c3ffb1` | Pair 02, arm B | `2f106111ba60f3f6d783e7d0bb46a55635834fe275236fd87ad3f2c6bb42e290` |
| `interleaved/03-s2-w1-night-short/A-7b6b3bc9` | Pair 03, arm A | `20656624c75502454aaa4ad1961e0ffd80f457902cca779e2c7dc4acc053522b` |
| `interleaved/03-s2-w1-night-short/B-d1c3ffb1` | Pair 03, arm B | `f3847b6ad6be764dd90c137587fb4b22afa6162f59315868e43841e3f568f635` |
| `interleaved/04-s2-w1-day/A-7b6b3bc9` | Pair 04, arm A | `d990a2f0ad17ab8ae9d32b19d187eb9f934b64107061b033a8c8e8c04b416980` |
| `interleaved/04-s2-w1-day/B-d1c3ffb1` | Pair 04, arm B | `56b1022db343b41712997be1d69bbb06004c12d268abc3d5a96bd47a576b74d7` |
| `interleaved/05-s2-w1-dense/A-7b6b3bc9` | Pair 05, arm A | `e608e55565310c272cc6ee31b0ed0b3baed66d9e896168af437360e7abbf98ce` |
| `interleaved/05-s2-w1-dense/B-d1c3ffb1` | Pair 05, arm B | `3a35a3b6b99a55cb7470a5abc682b62460128fed1554af7e83bab4afbcd871b4` |
| `interleaved/06-s2-w2-night/A-7b6b3bc9` | Pair 06, arm A | `2c3c2b9d469446c5366d8e8715d251ca578b273854f8f08556f2e13c4dda925e` |
| `interleaved/06-s2-w2-night/B-d1c3ffb1` | Pair 06, arm B | `4ad7f904774f5ac692e889939125c5f1f39d432f75a3c8730e1c27db0daec017` |
| `interleaved/07-s2-w6-night/A-7b6b3bc9` | Pair 07, arm A | `94f556c03718da192de4377db9982f7d4255ca6a0f1099509cef2c472ad265e0` |
| `interleaved/07-s2-w6-night/B-d1c3ffb1` | Pair 07, arm B | `4cdefe1419d41445dee2ada3a88842f546c724acbfff9ccb947eaf18bdf1cf84` |
| `interleaved/08-s2-w1-night-long-tc0/A-7b6b3bc9` | Pair 08, arm A | `47c6a21a8973c408fda443eacf3e76598c1c6dd0a314c9c3c79a5dbd6ba040ce` |
| `interleaved/08-s2-w1-night-long-tc0/B-d1c3ffb1` | Pair 08, arm B | `8434e4515b1381f6757688587be398b0e95510b4bb134ee36387929b6221b2da` |
| `interleaved/09-s3-sample-cold/A-7b6b3bc9` | Pair 09, arm A | `f4e9f9dfc6717a481acb1c178a9dbda50654cbca100b9fa9eb8fd0c3dd71b265` |
| `interleaved/09-s3-sample-cold/B-d1c3ffb1` | Pair 09, arm B | `d3ad0e495033ebe1871f3bcdbf09b499e57948c3f56e107301fc7a8bcc4c361c` |
| `interleaved/10-s3-w1-cold/A-7b6b3bc9` | Pair 10, arm A | `3600cdb8f14d54722990e564ee721cfc580ab5670f81fadbad305d7d5de930d7` |
| `interleaved/10-s3-w1-cold/B-d1c3ffb1` | Pair 10, arm B | `226d71d151d38283c0bd77c4e8804e3c0cd388e01d3930e5b37358cafc2cf085` |
| `interleaved/11-s4-w1-saturation/A-7b6b3bc9` | Pair 11, arm A | `4d67badcc0d69caa2e7d6155139e3b63b397979c0f57b9b8f0d772725f41b5bc` |
| `interleaved/11-s4-w1-saturation/B-d1c3ffb1` | Pair 11, arm B | `fcc955eb1d4e6272b21a5c27a63e6c1e88a138c307b4dc1fb05e0748aa9becf5` |
| `d879ea9f/baseline-aborted-addendum003/s1` | Aborted before addendum 003; retained, not used | `9fd5d294292f08ecbb9c0971f75b00c45b1d116249b5a6f1fc13ecd888176f20` |

Post-sync packs are under `postsync/` in the same evidence root:

| Pack | Role | `.tar.zst` SHA-256 |
|---|---|---|
| `postsync/01-s2-w1-night-short/A-7a2af5b7` | Post-sync pair 01, arm A′ | `258e9483dbc09c1920039e7255803ab8de4278130406d05894ae6ff708b70bbe` |
| `postsync/01-s2-w1-night-short/B-8f3f1fdb` | Post-sync pair 01, arm B′ | `77b64d2071b25fb54f959a1921d97b465875ae890b5b337eaa31ededbc5ca6de` |
| `postsync/02-s4-w1-saturation/A-7a2af5b7` | Post-sync pair 02, arm A′ | `ebf2dfbe78342d07bca0b60f96594459df67c9b34659da11392dc8a771b2c37d` |
| `postsync/02-s4-w1-saturation/B-8f3f1fdb` | Post-sync pair 02, arm B′ | `0d80d4e13029f2176d59c78d732eec315b7b3cff27084912ade00744edcbd525` |
| `postsync/03-s3-sample-cold/A-7a2af5b7` | Post-sync pair 03, arm A′ | `1738725d3e757e6af55ff3e72f502b30ccd678fd239a33138b295c806d548435` |
| `postsync/03-s3-sample-cold/B-8f3f1fdb` | Post-sync pair 03, arm B′ | `56e471c49ea98cc004ecfdbd4d9b33a4cd19950269d71b4e4baff7b532239fe9` |
| `postsync/04-s3-w1-cold/A-7a2af5b7` | Post-sync pair 04, arm A′ | `6898168cce210596abb6cb80c0ec5a4f8a61a61ae5a4277d6e661ccc7a93704e` |
| `postsync/04-s3-w1-cold/B-8f3f1fdb` | Post-sync pair 04, arm B′ | `1b30e14322108ff0a678ed5ec25c7361effac12f67ec44eceea4fe8afb2c68a6` |
| `postsync/s1/A-7a2af5b7` | Post-sync S1, arm A′ | `fe111ea1f87162e8135a19bbc2b72bfa856f659c3c9ccc2ef484ea3c66cf2186` |
| `postsync/s1/B-8f3f1fdb` | Post-sync S1, arm B′ | `97536ccb94859ca9d70563f15b44b9614a88710cafcb1d9b8c9a843e63429cc1` |

`postsync/steal-ticks.txt` holds the `home-dev-01` steal samples. The post-sync analysis is on `home-dev-02`
under `~/development-state/HVO.SkyMonitor/evidence/1170/postsync-analysis/`, computed from a mirror whose every
pack `SHA256SUMS` checks out:
- `postsync-comparison.json`: `047174e58df9ed525f0f997135f2e68ce3fe7b50112ab4b002aa5fa49861c4a6`
- `postsync-s4-allocation.json`: `8c35218c2ffef010fa0feec9689d4f95dd07176106314010e24e21115fd70d0d`
- `postsync-steal-cells.json`: `bb1f3a47b098bfb665881970bb6378a156ac4f078a42f70f2a3231c12d01d504`
- `s1-identity-7a2af5b7-vs-8f3f1fdb.json`: `124e476b99abd8702bc7a3c135c5c485b45644d91565f0ab7fd7b56a78e4037b`
- `s1-identity-d1c3ffb1-vs-8f3f1fdb.json`: `05d7202001f483c600598caefde01849977eb5bf4e4a49a96d81e253d03ab0ed`.
  The comparator lists only the first 500 differences.
- `s1-diff-groups-d1c3ffb1-vs-8f3f1fdb.json`: `ed4c2997d3a59000bf98f824651de2329d0fd4794472c3cfd221bc0ba9695ee4`.
  It holds all 3,062 differences, grouped by `s1-diff-groups.py`
  (`5e176cb4a032c8309405f2736c50bf3d03a9a442f4033a34b0bdd6ff8ffdca07`), which reuses the committed comparator's
  leaf walk and ignore pattern.

The review r0 re-runs are in `postsync-analysis/r0-correction/` on `home-dev-02`:
- `primary-comparison-v2.json`: `0315fb4bcf9cdc0bcdcf70f3410316e3ddff612136c404a270a4be46366ef8ed`
- `postsync-comparison-v2.json`: `b40ef3ba6342182d656f9311ce93e12c6dc4672ac259657c208eb41083bf811f`
- `primary-s4-allocation-v2.json`: `6ef18ed1811d11eb2a57446e09c850d406bff36d47c30097f63b2158802766c1`
- `postsync-s4-allocation-v2.json`: `8c35218c2ffef010fa0feec9689d4f95dd07176106314010e24e21115fd70d0d`,
  identical to `postsync-s4-allocation.json`.
- `negative-cases.py` (`c82a43537ade9fde537931ce4806727d088288b9a8126547aee3208a3bd897fa`) and its output
  `negative-cases.txt` (`753102185daa119e1fb039eeef347ff34008385ac5ffca33d8f7e21d89285b33`).
- `gate-cases.sh` (`14a7e21ad8d5fcc619206ce31e4fdaa095e12e1e481a0a9f8be581a4091d5d6b`). Its corrected output is
  `gate-cases.txt` (`ecaa9f4d52b258f8c902f1f1a7c1827be9fd5b5fb1637475c6c9483e4a8f878c`) and its 71592ad4
  output is `gate-cases-old.txt` (`4e1fae08f324ed4aa4bcdfa1d2889c010966f702c57360df383e583c06b15311`).
- `f6-mutation.txt`: `b8be95b2b9e68bf15ce658e58e3ba5ad56cdcc82789dc607cc41248be7cc5d27`
- The primary interleaved mirror on `home-dev-02` is a partial copy (each pack's `index.json` and
  `evidence.json` files) without `SHA256SUMS`. All 242 of those files match the `home-dev-01` packs'
  `SHA256SUMS`. The checksum list is `primary-mirror-sha256-from-home-dev-01.txt`
  (`b22e11cb0e78913c3cf9be8fcaa0ecc1d04e274bc33585a2533ce5eb5a1c6be9`).

The review r1 re-runs are in `postsync-analysis/r1-correction/` on `home-dev-02`:
- With the trial-evidence binding, the primary and post-sync comparisons are byte-identical to
  `primary-comparison-v2.json` and `postsync-comparison-v2.json` above.
- `n1-cases.py` (`ad9b6fdf5cd2caccf723dc21ca1ba91329fda3a441653a88b125ed79e9d16b0c`) and its output `n1-cases.txt`
  (`d0ef117bcb09f07745b8c5e6ae1e3794431f805abb1d90302683a3bd9d0c54c0`) hold the review r1 failure cases at 11d405b3
  and at the correction.
- `gate-waits-home-dev-01.tsv` (`2f505a1cc1dcaa7269eaa315eb95f92aa03ce2ec8e63efd766edaeb23b41b6b3`) lists every
  runner-recorded trial's gate wait and starting load.

The review r2 re-runs are in `postsync-analysis/r2-correction/` on `home-dev-02`:
- With the cell binding, the primary and post-sync comparisons are byte-identical to `primary-comparison-v2.json`
  and `postsync-comparison-v2.json` above. The primary comparison includes the valid `tc0-side-cell` slot.
- `p1-cases.py` (`31760c6293ef956e956571756740f998b3260fcf329affbdf1d1c92cba5e5abf`) and its output `p1-cases.txt`
  (`b94efc41664a4ab45d6b88465f37c05434dd02eefe1745fa1b62a21a80a55a20`) hold the review r2 failure cases at
  11d405b3, at 615909f0 and at the correction.
- `mirror-loads.tsv` (`8024f1f2d305492a17b900a9f850298f5038388b8dfcdf1356005f48f12768d2`) lists the 152 mirror
  trials' gate waits.

- `d942bd86/baseline/attribute-aborted-addendum004` is the baseline attribution run stopped when addendum 004
  arrived, with 4 of 10 trials complete. It is retained and not used; `7b6b3bc9/baseline/attribute` replaces it.
- Analysis outputs are in `analysis/` beside the packs:
  - `interleaved-comparison.json`: `7cf02e7a12527c24a6cbd2f5798ab50b4546f9391ef06b777eca4a2fe8cc1276`
  - `s1-identity-d942bd86-vs-d1c3ffb1.json`: `cb7a52613599a05e5ebcc14b3a1897c9c1a980814840ff867a7b84fafc1a071a`
- The on-CPU reclassification of both arms' cpu traces is on `home-dev-02` under
  `~/development-state/HVO.SkyMonitor/evidence/1170/reclassify/`. Each trace there was copied from its
  pack and hash-checked against the pack's `index.json`.
