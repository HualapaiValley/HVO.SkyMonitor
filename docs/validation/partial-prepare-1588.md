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
cancellation after intent. `ExitCodeContractTests` includes this command in its
complete 0/1/2/3/130 matrix and preserves the existing host-policy refusal gate.
The explicit abrupt-publication failpoint returns 75, as the shell publisher did.

## Comparison and retained inputs

The original shell shard remains authoritative until the complete comparison
succeeds. The recorded scratch root is
`/tmp/hvo-skymonitor-1588-implementation.EyRt3Q`.

- `before/` retains the exact original phase/shard/coordinator sources.
- `shell-before.log`, `shell-before.time`, and `shell-before-fixture/` retain the
  unchanged baseline execution and its fixture.
- `comparison-source/` retains the original shell with an observation-only
  wrapper that snapshots each command input and result. `record-phase.py` is the
  wrapper source; no recovery logic is replaced by it.
- `parity-complete-inputs/case-*/` retain the exact inventory bytes, before-state,
  before-evidence, remote filesystem fixture, fake Docker state, fault options,
  command arguments, exit result, output, after-state/evidence and per-command
  GNU time output.
- `PartialPrepareParityTests.RetainedShellCycle_MatchesTypedResultsAndDurableEvidence`
  replays every retained input through the real typed phase and typed fake host.
  It compares exit codes and all phase journal/evidence fields. Only wall-clock
  timestamp values are normalized. Each candidate commit digest is independently
  checked against its unmodified canonical ledger before digest normalization.
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
boundaries for both implementations. Shell timings are retained per invocation;
the typed replay records elapsed phase time and test-host peak RSS, with the
same Release configuration and fixture input bytes. Fixture snapshot/copy time is
reported separately from phase time. This slice makes no whole-lane speed,
production-network, ARM64, physical-device or release-qualification claim.

Final comparison results, immutable candidate range, selector output and candidate
gates are recorded in the issue/PR evidence ledger and retained evidence bundle.
