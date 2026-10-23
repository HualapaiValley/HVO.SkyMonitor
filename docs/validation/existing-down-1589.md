# Existing-services teardown migration evidence (#1589)

This is slice B of #893, following #1588/#1604 at
`4df5193e5967a7b02d1a31c7faf1a782e050da5d`. It ports the production
existing-services teardown to `Deployment.Cli`. The CLI owns owner authentication,
pause receipts, continuity, observation convergence, intent/completion, publication
recovery and private-file reconciliation. The shared SSH adapter exposes bounded OS
and HTTP primitives. `scripts/deploy/teardown-context.sh` is the deliberate Docker
CLI boundary; it scrubs all ambient endpoint/TLS overrides and preserves arguments.

The deploy-owned deletion path used by `deploy-services` remains in shell for its
later #893 port. The typed existing-services command refuses deletion before
creating phase artifacts or contacting a target. Syntax, catalog invocation and
Compose boundaries remain deliberate. No host-policy, inventory schema, installed
instance policy, other lifecycle phase or overall coordinator is redesigned.

## Assertion trace

Original lines refer to `scripts/deploy-contracts/existing-down.sh` at the base
above. Every loop vector is named; all MSTest methods below are in
`ExistingDownTests`. Tests execute the real `ExistingDown` phase through the shared
`DeploymentFakeHost`, using actual private Linux files/locks and typed HTTP/Docker
responses. `RealSshPrimitives_RunTheTypedPhaseWithLiteralPaths` also exercises the
actual SSH primitive adapter locally.

| Original assertion / vectors | Preserved production decision | MSTest |
| --- | --- | --- |
| 21–26, zero and malformed capture-sequence container | Exactly one matching nonnegative durable sequence, provisioned device, current config SHA-256, normalized hash | `Continuity_AcceptsZeroAndRejectsMalformedSequenceContainer` |
| 27–47, exact fixed/dynamic remote names and empty registry | Registered private paths have exact target identity and confined allowlisted names; cleanup verifies absence | `RegisteredUploadAndCompletedCredentials_AreReconciledOnRepeat`, `UnsafeBoundary_RefusesBeforeAnyComposeStop_AndCleansPrivateArtifacts`, positive/recovery cases |
| 48–55, unreachable upload | Failed cleanup retains file and registration; no target absence inferred from outage | `RegisteredUploadAndCompletedCredentials_AreReconciledOnRepeat` |
| 56–61, delete existing resources | Refuse before Compose mutation; preserve existing shared services | `InvalidAuthority_RefusesWithoutMutation(delete)` |
| 62–75, conflict and invalid pause receipt | HTTP 200 and exact paused receipt shape/state/version/UTC ordering required; no arbitrary conflict accepted | unsafe rows `pause-conflict`, `pause-receipt` |
| 76–82, mismatch / duplicate / change | Device and one durable sequence remain identical | unsafe rows `mismatch`, `duplicate`, `change` |
| 83–101, mode mismatch / observation hash drift / pre-pause raw-lane observations | Inventory mode must match; config hash fixed; raw/lane freshness after pause | unsafe rows `mode`, `hash-drift`, `pre-pause` |
| 102–110, wrong / stale / third / excess / fractional / excess-raw / fractional-raw / retry / quarantine / pressure / unrelated / busy / unhealthy / stale-section / stale-hybrid-worker / config-mismatch / config-not-current / config-mode-missing / malformed / terminal / equal-captured / decreasing-captured / pre-pause-captured | Exact bounded Hybrid tail, whole counters, healthy idle queues/workers, exact causal identities, valid current configuration, strictly newer safety samples | one unsafe MSTest row per named vector, with matching Hybrid inventory so a mode mismatch cannot mask the intended rejection |
| 111–119, continuously changing | Two consecutive identical safety fingerprints required; unsafe/nonmonotonic sample resets pair | unsafe row `changing` |
| 120–126, final control race | Fresh final-pause key and exact retained version | unsafe row `final-version` |
| 127–135, final unsafe / fingerprint / time | Final summary safe, newer, fingerprint identical | unsafe rows `final-unsafe`, `final-fingerprint`, `final-time` |
| 136–142, final hash drift | Final device/sequence/configuration boundary unchanged | unsafe row `final-hash` |
| 143–155, safe-then-unsafe and leased-only | Unsafe sample resets confirmation; active leases block stop | unsafe rows `safe-unsafe`, `leased` |
| 156–172, delayed west / A,B,B / stale control and disabled worker / pre-pause async sections / unique queries / upload cleanup | Safe A,B,B converges in exactly three candidate attempts; fresh cache-busting queries; async sections may predate pause; leases eventually drain | `GracefulStop_PreservesExistingResourcesAndDiagnostics_AndRepeats`, rows `changing-then-stable`, `delay-west`, `stale-async` |
| 173–176, exact final ledger | Schema 2, preserve policy, three completed stops plus completed existing-services preservation | all positive rows; `SameHostMultipleInstances_KeepIndependentProjectsAndDurableState` |
| 177–184, completed remote cleanup and cleanup crash | Credentials erased before passed publication; crash remains resumable | registered-credential test and interrupted cleanup row |
| 185–187, derived headers | Fresh derived file, exactly one antiforgery header and idempotency key, no replayed request | typed HTTP fixture checks every real phase request; `RealSshPrimitiveWrite_PreservesExactMultilineBytesAndTrailingNewlines` |
| 188, ledger / manifest / evidence / commit interruptions | Recover exactly one incomplete generation, require matching committed mirrors/digest | `InterruptedTeardown_RecoversEveryDurableBoundary` (four publication rows) |
| 189–190, tampered next-generation ledger / orphan | No malformed generation adopted; prior mirrors/commit retained | invalid-authority rows `tamper`, `committed-mirror`, `orphan` |
| 191–194, completed outage then retry | Completed stops revalidated against live authenticated host and container identity/state | `HostAndDockerFailures_NeverBecomeVerifiedAbsence` and positive repeat |
| 196–216, production catalog kind / corrupt image authority before up | These are unchanged catalog/up assertions, outside this phase port | Retained with the catalog/up shell owner |

The 90 teardown Unit cases also prove the exact 256-entry registry bound, complete secret-reference validation before host work, owner-only read-only secret files, response-key compatibility and numeric enum representations, the passed-publication cleanup barrier, and cleanup on reachable hosts after another host fails. Additional cases prove local run-lock contention, private-registry symlink refusal,
failed-prepare refusal, cancellation after durable intent, mutation/completion
interruption, preservation of unrelated runtime bytes and diagnostics, literal
quoted paths, multiple independent instances on one host, and real Docker shell
argument/environment handling. The shared installer exit matrix includes this
command's 0/1/2/3/130 mapping; explicit abrupt failpoints return 75.

## Evidence and resource method

The hvo-recorded scratch root is
`/tmp/hvo-skymonitor-1589-implementation.g6U3HF`. Its baseline archive and source
SHA identify the original shell; the complete baseline command is retained in
`shell.time`, with `shell.log` and the disposable fixture
`/tmp/hvo-deploy-test.p0fTVZ`. The unchanged authoritative shell shard passed in
746.76 seconds wall, 390.16 seconds user CPU, 394.39 seconds system CPU,
171,272 KiB peak RSS and 1,423,336 filesystem-output blocks, using GNU time on
native Linux x64, SDK 10.0.402, one shard worker and no real Docker resources.

This is a current phase measurement. The approved design's 991-second controlling
shard and 930–992-second complete-lane figures are historical measurements only.
No current full-lane improvement is claimed.

Target protected profile: `development/v1`, **Development v1 / Preflight** plus
**Development v1 / Build and Unit**. Tier C local evidence and the classifier
selection are distinct from those future protected checks and from production
qualification. No full workflow dispatch, promotion, publishing or real target
operation is authorized or performed by this implementation child.


## Complete comparison before cutover

`comparison-accepted-source/`, `comparison-accepted-source-pins.json` and
`comparison-accepted-cli.dll` pin the production input to the successful original
assertion cycle. `typed-cycle-accepted.log` and `.time` record all 56 real CLI
invocations passing the original shell assertions before retirement. That
instrumented cycle took 709.75 s wall and 167,312 KiB peak RSS; snapshot recording
is included, so it is not an equal-envelope speed comparison with the initial
746.76 s uninstrumented shell cycle.

`parity-final-inputs/case-000` through `case-055` retain each exact inventory,
secret-source hash (private source retained only outside git), command/fault
settings, before/after state, evidence and remote trees, image/HTTP fixture state,
outputs and actual exits. `shell-replay-results.json` records the original shell
replayed on all 56 identical inputs, with all exits matching. The Manual
`ExistingDownParityTests` audit compares every resulting path, type, mode and
content, including journals and independent committed-generation digests. Only
phase timestamps are normalized. `parity-final/parity-final.trx` passes that audit.

