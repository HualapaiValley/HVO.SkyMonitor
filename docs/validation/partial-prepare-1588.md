# Partial-prepare typed recovery evidence (#1588)

This is slice A of #893 under the approved #876/#877 design. It ports recovery of
failed split-host prepare runs. Other lifecycle phases and the coordinator remain
with their current owners. The intentionally retained shell boundaries remain:
Bash syntax, catalog installer invocation, Docker-context teardown, and Compose
invocation. The SSH adapter in the CLI exposes bounded OS primitives; it contains
no prepare/recovery policy, inventory interpretation, assertions or journal logic.

The starting production revision is
`bc00fa02af2991fc12a50d9dabd1575a5d36a4f7`. The shadow implementation was committed as
`c8820f823577646c6b2072a2952ae1cc4e706308` while the original shell phase and shard
remained authoritative. The target profile is `development-v1`; local Tier C
evidence is distinct from production qualification.

## Assertion trace

Line numbers below refer to the retained original
`scripts/deploy-contracts/partial-prepare.sh` at the starting revision. Every
assertion in a multi-line JSON predicate is listed in the behaviour column; loop
parameters name each vector. The implementation is in
`src/HVO.SkyMonitor.Deployment.Cli/SplitHost/`; the MSTest methods are in
`PartialPrepareRecoveryTests` unless qualified otherwise. Recovery decisions run
in C#, using `DeploymentTarget`, `PreparedTarget` and `PrepareCreation` contracts.

