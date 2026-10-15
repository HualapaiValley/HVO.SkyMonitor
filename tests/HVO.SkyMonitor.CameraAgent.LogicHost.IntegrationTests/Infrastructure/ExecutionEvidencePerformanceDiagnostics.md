# Issue 538 opt-in capture diagnostics (preparation; not acceptance)

Enable only for the existing Manual method with `HVO_ISSUE_538_DIAGNOSTICS=1` after a separately granted host slot.
The focused Release build with `-warnaserror` and scoped format verification passed with zero warnings/errors.
The initial format check identified four whitespace corrections; the corrected source passed both checks.
No diagnostic invocation has run, and no diagnostic or acceptance outcome is claimed.

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
