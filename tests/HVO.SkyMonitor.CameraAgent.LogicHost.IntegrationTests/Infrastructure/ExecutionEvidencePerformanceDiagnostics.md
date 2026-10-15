# Issue 538 opt-in capture diagnostics (not acceptance)

Enable only for the existing Manual method with `HVO_ISSUE_538_DIAGNOSTICS=1` after a separately granted host slot.
Counter-only diagnostic commit `cb392a616df10e21d4922066a03168904ee850d5` passed the focused Release build
and scoped format verification, then completed the declared 140 selections with the intended inconclusive
result. Its archived report localizes early latency to render but does not establish a causal JIT explanation.
The EventPipe handshake changes below are an **uncompiled, unformatted, uncommitted draft**. No EventPipe
invocation or acceptance result is claimed. A later build/format slot and a separate diagnostic slot are required.

The exact diagnostic repeat command (for a later authorized slot) is:

```bash
HVO_ISSUE_538_DIAGNOSTICS=1 dotnet test tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~ExecutionEvidencePerformanceTests.Issue538_MetadataAndLiveInterferenceAcceptance
```

Every diagnostic stage and producer/runtime snapshot records this flag-prefixed command and an explicit
`DiagnosticModeEnabled = true`. Default acceptance keeps its existing unprefixed repeat command.

The diagnostic path runs the same E538-I phases in order: idle-before, active, denied, idle-after. Each still has
five declared warmups and thirty measured operations, using the existing W1 geometry, graph, deadlines, recovery,
loss/overflow assertions and comparison rules. It adds no capture request or preconditioning and does not discard
or reclassify any measured row. M/C are not run in this diagnostic-only path. Completion calls `Assert.Inconclusive`,
even if every comparison passes, so it cannot satisfy performance acceptance.

Default acceptance leaves the environment flag unset. It creates no diagnostic collector, timer or ingress
wrapper and retains E538-I then M then C. Correctness cases do not opt in through the environment variable: only
the Manual method passes the optional collector into its own isolated fixture.

## Source inspection and the selected boundary

`VirtualSkyCameraModule.CaptureCoreAsync` already records eight numeric strings in `CameraFrame.Metadata.Extra`:
projection, prediction, reference-scene and render elapsed milliseconds, each with its associated process CPU
milliseconds. This draft copies those exact producer strings, with an explicit list of missing/non-finite fields.
It never derives stage times by subtracting unrelated measurements.

Those values do not survive the existing durable path:

- `CaptureLaneEnvelopeSerializer.Serialize` removes the frame from the retained submission.
- `RawCaptureDescriptorFactory` carries selected scientific/profile evidence, not arbitrary `Metadata.Extra`.
- `FrameReconstructor` reconstructs metadata without `Extra`.
- `FileSystemFrameStorageService.SaveCoreAsync` writes frame-index entries with `Metadata = null`; its versioned
  sidecar binds the descriptor and scene, not the producer stage dictionary.

Therefore a diagnostic-only `IRawCaptureIngress` wrapper observes the actual submission and the actual returned
committed receipt. It copies metadata after real acceptance and binds it to that receipt's capture ID, capture
sequence, manifest checksum and payload checksum. It delegates initialization, publication-state lookup,
recovered-live binding and recovery invalidation. The concrete production ingress singleton and all its other DI
interfaces remain registered. No module implementation/capability, production contract, artifact or receipt is
replaced or modified. No pixel buffer is retained.

## Bounds, timestamps and attribution

The collector retains at most 512 pending producer records and 140 selected records. Selection happens at the
existing warmup/measured upload sites using the real capture IDs and sequences. The normal sequence, checksum,
lineage and warmup-exclusion assertions remain. Other naturally acquired/drained frames are not relabeled as
measured. Evicted pending records and missing selected metadata are reported explicitly.

At 50 ms intervals, at most 24,000 samples retain UTC and monotonic timestamps with process-wide generation 0/1/2
collection counts, cumulative GC pause duration, allocated bytes, and cumulative JIT compilation time, method
count and compiled IL bytes. These APIs were verified in the pinned .NET 10 reference documentation available
locally. Unsupported APIs produce null values and explicit missing-diagnostic entries. Buffer overflow is counted.

