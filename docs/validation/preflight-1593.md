# Split-host inventory/preflight migration evidence (#1593)

This slice of #893 follows existing-services down (#1589/#1613), from
`8890edddec5a375add937e08fa29ba802cad77c3`, targeting `development/v1`.
`Deployment.Contracts` owns content-free probe observations, memory/architecture
normalization, persistent capture-continuity and transient-confirm predicates.
`Deployment.Cli` owns preflight admission, ordered probes, correlation, checks and
publication. It reuses `SplitHostInventory`, its unchanged versioned jq shape
predicate, `DeploymentHost`, confined private paths and `PartialPrepareState`'s
native lock/publication primitives. It never calls the old production preflight.

The inventory remains schema 8; the preflight manifest/evidence remain schema 1
with the same paths, fields, string-valued host observations and ordered checks.
Existing schema-7 recovery support in the preceding phases is unchanged. Remaining
shell phases still consume their identical inventory predicate. No installer,
host identity, persistent layout, source selection or signing policy is redesigned.
No live target, hardware, release, promotion or publishing operation was performed.
#893 remains open for later phase/coordinator/runbook acceptance.

## Assertion trace

Original line numbers below refer to the immutable base above. All named tests
are in `tests/HVO.SkyMonitor.Deployment.Cli.Tests`; every loop vector is retained.
The fake targets exercise real typed validators/phase code and Linux private
files/locks. Boundary tests also execute the actual bounded SSH primitive adapter
and disposable loopback TCP/HTTP targets.