| Original lines / vectors | Behaviour and assertions retained | Typed owner | MSTest |
| --- | --- | --- | --- |
| 14–49 | Exact schema 7 retained shape; validate with missing ambient TMPDIR; historical logic/east project names; catalog comma path accepted; schema-8-only target field and old host collision rejected; ordinary preflight rejects schema 7 | `SplitHostInventory` legacy validation/projection, `Project` | `Schema7Validation_PreservesHistoricalShapeAndPath`, `Schema7RetainedBytes_UseHistoricalDockerProject`, `UnmigratedShellPhases_RejectSchema7AndFailedPrepareBeforeAnyHostCall` |
| 50–99 | Immutable schema 7 rebinding; historical logic and east Docker projects block inspection; successful inspection is passed/dry-run and binds original inventory hash | Inventory hash/marker binding; `VerifyDockerAsync` | `Schema7RetainedBytes_UseHistoricalDockerProject` and retained full-cycle replay |
| 101–134 | Failed prepare is a separate recovery path; missing/wrong/contradictory confirmation and wrong run rejected without removing root; normal down rejects failed prepare | `PartialPrepareRequest`, local preflight/prepare validation | `MissingOrContradictoryConfirmationAndWrongRun_AreRejected`, `UnmigratedShellPhases_RejectSchema7AndFailedPrepareBeforeAnyHostCall` |
| 136–151, hash / installation / target / root / owner | Every inventory identity is bound to retained preflight/prepare; all five mutations reject as mismatch without target mutation | `ReadAsync`, `ValidatePrepare`, preflight correlation | `ChangedInventory_IsRejectedBeforeMutation` (five rows) |
| 153–154 | SSH identity drift and Docker daemon mismatch reject | `PartialPrepareTarget.Authenticate`, daemon correlation | `InvalidRemoteProvenance_IsRejectedBeforeMutation` (host, daemon) |
| 156–160 | A needs-prepare target absent at preflight cannot acquire unjournaled artifacts | `PartialPrepareTarget.Unprepared` | `InvalidRemoteProvenance_IsRejectedBeforeMutation` (unjournaled) |
| 162–173 | Ownership marker, prepare lock and sidecar content/flags must remain canonical and authenticated | `Bind`, `Resolve`, retained-state validation | `InvalidRemoteProvenance_IsRejectedBeforeMutation` (marker, lock, state) |
| 175–185 | Byte-identical marker/sidecar replacements do not inherit deletion authority | Descriptor binding and revalidation | `ByteIdenticalReplacement_IsPreservedAndRejected` (two pre-move rows) |
| 187–192 | A replacement after the last pathname check is restored and preserved rather than deleted | `Remove` quarantine/revalidation | `ByteIdenticalReplacement_IsPreservedAndRejected` (on-move row) |
| 194–196 | Unexpected application state blocks cleanup | Bounded immediate-entry verification | `InvalidRemoteProvenance_IsRejectedBeforeMutation` (application) |
| 198–210, container / network / volume / named container / named network | Compose-labelled resources and deterministic names must all be absent | `VerifyDockerAsync`, strict inspect absence | `DeploymentResources_BlockAllDeletion_IncludingAfterIntent` (five rows) |
| 212–217 | Resource absence is rechecked after durable intent and immediately before deletion; marker/sidecar remain on refusal | Intent publication followed by fresh resource scan | `DeploymentResources_BlockAllDeletion_IncludingAfterIntent` (late) |
| 219–223 | Context rebinding cannot redirect checks from the captured endpoint | Pinned `--host` per correlation/scan | `ResourceChecks_AlwaysUsePinnedEndpoint` and full-cycle replay |
| 225–233 | Unrelated artifacts preserved; dry-run/ambient Docker overrides cannot mutate roots; exact target order/dispositions, empty resources, passed dry-run; evidence omits paths | Docker environment scrubbing, projection/private publication | `InspectAndCleanup_UsesRealTypedPhase_AndResumesEachDeletionBoundary`, pinned-endpoint test and full-cycle replay |
| 235–249, marker / control / root / lock-state / lock | Each interruption fails with durable intent/completion; root/lock/sidecar eventually absent; repeat cleanup succeeds; unrelated artifact retained; final passed cleanup has exactly two completed resources | `PartialPrepareState`, intent/completion transitions, descriptor-bound empty cleanup | `InspectAndCleanup_UsesRealTypedPhase_AndResumesEachDeletionBoundary` |
| 250–260 | Preserve a preexisting persistent root; remove only newly created control/marker; report exact false/true/true flags | Creation authority in projection and cleanup | `PreexistingRoot_IsRetained_AndForeignUnpreparedTargetIsUntouched` |
| 262–273 | An unprepared ready root and foreign parent are outside cleanup authority; prior application bytes retained | `Unprepared` absence checks without ownership adoption | `UnpreparedReadyRoot_WithForeignParent_IsRetained` and full-cycle replay |
| 275–288 | Existing-service infrastructure context still scanned for run-labelled resources | All declared target contexts participate in scans | `ExistingServiceInfrastructureContext_IsStillScannedForRunResources` |
| 290–311, lock newline / duplicate state field / different creating run | Persistent prior-run artifacts require exact canonical marker/lock/state bytes; no provenance drift is repaired | `PartialPrepareTarget.Preserved` | `RetainedPersistentProvenance_RequiresExactCanonicalBytes` (three rows), `RetainedPersistentTarget_IsPreservedOnRepeatedCleanup` |
| 313–322 | Spaces, shell metacharacters and quotes remain literal through all cleanup boundaries; exact root/lock/sidecar absent after success | Argument-vector Docker calls and encoded SSH primitive protocol | `QuotedPaths_AreLiteralAcrossEveryHostPrimitive`, SSH primitive backend test and full-cycle replay |

Additional focused cases cover committed-publication tampering, orphan files,
next-generation recovery at all four publication boundaries, interrupted quarantine renames for all five objects, run/remote flock,
symlinks/hardlinks/modes, private-upload registry links, later-phase refusal and
cancellation after intent. A retained schema 7 inventory with more than 128 KiB of
valid endpoint data passes through bounded standard input, avoiding the Linux
per-argument limit and ambient temporary files. A mirror-publication failure retains one recoverable generation instead of stacking a second uncommitted failure generation. `ExitCodeContractTests` includes this command in its
complete 0/1/2/3/130 matrix and preserves the existing host-policy refusal gate.
The explicit abrupt-publication failpoint returns 75, as the shell publisher did.

## Comparison and retained inputs

The original shell shard remained authoritative through the successful complete
comparison. Its implementation and assertions were removed only after archiving
the exact tested sources and proof. The recorded scratch root is
`/tmp/hvo-skymonitor-1588-implementation.EyRt3Q`.

- `before/` retains the exact original phase/shard/coordinator sources.
- `shell-before.log`, `shell-before.time`, and `shell-before-fixture/` retain the
  unchanged baseline execution and its fixture.
- `comparison-source/` retains the original shell with an observation-only
  wrapper that snapshots each command input and result. `record-phase.py` is the
  wrapper source; no recovery logic is replaced by it.