The audit exposed a credential-registration ordering issue on owner-login
failure. A failing native regression was retained, then fixed by registering the
seeded owner password/config before attempting authentication. The three affected
real CLI vectors (pause conflict, invalid receipt and ordinary teardown) were
rerun on their pinned inputs; `auth-delta-results.json` and the per-case
`auth-*-after` receipts record their updated binary hash. The final 56-vector
audit incorporates those three receipts; the other 53 unchanged vectors retain
their original evidence. The final focused run passed 90 Unit plus one Manual
audit (`parity-and-auth-after.log`).

Before removing the superseded shell assertions and disabling the shell's
existing-services phase, `precutover-proof/` archived the complete changed source,
patch, source/receipt checksum maps, replay results and resource summary. Catalog
and up assertions formerly at the end of the down shard remain unchanged under
`existing-catalog-up`. The down shard now selects the typed Unit owner, and the
normal `down` entry forwards existing-services inventories to the typed CLI.

### Paired resource result

`phase-resource-summary.json` sums GNU time measurements of the same 56 phase
commands, excluding fixture restoration and snapshot copying. Both use one
worker, 60 drain attempts, zero test poll delay, native Linux x64 and Release SDK
10.0.402, with no warmup. Targets are disposable fake SSH/Docker/HTTP fixtures.

| Measurement | Original shell | Typed production CLI |
| --- | ---: | ---: |
| Sum of wall seconds | 653.87 | 669.70 |
| User CPU seconds | 352.97 | 444.43 |
| System CPU seconds | 333.00 | 323.06 |
| Maximum per-command peak RSS, KiB | 13,948 | 125,512 |
| Filesystem input / output blocks | 904 / 1,401,240 | 584 / 1,860,248 |

Typed real-process execution is 2.42% slower in this workload; this is not a
real-process speedup claim. A separate observation-only trace of the same ordinary
teardown vector (`process-boundary-profile/results.json`) records 200 versus 316
SSH calls, 256 versus 420 Docker calls, and zero versus 3,786 remote base64
processes for shell versus typed. The shared bounded SSH primitive adapter encodes
every argument and the typed file publication/confinement checks make additional
round trips. Those measured process counts explain additional CPU/I/O work; the
managed runtime also increases RSS. The tracing run is excluded from the paired
timing. The contract-lane improvement comes from moving deterministic policy
assertions to native tests while retaining actual SSH/HTTP/Docker boundary tests;
it does not establish improved target-host teardown latency.

## Local candidate evidence

Candidate and classifier commands, immutable source head, complete outputs and
GNU time records are retained under the scratch root. Integration uses only the explicitly selected local
Docker Unix endpoint; Unit has no production catalog bundle and an invalid Docker
endpoint. No production resource or remote target is used.


Code candidate: `6b9dd60b08e67026ce21de7b91057be85bcfcf9f`;
`post-correction-source/source-pins.json` retains all 31 changed files. The final
ledger commit changes documentation only. Classifier on the immutable range
`4df5193e5967a7b02d1a31c7faf1a782e050da5d..<candidate>` reports:

```text
mode=full deployment=true deployment_catalog=false deployment_shards=true
 deployment_installer=true complete=true shared=true cameraagent=true
 logichost=true combined=true delivery=true secret_scan_contract=false
 release_scripts=false
```

`complete=true` selects the affected/full-qualification plan; it does not override
the development/v1 protected profile or authorize a qualification dispatch.
`classifier-help-corrected.log` and the final-range classifier log retain the
selector output. No protected CI was dispatched by this child.

| Local gate | Result / retained evidence |
| --- | --- |
| Tool restore / solution restore | Passed; `candidate/tool-restore.*`, `restore.*` |
| Whole solution Debug / Release, warnings as errors | Passed, zero warnings/errors; 172.38 s / 193.93 s wall. Affected builds passed after whitespace and CLI diagnostic corrections. |
| Whole solution format | Initial whitespace findings in two new files corrected; full verification passed. The later two-file CLI presentation delta also passed affected format verification. |
| Package audit | Passed: no vulnerabilities or exact deprecated package occurrences |
| Shell syntax / classification / coordination / PR review tools | All four passed; `candidate/{shell-syntax,classification-guard,coordination-guard,review-tools}.*` |
| development/v1 profile / operations documentation inventory | Passed; `candidate/{v1-profile,docs-inventory}.*` |
| Complete Unit selection, invalid Docker endpoint, production catalog unset | 13,598 cases in 415.71 s. Initial 13,465 passed / 124 skipped / nine failed CLI presentation assertions. Corrected command listing and named-option diagnostics; affected run passed 356/356 without skips in 26.97 s. Every original failure is individually matched to a passing result in `unit-correction-audit.json`. |
| Complete Integration selection, separate pinned local Docker endpoint | 955 cases in 776.38 s. Initial 952 passed; three evidence tests refused stale Release source stamps after the presentation commits. Whole Release rebuild at the candidate SHA passed, zero warnings/errors, 79.22 s; exactly those three passed without skips in 49.31 s. `integration-correction-audit.json` verifies every case. |
| Effective complete category evidence | Original selections plus affected corrections: Unit 13,474 passed / 124 expected skips / zero unresolved failures; Integration 955 passed / zero skips or unresolved failures. `effective-category-results.json` matches cases by stable ID and name. These are combined evidence, not second whole-solution runs. |
| Current-head retained comparison audit | One Manual audit passed again across all 56 complete receipts; `candidate/Parity-current-head/`, 4.79 s |
| Focused ShellCheck | All six changed shell files passed at warning severity. An optional recursive external-source expansion was stopped at 3.68 GB RSS; its interrupted log is retained and is not a pass. Whole-repository ShellCheck remains full CI. |
| CLI/coordinator / installer / outage / normal-flow / filesystem boundaries | All passed; `focused/{deployment-cli,installer-boundary,logichost-outage,normal-flow,filesystem-qualification}.*`. Installer qualification here is the bounded fake-Docker guard, not the production installer campaign. |
| Complete parallel deployment shard lane | All ten passed, eight workers, 679.67 s wall (coordinator rounds to 680 s), 213,780 KiB maximum descendant RSS, 1,112.53 s user / 1,111.29 s system CPU, 68,424 / 1,531,176 filesystem input/output blocks. `focused/shards.{log,time}` and its retained per-shard directory. The typed down shard passed all 90 cases without skips; its full shard envelope was 47 s. This is a current absolute lane measurement, not a current before/after comparison. |
| Changed-project category discovery | 2,735 Unit / zero Integration / seven Manual = 2,742 total, disjoint and complete; `focused/category-audit.json`. Whole-repository exhaustive discovery remains full CI. |

The 124 Unit skips are 122 approved-catalog-dependent cases (the catalog bundle is
intentionally unset), one macOS-only case, and one provider state that cannot
occur for that transport. All 90 new teardown Unit cases passed without skips in
the full selection, correction run, and current down shard. No coverage threshold
or filter was lowered. Current affected coverage records 64/64 instrumented lines
for `DownObservation`, 155/173 for `ExistingDown`, and 151/153 for
`ExistingDownHost`. The four thin Docker-wrapper lines and source-checkout entry
are covered by real CLI comparison receipts rather than this in-process Unit
collector; `candidate/affected-coverage.json` records that distinction.

Every heavy build/test/benchmark used `flock -o /tmp/hvo-heavy-gate.lock timeout
...`, with `MSBUILDDISABLENODEREUSE=1`, `UseSharedCompilation=false`, and explicit
`-nodeReuse:false -p:UseSharedCompilation=false` on build/test commands. Exact
commands, GNU time records and all failed/interrupted attempts remain outside
git. Machine pin: Linux x86_64, kernel 6.8.0-142-generic, 12 logical processors,
15,707,108 KiB RAM, SDK 10.0.402 (`resource-host.json`).

The paired phase timing predates only the documented authentication registration
correction, whitespace, and CLI usage diagnostics; the auth delta was separately
replayed on three affected exact inputs. No lifecycle policy changed after the
complete comparison. The archived source/binary pins make that scope explicit.
Managed allocation totals, real-target throughput/latency/backlog, remote-host
qualification and a current full-lane before/after experiment are not claimed.

The Docker snapshots before and after Integration both contain zero running
containers. Testcontainers disposed its observed transient containers; no shared
resource was manually deleted. Retained fake-host trees, receipts and source
archives are evidence, and remain in the recorded scratch root and referenced
fixture directories. There was no nested delegation, live deployment, release,
promotion, publication, push, PR mutation or claim release by this child.