Each selected capture retains the full submission start, millisecond-normalized descriptor start, ingress entry
and ingress completion timestamps. Its diagnostic result references the runtime sample immediately before the
module start and immediately after ingress completion. The sample array is emitted once. Counter differences
over that bracket include all concurrent work in the shared test process; they cannot attribute a GC pause or a
compiled method to one capture or host. Exact pause intervals, method names and compilation tiers are explicitly
unavailable. Zero counter growth is an observation, not proof that a cause is absent.

## Evidence and overhead

Output is isolated under `TestResults/issue-538/<source>/<clean-or-fingerprint>/diagnostic/`. Every diagnostic stage
is marked `DiagnosticOnly-NotAcceptance`. The producer/runtime snapshot is written in a `finally` block, including
on a failed phase; completed phase JSON and a completed diagnostic-interference summary are retained separately.
The existing source fingerprint and repeat command remain attached. Retained numeric stage strings have a
deterministic subset SHA-256; that is distinct from the immutable producer manifest and payload checksums.

Sampling, metadata copying/hashing, counters and retention add overhead. Review that overhead and the actual
counter/timeline evidence before proposing any further acceptance measurement. The existing rule remains:
idle-bracket variation above 25% is inconclusive; it must not widen the regression allowance or remove outliers.

## Draft EventPipe attach and live-process rundown

This extends the test-only PID/attached/detached protocol in
`tests/HVO.SkyMonitor.CameraAgent.Tests/Issue1170ComposedPipelineEvidenceTests.cs` and
`docs/validation/issue-1170-pipeline.sh`. Both were inspected read-only; neither is changed.

An external runner creates a fresh absolute handshake directory and starts exactly one Manual invocation:

```bash
HVO_ISSUE_538_DIAGNOSTICS=1 HVO_ISSUE_538_TRACE_DIR='/absolute/fresh-output/attach' DOTNET_EventPipeThreadSamplingRate=0xA dotnet test tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~ExecutionEvidencePerformanceTests.Issue538_MetadataAndLiveInterferenceAcceptance
```

The trace directory flag requires diagnostic mode. Leaving it unset preserves counter-only diagnostics;
leaving both diagnostic flags unset creates no diagnostic collector, ingress wrapper, timer or EventSource.
No codegen, tiering, PGO or graph configuration is changed. The sampling environment setting is diagnostic
instrumentation only. It is required to be `0xA`, requesting ten milliseconds through CoreCLR's hexadecimal
configuration parser; this does not guarantee exact observed spacing.

Before creating the isolated agent, the collector atomically publishes `pid.json` with the actual testhost PID,
fresh run nonce, source/dirty fingerprint, exact env-prefixed repeat command and collector limits. It waits up to
90 seconds for a matching `attached.json`. During that wait it writes `enabled.json` only after the unique
`HVO-Issue538-ExecutionEvidence-Diagnostics` EventSource is actually enabled. The external runner must observe
that proof and a live collector before acknowledging attachment. A nonempty collector log is insufficient.
Nonce/PID/source/command matching and fresh files prevent stale acknowledgement reuse.

After attachment, EventSource `Provenance` embeds the same exact source/fingerprint/command in the trace, and
`ClockSync` carries UTC ticks, monotonic ticks and frequency. `CaptureCommitted` reports the existing real
receipt's identity, sequence, full module/ingress timestamps and payload hash; `CaptureSelected` reports only the
existing 140 selections and their phase/role/ordinal. Neither event adds a capture or changes selection.
The test context also writes source/command into TRX output. JSON artifacts carry the repeat command directly;
binary traces and tool logs have matching provenance events/headers and hashed artifact-index entries.