| Original assertion | Typed decision / regression |
| --- | --- |
| part 1, 14–18: legacy login email shape returns 405 | `PreflightBoundaryTests.LegacyLoginEmailShape_IsRejectedByDisposableHttpTarget`, real curl over the shared primitive adapter |
| part 1, 20–38: secret/certificate registry publication failure, successful upload followed by failed rename, temp absence and empty registry | `PreflightUploadTransport`; `PreflightSafetyTests.PrivateUpload_PublicationAndMoveFailuresVerifyAbsence` (both kinds); binary/chunk/literal-path regression `PreflightBoundaryTests.PrivateBinaryCopy_StreamsExactBytesWithLiteralPathsThroughRealPrimitiveAdapter` |
| part 1, 40–59: acceptance-run session registration survives initialization, upload removal verified before forgetting, remaining remote-private entry retained | `PreflightPrivateUploads.Initialize` and shared typed registration/cleanup; `PrivateCampaignRegistry_PersistsReinitializationAndVerifiedCleanup` |
| part 1, 61–70: Docker volume/network not-found versus transport/auth outage | `PreflightUploadTransport.RequireResourceAbsentAsync`; `DockerNotFound_ProvesAbsence_TransportAndAuthenticationNeverDo` (both resource kinds) |
| part 2, 14–17: syntax of production entry/libraries, harness, lifecycle helper, all shard bodies and SQL provisioner | Retained verbatim `scripts/deploy-contracts/preflight-2.sh` (`bash -n`) |
| part 3, 13–23: tampered registry arbitrary path never deleted | Complete registry validation before any target opens; `TamperedRegistry_RefusesBeforeHostAndPreservesArbitraryFile` |
| part 3, 25–59: valid continuity; raw DB missing/reset/behind; outbox missing/reset; artifact sequence behind/wrong agent; fleet DB missing/wrong instance/behind; isolated bypass | `PreflightContinuity.Validate`; all eleven rows of `PersistentCaptureContinuity_PreservesAllHistoricalBarriers` |
| part 3, 61–76: schema required application/image/limit keys, schema 8, runtime-root/catalog/target refs, explicit endpoint port and generic module path | `ExampleSchemaAndExecutableInventory_Agree`; the actual example passes `SplitHostInventory.ReadAsync` |
| part 3, 77–96: W0/W1/W2 source, dimensions, seed, warmup/measured operation count, source/config/options hashes, exact W0 readout | `CanonicalWorkloads_PinSourceConfigurationOptionsAndReadout` |
| part 3, 110–143: multiple catalogs, per-agent selection, friendly rename preserves root/project/manifest identity; duplicate UUID/root, missing catalog, invalid/overlong catalog ID, unsafe catalog ancestry; canonical persistent root versus arbitrary root | `CatalogSelectionAndFriendlyRename_PreservePersistentIdentityAndProject`, `InvalidCrossFieldsAndLayout_RejectBeforeTransport`, `InvalidShape_RejectsBeforeTransportAndMutation`, `PersistentProductRoot_IsCanonical` |
| part 3, 144–191: valid/duplicate/extra allowlist; eligible detail; same assessment and notification after review; Mailpit existing-message baseline/resume; complete evidence versus missing notification | `PreflightTransient`; `TransientAllowlist_RejectsDuplicateAndUnknownFields`, `TransientAudit_RequiresSameAssessmentAndCausalNotification`, both `TransientMailpit_UsesBaselineUnlessResumed` rows, `TransientEvidence_RequiresTheCompleteBoundReviewAndNotification` |
| part 3, 193–195: checked-in example accepted; arbitrary workload-profile path rejected | `ExampleSchemaAndExecutableInventory_Agree`, shape row for `.deployment.workload.profiles` |
| part 3, 196–231: shared target shape; null/equal/leading-zero SQL memory rejection; 4096M/4G conversion and 08G refusal; actual 9G shared capacity failure; colocated shared host accepted; inconsistent correlation/missing SSH/wrong/duplicate ports rejected | `SharedServicesShapeAndSqlLimits_PreserveEveryCurrentVector`, `MemoryLimit_UsesExistingUnitsAndRejectsLeadingZero`, `SqlMemory_IsCheckedAgainstTheSharedTargetObservation`, cross-field identity row |
| part 3, 232–269: two accepted runtime roots, ten rejected roots; delimiter-bearing module file accepted; three catalog versions and colon rejection; two bucket names; filesystem credential mapping rejection; schema-pattern agreement | Every `RuntimeRootShape_MatchesPublishedSchema` / `CatalogVersions_MatchPublishedSchema` row; `BucketNamesAndModulePaths_KeepTheirExistingLessRestrictiveShape`; ObjectStorage credential shape row |
| part 3, 271–295: valid read-only pass, three targets, architecture and Compose versions, needs_prepare, 0700/0600 state, explicit context/SSH selection, TCP rather than deferred HTTP, no mutation or secret exposure | `ValidSplitSameHostAndQuotedPaths_PreserveWireContractAndReadOnlyTargets`; `RealReadOnlyAdapter_ProbesTcpHttpAndLiteralRoots_AndHasNoMutationOperations`; complete real CLI receipts |
| part 3, 297–314: spaced roots, correlated same-host topology, duplicate owner-password reference | Three positive topology/path rows plus duplicate-owner shape row |
| part 3, 316–326: daemon name/ID mismatch, malformed architecture/OS/version, unsupported architecture does not leak raw output, missing Compose, unreachable SSH, missing Bash | Exact `UnsafeHostProbe_RecordsExactBoundedFailure` rows; `DockerArchitecture_KeepsOnlySupportedAliases`; `MissingRemoteBash_KeepsExit127DiagnosticAcrossPipeDisposal` |
| part 3, 328–335: actual TCP refusal and isolated public-authority deferral | TCP row, positive deferral assertions and actual loopback adapter test |
| part 3, 337–354: missing ss/timeout, OIDC issuer/token/malformed/oversized/status, host/Docker port conflicts | Probe rows and every `OidcDiscovery_RejectsEveryHistoricalAuthorityFailure` row |
| part 3, 356–362: product-root symlink and per-component runtime inspection | `ProductRootSymlink_IsRejectedLocallyBeforeAnyHostContact`; all `RuntimeRoot_EveryExistingComponentIsInspected` rows; `TrustedRootOwnedRuntime_RequiresPreparationForANonrootCaller` |
| part 3, 364–373: unsafe secret mode/stat/scan errors are bounded, zero transport; symlink/hardlink locks preserve sentinel bytes | `SecretSourceAndExtraction_ArePrivateUniqueAndNonEvaluating`, `LocalReadFailures_RejectBeforeTransportOrStatePublication`, all `UnsafeRunLock_IsNeverFollowedOrTruncated` rows; owned single-link mode normalization is additionally covered by both `OwnedSingleLinkRunLock_IsNormalizedAfterAcquisition` rows |
| part 3, 375–388: endpoint port/internal-port agreement, isolated HTTP acceptance, traversal/unknown fields, mutable image/tag/policy, credential URL, relative archive root and remote builder | Every corresponding `InvalidShape_RejectsBeforeTransportAndMutation` row; positive isolated/topology run |
| part 3, 390–415: globally duplicated sql/redis/certificate/owner/bootstrap and unused entries reject before transport | All six `DuplicateSecrets_RejectGloballyBeforeTransport` rows |
| part 3, 416–443: exact duplicate/missing/control-byte extraction, control bytes in sql/redis/client/certificate before rendering | Four extraction rows and all four `ControlSecretBytes_RejectBeforeRendering` rows |
| part 3, 445–468: persistent HTTPS on LogicHost/both agents, proxy and production catalog; valid persistent reachability, unreachable authority and missing curl | Five `PersistentMode_RequiresProductionHttpsAndProxy` rows plus three `PersistentAuthority_IsProbedAndNeverDeferred` rows |
| part 3, 470–483: accepted failed rerun replaces passed evidence; evidence symlink not followed or rewritten | `FailedRerun_ReplacesPassedEvidence_WhileRejectedInputsPreserveIt`, `RejectedResume_DoesNotRewritePriorStateOrEvidence(evidence-symlink)` |
| part 3, 485–517: completed-check/target tampering, changed inventory hash, invalid inventory, corrupt manifest preserve exact prior bytes | Every remaining `RejectedResume_DoesNotRewritePriorStateOrEvidence` row and rejected inventory regression |
| production `inventory.sh`, 3–204: exact schema/value predicates and cross-field routes/correlation/secret references/persistent policy/resource collisions | Reused `SplitHostInventory` versioned shape predicate and typed cross-field/layout validator; inventory/layout/shape matrices above, existing predicate-equality regression from #1588 |
| production `inventory.sh`, 206–260: private secret-source admission, control/entry/duplicate/reference scan; target order | Reused native secret parser; inventory/secret rows above; positive ordered target/check parity and exact receipts |
| production `preflight.sh`, 3–24: ordered content-free check fields/timestamps and memory units | `PreflightHostObservation`, `PreflightProbe`, `SplitHostPreflight`; exact receipt audit and memory rows |
| production `preflight.sh`, 26–105: SSH shape/correlation, CPU/memory/free/clock/thermal/throttle, root/ports, explicit Docker context/daemon/Compose/container checks | Probe matrix, real read-only adapter and complete receipts |
| production `preflight.sh`, 107–160: inventory order; shared SQL memory; TCP routes, isolated defer/persistent HTTP; completed target/check comparison; evidence-before-passed-manifest | Positive/refusal/resume tests, `AbruptEvidenceBoundaryAndCancellation_RetainTruthfulResumableState`, `ManifestRenameFailureBeforeAdmission_PreservesPriorPassedEvidence`, exact audit |

