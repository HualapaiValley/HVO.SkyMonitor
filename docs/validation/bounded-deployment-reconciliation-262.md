# Bounded deployment reconciliation scale validation

Issue: [#262](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/262).
Production revision: `3e118ee8a69a05794bbe6904368e33fc37b6f132`.
All 20 cases converged, five at each scale. Authority, batch, identity and durable-work
checks passed. The evidence supports the tested bounded reconciliation behavior;
allocation budgets and sustained-host resident-memory qualification remain outside
this result. No product or changelog change was needed.

After capture, this evidence branch incorporated `development/v1` through
`58e71cee1a1fab8b4b36b0ee548ef6ad95db5335`, including PR #1549's read-history
fixture repair needed for normal Integration validation. The authority and
reconciliation services, derivative scheduler, window resolver and retained #248
helper are byte-identical to the measured revision. Other processing, recipe and
seeding code changed upstream. These timing, identity and resource observations
remain measurements of `3e118ee8`; subsequent local gates validate the synchronized
branch separately and do not turn its newer binaries into measured endpoints.

## Workload and method

W3M: one deployment, 10/100/1,000/10,000 captures, one Available/Complete,
reconstructable Raw artifact per capture, 1 × 2 Mono16 metadata, deterministic
2026-07-31 fixture facts. Initial backlog equals capture count; arrivals and
payload-object reads/writes are zero. The disposable extension reuses the retained
[#248 fixture](../../tests/HVO.SkyMonitor.LogicHost.IntegrationTests/DeploymentLocationAuthorityIssue248BaselineTests.cs)
for seeding, policy prerequisites, exact state and production request identities.

One full-graph 10-capture warm-up precedes five fresh-database trials per scale
in one native Release process. Each case migrates/seeds a disposable SQL database
and starts a child host. One serial worker uses production discovery/state batches
of 250 and scheduling batches of 25, fresh scopes, real scheduler persistence and
window resolution. A separate SQL session makes a no-op registration update at
approximately 1 Hz during drain, with a 10-second command timeout.

Authority elapsed time ends when `ResolveAsync` returns after commit. Drain runs
from the subsequent worker phase through idle and writer shutdown. CPU/allocation
endpoints include authority, intervening queue assertions and drain. Startup,
migration, seeding, final state/identity verification and cleanup are excluded.
RSS starts before authority and is sampled after worker passes; its sampled maximum
is not a true peak. SQL data/log values are endpoint allocation/use snapshots.

Protocol v2 ran **2026-10-23 00:04:15–01:06:05 UTC**, exit 0, selected test 1/1 passed.
Cancellation windows were prospectively 30/60/180/1,200 seconds by scale, with a
10,800-second test/external bound. These are measurement windows, not product SLAs.
All tables show **median (minimum–maximum)** over five cases, without inferred
percentiles or discarded outliers. The fresh databases share process/JIT/pool/cache
and repeated-host history.

Environment: Ubuntu 24.04.5 x64 KVM guest, 12 reported Intel i9-14900K vCPUs,
15.0 GiB RAM, ext4 virtual disk (backing medium unverified), SDK **10.0.402**,
.NET/ASP.NET **10.0.13**, Docker **29.8.0**, effective Server GC in all cases.
Isolated fixture services use digest-pinned SQL Server 2022-CU26/Ubuntu 22.04,
Redis 7.4.11-alpine and Mailpit 1.31.0, with the filesystem object-store provider.
SQL Server CPU/RSS, actual SQL plans, physical I/O and wire bytes were not measured.

## Convergence and timing

| Captures | Authority ms | Drain seconds | Captures/second |
| --- | ---: | ---: | ---: |
| 10 | 77.651 (53.199–82.464) | 1.471 (1.129–2.256) | 6.800 (4.433–8.857) |
| 100 | 61.172 (56.757–62.047) | 7.639 (6.677–9.241) | 13.091 (10.822–14.976) |
| 1,000 | 68.339 (53.214–70.845) | 77.097 (51.458–86.354) | 12.971 (11.580–19.433) |
| 10,000 | 57.100 (39.008–76.699) | 626.251 (520.717–699.462) | 15.968 (14.297–19.204) |

Every authority operation used **nine commands, one transaction and one commit**,
with zero failures/rollback callbacks, zero scheduler calls before commit and zero
commands referencing `CentralFrames`, `CentralCaptureLocations`, `CentralArtifacts`
or `CentralDerivativeJobs`. Capture cardinality did not expand this authority shape.

Discovery/state/scheduling maxima were 10/10/10, 100/100/25, 250/250/25 and
250/250/25 by scale. Every case matched all exact resolved location bindings,
eligible artifacts, acknowledged audit/registration state and the **entire expected
ordered request-identity set**, with zero duplicate identities or unresolved-capture
backlog. Each durable reconciliation row was Completed, with discovered/completed/
scheduled counts equal to the capture count and no lease/error remaining. Failed
or scheduling-rejected telemetry was asserted absent; worker transaction failures
and rollback callbacks were zero.

Expected jobs are four per capture: encoded preview, annotation, image quality and
five-position rolling mean (40/400/4,000/40,000 total). All five identity-set hashes
match within each scale; raw JSON retains the digests and recipe/status groups.
The four boundary rolling windows remain Waiting for unavailable outside-range
positions; other jobs are Pending. **Reconciliation completion does not mean
payload execution or derivative-output completion.** Cloud assessment lacks its
clear-reference prerequisite; its policy/prerequisite checks still run.

## Resources and SQL growth

| Captures | Process CPU ms/capture | Reported allocation MiB/capture | Sampled maximum RSS MiB | Per-trial writer maximum ms |
| --- | ---: | ---: | ---: | ---: |
| 10 | 77.899 (62.680–313.149) | 12.203 (11.046–87.691) | 799.1 (453.4–950.5) | 3.800 (2.366–28.067) |
| 100 | 51.431 (46.064–99.319) | 8.122 (8.004–8.132) | 802.6 (495.7–970.3) | 3.544 (2.623–26.845) |
| 1,000 | 54.564 (39.077–60.638) | 7.763 (3.958–7.773) | 841.0 (575.7–996.7) | 14.303 (11.688–34.430) |
| 10,000 | 45.981 (40.332–51.439) | 7.745 (7.745–7.747) | 887.1 (648.3–1,068.0) | 32.404 (12.237–67.359) |

Writer columns aggregate each trial's maximum, not individual-operation percentiles.
The largest observed writer latency was 67.359 ms; none reached the 10-second timeout.
No sustained writer block or failed drain was observed. Worker commands/transactions
were identical across all five trials at each scale:

| Captures | Worker commands / committed transactions | Data used Δ MiB | Log allocated Δ MiB | Log used Δ MiB |
| --- | ---: | ---: | ---: | ---: |
| 10 | 1,253 / 35 | 3.062 (3.000–3.062) | 0.000 (0.000–0.000) | 1.676 (1.656–1.688) |
| 100 | 10,307 / 217 | 7.000 (6.938–7.125) | 64.000 (64.000–64.000) | 4.902 (4.836–5.020) |
| 1,000 | 101,415 / 2,068 | 40.875 (40.688–40.938) | 64.000 (64.000–64.000) | 12.680 (12.500–12.773) |
| 10,000 | 1,013,489 / 20,609 | 384.125 (383.688–385.562) | 256.000 (256.000–256.000) | -72.312 (-74.043–-72.051) |

Data-file allocation growth was 0/0/0/384 MiB by scale. At 1,000→10,000 captures,
commands grow 9.99× and committed transactions 9.97×; median CPU per capture falls
54.564→45.981 ms and median drain grows 8.12×. These observations support broad
approximately linear work for this fixed one-Raw/five-position workload, without
establishing general fan-out or query-plan complexity. Log-used deltas reflect
checkpoint/reuse state; negative deltas do not represent negative writes. Endpoint
snapshots do not capture transient active-log peaks.

### Resident memory and setup-only control

The 10,000-case RSS endpoints show two distinct effects:

| Trial | Before authority MiB | Sampled maximum MiB | After drain MiB |
| --- | ---: | ---: | ---: |
| 1 | 647.64 | 648.30 | 504.22 |
| 2 | 786.36 | 788.46 | 608.29 |
| 3 | 885.20 | 887.14 | 700.80 |
| 4 | 983.54 | 984.25 | 766.99 |
| 5 | 1,065.09 | 1,067.96 | 851.98 |

Each large case ends below its own starting RSS, while starting/ending RSS rises
across trials. This proves neither a plateau nor a leak-free or fully bounded
long-running host. A separate, prospectively authorized **setup-only control** ran
2026-10-23 01:14:31–01:15:18 UTC in a fresh process, exit 0 (1/1 passed). It used one
shared fixture parent and 21 sequential fresh-database child hosts, canonical DI
migration/startup, `SeedAsync(0)`, disposal/deletion and full GC. All cases asserted
zero captures, artifacts, derivative jobs, reconciliation work and recorded
scheduler calls. No reconciliation or scheduling workload ran. All 22 baseline/
after-host endpoints are retained separately.

| Post-full-GC endpoint | Baseline MiB | After 21 hosts MiB | Change MiB |
| --- | ---: | ---: | ---: |
| RSS | 336.902 | 726.152 | 389.250 |
| Managed, `GC.GetTotalMemory(true)` | 35.907 | 295.304 | 259.397 |
| GC heap, `HeapSizeBytes` | 37.101 | 298.912 | 261.811 |

Setup alone reproduces substantial retained process/managed growth. Exact heap
roots remain unidentified, and the control cannot be subtracted from the candidate.
The observed root `Factories.Count` was **zero at every endpoint**. The initial
factory-list retention hypothesis was withdrawn: this fixture already uses the
[delegated factory override](https://github.com/dotnet/aspnetcore/blob/v10.0.13/src/Mvc/Mvc.Testing/src/WebApplicationFactory.cs#L809-L824),
which differs from the base implementation. EF's
[provider cache](https://github.com/dotnet/efcore/blob/v10.0.13/src/EFCore/Internal/ServiceProviderCache.cs#L50-L64)
can persist across contexts; creating a host does not prove a fresh EF provider/model.
No model/provider counts, heap-root profile or native-memory attribution was collected.

### Allocation uncertainty

All raw counter observations are retained. Trial 4/10 reports **919,503,056 precise**
and **919,506,800 approximate bytes** (87.691 MiB/capture), despite the same 1,253
worker commands/35 committed transactions and ordinary CPU/drain values as its peers.
At 1,000 captures, total precise MiB by trial are **7,007.586 / 7,773.305 / 7,763.397 /
3,958.081 / 7,762.969**, again with identical SQL/transaction shape. Neither the high
nor low observations are discarded or explained as noise.

Precise/approximate deltas agree within 15,648 bytes, but use the same runtime
counter facility and provide no independent validation. The
[prior server-GC allocation limitation](../planning/performance-validation.md)
records negative/implausible deltas in another repository campaign. That history
justifies caution and does not establish this run's cause. Product attribution and
absolute allocation budgets remain unqualified; the table is a record of reported
counters. The setup control settles the existence of a setup confound, not all
resource attribution or production memory safety.

## Earlier attempt and comparison limits

The first frozen candidate used a prospectively declared 600-second 10,000 drain
window and was cancelled there. Only trial 1's 10/100/1,000 cases completed; the
1,000 drain was 67.6635 seconds and the large case logged 9,107 rolling windows
becoming Pending before cancellation. This failed/incomplete attempt remains
separate. Its projection was 659–677 seconds at 10,000, motivating the prospectively
recorded 1,200-second v2 window before the new run. No partial samples enter these
distributions, and no acceptance assertion was relaxed.

Original source SHA-256: `f398ade87cd4532943d4615296941feda72da264325a470c2fa1f4922bac1a2a`.
Original partial JSON (`candidate.json`/`candidate-incomplete.json`):
`ff219b92ae78a272577907295f94e062e89e3cfc11d816048987e91eda05f70f`.
Original failure log: `54a8d40203e9cef9738ea50d8d68f0ae6d45cc9f9d6bae4025eb3ff9f37eeb18`.
Its frozen test DLL hash is `6b38cbb0b40f56422f3cee34763d029a11b0406271b7ccc7e4f7a08174e66b7d`.
Source/query inspection did not identify concrete deployment-wide materialization
for this workload. No actual SQL plan was retained; logs cannot exclude every
internally handled retry.

The [historical #248 aggregate](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/248#issuecomment-5153036970)
completed 10/100 and was right-censored at 1,000/10,000 in all five trials. Its SHA-256
is `078B877CD874612179DAE14C8AC6FF7EA34F65F991E0E5F89C906EC5AFA1C1D4`.
Original files remain on the earlier evidence host and were not reauthenticated
here. Schema, host, SDK, instrumentation and transaction boundaries differ, so no
percentage speedup or matched-environment resource regression is claimed.

This measurement excludes payload execution/output checksums, object I/O, crashes,
multiple workers, real host polling delay, SQL CPU/RSS and sustained-host memory
qualification. Existing correctness suites cover separate fault scenarios; their
success does not extend these performance measurements.

## Retention and reproduction

The [20-case raw capsule](bounded-deployment-reconciliation-262.json) and
[measurement source](bounded-deployment-reconciliation-262.cs.txt) are accompanied by
[setup-control raw data](bounded-deployment-reconciliation-262-setup.json) and its
[61-line source](bounded-deployment-reconciliation-262-setup.cs.txt). JSON capsules
remove whitespace only; every original numeric token/value is preserved.

| Artifact | SHA-256 |
| --- | --- |
| Original v2 JSON | `2e4a6a50ee866bdb795fda5ed6d18f1dbc6e5da32bdd1bdd4d6ebc7ba9fef028` |
| Compact published v2 JSON | `6cc6b8abeff1cd381e7ce7605b443a7d3e76361b916fba528f421196bd8e49fe` |
| v2 source | `6a75ed509a167b74c0b845120d28701bcebf358f0d78022a765315ef03bda7aa` |
| Retained #248 helper | `84ae7ee1d098ab2ed5b4b703b58053382bcd97a2f29e9db0cc0b0e680064e825` |
| Frozen v2 test DLL | `99ff2bc6ee9ae89ba962b6fdc82633f36abf75395ec96db1eab376ae080bfe1e` |
| Product DLL | `acfc75e3beca08792cd96f139d0b0eb3a72dee52c2526aeae2bb42adfa10ade1` |
| Original control JSON | `38f3a6235b6da9804379c9ebbb40bf922c2afdfaf3fa201214578c0dbdb195b2` |
| Compact published control JSON | `4727c67c209ac0ccc9b2f61a547065910c0cc7d419bd1e1e1ef72fb218937096` |
| Control source | `d9e72e960edec6fd0e18fd71d4c379c7000814425d9ba0fce396fdbd79f5c1c5` |

Original/v2 logs, TRX, source snapshots, frozen binaries/manifests, environment,
control artifacts and the corrected diagnosis are retained with restricted local
permissions under `/home/roys/.local/state/hvo-evidence/HVO.SkyMonitor/262/run-20261023T000403-protocol-v2`.
Both disposable `.cs` files are removed from test compilation after capture; normal
inventories remain unchanged. Target protected profile is **development/v1 Preflight
and Build and Unit**. Tier C local validation remains required independently of the
final documentation-only classifier; no full `ci.yml` dispatch is implied.

For reproduction, check out the production revision, restore the pinned toolchain,
copy the source capsule to the integration project's `Issue262Measurement.cs`, and
build that project in Release with warnings as errors. On a disposable host:

```bash
mkdir -m 700 /tmp/hvo-262-reproduction
flock -o /tmp/hvo-heavy-gate.lock timeout 10800 env DOTNET_gcServer=1 \
  HVO_ISSUE_248_SMOKE=1 HVO_262_TRIALS=5 \
  HVO_262_OUTPUT=/tmp/hvo-262-reproduction/candidate-v2.json \
  dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --no-build --configuration Release \
  --filter FullyQualifiedName~Issue262_MeasureCurrentBoundedReconciliation \
  --logger trx --results-directory /tmp/hvo-262-reproduction/results
```

The smoke variable only suppresses recurring workers; this selects the #262
extension, not the old baseline smoke. Leave `HVO_EVIDENCE_REVISION` unset. For the
separate setup control, add its source capsule, rebuild, and select
`FullyQualifiedName~Issue262_SetupOnlyControl` with `HVO_262_CONTROL_OUTPUT` and a
600-second external bound. Remove disposable sources and rebuild after use.