In `finally`, after the existing workload and host cleanup, the harness records `completion-requested.json`,
emits another clock marker and `MeasurementComplete(runId,outcome)`, then waits up to 120 seconds for matching
`detached.json`. The collector stops on that event and nonce, flushes and performs rundown **while testhost is
alive**. The external runner acknowledges only after collector exit and records whether the actual testhost
was still alive. The harness retains failures in the producer/runtime snapshot. A workload exception remains
the primary exception; a trace failure after successful workload completion fails diagnostics. Successful
diagnostic completion still calls `Assert.Inconclusive`, never acceptance.

The stopping event is emitted for a failed workload as well as a completed one. Attach timeout, early collector
exit, detach timeout, nonzero tool exit, trace/file/time bounds and malformed markers retain their own diagnostic
artifacts. No automatic retry is permitted. Early termination never becomes an assertion that all 140 completed.

## Pinned collector and limits (proposal, not executed)

The installed executable is `/home/roys/.local/share/hvo-1170-tools/dotnet-trace`, version `10.0.745401+cef304c50763bf24f99566cb31d55540842e7ae9`.
Only read-only `--version`, `collect --help` and `list-profiles` commands were used for this draft. Its bundled TraceEvent assembly
is version `3.1.23`, under `.store/dotnet-trace/10.0.745401/dotnet-trace/10.0.745401/tools/net8.0/any`.
No package, product dependency or production configuration seam is added.

The proposed collector argv is pinned rather than inheriting tool defaults:

```bash
/home/roys/.local/share/hvo-1170-tools/dotnet-trace collect -p <actual-testhost-pid> --profile dotnet-sampled-thread-time --providers 'Microsoft-Windows-DotNETRuntime:0x1000020039:5,Microsoft-DotNETCore-SampleProfiler:0xF00000000000:4,HVO-Issue538-ExecutionEvidence-Diagnostics:0xFFFFFFFFFFFFFFFF:5' --buffersize 128 --duration 00:00:20:00 --rundown true --format NetTrace --stopping-event-provider-name HVO-Issue538-ExecutionEvidence-Diagnostics --stopping-event-event-name MeasurementComplete --stopping-event-payload-filter runId:<nonce> -o <fresh-output>/capture.nettrace
```