No new publication generation, ledger or evidence field is introduced. Preflight
retains `before-manifest-rename` and `after-evidence-before-passed-manifest`.
Private transport retains registration-publication and post-upload crash seams.
The local OS stat/reference-scan failures have native read failpoints because the
typed parser does not execute the old stat/grep shims. CLI exit-code tests include
preflight in the common 0/1/2/3/130 mapping; explicit publication crashes return 75.

One subtle original behavior was reproduced rather than changed: a failed target
probe publishes failed manifest/evidence, whereas a TCP/HTTP connectivity failure
leaves a running manifest containing the failed check and cleared/absent evidence.
It never restores a stale passed receipt. Invalid inventory, rejected resume and
unsafe/corrupt prior artifacts are rejected before target transport or journal
replacement. Cancellation after admission retains failed resumable evidence.

## Complete comparison before cutover

The hvo-recorded scratch is `/tmp/hvo-skymonitor-1593-pipeline.koc01d` on the host,
mounted at `/scratch` in the issue-only Linux containers. `baseline-source/` is the
Git archive of the immutable base. Its synthetic fixture Git commit supplies the
same source revision to both commands; the base archive remains the source pin.
`baseline-shell.log` / `.time` record the complete untouched shell shard passing
in 16.39 s wall, 244,268 KiB peak descendant RSS, including fixture/listener setup.
The preserved original disposable fixture is `/tmp/hvo-deploy-test.dQTgMH`.