## Initial-review correction F1

The initial deep review of `4df5193e..56e05f59` found that an interrupted
CameraAgent stop could retain an intent after Docker had stopped the application.
Recovery unconditionally required HTTP from that application before inspecting
Docker. The original fake HTTP handler continued to answer after a stop, masking
this durability failure. This section supplements the historical evidence above;
it does not replace or rewrite any original receipt or failed attempt.

Correction production commit: `2413a781b673dbd4984bf65c19a4668f2a1aef7f`.
The initial test-only async diagnostic-read correction is
`87196a92345b69e91b0a39af0a8bf6b47bbab291`. Seeded-credential cleanup and the
final tested production source are `cc558a296c100ac0e99a8bab8a8607e859056682`.
Final test-only transport-unavailability coverage is
`b3714de75a3e19fd9d0166d794df9884e4cb9ad1`. Evidence is retained under
`/tmp/hvo-skymonitor-1589-correction-f1.LCfzDF`.

### Red before production changes

`InterruptedStop_RecoversWithoutHttpToStoppedCameras` was added and run through
`dotnet test` against unchanged reviewed production code. Its eight rows cover
east and west, present correctly labelled stopped containers and verified
absence, and crash and cancellation immediately after Docker stop/verification
but before completion publication. Every camera rejects all HTTP after its
successful stop. All eight rows failed with
`System.IO.IOException: simulated curl: connection refused (application container stopped)`.

`red-before-fix.command`, `.log` and `red-before-fix/red-before-fix.trx` retain the
exact command and failures (8 failed / 0 passed / 0 skipped). `red-head.txt`,
`red-production.cs`, the empty `red-production-diff.patch` and
`red-production.sha256` bind that execution to reviewed production source:
`09ae8e7ec6bbb9a4a5dc65ae803f08d73bf0fe6d0d16db082603c929c1ec6ee6`.
The first attempt failed to compile the test fixture because its new contract
namespace import was missing; `red-compile-attempt.log` is retained separately
and is not the behavioral reproduction.

### Recovery behavior and regression ownership

A camera's retained intent now triggers a live Compose `ps --all -q` check against
the pinned endpoint. SSH authentication, retained preflight host/architecture/
daemon correlation, context binding and live Docker identity are verified before
and after that check. A present container must have one valid ID and the exact
project/service labels; its running state is read from Docker. Only verified
absence or stopped state permits completion without HTTP or another stop. A
still-running camera must authenticate again and complete the normal fresh
pause/drain/final-reassertion protocol. Fresh execution still drains before its
first stop; completed entries still require strict stopped/absent verification.
The stopped-camera branch also registers and erases the two known seeded owner
credential paths, which might not have reached login registration, then strictly
rechecks Docker state before completing the action. No HTTP failure is treated
as proof of a stop. Public journal/evidence schemas,
publication generations and strict identity/authority validation are unchanged.

The original 90 cases remain intact. Twenty new Unit rows are owned by:

- `InterruptedStop_RecoversWithoutHttpToStoppedCameras` (8): identical-input retry
  completes, never contacts either stopped camera, preserves every prior private
  diagnostic byte and unrelated runtime file, erases remote private files and
  registrations, and verifies ledger/manifest/evidence/digest agreement.
- `RetainedStopIntent_RequiresFreshDrainWhenCameraIsStillRunning` (2): an intent
  before any drain and a camera restarted after an interrupted stop both require
  a fresh control key and drain; unsafe leases refuse any new stop before a later
  healthy retry succeeds.
- `RetainedStopIntent_RejectsUnverifiedDockerAbsenceOrIdentity` (8): unreachable
  SSH/daemon, failed Compose/inspect, incorrect project/service labels, invalid
  IDs and multiple IDs retain the intent and refuse progress before stopped HTTP.
- `RetainedIntentBeforeLogin_StoppedCameraStillErasesSeededCredentials` (2): an
  intent interrupted before any HTTP, followed by a stopped or absent camera,
  still erases seeded owner credentials without contacting the stopped camera.
  These rows were first run against `87196a92` and failed because teardown passed
  while the seeded password remained. `seeded-red.log` and
  `seeded-red/seeded-red.trx` preserve both failures; the fix is `cc558a29`.

`green-focus.log` and `green-focus/green-focus.trx` pass all 108 teardown cases
without skips. That first green build reported a synchronous-read analyzer
warning in a diagnostic assertion; the subsequent async-read-only commit fixes
it. The final Debug and Release builds have zero warnings/errors.
`final/focus/` passes all 20 new rows and `final/red-green-audit.json`
individually matches all ten behavioral failures (eight original F1 rows plus
two credential rows) to passing final focused and complete-project Unit results.
The final test-only change replaces the SSH/daemon identity-fault fakes in two
rows with actual transport failure signals; the original identity tests remain
intact. Its affected Release build, eight-case reread and test-file format
verification pass in `final/transport-{build,focus,format}.*`. The complete
project result below is at `cc558a29`, supplemented by those eight passing
current-test-source results, rather than another unchanged whole-project run.

### Classifier and enumerated affected local gates

The selector is `scripts/ci:classify pull_request` on immutable correction range
`56e05f59f4d4118741ce8462f2fee0a27eb4d6aa..b3714de75a3e19fd9d0166d794df9884e4cb9ad1`;
`classifier-tested-code.log` records:

```text
mode=full deployment=true deployment_catalog=false deployment_shards=false
 deployment_installer=true complete=true shared=true cameraagent=true
 logichost=true combined=true delivery=true secret_scan_contract=false
 release_scripts=false
```

Target profile remains **Development v1 / Preflight** and **Development v1 /
Build and Unit**. No protected workflow was dispatched. The classifier's full
qualification flags do not override that profile. Under the review-correction
rule in `docs/planning/agent-execution.md`, this bounded CLI recovery/test-fixture
and inventory correction uses the following explicit affected gate set, retaining
the complete Tier C baseline above. It changes no shared transport contract,
persistence implementation, host project, migration, dependency, coverage policy,
shell behavior or test-category selection logic.

| Affected gate | Result / evidence relative to correction scratch; final gates use `final/` |
| --- | --- |
| Deployment.Cli test project and dependency closure, Debug and Release, warnings as errors | Both passed, zero warnings/errors; `final/debug.*`, `final/release.*` |
| Solution format verification restricted to all five changed C# files | Passed; `final/format.*` |
| Complete Deployment.Cli Unit selection with coverage, invalid Docker, production catalog unset | 2,634 passed / 121 skipped / 0 failed, 2,755 total; `final/cli-unit.*`, `final/cli-unit/cli-unit.trx`. Every skip explicitly requires an unavailable approved catalog bundle. All 110 teardown cases passed. |
| Changed-project category discovery and disjoint/complete audit | 2,755 Unit / 0 Integration / 7 Manual = 2,762 total; `final/category-{Unit,Integration,Manual,all}.*`, `final/category-audit.json` |
| Existing full 56-receipt Manual parity audit | Passed, no skips; `parity.*`, `parity/parity.trx`. This audits retained historical receipts, not 56 new binary executions. |
| Four CI-control guards | All passed; `final/{shell-syntax,classification-guard,coordination-guard,review-tools}.log` and `.exit` |
| development/v1 workflow contract and operations documentation audit | Both passed; `final/v1-profile.*`, `final/docs-inventory.*` |
| Installer qualification guard with fake Docker; deployment CLI/coordinator boundary | Both passed; `installer-boundary.*`, `deployment-cli.*`. No real installer campaign. |
| Affected exact-input recovery-path replay and phase measurement | See the retained-input assessment below. |
| Final patch whitespace check | Passed; no source whitespace errors. |

All 20 added rows retain the class's Linux-only condition. The CLI category
inventory rises from 2,735 to 2,755 Unit cases, the current repository inventory
from 13,598 to 13,618, and the existing M5 CLI skip pin from 322 to 342. Focused
actual discovery proves the changed project counts; exhaustive repository
discovery and execution on macOS remain their owning CI checks. The known upstream
count-only conflict remains for the parent to synchronize after correction review.
Current coverage records 163/181 instrumented line entries for `ExistingDown`,
154/156 for `ExistingDownHost` and 64/64 for `DownObservation`, including compiler
generated methods. All eight additional `ExistingDown` and three additional `ExistingDownHost`
entries are covered; `final/affected-coverage.json` records the counts.