The runtime mask is the union GC `0x1`, Loader `0x8`, JIT `0x10`, NGen `0x20`, method IL/native mapping
`0x20000`, and Compilation `0x1000000000`, at Verbose level 5. It requests collection and suspend/restart events,
named method loads, precompiled-code mapping and tier-control events. It does not enable heap dumps, forced GC,
allocation ticks or verbose JIT-inlining tracing. Requested events must subsequently be shown to be present;
provider enablement alone proves neither visibility nor absence of loss. Keyword definitions and optimization-tier
fields are in the [TraceEvent parser](https://github.com/microsoft/perfview/blob/v3.1.23/src/TraceEvent/Parsers/ClrTraceEventParser.cs).
The runtime documents [tier-control events](https://learn.microsoft.com/en-us/dotnet/fundamentals/diagnostics/runtime-tiered-compilation-events)
under the Compilation keyword; actual runtime/parser support must be recorded.

`dotnet-sampled-thread-time` requests `Microsoft-DotNETCore-SampleProfiler` with its pinned v10 profile
configuration (`0xF00000000000`, Informational 4), also specified explicitly in `--providers`. The source-backed
request is ten milliseconds; actual observed spacing is reported independently. These are
managed **thread-time** stack samples, not kernel on-CPU samples. Sleeping or scheduled-out threads can be
represented; stack occupancy must not be presented as measured CPU or scheduling delay. Actual process CPU
counters remain separate. The [tool documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace)
explains thread-time rather than CPU sampling. The target-specific sampling support is verified against runtime
`v10.0.12`, not inferred from tool 10 or generic documentation: [the config entry](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/inc/clrconfigvalues.h)
feeds [the CoreCLR adapter](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/vm/eventing/eventpipe/ep-rt-coreclr.h),
then [ep_init](https://github.com/dotnet/runtime/blob/v10.0.12/src/native/eventpipe/ep.c) multiplies configured milliseconds
by 1,000,000 and calls [the profiler setter](https://github.com/dotnet/runtime/blob/v10.0.12/src/native/eventpipe/ep-sample-profiler.c).
[GetConfigDWORD](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/utilcode/clrconfig.cpp) parses base 16 unless
the entry opts into decimal, which this entry does not: `10` would request 16 ms, while `0xA` requests 10 ms.
The generic [EventPipe documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventpipe) labels the
setting 11+, which conflicts with this pinned target source. Retrieved reference bytes/hashes are retained outside
repo under `/tmp/issue538-eventpipe-draft/reference/`. This source establishes supported requested configuration;
the offline report still records observed per-thread min/median/p95/max spacing and missing/truncated samples,
not a guaranteed 100 Hz rate or proof of scheduling behavior.

The external draft runner is `/tmp/issue538-eventpipe-draft/runner.py`. It remains outside the repository pending
review. It requires a clean, exact reviewed SHA and a verified compiled-file manifest; it never builds, retries,
or acquires a second lock. A future granted invocation has exactly one outer bounded acquisition:

```bash
flock -o -w 600 /tmp/hvo-520-heavy.lock python3 /tmp/issue538-eventpipe-draft/runner.py --repo <this-worktree> --source <reviewed-clean-sha> --out <fresh-evidence-directory> --binary-manifest <verified-build-manifest.json> --lock-already-owned --execute-authorized-diagnostic
```

This is a proposed command, not authorization to run it. The draft bounds PID publication at 180 seconds,
attachment at 90, trace duration at 1200, detach/rundown at 120, and the runner at 1500. These are collector/handshake
bounds; each unchanged E538-I phase retains its 180-second deadline. The EventPipe buffer is 128 MiB, trace soft
limit 896 MiB with 128 MiB reserved for rundown, hard per-file trace limit 1 GiB, and log guard 32 MiB. Crossing a guard
is incomplete diagnostic coverage even if a partial file opens. A log can exceed its polling guard before
cleanup; trace has an OS hard file bound. Cleanup signals/kills only runner-owned process groups and never
enumerates/removes another session's containers or build servers. A killed testhost may leave owned containers;
that failure requires explicit ownership-based cleanup, never global Docker pruning.

The reviewed external cleanup correction tracks each owned launcher's new session/group and Linux process
identity (PID, UID and `/proc` start ticks), retaining pidfds for verified descendants and the nonce-bound actual
testhost. Discovery requires a live pinned session-generation anchor or a live pinned parent; numeric group
membership alone is insufficient. Cleanup still discovers and targets those verified descendants after the
launcher exits. It signals retained pidfds, with no numeric PID/group fallback. Reused or unverifiable identities
are refused and retained in `process-cleanup.json`; unsupported pidfds or residual processes fail cleanup rather
than claiming the host is clear. The offline analysis guard shares this helper. Discovery caps are 4,096 scanned
processes and 1,024 recorded owned identities; signal history caps at 8,192 records with an explicit omitted count.
Cleanup waits at most ten seconds per owned session and records surviving verified or unverified candidates.
This bounded cleanup allowance follows the collection/runner deadline; it cannot extend a workload phase.

The runner preserves structured argv, runtime overrides, source/test/config trees, tool/assembly hashes, verified
binary provenance, logs/TRX, marker history, all stage/snapshot JSON, raw trace and an artifact hash index. It
distinguishes actual assertion failure from the expected inconclusive diagnostic result and independently checks
the complete JSON and four groups of 5+30. Finalized trace remains analysis-pending, not a cause or acceptance.

## Offline reader and conclusions it can support

The uncompiled draft reader `/tmp/issue538-eventpipe-draft/TraceReader.csproj` references the installed TraceEvent
and FastSerialization assemblies directly. `Program.cs` converts the **finalized** trace to ETLX after collection,
retains actual event names/IDs/versions/PIDs/thread IDs/timestamps and named payloads, and resolves managed sample
stacks from method/load/rundown metadata. It emits provenance in every JSON/JSONL and reports:

- parsed method-compilation starts, named method loads and their actual optimization-tier fields;
- module/load/rundown events, tier settings/pause/resume/background-JIT events actually observed;
- GC start/stop and suspend/restart endpoints, and all 140 exact selection identities;
- reported lost events, unknown/missing tier fields, decoder failures, unresolved/truncated/missing sample stacks,
  missing provenance/completion/clock/rundown markers, observed per-thread sample-spacing statistics and decoded-output limits.

Reader limits are 1 GiB input, at most 3 million events and 512 MiB decoded JSONL, 150,000 retained sample gaps
per thread, depth 96 per stack, and 300 seconds of enumeration after conversion. The external draft bounds
**conversion too**: 600 seconds total, a 4 GiB sampled-RSS guard and a 2 GiB per-file hard limit, with fresh output.
These guards are proposed, not run. The RSS guard does not limit virtual address reservation.
The external `/tmp/issue538-eventpipe-draft/analyze.py` drafts those guards at 100 ms polling; the RSS/log
thresholds are polling guards with possible overshoot, not hard memory ceilings. It records actual peak RSS and
retains partial output on timeout/resource/reader failure. It never collects, builds, retries or acquires a lock.
The reader has not been built, so assembly/API/event-name compatibility remains explicitly unverified. Only
following a separate authorized compile and offline-analysis capacity grant, the reproducible analysis form is:

```bash
python3 /tmp/issue538-eventpipe-draft/analyze.py --reader-dll /tmp/issue538-eventpipe-draft/bin/Release/net10.0/TraceReader.dll --trace <finalized-capture.nettrace> --request <attach/pid.json> --retention <diagnostic-producer-runtime-retention.json> --out <fresh-reader-evidence> --execute-authorized-analysis
```

The reviewed reader correction compares the **entire** source object (including branch, dirty state and dirty
fingerprint), diagnostic mode/flag and exact command across request, retention and handshake markers. It checks
the retained EventPipe nonce, directory, provider/profile and attached/detached states against those markers,
then checks in-trace nonce/PID/source/fingerprint/command. The current test snapshot has no direct PID property:
the reader reports that fact and binds its nonce/directory through the actual `pid.json` and attached/detached
PID fields; it never invents a retained PID field. Missing markers remain partial visibility, while mismatched
invocation provenance prevents association. Each sampled frame retains order, actual hexadecimal code address,
method name and resolution status, including resolved frames. Raw addresses remain available to compare later
with live method address/size/ReJIT/version records; the reader performs no automatic version/causal mapping.
The installed parser's `IsJitted`/method flags distinguish expected non-JIT `Unknown` tiers from unknown JIT
tiers or uncategorized tier data. Only the latter reduce tier visibility; all raw payloads remain retained.

Correlate the retained full producer module/ingress intervals to the trace via both clock markers and monotonic
frequency; quantify alignment error or clock discontinuity. Keep all rows, including the original measured
outliers. Match live method loads by CLR instance/module/method token or ID and code version/address; distinguish
actual JIT start/live-load events from rundown snapshots. Pair GC pauses only with unambiguous matching
suspend/restart endpoints; pair collection start/stop by its actual instance/count identifiers. Unmatched,
overlapping or missing endpoints prevent exact interval attribution. Named runtime compilation activity on a
background thread is not renderer CPU. Observing optimization of an identified hot render method and a stack
change can strengthen a hypothesis, but overlapping JIT totals or a temporal coincidence alone prove no cause.

Any nonzero reported event loss, early collector stop, absent rundown, missing method/tier data, unknown parser
events, output truncation or missing samples prevents negative/absence conclusions and can prevent attribution.
Zero reported loss is only the decoder's observation; it does not promise every relevant event was requested or
emitted. In-process hosts and other threads share this trace. Attachment and tracing can also change startup/tiering timing. A quiet diagnostic trace does not explain or
exonerate the earlier uninstrumented outliers. Instrumentation adds overhead, so a trace cannot
replace the uninstrumented acceptance result or justify widening its >25% idle-drift rule. No preconditioning
expansion, outlier removal, count change, graph change or acceptance waiver is proposed here.