`captured-shell.log` / `.time` record a second complete passing original assertion
cycle with snapshot capture, 16.52 s wall. `preflight-parity/case-000` through
`case-068` retain every exact inventory, private source, semantic fault settings,
before/after state/evidence/remote tree, original stdout/stderr and exit. Its
preserved fixture is `/tmp/hvo-deploy-test.XcmjJW` in `hvo1593-linux`.

`attempt-4` completed before authority changed. Final `attempt-5` repeats the complete shell/typed comparison after preserving owned-lock mode normalization: 69/69 original exits
match, and every resulting path/type/mode/content matches. Host free-space is
pinned to 10,000,000 KiB for both executions so the harness's own files cannot
change the observation. The original hardlinked lock is recreated as a hardlink;
the capture copy alone cannot retain its external link. Tool-absence knobs apply
equally to the typed primitive session, and stat/grep OS faults translate to the
matching native read failpoints. Only phase timestamps and observed clock-offset
seconds are normalized. Configured routes, identities, roots, check order,
statuses/reasons, private registration and all other fields remain exact.

`accepted-attempt4.json`, `precutover-source/`, `precutover-source-pins.json`,
`precutover-cli.dll` and `parity-fixture-pins.json` pin the accepted source,
executable and fake tools before authority changed. The Manual
`PreflightParityTests.CompleteOriginalCycle_HasExactExitStateEvidenceAndRemoteParity`
passed over all 69 receipts (`parity-audit.log`, `parity-audit/parity.trx`). Original
failed attempts and their outputs were preserved. The comparison exposed the
connectivity publication distinction and a missing-Bash diagnostic overwritten by
stdin-disposal's broken pipe; both now have explicit typed regressions. Final
`accepted.json`, `final-source/`, `final-source-pins.json` and `final-cli.dll`
pin the final implementation. `preflight-lock-mode-2/` retains two additional
exact original/typed comparisons: owned single-link run locks initially 0644 or
0666 both pass, retain bytes and become 0600 after locking. Their complete
state/evidence/remote trees also match. The first lock-mode setup failed because
an absent before-state directory was copied as absent; that partial setup and
its failure remain in `preflight-lock-mode/` and tool output.

## Current resource evidence

`preflight-resource-summary-final.json` aggregates GNU time for the same 69 phase
commands, excluding fixture restoration, snapshot copying and listener setup.
One worker, .NET SDK 10.0.402, Release, ARM64 Linux, no warmup, disposable fake
SSH/Docker/loopback HTTP, no actual deployment or Docker mutation.

| Measurement | Original shell | Typed CLI |
| --- | ---: | ---: |
| Sum wall seconds | 11.45 | 15.77 |
| User CPU seconds | 8.37 | 13.63 |
| System CPU seconds | 3.21 | 1.42 |
| Maximum per-command peak RSS, KiB | 11,168 | 105,916 |
| Filesystem input / output blocks | 10,448 / 9,432 | 96 / 215,360 |

