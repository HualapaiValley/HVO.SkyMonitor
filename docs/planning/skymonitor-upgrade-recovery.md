# SkyMonitor Upgrade Recovery: Implementation and Qualification Map

Contract revision [#1606](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1606),
2026-10-23; source baseline `4df5193e5967a7b02d1a31c7faf1a782e050da5d`.
The [install contract section 6](../hvo-install-contract.md#skymonitor-approved-target-upgrade-time-checkpoint-recovery)
owns approved target behavior. This map identifies delivered inputs and remaining
work; it implements no runtime backend, on-disk migration or new command.

## Baseline and Recommendation for the Studies

Credit the delivered mechanism first. `CameraAgentStateSnapshot.cs` uses SQLite
online backup, verifies self-contained copies with hashes, integrity/schema/row
facts, and durably publishes a manifest outside the instance under product
operations. It also stages and verifies restoration and removes live sidecars.
This is selected DB copying, not a filesystem snapshot or a complete instance
checkpoint. The [current lifecycle runbook](../runbooks/deployment-installer.md#cameraagent-lifecycle)
documents its store exclusions, best-effort counter carry and retained rollback.
The new target requires full mutable-state/reference coverage, verified external
facts and an upgrade-only decision boundary instead.

LogicHost already has SQL recovery records/scripts and offline object-store
maintenance/restore primitives. The [#586 report](../validation/logichost-filesystem-qualification-586.md#final-disposition)
and [lifecycle preparation](logichost-lifecycle-contract.md) retain their actual
topology, image and qualification limits. These are reusable evidence, not proof
of a new SQL/object/config checkpoint implementation.

**Recommendation at this contract stage:** retain the existing backends/layout
as the study baseline. There is no measured evidence here that changing them
reduces total complexity or elapsed recovery time. No new filesystem, snapshot
framework, general backend matrix or routine CI campaign is justified by this
specification alone. No alternate backend was provisioned or benchmarked here.
Remaining uncertainty is the cost of completing the required coverage/fencing
versus one promising alternative, to be measured by #1607/#1608.

## Requirement and Implementation Map

Paths below are repository-relative. They identify implementation seams, not
authorization to edit another owner's files. #893 owns CLI parity and #1292
owns host policy; coordinate their contracts without taking their claims.

| Requirement | Delivered inputs / relevant paths | Remaining adoption and qualification |
| --- | --- | --- |
| `UPGRADE-RECOVERY-001`: pending-upgrade checkpoint/image pairing; no downgrade | `src/HVO.SkyMonitor.Deployment.Cli/CameraAgentStateSnapshot.cs`, `CameraAgentLifecycle*.cs`, `CameraAgentStateMigration.cs`; `docs/hvo-install-contract.md` section 6 | Replace reverse-readability reliance and retained-image toggling with the exact pending checkpoint/config/catalog/image pair; preserve supported forward inputs. No post-commit grace period or daily command. |
| `UPGRADE-RECOVERY-002`: complete logical point and non-rewindable facts | `src/HVO.SkyMonitor.Deployment.Cli/CameraAgentStateLayout.cs`, `CameraAgentStateCounters*.cs`; `docs/runbooks/product-instance-layout.md`; LogicHost `src/HVO.SkyMonitor.LogicHost/Data/CentralRecovery*.cs` and `deploy/sql/` | Inventory every DB, recovery file, mutable file/config/key and referenced payload, including external roots. Prove coherent DB/file generations and identity/catalog pairing. Replace broad store exclusions/best-effort carry with minimal verified exceptions or proof that the fence prevents new external facts. SQL Server remains central authority. |
| `UPGRADE-RECOVERY-003`: all-writer fence, candidate health and durable decision before admission | `src/HVO.SkyMonitor.Deployment.Cli/CameraAgentLifecycle*.cs`; current authenticated lifecycle/drain operations and host health endpoints | Prove every writer and external acknowledgement is fenced. Qualify semantic health, dependency/auth/read checks and isolated persistence/retrieval probe under a timeout. Commit durably before resuming; interrupted decision never rewinds acknowledged work. |
| `UPGRADE-RECOVERY-004`: durable CameraAgent protected/unprotected path and interruption-safe restore | Snapshot/journal/retention code in `src/HVO.SkyMonitor.Deployment.Cli/`; `tests/HVO.SkyMonitor.Deployment.Cli.Tests/CameraAgentStateSnapshotTests.cs`, `LifecycleContractTests.SnapshotRecovery.cs`, `LifecycleContractTests.RecoveryReads.cs` | Detect actual qualified capability; verify completion or warn and durably record unprotected without demanding backup proof. Missing/partial/damaged/space-exhausted checkpoints never claim protection. Journal/capsule/receipt survive outside reverted roots. Failed unprotected mutation cannot start an old binary. |
| `UPGRADE-RECOVERY-005`: bounded studies and risk-based evidence | `docs/planning/performance-validation.md`; `docs/validation/logichost-filesystem-qualification-586.md`; `scripts/ci:classify`; `docs/development-v1.md` | #1607 and #1608 compare end-to-end costs, preserve current gates, propose justified evidence reuse and recommend keep/change. Backend adoption and gate changes require subsequent reviewed work. |

Existing `PreviousReleaseStoreReadabilityTests` under
`tests/HVO.SkyMonitor.CameraAgent.Tests/State/` and
`CameraAgentSnapshotRollbackExceptionTests` under
`tests/HVO.SkyMonitor.Deployment.Cli.Tests/`, plus the frozen
`tests/fixtures/state-compatibility/previous-release/` still enforce the delivered
policy. This documentation does not remove or relax them. Their eventual
replacement must prove supported forward migration and whole-point recovery
before dropping the universal old-reader obligation. Existing explicit
backup/restore remains independently supported and tested.

## Qualification Children and Remaining Experiment Questions

Both children depend on this contract and then may run independently:

- [#1607 CameraAgent](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1607):
  on disposable Linux images, can existing SQLite checkpoints plus pinned files
  and complete mutable-state coverage meet the invariant with less overall work
  than a qualified filesystem snapshot? What provisioning, privilege, mount and
  crash-recovery costs does the best alternative add? Can one instance revert
  while a running sibling and shared catalog selections remain unchanged?
- [#1608 LogicHost](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1608):
  which smallest native SQL Server checkpoint/restore method plus object/config
  cutoff gives a coherent point? Can existing offline object tools be reused
  without a full archive per upgrade? What DB log/backup chain, identity/key,
  service-topology and restore-time costs remain? Feed results into #1244's
  explicit backup/restore and #1245's upgrade recovery without conflating them.

Time-box each initial investigation/probe to roughly 60–90 minutes. Reuse
existing observations and test only the most promising alternative first.
Stop early if no credible net benefit remains. Compare total implementation and
maintenance complexity, dependencies/setup, operator steps, failure modes, test
upkeep, downtime and end-to-end upgrade/recovery time at a named state size;
record bytes/I/O, CPU/RSS and backlog effects when applicable. Faster snapshot
creation alone is not enough. Report a short keep/change recommendation, actual
commands/observations, setup costs and remaining uncertainty. A keep result
needs no manufactured implementation PR. No study result here is production
qualification, a release or permission to format a device.

## Proposed Regression Routing and Evidence Reuse

This is a proposal for later reviewed adoption; all existing required gates,
risk tiers and `development/v1` Preflight / Build and Unit checks remain intact.
`scripts/ci:classify` still selects affected local scope. This contract creates
no classifier lane or every-PR filesystem matrix.

| Trigger | Proposed recovery evidence |
| --- | --- |
| Lifecycle/admission/health decision, backend, mount/layout, DB-file reference contract, checkpoint/restore/journal/capsule or durability behavior changes | Full affected storage recovery campaign: contract section 6's single-instance/sibling proof, complete DB recovery files/payloads, catalog/key/identity pairing, every-writer fence, durable image pairing, interruption cuts and missing/partial/corrupt/space failures. Include CameraAgent unprotected failures and no downgrade/daily command. Qualify the exact changed topology/candidate; do not infer support for an untested filesystem. |
| Schema or configuration change affecting checkpoint contents or recovery invariants | Treat as a recovery-boundary change above; prove supported forward migration, restored old image on its own checkpoint, reference and capsule correctness. Current old-reader tests remain until replaced by reviewed policy. |
| Other code, UI or documentation changes with unchanged recovery boundaries | Focused affected tests plus existing mandatory gates. Reuse applicable recovery evidence with the original source/image/backend/kernel/mount/tool identities, state scale, checksums, invariant coverage and limits, and explain why the new diff cannot invalidate it. Missing or changed relevant identities require new evidence. |

The adoption PR records selector output, changed paths/boundaries, reused report
links and identities, checks actually run and remaining limitations. No unrun
test is a pass. New mandatory campaigns or any reduction of required checks
need their own reviewed policy/implementation change.