The source/binary behavior correction is confined to `Deployment.Cli`; its full
Unit selection includes adjacent publication, authority, cancellation, primitive
adapter and cleanup contracts. The changed project owns zero Integration cases.
No new cross-project failure or dependency invalidated the prior complete Unit,
Integration, architecture/publish, package, unrelated shell/shard or full-lane
baseline, so those unchanged long suites are not repeated. The classifier excludes
catalog and exhaustive shards for this correction. No acceptance case is deferred.

### Retained-input assessment and affected measurement

`vector-selection.json` assesses all 56 immutable original inputs: 39 retain a
camera intent and enter the new live-state probe; the other 17 keep their prior
path. All 39 were executed again with the corrected `87196a92` binary. Every exit
and every state/evidence/remote path, type, mode and byte matches the original
receipt, including independent journal digests; only phase timestamps are
normalized. `affected-replay/{case-003..case-041}/receipt.json`, complete resulting
trees and `results.json` retain the proof. The original receipt directory and
fixture roots were never modified.

Those 39 historical phase receipts (including the prior auth-registration delta)
sum to 664.24 s wall, 440.48/321.29 s user/system CPU, 125,512 KiB maximum per-command
RSS and 296/1,858,080 filesystem input/output blocks. The current isolated replay
sums to 734.54 s wall, 469.56/372.00 s CPU, 125,404 KiB maximum RSS and
248/1,858,440 blocks. Fixture copies and namespace setup are outside phase timing.
This 10.6% historical/current wall increase is disclosed; the wrapper/environment
is different, so it cannot be assigned entirely to the recovery probe.
`affected-resource-summary.json` retains both the original and auth-adjusted pins.

To check the correction under the same current envelope, a scratch Git archive
of exactly reviewed head `56e05f59` was built with SDK 10.0.402 and that explicit
source revision, then compared with final production source `cc558a29` on the
same pinned continuously-changing (`case-033`) and ordinary successful
(`case-041`) inputs. Both binaries matched all expected exits and complete
snapshots. `build-reviewed.*`, `reviewed-source/`, `paired-{reviewed,corrected}/`
and `paired-resource-summary.json` retain sources, binary hashes, full receipts
and GNU time records.

| Current controlled single-pass measurement | Reviewed / corrected case-033 | Reviewed / corrected case-041 |
| --- | --- | --- |
| Wall seconds | 22.47 / 22.16 | 9.16 / 8.37 |
| User / system CPU seconds | 14.84/10.62 / 14.44/10.87 | 6.51/4.41 / 6.06/4.12 |
| Peak RSS KiB | 125,776 / 119,260 | 87,360 / 85,496 |
| Filesystem input / output blocks | 8/65,432 / 8/65,440 | 0/7,000 / 0/7,000 |

Both sides use the same private namespace, UID/GID 1000, one worker, 60 attempts,
zero poll delay and no warmup. These two samples show no correction regression
in their current envelope; they do not establish a statistical speedup or a
current complete-lane comparison. The older 56-command timing above remains a
historical source/binary pin, not a measurement of this final correction.

The later credential cleanup executes only after verified stopped/absent state.
`vector-live-states.json` proves all 39 original retained-intent inputs instead
have running cameras, so that added cleanup branch is outside those 39 measured
paths. Its final-head behavior is covered by the native crash/cancellation and
seeded-credential cases; the two representative measurements also use the final
production source. The final transport-test-only delta changes no production
source. No real-target latency/throughput/backlog, remote-host qualification,
managed-allocation totals or successful old-versus-new stopped-retry timing is
claimed; the old stopped retry cannot finish without an external restart.

Every build/test/measurement uses the host heavy-gate lock and timeout. Builds
and tests disable node reuse/shared compilation; Unit runs leave production
catalogs unset and use an invalid Docker endpoint. Fake-Docker boundary scripts
supply only their own disposable fakes. There are no real Docker resources or
remote target operations. The isolated replay uses a private mount/network
namespace, read-only host root and writable binds only for correction scratch and
a cloned fixture; the command runs as the original UID/GID 1000. An unprivileged
namespace initially mapped root ownership to UID 65534, correctly causing an
installer refusal before teardown; that failed attempt, its source, output and
time remain in `affected-replay-unprivileged-failed*`. `replay-wrapper-probes.txt`
records the wrapper probes and corrected identity mapping. Namespace mounts
release automatically. Disposable fixture copies and receipts are retained as
evidence. Baseline and parent review files are unchanged. No push, PR/issue
mutation, base synchronization, delegation, watcher, claim release or lifecycle
cleanup was performed by this implementation child.


## F2 correction: restart-valid stopped-camera configuration