The aggregate typed command envelope is 37.7% slower. The exact receipts isolate
the difference: 32 refusals before state creation total 1.63 s shell versus
6.09 s typed; the other 37 host/state cases total 9.82 s versus 9.68 s. Managed
process/runtime startup dominates short invalid-input invocations; the runtime
also increases RSS. Observation-only syscall profiles of one ordinary and one
before-state refusal are retained in `attempt-profile/*.strace` and `profile.log`,
excluded from these measurements. The invalid-input profile shows 586 mmap and 281 futex calls in the managed process versus 50 and 6 for shell, despite only 2 versus 10 execve calls. The ordinary case has 179 versus 505 execve calls. These observations support runtime startup as the source of the short-refusal overhead; they are not timing claims. No target-phase or whole-lane speedup is claimed.

## Focused validation and runtime envelope

The focused project was built with warning-as-error, pinned SDK 10.0.402,
`-nodeReuse:false -p:UseSharedCompilation=false`, under the host's
`flock -o /tmp/hvo-heavy-gate.lock timeout ...`. Unit selections unset
`HVO_PRODUCTION_CATALOG_BUNDLE` and set
`DOCKER_HOST=unix:///tmp/hvo-no-docker.sock`. Logs and TRX are retained under the
scratch root; the handoff below records the final counts and affected guards.

Original runtime `hvo1593-linux` has overlay directories with link count 1.
Existing installer directory-authentication tests require directory link count
at least 2. The parent's unchanged-source reproduction passed with tmpfs
(`runtime-auth-tmpfs.log` and TRX); this is an environment limitation, not an ARM64
product defect. Corrected `hvo1593-candidate` uses an executable 4 GiB `/tmp`
tmpfs. Its non-root `vscode` invocation also passed the two existing release-folder
exit-code rows whose root execution correctly hit root refusal first
(`candidate-nonroot/root-refusal.log` and TRX). Product safety checks were preserved.
The final Tier C candidate gate belongs to the parent and is not duplicated here.

Target protected profile is **Development v1 / Preflight** plus **Development v1 /
Build and Unit**. Classifier-selected local evidence is distinct from that future
protected CI and from production qualification. No full qualification dispatch
was requested or performed.

## Implementation handoff checks

All commands use the assigned worktree, SDK 10.0.402 and the host heavy lock.
The focused Release build is:

```bash
PATH=/opt/homebrew/bin:$PATH flock -o /tmp/hvo-heavy-gate.lock timeout 600 \
  docker exec hvo1593-linux bash -c 'cd /Users/roys/.t3/worktrees/HVO.SkyMonitor/agent-1593-893-slice-c-port-split-host-preflight-to && dotnet build tests/HVO.SkyMonitor.Deployment.Cli.Tests/HVO.SkyMonitor.Deployment.Cli.Tests.csproj --no-restore --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false'
```

It passes with zero warnings/errors (`preflight-build-lock-parity-2.log`, 37.01 s).
The final affected test command uses `docker exec --user vscode
-e DOTNET_CLI_HOME=/scratch/candidate-nonroot/dotnet-cli hvo1593-candidate bash -c`
inside the same host lock/600-second timeout, with:

```bash
env -u HVO_PRODUCTION_CATALOG_BUNDLE DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  dotnet test tests/HVO.SkyMonitor.Deployment.Cli.Tests/HVO.SkyMonitor.Deployment.Cli.Tests.csproj \
  --no-build --configuration Release \
  --filter 'TestCategory=Unit&(FullyQualifiedName~HVO.SkyMonitor.Deployment.Cli.Tests.Preflight|FullyQualifiedName~ExitCodeContractTests|FullyQualifiedName~CommandLine|FullyQualifiedName~ExistingDown|FullyQualifiedName~PartialPrepare|FullyQualifiedName~ProcessRunner)' \
  -nodeReuse:false -p:UseSharedCompilation=false \
  --logger 'trx;LogFileName=preflight-final-3.trx' \
  --results-directory /scratch/candidate-nonroot/preflight-final-3
```

