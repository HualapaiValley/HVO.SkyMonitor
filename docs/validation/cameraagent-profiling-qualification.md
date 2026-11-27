# Standalone CameraAgent telemetry qualification plan

Qualify the exact immutable product build, image, Linux kernel, architecture,
libc, GC mode, CPU/memory limits, Silo backend and device, tools, and
collector/Prometheus configuration before enabling profiling in production.
Use a disposable instance and a host with approved capacity. Keep the
[canonical workload protocol](../planning/performance-validation.md) and
profiling [operator runbook](../runbooks/cameraagent-production-profiling.md)
with the evidence. The earlier [P5 result](issue-1151-p5-rss-diagnostics.md)
concerns a different workload and observer.

## Matched arms

1. Compare `Disabled`, `Baseline`, and `Enabled` on the same build and initial
   snapshot. Keep console log level, capture cadence, camera seed, GC, limits,
   scrape request schedule and collector configuration fixed across arms.
   Record expected scrape failures in Disabled. Run a clean instance for each
   arm and counterbalance order. Capture every attempt and explain any invalid
   result. For elapsed time and allocation comparisons use the same observer
   in all arms and record its cost in a separate observer-on/off control.
2. Use W1 Mono16 and W2 RGGB16 with the same production graph. Include W3M
   durable queue and W3P payload recovery when qualifying storage, and W4
   concurrency 1, 4, and 8 for request/UI, SQLite and configured Silo contention.
   Use fixed initial queue counts, arrival rates, drain checkpoints, and output
   SHA-256 invariants. Run five independent trials per arm after the canonical
   warm-up. Record median, min and max and use p95 only with at least 30
   measurements per reported population.
3. Before execution declare the expected event rates. Start with timer-driven
   Silo readiness about 0.2/s while idle, and size work-driven signals from
   captures/s times file publications and journal writes per capture, plus
   observed UI refreshes and on-demand SVG requests/s. Include busy worker
   loops and retry/error rates from the scenario. Record histogram points and
   live series per target, collector frequency, retention, overflow and scrape
   limits. Confirm the actual scrape contains the expected families.
4. Compare process CPU per capture, allocation rate, RSS and LOH, GC pauses,
   ingress and exposure-start latency, throughput, backlog/drain, output
   checksums, durable write and sync counts, and collector CPU/RSS and
   export queue/drop/retry behavior. Keep host/process and collector accounting
   separate. Capture an explicit zero-result for absent failures.
5. Repeat the Enabled arm with a reachable collector, with stopped collection,
   and with a collector that accepts connections but does not acknowledge
   requests. Restore it in the same test and verify bounded recovery.
   Exercise missing dashboard scrapes independently of OTLP failure. Verify
   capture cadence, acknowledged local publication, output hashes, bounded
   backlog and resources, redaction, and app health during the fault and
   recovery. Record suppressed or dropped telemetry and confirm diagnostic
   tools, new permissions and credentials remain outside routine operation.

## Admission thresholds

Predeclare any stricter installed-instance requirement before the trials.
Use a starting ceiling of 5% additional median CameraAgent CPU per capture
and allocation rate and 5% additional p95 capture-start and local publication
latency for Enabled versus Baseline. Require at least 95% of baseline
throughput, no sustained backlog growth, identical correctness and no loss of
acknowledged output. Allow at most 5% additional peak RSS and LOH or a 4 MiB
measurement floor, without violating instance headroom or an existing limit.
Budget the private collector per agent separately (initially 0.05 CPU core
and 128 MiB RSS for incremental scrape/aggregation/export), and apply the
runbook's live-series and scrape caps. Report Disabled to Baseline deltas
alongside the decision. Explain interval variance, profiling observer cost
and any resource pressure; narrow or reject the profile if any declared gate
fails. Fix thresholds from accepted observations before paging on latency or
capacity. With no measured baseline, report availability and OOM alerts only.

Record exact platform and commands, workload identities and rates, all
baseline/candidate and outage trials, measurements, hash and redaction
checks, decision and retention location. This document specifies a campaign.
It contains no completed production measurements or production profile
qualification. Linux cgroup/diagnostic capability, supported ARM64/Raspberry
Pi runs, production thresholds and live Collector/Silo failure evidence
require the corresponding qualified host.