- `parity-final-inputs/case-*/` retain the exact inventory bytes, before-state,
  before-evidence, remote filesystem fixture, fake Docker state, fault options,
  command arguments, exit result, output, after-state/evidence and per-command
  GNU time output.
- `PartialPrepareParityTests.RetainedShellCycle_MatchesTypedResultsAndDurableEvidence`
  replays every retained input through the real typed phase and typed fake host.
  It compares exit codes, both phase-file inventories, every phase journal/evidence
  field, timestamp key presence, and remote filesystem paths, types, modes and
  content checksums. Only wall-clock timestamp values are normalized. Both shell
  and typed commit digests are independently checked against their unmodified
  canonical ledgers before digest normalization.
- `pre-cutover-proof/` retains the committed source archive at `8594b5c0`, the
  exact tested worktree patch and C# sources, and the successful 45-vector JSON
  result with its checksum. This archive was created before removing either the
  superseded implementation or its assertions.
- Original failed focused logs/TRX and comparison attempts remain alongside the
  corrected results. They are never overwritten with passing logs.

The complete input set contains 45 recovery invocations. The preflight/prepare
fixtures and unrelated phases retain their original authority. Race and Docker
faults are expressed through typed fake-host options at the equivalent primitive
boundary; a shell phase invocation is never counted as a typed recovery test.

## Resource method

The current unchanged shell shard at `bc00fa02` took 67.23 seconds wall,
30.87 seconds user CPU, 39.03 seconds system CPU, 117468 KiB peak RSS and 20064
filesystem-output blocks, using GNU `/usr/bin/time -v`. It ran on native Linux
x64 with SDK 10.0.402 and disposable fake hosts, one shard worker and no live
Docker resources. The design's historical 77-second partial-prepare result is a
separate measurement from main run 35280447884, not this before value.

The direct comparison uses the same 45 input vectors and failed/successful state
boundaries. Their immutable inventories name `8594b5c0`; the recovery shell code
is byte-identical to the starting revision, retained in `comparison-source/`.
The typed tested source is exactly the `pre-cutover-proof/` archive plus patch.
Both execute on the same native host and disposable filesystem inputs, with the
CLI test assembly built in Release. `phase-resource-summary-initial.json` records:

| Equivalent 45-vector phase work | Shell | Typed |
| --- | ---: | ---: |
| Sum of phase wall time | 44.01 s | 1.327 s |
| Peak RSS | 12032 KiB maximum child | 117548 KiB test host |

Shell timings come from each retained `shell.time`; typed phase time is the sum
of stopwatches around the real phase calls, excluding fixture copying. RSS scopes
differ: the typed value includes the resident .NET/MSTest host and its fixture,
whereas the shell value is the largest command child. The higher typed RSS is a
runtime/test-host cost and is explicitly retained; these figures do not establish
production working set or the RSS of an installed CLI invocation. The unchanged
whole-shell-shard value above includes fixture setup and assertions and must not
be substituted for phase time. The separate Manual replay's GNU time output is
`typed-parity.time`, with discovery and fixture copying included.

Reproduction (all evidence paths are under the recorded scratch root): capture
with `comparison-source/scripts/test:deploy-environment --shard partial-prepare`,
using `record-phase.py`; replay with `HVO_PARTIAL_PREPARE_PARITY=<scratch>/parity-final-inputs`
and `dotnet test tests/HVO.SkyMonitor.Deployment.Cli.Tests --configuration Release
--filter FullyQualifiedName~PartialPrepareParityTests`. The retained source,
wrapper, environment manifest, raw per-vector timings and result JSON make both
sides reproducible without live hosts. Use the shared heavy-gate flock and the
repository's build flags when rerunning.

The expanded migrated shard has 79 native Unit cases. Like the existing installer
contracts, those cases and the Manual replay declare their Linux-only descriptor
semantics explicitly. The macOS inventory retains their discovered cases and adds
the 79 new recovery cases plus seven Linux-only exit rows to its skip pin; no
Linux case is skipped. The migrated shard is a superset, so its
wall time is not an identical-workload comparison. This slice makes no whole-lane
speed, production-network, ARM64, physical-device or release-qualification claim.
Final immutable candidate range, selector output and candidate gates are recorded
in the issue/PR evidence ledger and retained evidence bundle.
