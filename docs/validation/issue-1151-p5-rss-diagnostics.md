# P5 RSS failure disposition and diagnostic retention (#1151)

The historical RSS excursion remains **unresolved**. The retained evidence
establishes a failed whole-process RSS assertion followed by separate passes;
it does not identify a leak, establish observer neutrality, or qualify stable
production memory. [#1151](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1151)
delivers a bounded diagnostic record and this disposition. Production profiling
and any controlled overhead experiment remain under
[#244](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/244).

## Retained observations

All three observations bind product `8388002ce69aa1fa194e7644bd8f35352ac10acf`
/ tree `f045fcc2661ac6556d6765c13d5b03b3f223845e`. They are separate attempts,
not matched baseline/candidate trials.

| Harness | Observation | Retained limitation |
| --- | --- | --- |
| `224d06d73f819cd1ea41dc0cd195bdc606268205`, tree `980a277b4add708a766b0b07be580d9216601792` | Canonical P5 C1/W2 growth 39,092,224 bytes exceeds 26,807,296 by 12,284,928 bytes. | Trial ordinal, absolute medians, sample trajectory and actual GC mode were not retained. |
| `3fc4fe0a18fd2e71a5aab87c96fb99f591fba1d3`, tree `adc321bf53e96dd6b9dc652d39b4cf18e5714c0c` | A later derivative pass includes a C1 margin of only 89,088 bytes; subsequent import/provenance/hostname failures are separate results. | Successful raw RSS trajectories were not emitted; a derivative pass is not a completed import. |
| `97dc0474c1c1696a5345f1953d2dc3d0cef8c259`, tree `6dd20ff23132abe54e32d2d469aa1736812545c1` | Completed import; all ten P5 trials pass. Actual derivative report records .NET 10.0.12, Ubuntu 24.04.4 x64, eight CPUs and workstation GC (`serverGarbageCollection=false`). | Successful raw RSS trajectories are absent; medians cannot be independently recomputed from samples. |

The failed 31,709-byte raw TRX is bound by
SHA-256 `c192da1865c608aa9b9f542bb8c17853dbdfcd99053cedd9714f937aa72f333c`;
the retained triage's selected-facts receipt records one failed method,
no timeout/abort, no attachments, and the RSS assertion at harness line 944.
Its method duration is 17:27.1847094, which does not identify the P5 trial.
Source control flow identifies C1 because C1 precedes C4 and the envelope
depends on concurrency.

The final private terminal pack is bound by SHA-256
`cf4519d1ea15cf6506ddf6702a1e72d130c1cc8a1c889c8be8db42b855898338` and the
[#971 terminal handoff](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/971#issuecomment-5986089196).
The actual derivative JSON inside its nested archive is 249,184 bytes, SHA-256
`16675f7dcb10f0f585ec9ca83dabe83f7658d19e2d0fe98bcaff56b43b347953`.
The pack and that report were independently read for #1151 without changing the
originals. The pack REPORT's statement that P5 uses 25,233,408-byte frames is
incorrect: its actual derivative JSON and source use W2, 12,879,360 bytes;
25,233,408 is W6. The original pack remains unchanged.

These are the retained final-report scalar results, not reconstructed raw data:

| Concurrency | Trial | Samples | RSS growth bytes | Margin bytes |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 1 | 6,015 | 26,497,024 | 310,272 |
| 1 | 2 | 6,013 | 1,044,480 | 25,762,816 |
| 1 | 3 | 6,013 | 364,544 | 26,442,752 |
| 1 | 4 | 6,013 | 782,336 | 26,024,960 |
| 1 | 5 | 6,023 | 1,003,520 | 25,803,776 |
| 4 | 1 | 6,013 | 5,107,712 | 60,337,664 |
| 4 | 2 | 6,013 | 5,181,440 | 60,263,936 |
| 4 | 3 | 6,013 | 8,626,176 | 56,819,200 |
| 4 | 4 | 6,013 | 4,583,424 | 60,861,952 |
| 4 | 5 | 6,013 | 6,647,808 | 58,797,568 |

## Comparability and observer limits

The canonical P5 workload is 3096 x 2080 BayerRggb16, stride 6,192, seed 2025,
the `image-quality` recipe, 30 initial jobs, three disabled arrivals over
30 seconds, six enabled arrivals over 60 seconds, and a ten-second arrival
interval. Each completed trial has 39 jobs and 502,295,040 logical input bytes.
The order is P1, P2, P3, P4, then five C1 trials followed by five C4 trials.
Pretrial stabilization sets LOH `CompactOnce`, performs forced compacting full
GC, waits for finalizers, and performs another full GC.

The #1151 starting branch's derivative source is byte-identical to retained
224d. Retained #971 source-equivalence evidence compares the later failure-only
retention addition with 224d and attests unchanged workloads, settings, math,
ordering, helpers and 10 ms sampling. The final readiness report attests all
1,536 C#/Compose entries equal between reviewed 3fc4 and 97dc; subsequent
changes address named-branch provenance and generated hostname/environment
handling. These source checks do not establish equivalent runtime histories.
SDK 10.0.401/runtime 10.0.12 are recorded for the import attempts. Failed-process
GC mode remains unknown: an absent runtimeconfig GC property does not exclude
ambient overrides. The fresh-process diagnostic requires server GC and omits
P1-P4 history, so it cannot silently replace a faithful reproduction.

The RSS observer samples `Process.WorkingSet64` for the entire in-process
testhost/LogicHost every 10 ms. It begins after disabled arrivals and GC
stabilization, includes worker start, continuing arrivals, completion and worker
disposal, then stops before final correctness validation. Middle/final thirds
use sample counts, sorted lower medians, and growth clamped to zero. The
unchanged envelope is `(concurrency + 1) * 12,879,360 + 1,048,576`: 26,807,296
bytes at C1 and 65,445,376 at C4. Delayed/coalesced timer ticks alter the time
represented by sample-count thirds; 10 ms observations do not bound every peak.

RSS includes native and managed resident pages across fixture, SQL seeding and
polling, worker maintenance, telemetry, and observer activity. The existing
observer allocates timed samples into a concurrent queue and creates/disposes a
Process object on each tick. Its memory and CPU are part of that process; its
queue is not bounded by the new export cap. The original failed trial has no
retained managed-heap, GC committed-memory, allocation or GC trajectory.
Managed heap estimates, GC committed bytes and cumulative allocated bytes are
different quantities; none is an RSS decomposition or a live-buffer census.

External #971 observation included process/status/fragment reads, report
retention and numerical/provenance audits. The final pack binds the report-copy
helper's observation at 2026-10-04T23:00:03.668316Z, but it contains no matched
observer-disabled control or measured observer resource budget. Launcher
RUSAGE_CHILDREN and operational RSS samples have different scope from P5. They
cannot replace missing P5 samples or prove observer neutrality. Source changes,
GC history, native/managed residency and observation activity remain possible
contributors; these are hypotheses, not a supported causal attribution.

## New failure record

The canonical producer now attempts a private `hvo-logichost-p5-trial-diagnostic-v1`
record before the unchanged RSS assertion. An error or cancellation during
sampled recovery or later trial validation also attempts a partial record after
stopping sampling, then rethrows the original failure. Missing correctness,
backlog or timing fields remain null; a partial trial does not establish a
plateau result. Retention failure cannot turn a failed trial into a pass. An RSS
assertion retains both assertion and retention exceptions; other failures retain
their original exception and emit the retention exception type.

Records live only under the ignored
`tests/HVO.SkyMonitor.LogicHost.IntegrationTests/TestResults/p5-diagnostics/`.
Unix directories require mode 0700 and new files use 0600. Symlink/reparse
ancestors and existing nonprivate directories are rejected. Publication flushes
an exclusive staging file, atomically renames it without overwrite, and removes
staging on cancellation, oversize or publication failure. Windows private
publication is explicitly unsupported rather than claiming an ACL guarantee.
This is bounded diagnostic publication, not a power-loss durability claim.

The record contains ordinal/scenario, concurrency, safe workload identity and
hash, scale, completed phase/trial counts, actual runtime/GC mode, assembly hash
and Git identity, available durable-backlog/correctness state, resource scalars,
heap/commit/GC endpoints, and an ordered RSS sample prefix. It contains no image
or raw-frame bytes, object keys, credentials, environment dumps, SQL text,
telemetry tag arrays or exception-message payload. Endpoints are labelled as
endpoints; raw heap/GC trajectories and exact sampling UTC are not manufactured.

At most 16,384 original RSS samples are exported. Observed count, retained count,
truncation and prefix policy are explicit; medians still use **all** original
samples. A truncated record cannot reconstruct those medians. A streaming
4 MiB byte bound applies to the entire file; it fails publication rather than
leaving a partial JSON document. Both limits bound the diagnostic export, not
the pre-existing live observer. Successful canonical trajectories remain
unemitted; the existing fresh-process report remains a separate diagnostic.

No serialization, hashing or file publication occurs inside the sampled
interval. Identity strings are prepared before stabilization; added GC counters
and a non-collecting heap observation occur at the endpoints. Their allocations,
CPU, native effects and any influence on later trials are **unqualified**.
Post-stop work is not proof of zero measurement effect. No matched production
baseline/candidate comparison, RSS benefit, throughput improvement, or observer
overhead percentage is claimed. CPU, latency, throughput and backlog cost of
retention are N/A as production comparisons; its output cap and failure behavior
are deterministically verified. A later measurement must separately report
observer CPU, allocations/RSS, filesystem bytes/operations, pauses, sampling
gaps and interference with continuing work.

## Validation and future execution boundary

The Docker-free `P5TrialDiagnosticsTests` fixtures verify reconstructable sample
publication and original assertion, a successful assertion's inactive recorder,
canceled retention, record-construction failure, streaming oversize, cancellation
during serialization, no overwrite, private modes, nonprivate-directory refusal
and symlink refusal. They are validation of retention behavior, not scientific
measurements. Reproduce them with:

```bash
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test \
  tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj \
  --configuration Release --filter FullyQualifiedName~P5TrialDiagnosticsTests
```

Tier C local candidate results, classifier selection and exact-range independent
review are recorded in the #1151 issue/PR ledger. Protected CI uses only
`profile=development-v1` Preflight and Build and Unit; it is integration evidence.
The Manual canonical method is not part of those Unit/Integration selections.
No canonical scientific measurement is authorized or performed by this change.

Any future reproduction needs a declared execution host and capacity, immutable
product/harness/build identities, actual GC/runtime observation, the unchanged
workload/threshold/seed/order above, sampling/observer settings, declared attempt
count and retention of **every** result. Its plan must distinguish a
history-preserving reproduction from an observer-on/off control. A changed
observer control is a distinct experiment and cannot retroactively clear the
historical failure. Do not retry until green, resample, relax the envelope or
change scale/GC to obtain a pass. This development/v1 change does not replace
the accepted #971 pin or establish admissibility under its historical allowlist.

No production defect has been established, so no worker optimization or owning
product correction is proposed. #244 retains the unresolved overhead and
production-diagnosis boundary. Completed imports and milestones remain closed;
production qualification, installers/live upgrades, installed instances,
physical hardware, `/mnt/hvo-1058` and reserved main promotion remain excluded.
