# Same-load annual schedule validation reuse (#1591)

This implements the bounded follow-up identified by [the #1559 attribution
record](issue-1559-protected-read-attribution.md#smallest-bounded-follow-up).
The scope is a single `FileCameraAgentConfigurationLoader.LoadAsync` call.
#1041 remains open for the dominant retained parsing cost and separately scoped
investigation. This does not address the endpoint file-reopen snapshot race.

## Validation boundary

The loader still reads and deserializes the actual file, checks the current file
contract, and validates the configured observer before retained-location
initialization. The first schedule pass includes the 2024 leap year's 366 days
and every distinct date exception, including dates outside that year. A token
is usable only after all configuration validation succeeds. The real location
store then initializes/reconciles and validates its protected history, retained
files and journal in their existing order. No store code changed.

The second compatibility pass always checks setpoints against the exposure
envelope and sensor gain range, resolves the effective observer and the system
time zone, and compares actual values against the first pass's snapshot:

- The entire serialized schedule, including nested weekly boundaries, profiles,
  date exception windows, blackouts and compatibility fields.
- The entire envelope, configured sensor response and control policy, including
  their nested collections. This is deliberately stricter than comparing only
  the currently consumed gain limits.
- Latitude, longitude, elevation and the exact time-zone identifier, plus
  `TimeZoneInfo.HasSameRules` for the resolved rules consumed by expansion.

The snapshots retain full values, not fingerprints, file metadata, location
versions or collection/object reference equality. The annual start date/day
count and concrete solar calculator are constants within this method. The
expander/calculator have no durable side effects. Control-policy validation
still runs in the initial full configuration pass; it is not independently
memoized. A changed input runs all expansions again. All date-exception passes
must succeed before a snapshot can authorize reuse.

Successful evidence lives in one local variable. There is no static or service
validation-result cache, cross-call sharing, single-flight mechanism, retained-evidence index,
identity authority or publication of failed/canceled work. Every independent
load still validates and initializes retained state. The configured and effective
observer checks keep their order. CLI deadlines/caller cancellation and endpoint
authentication, owner readiness and pre-publication identity comparisons are
unchanged.

## Correctness evidence

The regression-first baseline is test-only commit `4da83429` (runtime inherited
unchanged from `fc716969`). Both fixed and solar
`UnchangedObserverExpandsOncePerIndependentLoad` cases fail on that runtime:
expected one annual expansion, observed two. Baseline warning-clean compilation
was corrected to use `CancelAsync` before the retained final baseline build.

The candidate's focused suite covers both schedule types, all four observer
components independently, different time-zone rules with the same identifier,
real current-file schedule/envelope/sensor-range edits, nested collection value
comparison, control-policy changes, a future invalid date exception, invalid
effective time zones, failed/canceled loads, and four concurrent loads paused
inside retained initialization. The original authenticated endpoint regression
continues to cover same-length/same-timestamp edits, delayed and concurrent reads,
fresh file/journal corruption, unsupported configuration/history schemas, token
protection and current owner readiness. It now asserts one annual expansion for
unchanged reads and two after the real audited manual-location change.

## Comparative method

Use the real authenticated disposable endpoint, fixture corpus, counters and
short-root preflight from #1559/#1578. The Manual harness now retains 40 settling
reads separately before 30 measured reads per fixed/solar workload. Five trials
per revision are interleaved B,C,C,B,B,C,C,B,B,C on one host, one allowed CPU and
a 384 MiB managed heap cap. Fixture setup, first fresh-host reads, no-listener
controls, settling, measured reads, concurrent batches and real edits remain
separate phases. Retained pre/post checksums and semantic schedule identities
must match. Disposable absolute roots make whole-file configuration hashes vary
between fresh fixtures; no byte-identical whole-configuration claim is made.

The primary sample unit is each trial's median of 30 steady reads. Report the
five-trial median and min/max; do not pool process samples to infer a percentile.
Inspect early/late measured and settling stages before attributing an improvement.
Process CPU/allocation/I/O counters include the in-process client, host and
listener. Nested stages overlap and must not be added. RSS is sampled, while its
peak is a process-lifetime high-water mark including fixture setup. A heap cap
is not an RSS limit; warm OS-cache logical I/O is not physical disk throughput.
Four-request batch throughput drains caller requests and is not a durable
worker-backlog or sustained-load qualification.

## Primary results

The primary baseline is `4da834295aa37be242dac6ae9b6e3ab69ef6dc63`; candidate
is `f904018368c05194f5eb088a26fd73bef294b845`. Executable inputs were committed
and clean; only the unfinished Markdown report was untracked. A later report /
diagnostic-only commit does not change the measured production sources. Both
revisions use identical Manual harness logic and fixed/solar fixtures. The
baseline source commits are retained in this PR's history for reproducibility.

Trials ran 2026-10-23 on Ubuntu 24.04.5, x64, Intel Core i9-14900K, about 15 GiB
host RAM, ext4, SDK 10.0.402 / .NET 10.0.13, native Release. Every measured test
process used one kernel-affined CPU, `Environment.ProcessorCount=1`, workstation
GC and exactly 402,653,184 bytes managed heap availability. OS caches and native
memory were not capped. No installed service, physical device or Pi was used.

Each cell below is the median of five trial medians, followed by their min–max.
Times are milliseconds; allocated memory is MiB (1,048,576 bytes). There is no
p95/p99 inference from five trials.

| Metric | Fixed baseline | Fixed candidate | Solar baseline | Solar candidate |
| --- | ---: | ---: | ---: | ---: |
| Endpoint, ms | 190.02 (179.49–197.29) | 172.58 (171.23–197.59) | 191.36 (183.62–202.19) | 181.25 (165.90–189.12) |
| Process CPU, ms | 184.41 (176.06–194.36) | 167.94 (161.59–193.94) | 188.71 (179.59–200.71) | 171.42 (162.73–179.51) |
| Process allocation, MiB | 122.25 (122.25–122.25) | 121.06 (120.44–121.06) | 133.71 (133.71–133.71) | 126.48 (126.48–126.48) |
| Annual expansions, ms | 3.84 (3.63–4.10) | 1.94 (0.84–2.06) | 31.06 (29.45–32.19) | 15.12 (14.72–15.64) |
| Annual expansion allocation, MiB | 2.39 (2.39–2.39) | 1.19 (1.19–1.19) | 14.47 (14.47–14.47) | 7.23 (7.23–7.23) |
| Retained files + journal, ms | 177.82 (169.52–187.31) | 163.93 (158.35–189.16) | 156.57 (148.05–166.18) | 154.33 (146.28–161.48) |
| Max sampled steady RSS, MiB | 302.59 (296.90–304.59) | 298.27 (294.14–312.09) | 392.38 (355.93–399.83) | 389.58 (377.39–444.64) |
| Four-request batch completion, ms | 694.55 (656.69–719.95) | 699.01 (630.27–744.72) | 777.52 (730.37–798.04) | 714.77 (667.64–736.90) |

Every one of the 300 baseline and 300 candidate instrumented steady endpoint
reads had the expected counts: two annual expansions before, one after, and one
retained-file plus one retained-journal validation in both. Concurrent batches
had eight versus four expansions for four calls. Real manual-location changes
still had two expansions. All retained pre/post checksums matched; schedule,
rig, location and catalog identities matched across equivalent trials. The
ordered manifest corpus hash was identical in all workloads and revisions.

The reliable direct effect is removal of one expansion: approximately half the
expansion time and allocation, and approximately 1.25 MB less whole-read
allocation for fixed / 7.58 MB less for solar (about 0.97% / 5.41%). The full-value
snapshot adds only a small part of the saved allocation. Listener-free controls
also allocate less (about 1.26 MB fixed / 7.97 MB solar); their earlier timing
phase is not used for a settled endpoint speed claim.

Whole-endpoint medians were lower by 17.44 ms fixed / 10.10 ms solar, but the
trial ranges overlap. The fixed retained stage, which this change does not
modify, also shifted by about 13.89 ms. Fixed endpoint early-to-late medians
within individual trials often fell substantially, so the entire fixed endpoint
difference cannot be credited to reuse. Solar expansion per-call medians remain
near 15 ms from late settling through early/late measurement; the former #1559
30-to-16 ms expansion phase transition is absent. Candidate fixed trial c4 is
already in a faster approximately 0.9 ms expansion phase during late settling,
while the other processes are near 2 ms. These differences remain visible in
the raw record and are not attributed to this optimization. The solar endpoint
saving is consistent with the changed stage, but does not establish a portable
5.28% latency guarantee. Retained parsing remains dominant.

Logical reads remain about 32.7 MB per read, with approximately unchanged syscall
counts and zero median physical storage reads under warm OS caches. Fixed
four-request completion is about 0.64% higher at the medians, with overlapping
ranges; solar completion is about 8.07% lower. Median batch drain rates are
about 5.76→5.72 fixed and 5.14→5.60 solar requests/s. There is no durable backlog
in this harness and no sustained-load/admission or parallel retained-scan claim.

Median maximum steady RSS was slightly lower, but two candidate solar trials
sampled about 444–445 MiB versus a baseline maximum about 400 MiB. Candidate
process-lifetime peaks reached about 475 MiB, including setup and later phases,
versus about 418 MiB baseline. Lower allocation alone does not explain RSS.
The supplemental diagnostics below investigate live/committed managed memory
and collection timing before making a memory-regression disposition.

The [raw ten-trial archive](issue-1591-schedule-validation-reuse.json.gz) retains
all phases, identities, counters and spans. Its SHA-256 is
`84287862c9f6756fd54438f0791ace45326c9119bb14ad4d51b9b0a25ca37e91`.
The [uncompressed summary](issue-1591-schedule-validation-summary.json) retains
per-trial metrics and early/late expansion and endpoint medians. The earlier
35,049 ms observation is not part of these results.

## Supplemental memory diagnostics

Two interleaved trials per revision (B,C,C,B) used 120 settling reads and the
same fixed/solar corpus. The optional `HVO_INSTALLATION_READ_GC_DIAGNOSTICS=1`
records managed heap/commit/fragmentation, collection counts and RSS after each
sample's counters stop. A forced compacting collection probe runs only after
all timed phases, both real edits and both fixture disposals. Its before/after
survivor and committed heap data are separate from endpoint timings. The longer
settling cohort is diagnostic evidence; it does not silently replace the five
primary trials above.

The supplemental source pins are baseline
`2d341fcbf726eb31761e5961c9c4762a5a814aab` and candidate
`190f7db1e6cc681887ec7e424b341821d3ea9b31`. The source manifest verifies identical
Manual harness tails and unchanged production/fixture input hashes relative to
the primary measurements. The provenance merge retains baseline commits and
verifies that merging them does not change the candidate's tree.

| Trial | Fixed max RSS / max committed heap, MiB | Solar max RSS / max committed heap, MiB | Gen2 count before final probe | Allocated managed heap after probe, MiB |
| --- | ---: | ---: | ---: | ---: |
| baseline gb1 | 278.06 / 58.25 | 440.84 / 196.94 | 14 | 43.85 |
| candidate gc1 | 303.73 / 83.39 | 420.49 / 173.78 | 8 | 44.56 |
| candidate gc2 | 304.55 / 82.52 | 401.30 / 148.03 | 10 | 45.86 |
| baseline gb2 | 278.65 / 57.33 | 416.83 / 171.96 | 14 | 44.20 |

The longer settling also resolves the fixed endpoint drift: early→late medians
are 159.05→155.44 and 157.71→157.58 ms for baseline, 156.20→160.04 and
154.96→156.02 ms for candidate (about ±2.5%, rather than the larger primary
phase shifts). Settled fixed expansion totals are about 1.6–1.9 ms for two calls
versus 0.8 ms for one. Thus there is **no demonstrated material fixed endpoint
latency benefit**; its eliminated work is smaller than the endpoint noise.
Solar early/late expansion totals stay near 29 ms before / 15 ms after, and
endpoint early/late changes are about 1.5% or less. The direct solar stage saving
persists after the longer warm-up. This diagnostic cohort has two trials per
revision, so it is reported as phase evidence and does not replace the primary
five-trial statistics or support new percentile claims.

The roughly 25 MiB fixed RSS increase tracks roughly 25 MiB additional managed
commitment. Candidate fixed windows have fewer generation-2 collections (counts
5→6 versus baseline 8→9); one candidate solar steady window has no gen2
collection (7→7 versus baseline 12→13). Its pre-probe allocated heap is larger,
but the post-probe value returns to the same approximately 44–46 MiB range.
This supports a GC commitment/cadence explanation for snapshot RSS variation,
rather than accumulation of reusable validation results or retained buffers.
The source retains no such results across calls. Static serializer options
retain bounded framework/type metadata, not configuration validation evidence.
The probe's values include runtime/test metadata and captured reports; they are
not an allocation-stack or exact live-object accounting.

GC heap size is not live bytes: gc1 retains about 64 MiB recorded fragmentation
after the probe. The probe requests compacting gen2 collections but does not
change LOH compaction policy or require the runtime to return committed pages
to the OS. Its RSS therefore need not fall with allocated managed bytes.
Supplemental solar baseline RSS reaches above either candidate's steady maximum,
showing the primary upper outliers are not a consistent directional RSS change.
The diagnostics explain the fixed commitment tradeoff and support the GC-phase
interpretation of the primary outliers; they do not attribute every RSS page.

Disposition: the reduction in allocation/expansion is demonstrated. Lower
allocation changes GC cadence and can increase committed heap and transient RSS;
no monotonic live-heap growth or new unbounded retention was found. The observed
475 MiB primary lifetime peak remains part of the recorded envelope. There is
no RSS reduction guarantee and the 384 MiB managed heap limit is not an RSS
budget or Pi qualification. Retained parsing and any future hard-RSS requirement
remain separately scoped work.

The [four supplemental reports](issue-1591-schedule-memory-diagnostics.json.gz)
retain every phase and probe. Archive SHA-256:
`f2bf507b36d7f31e4f15949d276677e4148c0b1b8294500a3d610754e6fb3407`.
The [source and environment manifest](issue-1591-schedule-validation-inputs.json)
records both cohorts' revisions, input checksums and harness-equivalence checks.

## Reproduction

Use short absolute output roots registered with `hvo scratch HVO.SkyMonitor 1591`;
#1578's preflight checks the prospective recovery socket path. Build each pinned
checkout before applying measurement constraints. For each trial, choose a new
registered output subdirectory and run from the corresponding source checkout:

```bash
project=tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj
flock -o /tmp/hvo-heavy-gate.lock timeout 600 \
  dotnet build "$project" --configuration Release -warnaserror \
  -nodeReuse:false -p:UseSharedCompilation=false

# measure_cpu is one CPU allowed by taskset -pc $$ (CPU 0 in this evidence).
# evidence_root is a fresh short registered output directory for this trial.
flock -o /tmp/hvo-heavy-gate.lock timeout 900 \
  env -u HVO_PRODUCTION_CATALOG_BUNDLE DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  DOTNET_PROCESSOR_COUNT=1 DOTNET_GCHeapHardLimit=0x18000000 \
  HVO_INSTALLATION_READ_EVIDENCE_DIR="$evidence_root" \
  HVO_ATTRIBUTION_REVISION="$(git rev-parse HEAD)" \
  taskset -c "$measure_cpu" dotnet test "$project" \
  --no-build --no-restore --configuration Release \
  --filter 'FullyQualifiedName~InstallationVerificationReadTests.ConstrainedDisposableReadsRecordStageAttribution' \
  --logger 'trx;LogFileName=attribution.trx' --results-directory "$evidence_root/trx" \
  -nodeReuse:false -p:UseSharedCompilation=false
```

Primary source pins hard-code 40 settling / 30 measured reads. Repeat in order
B,C,C,B,B,C,C,B,B,C, five trials per source. For the supplemental source pins,
add `HVO_INSTALLATION_READ_GC_DIAGNOSTICS=1` and
`HVO_INSTALLATION_READ_SETTLING_READS=120` to the same runtime command and use
B,C,C,B. The default settling value remains 40; the report records its actual
settling/measured counts. Each fixture removes its generated `iv-*` host/socket
state on disposal while retaining reports, TRX and caller-owned output roots.
The exact trial commands and terminal results remain in registered evidence
scratch. Raw archive/summary JSON supports recomputation without rerunning the
workload.

## Local validation and observability

Target profile is **development-v1**, risk Tier C. The selector is
`scripts/ci:classify pull_request <merge-base> <candidate>`; its selected lanes
are cameraagent and combined, `mode=full complete=false`, with all other
component/deployment flags false. This selects local affected coverage; it does
not authorize a full CI workflow dispatch. The complete Tier C candidate gate
runs the warning-clean solution Debug/Release builds, format verification,
package audit, four CI-control guards and the complete positive Unit / separate
Integration selections. Canonical per-project commands from `ci.yml` retain
Unit/Integration TRX and coverage slots, including supplemental Unit selections
and architecture Integration cases. Selected cameraagent/combined component
coverage, publish, migration shape, policy and focused category discovery are
recorded alongside the gate results.

Fifteen focused reuse Unit cases, the authenticated endpoint Integration
regression, affected formatting, warning-clean baseline/candidate Release
builds, operations documentation audit and all four control guards pass. Guards
also pass after merging target `12bc513a`; summed inventories are Unit=13345,
Integration=939, Manual=277, Soak=1, External=0, Hardware=1. No new test categories
or projects were introduced. The complete candidate gate at
`190f7db1e6cc681887ec7e424b341821d3ea9b31` completed successfully on
2026-10-23 at 11:31 UTC: all 46 steps exited zero. Unit selected 13,345 cases:
13,221 passed, zero failed and 124 explicitly skipped/inconclusive (122 require
approved catalog bundles, one is OSX-only and one provider cannot reach the
asserted ambiguous-delete state). Skipped cases are not claimed as passes.
Integration passed all 932 cases in its six selections plus all 7 architecture
Integration cases. CameraAgent Unit discovery returned 6,980 cases. Selected
cameraagent coverage was 89.9468% line / 76.5384% branch; combined coverage was
33.7872% / 19.6669%; both component baselines passed. Generated logger-source
lookup warnings during report rendering did not affect the coverage gates.
The [local gate ledger](issue-1591-local-gate.json) retains exact commands,
selector, source pin, durations, terminal codes and TRX/coverage checksums.
Subsequent target synchronization and affected validation are recorded in the
PR ledger; no unchanged long suite or performance cohort is repeated. Target
`523d43d9` subsequently adds two unrelated CameraAgent Unit cases and no
production or protected-read fixture changes. Its merge resolves only the
changelog and summed inventories (Unit=13,347; CameraAgent Unit=6,982).
All four control guards and the documentation audit pass on that union.

The signal manifest uses existing `configuration.load`, `configuration.validate`,
`configuration.schedule-compatibility`, annual expansion and retained-validation
spans. Healthy unchanged reads keep both compatibility stages and one expansion;
changed observers keep two expansions. The real endpoint regressions assert
fresh rejection and successful repair for each corrupt/unsupported state.
No host logging/metrics/health contract changed, no source was added to the
operational tracing provider, and no new label cardinality or payload/path
logging was introduced. There is no worker/backlog or retry policy change.
CLI deadline/caller-cancellation behavior remains covered by the unchanged
complete Unit selection.