Result: **684 passed, zero failed/skipped**, including all 174 new preflight
cases. Evidence: `candidate-nonroot/preflight-final-3.log` and its TRX. The broader
accidental `~Preflight` selection remains in `preflight-final-2`: 736 passed and
three existing state-reset cases refused the candidate container's empty
`/etc/machine-id`. The parent provisioned a container-only identity; no product
or source check was weakened. The parent
confirmed those exact three unchanged rows pass with the container-only identity
(`candidate-nonroot/runtime-machine-id.{command,log,time,exit}` and TRX).

The original root prepare/images invocation also reached the existing
`root-parent` refusal assertion, whose simulated root owner equals that caller's
UID 0; `prepare-images-cutover-3.log` and fixture
`/tmp/hvo-deploy-test.iotudN` are retained. The earlier log-shape failure and
compile-constant correction remain in `prepare-images-cutover{,-2}.log` and
`preflight-build-lock-parity.log`. The prepare transport itself remained quoted;
its assertion now matches that command independently of preflight's old TCP log.

`preflight-format.log` and `preflight-format-lock.log` cover only the changed C#
files. Native macOS shell syntax/ShellCheck attempts lacked GNU realpath and
ShellCheck respectively; the Linux retries are retained. Raw ShellCheck without
source following also reported existing cross-file globals and info/style
messages; the final focused command uses the repository's warning threshold and
source-following policy. Complete Tier C builds, format verification, Unit,
Integration, package/control gates and target-head classifier remain the parent's
responsibility on the committed handoff.

Final affected boundary evidence:

| Command / selection | Result | Retained evidence under `/scratch` |
| --- | --- | --- |
| Complete original/typed replay (`REPLAY_ATTEMPT=5 python3 /scratch/replay_preflight.py`) | 69/69 exact exits/trees | `parity-replay-5.log`, `preflight-parity/case-*/attempt-5/` |
| Additional lock replay (`REPLAY_ATTEMPT=1 python3 /scratch/replay_lock_mode_2.py`) | 2/2 exact exits/trees | `lock-mode-parity-2.log`, `preflight-lock-mode-2/` |
| Manual `PreflightParityTests`, `HVO_PREFLIGHT_PARITY=/scratch/preflight-parity` | 1 passed, zero skipped | `parity-final-audit.log`, `parity-final/parity-final.trx` |
| `scripts/test:deploy-environment --shard prepare-images`, nonroot candidate | Passed complete shard | `candidate-nonroot/prepare-images-cutover.log`; fixture `/tmp/hvo-deploy-test.SudFja` retained in candidate |
| `scripts/test:deploy-environment --shard preflight` | 174 passed, zero failed/skipped; production `bash -n` retained | `preflight-cutover-final.log` |
| `scripts/test:deploy-environment-cli` | Passed | `deploy-cli-focused.log` |
| Homebrew Bash `scripts/test:ci-classification` | Passed classifier/Required CI and release/secret contract vectors | `classification-focused.log` |
| `scripts/ci:shell-syntax` | Passed 202 files, Bash 5.2.21 | `shell-syntax-final-2.log` |
| ShellCheck `-x --severity=warning` changed shell files; harness without `-x` | Passed, local ShellCheck 0.10 | `shellcheck-final-2.log` (empty success output); protected pinned tool remains 0.9.0 |
| CLI `--list-tests --filter TestCategory=Unit` / `Manual` | 2958 / 8; inventories agree | `preflight-category-{Unit,Manual}.log` |
| `git diff --check` | Passed | terminal handoff |

No additional container, real image, live target, worktree, branch or scratch root
was created by the implementation. Retained artifacts are beneath the existing
hvo-tracked scratch root, plus explicitly preserved disposable container fixtures.
Original failed comparison/build/test/tool attempts remain intact. The parent
owns container/scratch cleanup after review and finalization.