Recorded 2026-10-23 19:11:54 UTC. This is the bounded local correction of blocking F2 from
[correction review 1](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1613#issuecomment-6372274779),
with its [accepted disposition](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1613#issuecomment-6372275164).
The prior ledger above is unchanged. Reviewed production starts at
`e9a6577c28b2e4cad33c7ebdf5d4805607ad5dc5`. The test-first commit is
`c60f2312705541071c6512d6d3fe7dd476f6fd08`; initial production correction is
`499bf78b0e53302b3b6b3457cf18744a5a676e9b`; final production is
`feca3639cb4ef06fdf41a3830dbd33cbf744b1a0`; all final candidate gates use
`b723c44c2c70eb89713b7d85c62297e7691582b1`. Evidence is retained in the hvo-recorded scratch
`/tmp/hvo-skymonitor-1589-correction-f2.lSCeEx`.

### Actual startup-validator red and green

Both existing rows of
`RetainedIntentBeforeLogin_StoppedCameraStillErasesSeededCredentials` now seed
`config/private/owner-password` and the real key-per-file value
`LocalIdentity__AdminPasswordFile=/run/hvo-private/owner-password`, with no
allow-missing setting. They interrupt at the first east intent before HTTP,
externally stop east, and retry with both present stopped containers and verified
absence. After teardown they overlay the final key-per-file directory on the
actual embedded CameraAgent `appsettings.json`, bind the actual
`LocalIdentityOptions`, and invoke `IStartupValidator.Validate()` through the
same Bind / ValidateDataAnnotations / ValidateOnStart registration used by
CameraAgent. The password-file setting must be absent, so Program's password-file
loader has no file to apply; the final bound password remains empty.

The product validator, `CameraAgentIdentityPolicy` and `ApplicationUser` are
source-linked unchanged, with the ASP.NET shared framework supplying their real
framework dependencies. No host ProjectReference or combined test dependency was
added. Narrow type-specific analyzer suppressions preserve the linked product
visibility and unused password-policy helper; no validation or test rule is
suppressed. Validator source SHA-256 is
`0aaf757fc7ef9199ddca77e58959791019d18c868ca2eb0330a3e0372e456ead`.
`red-source-pins.json` and `candidate-source-pins.json` also pin its dependencies,
actual appsettings, host startup source, changed files and shared adapter.

The exact red command, through the recorded heavy-gate/environment wrapper, was:

```bash
timeout 360 dotnet test tests/HVO.SkyMonitor.Deployment.Cli.Tests/HVO.SkyMonitor.Deployment.Cli.Tests.csproj --configuration Release --filter FullyQualifiedName~RetainedIntentBeforeLogin_StoppedCameraStillErasesSeededCredentials --logger trx\;LogFileName=red-before-fix.trx --results-directory /tmp/hvo-skymonitor-1589-correction-f2.lSCeEx/red-before-fix -nodeReuse:false -p:UseSharedCompilation=false
```

Both rows failed with the actual startup exception:
`AdminPassword is required until local identity has been seeded.`
`red-before-fix.log`, `red-before-fix/red-before-fix.trx`, `red-head.txt`, both
empty `red-production-diff*.patch` files and `red-production.cs` prove unchanged
reviewed production. Its ExistingDownHost SHA-256 was
`76c71ccdb47d383fbbeb1faca8f610e82ba6cdd8acae06e4be12afc3fe87a8f1`.
The final production SHA-256 is
`0eb325a37d87a7e565294c31a2eaa7e497b597900965f66a31fb224aecfdeb61`.
The final green command was:

```bash
timeout 420 dotnet test tests/HVO.SkyMonitor.Deployment.Cli.Tests/HVO.SkyMonitor.Deployment.Cli.Tests.csproj --no-build --no-restore --configuration Release --filter TestCategory=Unit --settings tests/coverage.runsettings --collect:XPlat\ Code\ Coverage --logger trx\;LogFileName=cli-unit.trx --results-directory /tmp/hvo-skymonitor-1589-correction-f2.lSCeEx/cli-unit -nodeReuse:false -p:UseSharedCompilation=false
```

`final-red-green-unit-audit.json` individually matches both red rows to passing
final results, preserves all 2,755 prior CLI Unit outcomes, verifies every one of
the original 110 teardown cases, and proves all 22 added rows pass. The final
selection has 2,656 passed / 121 skipped / zero failed, 2,777 total; all 132
teardown cases pass without skips. `catalog-skip-audit.json` confirms the exact
same 121 approved-catalog-dependent skips as the retained F1 run.

### Publication, cleanup ordering and interruption behavior

Only verified stopped/absent recovery enters the new transition. It authenticates
and correlates the target, confines the existing AllowMissingAdminPassword path,
rejects symlinks, hard links, directories and non-private modes, and uses the
existing confined writer to repair missing, false or interrupted partial values.
A valid published `true` is preserved without unlinking it. The exact bytes and
open descriptor/path identity are verified, the file descriptor is synchronized,
and then its containing directory is synchronized and rechecked. Only after that
barrier and renewed target correlation is the password-file reference registered
for cleanup. Its verified removal and directory synchronization precede
registration/removal of the password itself. This prevents failure cleanup from
erasing the password before its restart configuration is durable, and prevents a
retained password-file reference from pointing at an already-erased password.
The caller still strictly rechecks live Docker identity/stopped state before
completion. No stopped-camera HTTP or additional stop is introduced; still-running
cameras retain the unchanged fresh-authentication/drain path and F1 behavior.

Twelve interruption rows exercise write failure/cancellation, a torn empty write,
file sync, directory sync, password-reference sync and cleanup, with crash,
I/O failure or cancellation as appropriate. Before publication, credentials and
intent remain and identical retry repairs partial state. After reference cleanup,
the actual startup validator already succeeds even on interrupted runs. Tests
assert both synchronization barriers before credential removal and durable
reference retirement before password unlink. Four unsafe-setting rows refuse
without deleting seeded credentials or changing the hazardous file; three
existing-setting rows prove partial-value repair and inode-preserving retries;
one corrupt-write row refuses a nominally successful write with wrong bytes;
two present/absent rows run the real shared SSH file primitives locally with
literal quoted paths. Normal successful-down controls also run the actual
startup validator. All these rows retain the existing Linux-only condition.

The real adapter's existing `sync` primitive invokes Linux file synchronization;
its existing `path` primitive addresses the retained open descriptor. The native
test adapter now flushes that actual descriptor with RandomAccess.FlushToDisk,
while its existing directory-sync behavior is unchanged. The successful real
adapter regressions exercise both file and directory barriers. No primitive,
publication schema, journal generation, host policy or production dependency was
redesigned. No physical power-loss or real remote-host qualification is claimed.

### Classifier and explicit affected gates

The selector is `scripts/ci:classify pull_request` on immutable correction range
`e9a6577c28b2e4cad33c7ebdf5d4805607ad5dc5..b723c44c2c70eb89713b7d85c62297e7691582b1`.
`classifier-candidate-complete.log` records:

```text
mode=full deployment=true deployment_catalog=false deployment_shards=false
 deployment_installer=true complete=true shared=true cameraagent=true
 logichost=true combined=true delivery=true secret_scan_contract=false
 release_scripts=false
```

Target profile remains **Development v1 / Preflight** plus **Development v1 /
Build and Unit**. No protected workflow or qualification was dispatched. Under
the review-correction rule, the complete original Tier C baseline remains above;
the following explicitly enumerated affected set covers this bounded recovery,
test-adapter, source-link and inventory delta. `gates-summary.json` retains every
exact command and exit; `candidate-source-pins.json` binds all tested source.

| Affected local gate | Final result / scratch evidence |
| --- | --- |
| CLI test project/dependency closure Debug and Release, warnings as errors | Both passed, zero warnings/errors; `debug.*`, `release.*` |
| Solution format for all six changed C# files | Passed; `format.*` |
| Complete CLI Unit with coverage and invalid Docker | 2,656 passed / 121 unchanged catalog skips / zero failed; `cli-unit.*`, `cli-unit/cli-unit.trx` |
| Actual CameraAgent LocalIdentityOptions, LocalIdentityPasswordFile and CameraAgentIdentityPolicy Unit tests, with affected Release build | 51 passed / zero skipped; `host-adjacent.*`, `host-adjacent/host-adjacent.trx` |
| Production reference graph, transitive host/test exclusion, imported/conditional graph and TestSupport direction | Four passed / zero skipped; `architecture-adjacent.*`, `architecture-adjacent/architecture-adjacent.trx` |
| Changed-project category discovery and disjoint/complete audit | 2,777 Unit / zero Integration / seven Manual = 2,784; `category-*.diag`, `category-audit.json` |
| Four CI-control guards on stable tested code | All passed; `stable-{ci-shell-syntax,test-ci-classification,test-coordination-guard,test-pr-review-tools}.*`, matching before/after `stable-guard-head*.txt` |
| v1 workflow contract and operations documentation audit | Both passed; `stable-test-development-v1-ci.*`, `stable-docs-audit-operations.*` |
| Exact-input path assessment and normal-path source equality | Passed for all 56 inputs, including 39 retained running inputs; `vector-assessment.json`, `vector-source-pins.json` |
| Final patch whitespace and append-only ledger check | Passed; final handoff retains current-head check and source pins |

CLI Unit inventory increases by 22, repository Unit inventory to 13,640, and the
M5 CLI skip pin from 342 to 364. Actual changed-project discovery proves the
counts; exhaustive repository discovery and macOS execution remain their CI
checks. Current coverage records 163/181 instrumented line entries for
ExistingDown, 181/183 for ExistingDownHost and 64/64 for DownObservation, including
generated methods; `affected-coverage.json` retains the counts. No coverage or
category-selection policy changed. The selected graph checks, classifier guard,
actual-host tests and complete CLI Unit cover the source-link/framework boundary;
there is no new host project reference, production package change or host source
change. CLI owns zero Integration cases. No new failure invalidates the original
whole-solution Unit/Integration, package, publish, architecture or unrelated shell
baseline. No acceptance case is deferred. The known upstream count conflict is
left for the parent after independent correction review. The existing #1589
operator-facing recovery Fixed entry already covers this correction.

### Retained measurements, failures and scope limits

`vector-assessment.json` independently reads every original input's retained
ledger, inventory and container JSON. All 39 inputs with camera intents have a
single matching running camera container; none enters this stopped-camera branch.
`vector-source-pins.json` pins those exact inputs. Normal LoginAsync and WriteAsync
are byte-identical to reviewed e9a6577c. The production shared SSH adapter and all
measured workload settings are unchanged. The F1 39-vector replay, paired
measurements and original full Tier C evidence therefore remain applicable;
no unchanged performance harness or long solution suite was rerun. New stopped
recovery behavior is exercised by the current native and real-adapter rows, not
claimed as a new timing, throughput, allocation or remote-target result.

Failed attempts are retained: `red-wrapper-failed.*` records an evidence wrapper
edited while active (shell read offsets shifted, exit 126; its child's exit 1
remains in GNU time, but the build log was overwritten, so it is not red proof);
`red-compile-failed.*` records the initial test configuration API error and linked
source analyzer diagnostics; `green-focus.*` records a later test-only syntax
error. The corrected intermediate `green-focus-2/green-focus.trx` passes 130 cases
at 258070f2; final complete Unit at the pinned candidate supersedes it with all
132 cases, including the final password-reference barrier and two extra rows.
All failed attempts remain distinct from the valid two-row behavioral red.

Heavy builds/tests/format used the host `flock -o /tmp/hvo-heavy-gate.lock`, a
bounded outer timeout and individual gate timeouts, node reuse/shared compilation
disabled, production catalogs unset and invalid Docker. Scratch is retained for
review; test fixtures dispose their local temporary files/processes. No actual
Docker resource, remote target, push, GitHub mutation, base synchronization,
watcher, delegation, claim release or lifecycle cleanup was performed. T3-only PR
registration satisfies the harness requirement; the parent owns lifecycle.
Full source/command/limitations and final resource state are in the local handoff.


## Final target synchronization and local candidate evidence (2026-10-23)

The parent owns independent review, GitHub updates, finalization and merge. This
sole-editor subtask merged the fixed target locally, refreshed local evidence,
and made no push, GitHub mutation, review dispatch, PR watch, claim release or
workspace cleanup. Independent deep correction round 2 had converged on
`d526b1dfc496fc56dd8f038a88bd63d1d96226e3`; its
[full report](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1613#issuecomment-6372909558)
and [disposition](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1613#issuecomment-6372909935)
carry F1 and F2 as verified corrected. This local evidence is not an independent
base-sync verdict.

### Immutable merge and count resolution

Merge commit `896fc6f7d63a27dce374131cb29af3063dd6968a` has exactly these
parents, in order:

- Reviewed PR head: `d526b1dfc496fc56dd8f038a88bd63d1d96226e3`.
- Pinned target `origin/development/v1`: `58164c350bb7a1a9f977b548facef4c4c70868a0`.

The original PR merge base was
`4df5193e5967a7b02d1a31c7faf1a782e050da5d`; the synchronized PR merge base is
exactly the pinned target. The target was not refreshed during active gates.
The target must be freshly checked by the parent before finalization.

`git merge --no-commit --no-ff 58164c350bb7a1a9f977b548facef4c4c70868a0`
produced only the expected `docs/runbooks/ci-pipeline.md` conflict.
`git show --remerge-diff 896fc6f7d63a27dce374131cb29af3063dd6968a` proves that
only the two count lines were manually resolved. Counts came from the actual
sum of every `tests/*/test-categories.json`: Unit **13778**, Integration **955**,
Manual **284**, Soak **1**, External **0**, Hardware **1**. Original merge-base
counts were Unit 13501 / Manual 282; the PR contributes Unit +139 / Manual +1,
and the target contributes Unit +138 / Manual +1. Target prose was internally
stale (Unit row 13616, declaration/inventory 13639); neither number was used as
a merged count. Both parents' category deltas remain, and all seven nonempty
added changelog lines from both sides are retained.

The following two binary Git patches are byte-identical (SHA-256
`ef808bb493db49b14d788214ab2ffa826cb77a3606c273616ef417085f14f391`):

```bash
git diff --binary --no-ext-diff --no-renames --unified=3 4df5193e5967a7b02d1a31c7faf1a782e050da5d d526b1dfc496fc56dd8f038a88bd63d1d96226e3 -- . ':(exclude)CHANGELOG.md' ':(exclude)docs/runbooks/ci-pipeline.md'
git diff --binary --no-ext-diff --no-renames --unified=3 58164c350bb7a1a9f977b548facef4c4c70868a0 896fc6f7d63a27dce374131cb29af3063dd6968a -- . ':(exclude)CHANGELOG.md' ':(exclude)docs/runbooks/ci-pipeline.md'
```

This proof includes the category inventories and M5 workflow. All 29 affected
product/test/script file states are also byte-identical to the converged head.
The later evidence-only commit appends this section; its new prose is additional
review scope and is not included in the unchanged patch claim.

### Selector and target profile

Profile is **development-v1**: **Development v1 / Preflight** and
**Development v1 / Build and Unit**. Tier C complete local validation remains
required. No full-qualification workflow was dispatched or requested.

The exact classifier invocations were:

```bash
./scripts/ci:classify pull_request d526b1dfc496fc56dd8f038a88bd63d1d96226e3 896fc6f7d63a27dce374131cb29af3063dd6968a
./scripts/ci:classify pull_request 58164c350bb7a1a9f977b548facef4c4c70868a0 896fc6f7d63a27dce374131cb29af3063dd6968a
```

| Output | Base-sync range | Full synchronized PR range |
| --- | --- | --- |
| mode | full | full |
| deployment | false | true |
| deployment_catalog | false | false |
| deployment_shards | false | true |
| deployment_installer | false | true |
| complete | false | true |
| shared / cameraagent / logichost / combined / delivery | all true | all true |
| secret_scan_contract / release_scripts | both false | both false |
| paths | 78 | 35 |

The classifier selects affected local/full-qualification scope; `complete=true`
does not authorize a full workflow. Local gates below combine this selection
with the issue's Tier C acceptance and the explicit synchronization handoff.

### Merged-head gate evidence

UTC checkpoint: 2026-10-23T19:55:57Z. All gates completed; next is the parent-requested independent deep base-sync review. No local gate blocker remains.

All heavy builds and test steps ran under `flock -o /tmp/hvo-heavy-gate.lock timeout ...`, with bounded per-gate timeouts, `MSBUILDDISABLENODEREUSE=1`, and shared compilation disabled. The candidate used `flock -o /tmp/hvo-heavy-gate.lock timeout 7200 bash /tmp/hvo-skymonitor-1589-base-sync.GI1oaL/gates.sh`; follow-up work used a bounded lock wait and `timeout 1800` around `extras.sh`. Both exited 0. The follow-up queue briefly waited for another worker, which was left untouched.

Every `.head` pin for the build/test/control/category/boundary runs is `896fc6f7d63a27dce374131cb29af3063dd6968a`. The full Release build completed before Unit and strict source-stamp Integration; no tracked file or HEAD changed during either selection.

| Gate command (inside its recorded timeout) | Result |
| --- | --- |
| `dotnet tool restore` | Pass |
| `dotnet restore HVO.SkyMonitor.v9.slnx -nodeReuse:false -p:UseSharedCompilation=false` | Pass |
| `dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Debug -warnaserror -nodeReuse:false -p:UseSharedCompilation=false` | Pass, zero warnings/errors |
| `dotnet build HVO.SkyMonitor.v9.slnx --no-restore --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false` | Pass, zero warnings/errors |
| `dotnet format HVO.SkyMonitor.v9.slnx --no-restore --verify-no-changes` | Pass, complete solution |
| `./scripts/package:audit` | Pass |
| `./scripts/ci:shell-syntax` | Pass |
| `./scripts/test:ci-classification` | Pass |
| `./scripts/test:coordination-guard` | Pass |
| `./scripts/test:pr-review-tools` | Pass |
| `./scripts/test:development-v1-ci` | Pass |
| `./scripts/docs:audit-operations` | Pass; inventories and runbook agree |
| Complete Unit selection below | 13654 passed / 124 expected skips / 0 failed; 13778 total |
| Separate complete Integration selection below | 955 passed / 0 skipped / 0 failed |
| `./scripts/test:deployment-installer-qualification` | Pass; bounded fake-Docker guard |
| `./scripts/test:deploy-environment-cli` | Pass |
| `./scripts/test:deployment-logichost-outage-contract` | Pass |
| `./scripts/test:deployment-normal-flow-contract` | Pass |

Exact Unit and Integration commands:

```bash
timeout 900 env -u HVO_PRODUCTION_CATALOG_BUNDLE DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter TestCategory=Unit --settings tests/coverage.runsettings --collect:XPlat\ Code\ Coverage --logger trx --results-directory /tmp/hvo-skymonitor-1589-base-sync.GI1oaL/Unit -nodeReuse:false -p:UseSharedCompilation=false
timeout 1500 env -u HVO_PRODUCTION_CATALOG_BUNDLE -u DOCKER_CONTEXT -u DOCKER_TLS_VERIFY -u DOCKER_CERT_PATH DOCKER_HOST=unix:///var/run/docker.sock dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter TestCategory=Integration --settings tests/coverage.runsettings --collect:XPlat\ Code\ Coverage --logger trx --results-directory /tmp/hvo-skymonitor-1589-base-sync.GI1oaL/Integration -nodeReuse:false -p:UseSharedCompilation=false
```

All 132 teardown cases passed individually, including carried F1/F2 regressions. The CLI assembly has 2656 passed / 121 catalog-dependent skips / zero failures; its complete skip set is identical to F2. All 51 adjacent actual-host identity/password cases and four graph cases passed in the complete selections. All seven architecture Integration cases also passed. `trx-summary.json` and `boundary-results.json` bind these statements to raw TRX; `teardown-current-results.json` retains each teardown row.

Real six-category discovery plus unfiltered discovery passed for each affected inventory, with complete, disjoint case-ID unions:

| Project | Unit | Integration | Manual | Soak | External | Hardware | All |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `HVO.SkyMonitor.Deployment.Cli.Tests` | 2777 | 0 | 7 | 0 | 0 | 0 | 2784 |
| `HVO.SkyMonitor.CameraAgent.Tests` | 7093 | 286 | 94 | 1 | 0 | 1 | 7475 |
| `HVO.SkyMonitor.CameraAgent.AcceptanceTests` | 30 | 19 | 88 | 0 | 0 | 0 | 137 |
| `HVO.SkyMonitor.Processing.Tests` | 829 | 0 | 15 | 0 | 0 | 0 | 844 |

Each discovery invocation used `dotnet test tests/<project>/<project>.csproj --no-build --no-restore --configuration Release --list-tests --filter TestCategory=<category> --diag <owned-evidence-file> -nodeReuse:false -p:UseSharedCompilation=false`, with catalog unset and invalid Docker. The seventh invocation omits `--filter`. `category-audit.json` compares actual IDs/counts against all six inventory keys.

Coverage collection retained 40 raw Unit and 39 raw Integration Cobertura files, all parsed successfully and individually SHA-256 pinned in `coverage-manifest.json`. This is local selection evidence; canonical CI aggregation, non-regression floors, whole-repository category discovery, whole-repository ShellCheck and macOS qualification remain with their documented full-CI owners. No selection, skip, timeout, category or coverage policy was weakened.

### Retained boundaries, limits and resources

The applied synchronization delta changes Processing and CameraAgent UI code/tests, their inventories, recovery target documentation and associated evidence. Prior solution build/Unit/Integration evidence was refreshed for these new source boundaries. The CLI teardown product/tests/scripts retain their converged bytes; all 14 source/dependency hashes selected from F2 remain unchanged. Additional pins prove unchanged host Program/appsettings, shared SSH host adapter, typed teardown, shard table and shell Docker-context teardown boundary. The current recovery-ordering policy explicitly keeps strict existing-behavior #893 CLI parity independent of new persistent-layout studies. No layout, upgrade implementation or new recovery architecture was introduced.

The original phase/parity/input/configuration and paired resource evidence, plus F1/F2 evidence already justified above, remains applicable. No unchanged long shard, full installer campaign or benchmark was repeated beyond the explicitly required complete Unit/Integration selections. No new throughput, allocation, target-latency, backlog or whole-lane performance claim is made.

All merged-head build, test, control, category and boundary gates passed on their first attempt without a product correction. The expected initial merge conflict is retained in `merge.log` and `merge-remerge-diff.patch`. A derived audit initially grouped CLI tests by namespace and omitted four shared test rows from its CLI-only subtotal; assembly-based attribution corrects that subtotal to 2777 while preserving the raw results and the earlier derived JSON. This was an evidence-labeling correction, not a product/test failure. The initial ledger whitespace check exited 2 on two copied command lines at documentation-only commit `d44e58846f41ce75169ea3d466e56b712761ebd3`; `ledger-whitespace-red.log` reproduces it on that immutable range. Removing the two trailing spaces preserves command arguments and requires only documentation/whitespace checks. Both the failed check and corrected evidence are retained. Historical failed attempts and private fixtures were preserved. No historical private environment map or credential fixture was printed, and no new full environment capture was made.

Post-gate resource audit found no remaining gate processes in this workspace and zero Docker containers. Image, network and volume inventories match their pre-Integration baseline. Test fixtures disposed their own transient resources. No broad kill, Docker removal, shared-service mutation or workspace/scratch cleanup was issued. All build outputs, coverage, failed historical evidence and scratch remain retained.

Evidence root: `/tmp/hvo-skymonitor-1589-base-sync.GI1oaL`. `gates-summary.json` contains every exact command, source pin, exit and available timing; companion `.log`, `.time`, `.exit` and `.head` files retain raw evidence. `candidate-source-pins.json` pins 3801 tracked source/build/test/script files, `source-after-integration.json` verifies all unchanged, and `release-binary-pins.json` identifies the rebuilt assemblies. Merge/conflict, inventory, changelog and byte-identical patch proofs are separate JSON/patch artifacts. Final immutable-range classifier outputs after this documentation append are retained in the same root.

The follow-up commit changes only this validation ledger and retains the validated code/source pins. A later invocation that requires the latest Git commit in binary source stamps should rebuild first. The parent should request a **deep** base-sync review of conflict/count correctness, current recovery-contract context, host startup/source-link and Processing/UI boundary interactions, and individually carry F1/F2 from the verified correction-2 report. Protected v1 checks, a fresh target proof, push, finalization and merge remain parent work.

### Fresh documentation-only target synchronization

After the complete merged-code gate, the target advanced to
`6bc53381dc75681a9fdde6c0ab39cd3ee6843cce` through the #1615 documentation
comparison and its immutable synthetic-digest secret-scan exception. Merge
`d816ab423027f52461245e9373a680cdad7dd8ba` incorporates that tip. The sole
conflict was the Unit row in this runbook: both parent updates are preserved by
using the actual combined inventory, Unit 13,778 / Integration 955 / Manual 284.
The target's corrected Unit row does not remove this PR's additional cases.

`git diff --exit-code 896fc6f7 HEAD -- src tests scripts tools .agents AGENTS.md
Directory.Build.props Directory.Build.targets Directory.Packages.props global.json
.github` passed. Runtime, tests, build inputs and workflows remain byte-identical
to the complete-gate source, so the 13,654 Unit passes / 124 expected skips and
955 Integration passes remain applicable. New changes are upstream documentation,
validation evidence and `.gitleaks.toml`; no unchanged long suite was repeated.

The exact fresh merge range's classifier reports `mode=full complete=true
 deployment=false secret_scan_contract=true`, with all component flags true.
The full PR range reports `mode=full complete=true deployment=true
 deployment_shards=true deployment_installer=true secret_scan_contract=false`.
The classifier-selected additional secret-scan contract passed all 62 cases, and
the actual full-history content-free secret scan passed. All four CI-control
guards, the v1 profile and operations documentation audit passed again on the
fresh merge. The new allowlist parses and retains the exact immutable upstream
commit/path restriction. Target protected profile remains Development v1
Preflight plus Build and Unit; no qualification workflow was dispatched.

`fresh-pr-diff-proof.json` proves that the reviewed PR delta and fresh PR delta
are byte-identical when excluding only `CHANGELOG.md`, the CI count runbook and
this append-only validation ledger. M5 and category inventory deltas remain
included. The excluded changes are explicitly part of the base-sync review.
All commands, gate exit records and the exact patch hash are retained beside the
original complete-gate evidence under the same recorded scratch root. This final
append changes documentation only; independent base-sync review and finalization
remain the parent's next actions.


## Second pinned target synchronization: weather overlay (#1616)

Recorded 2026-10-23 20:34:04 UTC. Previous independently reviewed head is
`31c68beeba93d595002074be09c14924251b45e5`, CLEAN under request
`hvo-1589-pr1613-base-sync-r0`; its complete request/report remain in
`/tmp/hvo-skymonitor-1589-review-base-sync.6hQCJk/`. F1 and F2 were individually
VERIFIED_CORRECTED in that report. The parent reported the PR returned to draft,
auto-merge disabled and the old finalizer terminated before this local work.
Remote lifecycle state was not changed or monitored by this child.

Target is pinned to `dffab19529c55769e25fbd162fe06b248386eed0`; it was merged,
without rebasing, in `c85de4ff3c93a14ae476759aa9c974d4ab4ebaf1`, with previous
reviewed head and that target as its two parents. The corrected, tested code head
is `cacbcb7d1f23b21ff2fc46546b452d2987d36a09`. No later target was fetched or chased.
All evidence below is retained in the existing hvo-recorded scratch
`/tmp/hvo-skymonitor-1589-base-sync2.fY8E0L`.

### Conflict resolution and complete PR proof

Only `CHANGELOG.md` and `docs/runbooks/ci-pipeline.md` conflicted. The sole changelog
Fixed block retains both the #1589 recovery line and #1564 weather-label line;
the unconflicted #1564 Added entry and every earlier entry from both parents
also survive. Both runbook conflicts contain only counts. All per-project
inventories were summed, yielding `Unit=13816`, `Integration=955`, `Manual=284`,
`Soak=1`, `External=0`, `Hardware=1`. Every inventory is independently verified as
new target plus the reviewed PR's own delta: +139 Unit/+1 Manual. Upstream adds
38 Unit cases (Processing 23, CameraAgent 13, combined 2).

The first merge commit used a greedy marker expression that truncated the tails
of both documentation files. The operations audit failed before any build/test;
an additional all-parent-entry assertion exposed the lost changelog entries.
`cacbcb7d` reconstructs the merge from its immutable inputs with a line-by-line
marker parser, preserving 438 changelog lines and all 1149 runbook lines.
The failed audit, original conflict text, exact parser and corrective diff are
preserved in `docs-inventory-truncation-failed.*`, `*.raw-conflicts`,
`resolve-conflicts.py`, `merge-remerge-diff.patch` and `conflict-resolution.json`.
No product source was edited during this correction.

The complete PR patch `6bc53381..31c68bee` is byte-identical to
`dffab195..cacbcb7d`, excluding exactly changelog, the runbook and this append-only
ledger. Its 188162 bytes have SHA-256
`19134579afce76790b83c0bd19b1ce5bb373213c9137c64cb2e5b0bacb20104e`.
M5 and every category inventory remain included. `reviewed-pr.patch`,
`merged-pr.patch`, `merge-proof.json`, `inventory-addition-proof.json` and
`changelog-retention.json` retain the separate proofs; excluded documentation is
checked explicitly above. The earlier ledger remains an exact byte prefix.

### Selector and affected gates

Protected profile remains **Development v1 / Preflight** plus **Development v1 /
Build and Unit**. No protected or qualification workflow was dispatched. The
selector is `scripts/ci:classify pull_request`, using immutable endpoints:

| Range | Flags |
| --- | --- |
| Review delta `31c68bee..cacbcb7d` | `mode=full complete=false shared=true cameraagent=true logichost=true combined=true delivery=true`; `deployment=false deployment_catalog=false deployment_shards=false deployment_installer=false secret_scan_contract=false release_scripts=false`; 24 paths |
| Complete PR `dffab195..cacbcb7d` | `mode=full complete=true shared=true cameraagent=true logichost=true combined=true delivery=true deployment=true deployment_shards=true deployment_installer=true`; `deployment_catalog=false secret_scan_contract=false release_scripts=false`; 35 paths |

`complete=false` is authoritative for this component delta. The full PR's
existing deployment scope does not request another complete candidate run or
full CI dispatch. `affected-gate-plan.json` explicitly enumerates refreshed and
retained gates with their source rationale; exact commands, tested heads,
outputs, exits and GNU time are in `gates-summary.json` and each named gate's
`.command`, `.head`, `.log`, `.exit` and `.time`. Driver scripts/commands retain
the outer `flock -o /tmp/hvo-heavy-gate.lock timeout ...` acquisition.

| Affected local gate at `cacbcb7d` | Result |
| --- | --- |
| Four CI-control guards, before base-sync review | Shell syntax, classification, coordination and PR-review tools all pass |
| v1 profile and operations documentation audits | Both pass after the documented conflict correction |
| Debug builds, warnings as errors | Processing, CameraAgent, combined-host, LogicHost, Architecture and Deployment.Cli test roots and dependency closures all pass, zero warnings/errors |
| Release solution build, before strict source-stamp consumers | Pass, zero warnings/errors; 3:08.63 wall |
| Format verification of every changed C#/Razor file | Pass |
| `unit-Architecture` with coverage | 32 passed / 0 expected skips / 0 failed |
| `unit-CameraAgent` with coverage | 7105 passed / 1 expected skips / 0 failed |
| `unit-CameraAgent.AcceptanceTests` with coverage | 30 passed / 0 expected skips / 0 failed |
| `unit-CameraAgent.LogicHost` with coverage | 76 passed / 0 expected skips / 0 failed |
| `unit-LogicHost` with coverage | 957 passed / 1 expected skips / 0 failed |
| `unit-Processing` with coverage | 852 passed / 0 expected skips / 0 failed |
| `unit-ProcessingRunner` with coverage | 29 passed / 0 expected skips / 0 failed |
| `teardown` with coverage | 132 passed / 0 expected skips / 0 failed |
| `graph-integration` with coverage | 3 passed / 0 expected skips / 0 failed |

The nine runs total 9216 passed / two unchanged expected skips / zero failures:
9213 Unit passes plus three Integration graph passes. Production catalogs are
unset and Docker is deliberately invalid for every positive Unit selection.
The graph Integration checks need no Docker and use the same invalid endpoint.
All build/test invocations disable node reuse and shared compilation. The two
skipped cases individually match the prior complete-gate names; no skip, filter,
timeout or coverage policy was weakened.

`boundary-results.json` and `new-weather-case-results.json` verify every added
weather row, all 11 both-host conformance rows, the complete 132-row teardown
name set, all 51 actual-host identity/password rows and four graph checks.
F1's eight stopped/absent crash/cancellation rows, two running-camera fresh-drain
rows and eight Docker refusal rows pass. F2's two actual startup-validator rows
and all 22 added restart-setting/durability/interruption rows pass. This is local
regression evidence; the parent still obtains independent base-sync review.

Two evidence-only reconciliation attempts are preserved. The first correctly
noticed that upstream replaced `QualityDraftAuthorizationAndIdentifierValidationPrecedePersistence`
with `ScalarDraftAuthorizationAndIdentifierValidationPrecedePersistence` rows.
The source diff retains the old behavior under `ImageQuality` and adds
`WeatherCloudOverlay`; both pass. The explicit mapping proves all 7996 prior
cases in the three changed assemblies survive plus 38 added cases. The second
noticed that each coverage report also has an attachment copy: all 18 XML files
parse, and each pair has the same SHA-256, yielding nine unique reports.
`results-audit-rename-failed.*`, `results-audit-coverage-failed.*`, earlier audit
scripts and `test-case-rename-proof.json` preserve both attempts. Corrected
`results-audit.*` passes without rerunning or changing an executable test.
`coverage-manifest.json` retains every raw file and checksum. Canonical coverage
aggregation/floors remain with their full-CI owner.

Real unfiltered and six-category discoveries prove complete, disjoint case-ID
unions for exactly the three changed inventories:

| Project | Unit | Integration | Manual | Soak | External | Hardware | All |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `HVO.SkyMonitor.CameraAgent.Tests` | 7106 | 286 | 94 | 1 | 0 | 1 | 7488 |
| `HVO.SkyMonitor.CameraAgent.LogicHost.Tests` | 76 | 0 | 1 | 0 | 0 | 0 | 77 |
| `HVO.SkyMonitor.Processing.Tests` | 852 | 0 | 15 | 0 | 0 | 0 | 867 |

`category-audit.json` and retained per-category diagnostic messages bind these
counts to actual discovery. No whole-repository exhaustive discovery was repeated.

### Retained boundaries, resources and handoff

The complete Tier C baseline at `896fc6f7` remains retained: full Debug/Release,
format, package/tool/restore/control evidence, 13654 Unit passes with 124 expected
skips, and 955 Integration passes. It is supplemented by this affected gate set,
not presented as a second unchanged whole-solution run. Comparison against all
3801 baseline source pins finds 3786 unchanged files; the 15 changed existing
files and four new source files are confined to the pinned weather descriptor,
recipe, CameraAgent UI and associated tests/inventories. All 3894 current source
pins remain unchanged through these gates. Thirteen rebuilt binary hashes and
47 generated informational-version source stamps bind the Release closure to
`cacbcb7d`. See `baseline-source-delta.json`, `retained-gate-source-audit.json`,
`source-after-gates.json` and `release-binary-pins.json`.

The 20 explicit F1/F2/startup/SSH/shell pins in
`retained-boundary-source-proof.json` also match. Installer and typed teardown
source/tests, host Program/appsettings/identity validator, shared transport,
Docker-context shell boundary and shard table retain their reviewed bytes.
Project/dependency/tool/package manifests, coverage/category selection engine,
CI controls and secret/release inputs are unchanged. Package/tool audits and the
prior 62-case secret contract/content-free full-history scan remain applicable.

The shared-library siblings and SQL Server/Redis/Mailpit, object storage,
migrations, persistence and startup registrations have no source/configuration/
contract change. Their complete baseline Integration results are retained;
changed recipe/descriptor/graph interactions are refreshed by the full affected
project Unit selections, in-process both-host reconstruction, live graph
coordinator behavior and repository graph checks above. The classifier does not
select deployment contracts for this sync. Unchanged CLI Unit remainder,
distribution/installer/shard/catalog boundaries, original phase parity and
performance evidence remain retained. No long shard, installer campaign,
performance harness, whole-solution Unit/Integration repeat or new production
qualification was run. No new throughput, allocation, target latency, backlog or
whole-lane performance claim is made.

All heavy work is serialized by the host lock and bounded timeouts. The largest
recorded single-process gate RSS is 3374372 KiB; GNU time retains CPU, wall and
filesystem measurements per gate, without treating them as a benchmark.
The final resource audit reports zero workspace dotnet/testhost processes and
zero Docker containers. Docker images (5), volumes (3) and networks (3) have the
same ID inventories as before testing. Fixtures disposed their transient state;
no explicit resource, branch, worktree, scratch, claim or lifecycle cleanup was
performed. Historical fixtures/evidence remain preserved; no private case,
environment map or secret file was printed, and no full environment capture or
credential testing was performed.

This final ledger append changes no tested source. Lightweight controls/v1/docs
are repeated on its committed head before handoff; final immutable-range
classifiers and the complete-PR hash proof are retained separately. A subsequent
strict source-stamp test requires a rebuild at the then-current commit.
Finished: pinned merge, conflict correction, source/delta proofs and affected
local gates. Current: documentation handoff and final lightweight checks.
Next: parent pushes the final local head, requests a deep base-sync review of
`31c68bee..<final-head>` with both F1/F2 carried, then performs fresh target/head
proof and development/v1 finalization. Blockers: none in local implementation;
independent review and protected checks remain parent-owned. No push, GitHub
mutation, watcher, delegation, finalization or claim release was performed.
The required T3-only PR association does not mutate GitHub or enable watching.
