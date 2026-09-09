# Full-Resolution Standalone CameraAgent Validation

Issue [#171](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/171) validates one standalone
CameraAgent with LogicHost and all shared central services absent. The workload uses a
1936x1216 Mono16 ASI174 profile, five-second exposure and minimum-start cadence, the verified
Production HYG 4.2 snapshot, synthetic calibration, five-frame rolling combination, local
previews and annotation, durable storage, restart recovery, retention configuration, health,
operations, and gallery access.

## Retained Image

The sanitized deterministic annotated JPEG below is retained from the reviewed production
smoke. Its SHA-256 is
`E859398445752A20EED0EBCFFF4E5F624EDE523F7416B81A881FA88F6E10171C`.

![Full-resolution standalone annotated VirtualSky frame](cameraagent-standalone-171.jpg)

The decoded retained JPEG is 1936x1216 Mono8. The smoke compares it with the corresponding
decoded unannotated JPEG and requires the image-circle and cardinal overlay to survive JPEG
compression.

## Reproduction

Install the approved catalog package and run:

```bash
HVO_CATALOG_PERF_ROOT=/var/lib/hvo/skymonitor/catalogs/hyg-v42-production \
  ./scripts/test:cameraagent-standalone-production-smoke
```

The runner produces five fresh, approximately one-minute trials under
`TestResults/issue-171/production-smoke`. Each trial first requires a quiescent host (one-minute
load average at or below `HVO_SMOKE_MAX_LOAD1`, default half the processor count; see the
smoke-test runbook), because the arrival budget is a cadence contract rather than a load test.
The initial load observation may admit immediately; every observation after a wait must complete
at or before the inclusive monotonic deadline, including one that first falls below the limit.
Evidence is staged and published only after every gate passes. A probed Linux atomic directory
exchange keeps the prior canonical path intact through a publication failure or interruption, so
a refused or failed run cannot create a canonical-path gap.
The review inputs are:

- `five-trial-summary.json`
- `trial-*/evidence/issue-171-standalone-smoke.json`
- `trial-*/evidence/issue-171-cadence-diagnostic.json` (per-capture intervals, jitter, start
  reasons, within-budget flags, structural violations, host load or `null` when unavailable,
  and GC pauses; written before any cadence invariant or the arrival budget is asserted)
- `quiescence-log.tsv` (per-trial admission: observed load, limit, processors, wait)
- `trial-*/evidence/issue-171-full-resolution-annotated.jpg`
- `reference-calibration-W1/processing-performance-W1-reference-calibration.json`
- `reference-calibration-W2/processing-performance-W2-reference-calibration.json`
- `local-graph/local-processing-performance.json`
- `local-graph/fault-recovery.json`
- `local-graph/runtime-signals.json`

Every trial records the commit, branch, complete dirty-tree SHA-256, fixed scene epoch,
catalog and deployment identities, selected catalog row IDs, recipe and fully resolvable
lineage, artifact checksums, actual monotonic module-start intervals, CPU, RSS/LOH,
allocations, process I/O, throughput, latency, queue count/bytes/age, and drain behavior.
The restart gate interrupts durable lane work, proves it remains pending after the old host
has stopped, starts a new host, and hashes every recovered raw and derivative payload. It
also requires zero duplicate logical outputs and zero attempted central HTTP requests.
