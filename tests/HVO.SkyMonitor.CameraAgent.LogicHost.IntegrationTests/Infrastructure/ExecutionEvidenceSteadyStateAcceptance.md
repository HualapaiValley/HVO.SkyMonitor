# Issue 538: opt-in cold-start and steady-state acceptance

The maintainer approved this separate manifest after the retained original runs
remained Inconclusive under the unchanged 25% idle-bracket drift rule. Those results
remain historical evidence. This mode does not waive acceptance or establish a JIT,
GC or other causal explanation for startup behavior.

```bash
HVO_ISSUE_538_STEADY_STATE=1 dotnet test tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~ExecutionEvidencePerformanceTests.Issue538_MetadataAndLiveInterferenceAcceptance
```

Default invocation still runs the original I => M => C manifest. Diagnostic mode
still runs only the original four I phases and retains exactly 140 selections;
steady-state mode refuses diagnostic mode or an EventPipe request. No diagnostic
collector is installed in this acceptance mode. Results use the separate
`TestResults/issue-538/<SHA>/<clean-or-dirty-fingerprint>/steady-state/` namespace.
Every new stage/final artifact records the actual mode, flag-prefixed repeat
command and complete source/fingerprint. Existing result directories are untouched.

On the same fresh isolated W1 fixture, select the first 30 actual capture/raw-upload/
central-preview operations. There is no initial exporter cycle, evidence request,
extra acquisition or adaptive warmup. All raw lengths/checksums, central output
verification and exact job/output lineage use the real existing paths. Retain full
capture, module, edge-step, ingress, upload and central latencies, resource/SQL
bookends, per-operation absolute process snapshots and health. Pre-host resource
bookends include fixture startup, which is separately timestamped; the cold
operation/drain phase has its own 180-second cooperative deadline after binding.
The in-process factory startup has its existing fixture lifetime, not a newly
invented cancellation API.

After the 30 selected operations, pause normal admission and await the unchanged
accepted-execution terminal barrier. Upload every naturally admitted raw and
remaining derivative, complete its central job, then drain the actual exporter.
Assert canonical SQL bytes and exact hashes/ACKs, no evidence pending/retry, no raw
or derivative upload held, and no pending central jobs. Record imported unit and
observation additions and per-binding due-check counts before/after this barrier;
the production reconciler stays enabled. All cold selection and drain work shares
the cold deadline. Only after the barrier does capture resume for the original
idle-before / active / denied / idle-after phases, each exactly 5 warmup + 30
measured operations under its original 180-second deadline.

The opt-in completed-operation observer retains every actual isolated-host tuple
as it completes, including unselected startup, warmup-drain and final-drain frames.
An immutable stage observation and a separate exact selection registry distinguish
30 cold, 20 warmup, 120 measured and naturally admitted operations. No capture is
selected twice or dropped. Final convergence requires the retained tuple count to
equal the durable raw-capture count, consecutive sequences and unique jobs. The
4096-record retention bound fails instead of evicting data. Retention/health probes
remain within operation wall time; process counters include both in-process hosts
and harness, while SQL container cgroup counters remain separate. The observer is
absent from default and diagnostic invocations.

Cold results and natural drain records are reported separately. Only the original
four phases enter the same ten comparisons. Idle p95 drift >25% is Inconclusive;
otherwise material regression is >max(25%, measured bracket variation). Existing
failure/overflow/quarantine/abandonment and bounded raw/central backlog assertions
remain. No counts, geometry, graph, seed, cadence, deadlines or thresholds change.
M then remeasures the original 10,000-observation history; C remeasures concurrency
1/4/8, each 20 warmup +200 measured requests. The receiver feedback correction
changed their measured path, so earlier M/C results remain historical baselines.

The optional diagnostic ingress wrapper also explicitly delegates the upstream
`IRawCaptureIngress.InitializeForOperationsAsync` to the real inner implementation.
This preserves its operational recovery semantics instead of substituting the
interface's default `InitializeAsync` implementation. No product API or schema is
changed here.

This implementation is committed before focused compilation. Actual build/format/
correctness results and exact compiled-source hashes belong in the external gate
ledger. No Manual invocation or acceptance result is implied by this document.
