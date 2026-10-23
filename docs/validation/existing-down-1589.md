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
