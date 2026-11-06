# Upgrade recovery: runtime complexity and recurring cost (#1615)

2026-10-23. Source `58164c350bb7a1a9f977b548facef4c4c70868a0`,
`development/v1`; analysis and disposable software measurements only. This
supplements, rather than reruns or rewrites, [#1607](cameraagent-filesystem-decision-1607.md)
and [#1608](logichost-upgrade-recovery-1608.md). The
[approved #1606 contract](../hvo-install-contract.md#skymonitor-approved-target-upgrade-time-checkpoint-recovery)
is the common policy for every proposed option.

## Recommendation and accepted tradeoffs

**Simplify the policy and recovery protocol on the existing storage primitives
first (option 2).** Retain SQLite online backup and explicit full backup/restore;
complete the inventory, writer fence, physical-reference protection and exact
checkpoint/image/config/catalog pairing. Stop retaining a post-commit rollback
transaction. Replace universal previous-reader proof with forward-input and
paired-checkpoint recovery proof in that implementation, then adopt affected
evidence reuse separately. Neither gate reductions nor runtime changes land here.

This is a positive choice: current copies are self-contained and already have
hash/integrity checks, staged replacement and directory synchronization; they
work without converting existing ext4 installations or giving the lifecycle
process filesystem-provisioning authority. The observed current-source
small-state migrating operation takes under a second in a fake-client harness.
The expensive repeated work in the PR sample includes builds, whole Unit runs,
real-container qualification and review corrections, rather than a measured
multi-second checkpoint bottleneck. Most policy savings are available with
either storage mechanism.

**A controlled Btrfs instance unit remains a credible alternative, with a real
advantage:** it can replace per-store restore ordering and mutable-file copying
with one retained tree and selection protocol, and intrinsically retains covered
payload extents. Its primitive experiment below measures that opportunity. The
foregone benefit is simpler *local contents restoration*, especially as mutable
files or DBs grow. We accept continued DB enumeration, staging, sidecar handling
and file pinning for now. We do not conclude that the filesystem has no benefit.
Its net maintenance benefit across the actual install envelope is provisional:
a privileged provisioner, conversion, mount/topology admission and an unavailable
capability branch remain, alongside the shared fence and recovery journal.

The smallest next product PR is a CameraAgent **sealed recovery inventory**:
record every DB, configured reference root, mutable config/key file and exact
predecessor image/catalog identity in the authenticated checkpoint; restore from
that inventory rather than rediscovering roots through candidate-written schema.
Prove missing payload/key and wrong-pair refusal plus recovery after an unreadable
candidate journal. Keep current public commands and mandatory guards. This
bounded seam removes a named recovery dependency before attempting a backend.

The subsequent pending-upgrade transaction must finish the minimal external-fact
capsule outside reverted roots, all-writer fence through candidate health and
durable commit before admission. It must durably warn/record unprotected creation
failure and leave an unprotected changed-state failure stopped. Protected failure
restores only its paired predecessor. Remove retained post-commit toggling and
its tests only when replacement proof passes. Coordinate #893's CLI parity and
#1292's host policy without taking their work. These are proposed slices for the
affected stories, not a deployment or a claim that #1606 is already delivered.

Confidence: high in the source/gate accounting and shared obligations; medium in
the direction to simplify current primitives first; low in production-scale RTO
and comparative engineering-hour estimates. Reverse the backend recommendation
if a bounded implementation on one controlled Linux instance demonstrates fewer
recovery-specific paths/tests after including provisioning/fallback, or if
representative DB/mutable-file copying dominates a stated pause budget. The
smallest remaining experiment is the same paused candidate with real health and
an interruption at each tree-selection boundary, on copy versus Btrfs, including
one unavailable-capability case. No broad filesystem matrix is needed.

## Equal policy, unequal delivered guarantees

All eligible options have no downgrade or post-commit recovery window, an exact
pending checkpoint/predecessor pair, complete mutable-state/reference coverage,
durable decision before new acknowledged work, and independently supported
explicit disaster recovery. CameraAgent may proceed warned and unprotected;
LogicHost has no equivalent approved unprotected shortcut. These differences
must remain visible to the operator.

| Option | Observed implementation | Work charged before equivalent #1606 protection | Recurring advantage and accepted disadvantage |
| --- | --- | --- | --- |
| 1. Delivered baseline and actual gates | Pure image swap when no declared migration; selected SQLite copies otherwise; retained rollback; explicit backup separate | Complete file/config/key inventory, external-root closure/pins, all-writer fence, verified external facts, exact pair and protected/unprotected receipt. Remove universal downgrade/old-reader obligations under the common policy. These are **not free baseline capabilities**. | Reuses tested recovery and broad installation support. Delivered exclusions and best-effort carry cannot claim complete recovery; retaining unnecessary post-commit procedures adds ongoing work. Not selected unchanged. |
| 2. Same primitives, narrower transaction and affected evidence | Proposed product change; current-source timings below measure delivered code only | Same common completion; simplify retained transaction/carry and recovery inventory, stage mutable files, qualify forward migration and exact predecessor restore | Retains self-contained DB verification, portable ext4 layout and existing backup integration. Still owns selected-copy/file staging, sidecars and physical pin lifetime. Policy and evidence reuse remove obligations independently of filesystem choice. Selected first. |
| 3. One controlled Btrfs instance unit | Disposable primitive prototype below; no product backend | Same common completion, plus controlled provisioning/conversion, nested/external mount rejection or explicit coverage, privileged setup, owned selection journal and bounded space/cleanup behavior | Restores covered DB/WAL/files as one tree; removes local per-store restore/staging implementation in an all-Btrfs-only product. Supporting current installations too retains those copy paths, so it adds a capability branch rather than deleting all code. Forego this possible local simplification pending the resolving experiment. |

Option 1 is the measured reference, not a weaker-protection competitor scored as
equivalent. Normalizing its procedures to #1606 moves it toward option 2. Neither
alternative gets exclusive credit for removing old-reader obligations or the
full archives already removed from automatic upgrades by
[PR #1340](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1340) and separated
into explicit operations by [PR #1369](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1369).

## Runtime paths, failures and what can actually disappear

Source references below are relative to the recorded source SHA. The principal
implementation is [CameraAgentLifecycleManager.cs](../../src/HVO.SkyMonitor.Deployment.Cli/CameraAgentLifecycleManager.cs),
especially `ChangeImageAsync`, `ResolveRetainedSnapshotAsync`, `RestoreStoresAsync`
and `RecordRestorePlanAsync`; admission is in its
[Admission partial](../../src/HVO.SkyMonitor.Deployment.Cli/CameraAgentLifecycleManager.Admission.cs).

| Delivered path and test evidence | Option 2 disposition | Option 3 difference, under the same policy |
| --- | --- | --- |
| Candidate declaration/state preflight; `CameraAgentStateMigration.DeclaredChanges`; `UpgradePreflightTests.StateMigration.cs` | Keep supported forward inputs and actual on-disk checks. A declaration alone cannot establish that mutable config/files are unchanged. Checkpoint-free pure swaps need that proof; do not carry today's heuristic into a stronger guarantee silently. | Still needs forward-input/mount preflight. Tree snapshots may make always-checkpoint simpler than a migration heuristic, but that is an implementation choice to measure. |
| `Prepared` recovery records and `MutationStarted`; `Drained`; stop; optional `StateSnapshotRecorded`; candidate start and restart; health; `CandidateVerified`; manifest/result + `Committed`; resume | Keep durable intent, writer fence, exact image/config/catalog identities, health and commit/resume replay. Audit every writer, not just capture. No full state machine disappears. | Same lifecycle states plus capability/topology verification and snapshot/selection intent. Snapshot creation alone does not implement admission or candidate health. |
| Failure before commit restores exact prior Compose/manifest/result and selected stores; interruption authenticates pending snapshot and restores before retry; `SnapshotRecovery` and `RecoveryReads` tests | Keep corrupt/wrong-instance/partial checkpoint refusal and interrupted restore. Read recovery inventory/facts from the authenticated point and capsule rather than arbitrary candidate schemas wherever possible. This can remove degraded candidate-profile parsing from upgrade recovery, once external roots and facts are sealed. | Local DB/WAL/file restoration can become tree clone/selection. Still must authenticate the point and journal selection durably, recover a split selection, retain logs and stay fenced on uncertainty. |
| Committed upgrade retains `PreviousImage`, `PreviousStateSnapshot`, previous Compose and snapshots the *current* stores again when rolling back, permitting a later roll forward | Remove this post-commit toggle and its second snapshot/retained selection from upgrade-only behavior. Committed resume may finish admission, never restore predecessor state after acknowledged work. Explicit archive restore remains a different command. | Same removal, caused by policy. Btrfs does not earn it a second time. |
| `CameraAgentStateSnapshot.CreateAsync/VerifyAsync/RestoreAsync`: ten static DB locations plus discovered outboxes; online backup, manifest hashes/facts, staged copies, absent-store removal and stale sidecar cleanup | Keep and complete these mechanisms; add complete mutable-file coverage. `SqliteStores` excludes orbital, space-weather and calibration stores for different historical reasons. Replace broad exclusions with minimal non-rewindable facts or fence proof. | Whole covered-tree recovery removes the per-store replacement/sidecar removal loop, new-store omission list and ordinary mutable-file copy ordering *for this backend*. Keep SQLite integrity/logical checks. DB-specific code remains for explicit archives, supported old archive reads and any copy-capable installation. |
| `CameraAgentStateCounters*`, `CameraAgentStateCountersAuditTests`, `RawCaptureIngressTests.SnapshotRollback`, provider retry floors, exported identities | A proved no-new-external-facts fence can remove candidate-state best-effort counter reading during an upgrade. Otherwise preserve minimal verified facts in the external capsule; never retire all counter audits or archive credential/counter reconciliation. | Exactly the same requirement. Rewinding an entire unit without a capsule would also rewind retry floors/revocations. |
| Physical payload lifetime, maintenance/deletion lease, configured external outboxes; #1059's physical-product negative control | Keep a durable reference inventory and retention hold; hashes prove content, pins prevent deletion through recovery. Full copying is not required for immutable payloads. | A unit retains its own covered extents, reducing internal pin bookkeeping. External roots, nested subvolumes, shared catalogs and external acknowledgements still require explicit inclusion/pins. Sibling isolation is by UUID unit, never whole-product revert. |

Candidate readiness currently includes authenticated owner/catalog verification
and a second start, plus `ConfirmDrainedAsync`/continuity comparison; it is not
yet proof of #1606's semantic persistence/retrieval probe or every-writer fence.
Neither successful test fakes nor our primitive restore establishes that proof.
Forward schema/layout evolution still needs declared supported inputs, coherent
recovery and interrupted migration handling. It no longer needs every old binary
to understand new tables. Existing archives still need their published readers.

## Current-source and candidate measurements

### Delivered lifecycle, three small-state trials

Reused the unchanged `UpgradeVolumeTests` harness and real SQLite/filesystem
operations at the source above, SDK 10.0.402, Linux x64 on ext4. It generates
192 retained payload files, 69,206,016 bytes, plus synthetic Identity and
200,000-row raw journal stores: total measured instance 195 files,
90,844,280 bytes. Same retained payload tree for all three trials; canonical
payload inventory digest
`081ea37f03db2d003be02eb424c5814cbde38e41a5bfd7df7183dd80c3336b26`.
The journal uses `randomblob`, and is freshly seeded per trial; its exact
temporary DB bytes were not retained. Source/harness hashes and outcomes are in
[volume-result.json](evidence/issue-1615/volume-result.json).

| Whole fake-client operation, seconds | Trial 1 | Trial 2 | Trial 3 |
| --- | ---: | ---: | ---: |
| Upgrade, unchanged contract | 0.16 | 0.19 | 0.17 |
| Rollback, unchanged contract | 0.06 | 0.07 | 0.06 |
| Upgrade, declared migration | 0.14 | 0.13 | 0.13 |
| Rollback, declared migration | 0.37 | 0.38 | 0.37 |

All twelve operations report `completed`, with zero full backups. Process peaks
are 151,863,296 / 137,777,152 / 137,601,024 bytes. The reported cgroup peak is a
shared ancestor's lifetime value, **not** experiment memory; no CPU/memory quota
was imposed. GNU time in the retained JSON covers test invocation/build/setup,
not operation-only CPU or physical I/O. Two-decimal operation timings are not
percentiles. Pause/drain, Docker, health and resume are fake clients; the journal's
paths/hashes do not establish payload reference closure. No live readiness,
writer-pause, backlog, Pi, USB/NVMe or large-DB guarantee is inferred. Historical
2/6/20 GiB #1340 results remain separate observations in #1607, not rerun data.

Reproduction, with `study_root` from `hvo scratch HVO.SkyMonitor 1615 evidence`:

```bash
flock -o /tmp/hvo-heavy-gate.lock timeout 1200 bash -c '
  set -e
  for trial in 1 2 3; do
    env -u HVO_PRODUCTION_CATALOG_BUNDLE \
      DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
      HVO_UPGRADE_VOLUME_SOURCE="$study_root/volume" \
      HVO_UPGRADE_VOLUME_GIB=0.0625 \
      HVO_UPGRADE_VOLUME_REPORT="$study_root/volume-$trial.json" \
      /usr/bin/time -v -o "$study_root/time-$trial.txt" \
      dotnet test tests/HVO.SkyMonitor.Deployment.Cli.Tests/HVO.SkyMonitor.Deployment.Cli.Tests.csproj \
        --configuration Release -nodeReuse:false -p:UseSharedCompilation=false \
        --filter FullyQualifiedName~UpgradeVolumeTests
  done'
```

Export `study_root` before invoking the shell. Setup generation/copying is
outside the harness's operation timings. Inspect JSON outcomes, not just the
test exit code: the harness records caught operation failures without asserting
success.

### One controlled Btrfs unit, bounded primitive comparison

The [executed probe](evidence/issue-1615/unit-probe.py) and
[result](evidence/issue-1615/unit-probe-result.json) compare SQLite online backup
plus two mutable-file copies against a read-only Btrfs unit checkpoint and
writable-clone selection. Both run on the **same Btrfs image**, with the same
upgrade-only policy assumptions; this isolates primitives, not ext4 versus Btrfs
device performance. One new 768 MiB regular image file on ext4, Linux
6.8.0-142-generic x64, btrfs-progs 6.6.3, Python 3.12.3 and SQLite 3.45.1.
The image was mounted with `user_subvol_rm_allowed`; no existing device was
formatted. [Reproduction commands](evidence/issue-1615/README.md#disposable-btrfs-setup)
include the heavy lock, time limit and exact privilege boundary.

The controlled unit contains a 24,449,024-byte DB with 200,000 rows, 65,536
mutable config/key bytes and the same 192 payload files (69,206,016 bytes).
Rows actually reference those files and their hashes. Every checkpoint has a
committed **4,152-byte WAL**, with a changing generation marker. The candidate
rewrites row hashes, drops the predecessor table, creates an incompatible table,
sets the marker to -1 and changes config/key contents. Each of six restores proves
integrity, schema 12, row count, ordered row digest, every physical reference hash,
config/key hashes, the checkpoint's exact WAL marker and the unchanged separate
subvolume's static marker. No running sibling or real host readiness is simulated.

| Measured primitive, median seconds (range, three trials each) | SQLite/file copy | Btrfs unit |
| --- | ---: | ---: |
| Checkpoint, including filesystem sync | 0.048710 (0.043596–0.052555) | 0.003925 (0.003424–0.004045) |
| Incompatible candidate mutation, including sync | 0.221669 (0.213200–0.236181) | 0.221806 (0.211071–0.235514) |
| Restore/selection, including sync | 0.031504 (0.029501–0.032288) | 0.004512 (0.004061–0.004577) |
| Full logical/file verification after restore | 0.419298 (0.417046–0.425020) | 0.542470 (0.535557–0.552430) |
| Checkpoint + restore + verification, per-trial sum | 0.499512 (0.498117–0.501889) | 0.550907 (0.543042–0.561052) |

Btrfs removes about 72 ms of median checkpoint/selection primitive work here;
it does **not** demonstrate lower verified recovery cost in this tiny fixture.
Its verification includes opening the recovered WAL-bearing clone, while the
copy is already self-contained. Fixed copy-then-snapshot order, three warm trials
and loop-on-ext4 storage limit inference; this is not a production slowdown
verdict or statistically established write-overhead comparison. Both mechanisms
still verify the point and application before admission.

Filesystem-wide used allocation increased **24,580,096 bytes** at each copy
checkpoint, versus **32,768 bytes** at each snapshot. Candidate mutation with
the snapshot retained added **24,592,384 bytes** each trial; the copy path's net
used allocation changed slightly downward (61,440–131,072 bytes). These counters
include metadata and asynchronous reclamation, not exclusive referenced bytes
or device write amplification. Snapshots trade upfront copy space for retained
changed extents; they do not eliminate headroom or cleanup policy.

The entire accepted probe, including setup and all six cases, took 5.46 s,
3.33 s user / 1.20 s system CPU, largest-process RSS 23,712 KiB. GNU time records
549,192 input and 1,979,968 output filesystem blocks; loop/host accounting and
setup are included, so neither per-upgrade physical I/O nor an isolated CPU
benefit is measured. There is no live arrival rate or backlog in either probe.

An initial attempt failed on ordinary-user deletion of a read-only checkpoint;
mount/cleanup and usage inspection used explicit privilege in the retry. A pilot
then passed restoration but its no-op update emitted no WAL for Btrfs, so those
timings were excluded. The accepted new-image run uses closed connections and
the asserted committed marker above. Both earlier images and the accepted image
were unmounted; scratch files are issue-owned disposable resources.

This validates the local contents-restoration advantage, not the product state
machine. The prototype's two renames have no durable selection journal; neither
path implements writer admission, crash/power-cut recovery, the external-fact
capsule, exact image/catalog authentication, mount failure/space handling or
semantic health. It does not test large WAL, external roots or a running sibling.
It therefore cannot claim full #1606 recovery on either mechanism. Btrfs's
[documented nonrecursive snapshot boundary and shared extents](https://btrfs.readthedocs.io/en/stable/btrfs-subvolume.html#nested-subvolumes)
still require topology checks; its unit-local simplification does not cover
external SQL/objects or replace disaster recovery.

## Observed recurring validation cost

The named convenience sample is two lifecycle/storage PRs (#1340 image swap,
#1369 explicit backup/restore) and one ordinary configuration/UI PR (#1614).
It is not a representative estimate of arrival rate or causality. All were
merged; all sixteen branch workflow runs were completed, successful Development
v1 runs, attempt 1. [pr-costs.json](evidence/issue-1615/pr-costs.json) retains API
run/job/step times, URLs, exact heads and reduced final artifact manifests.

| PR | Runs (draft included) | All job allocation seconds | Sum of run critical-path seconds | Final ready run critical path | PR opened → merged elapsed |
| --- | ---: | ---: | ---: | ---: | ---: |
| [#1340](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1340) | 8 | 947 | 975 | 387 s | 14 h 6 m 29 s |
| [#1369](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1369) | 6 | 750 | 770 | 462 s | 9 h 13 m 15 s |
| [#1614](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1614) | 2 | 565 | 574 | 511 s | 15 m 4 s |

Job allocation time is the sum of running job intervals, **not CPU seconds**;
skipped jobs count zero even where API timestamps are reversed. Run critical
path is creation through latest active job completion, including queue and
inter-job waits, not the sum of potentially overlapping jobs. These branch runs
do not overlap one another. Initial queue for the final runs was 3/3/4 s;
remaining orchestration/wait is included only in critical path. PR elapsed
includes author work, idle periods, review and gates; it is **not** a measurement
of review wait or active development hours. No workflow run is being watched or
launched for this sample.

| Final run and API job seconds (Preflight / Build and Unit) | Artifact restore / build / Unit seconds | Interpretation |
| --- | --- | --- |
| [39082396293](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/39082396293), 53 / 329 | 1.98 / 122.46 / 193.11 | Lifecycle PR still gets the common v1 build/Unit gate. |
| [39192227439](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/39192227439), 68 / 389 | 4.58 / 142.48 / 228.39 | Explicit archive contracts contribute, but no per-test critical-path attribution follows from these totals. |
| [40004104603](https://github.com/HualapaiValley/HVO.SkyMonitor/actions/runs/40004104603), 77 / 428 | 2.01 / 146.00 / 266.27 | Ordinary UI/configuration also pays for the entire solution. Filesystem replacement alone saves none of this fixed gate. |

Stage times come from artifact GNU time; job steps rounded to seconds are also
retained. PR workflow `head_sha` names the PR head, while artifact `headSha`
names the checkout's synthetic merge. For #1614 the tested merge was `c002bff2`,
parents `758208bc` and `7ed6a479`; do not label its TRX as a direct PR-head run.

The #1614 artifact contains ten `PreviousReleaseStoreReadabilityTests` cases
with summed duration **13.9581923 s**, six
`CameraAgentSnapshotRollbackExceptionTests` cases totaling **0.0352838 s**, and
seven `CameraAgentStateSnapshotTests` cases totaling **2.4627093 s**; see
[selected-unit-timings.json](evidence/issue-1615/selected-unit-timings.json).
Test-duration sums overlap other work and are not incremental gate savings.
Even removing every reverse-read case would not remove restore/build; replacement
forward/recovery tests cost time too. The artifact's whole `LifecycleContractTests`
class includes 1,001 results summing 111.33 s, not 1,001 upgrade-only tests to delete.

Available local ledgers separate the larger repeated work:

- #1340's [initial PR body](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1340#tests)
  records deployment shards at **692 s**, explicitly run despite the classifier
  not selecting them. [Correction 1](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1340#issuecomment-6235883430),
  [2](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1340#issuecomment-6238304295),
  [3](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1340#issuecomment-6240960612)
  and cap exceptions record repeated builds/Unit/whole Integration and installer
  campaigns. Their individual local durations are **unknown**, not zeros or
  inferred from comment timestamps. The final base-sync addendum reuses unaffected
  gates and reruns affected central/architecture gates.
- #1369's [Tests ledger](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1369#tests)
  names nine numbered local gates plus intermediate records, separating
  corrections and merged heads. Seven distinct gates include installer times:
  **278, 293, 292, 286, 305, 422, 423 s**, totaling **2,299 s (38.32 machine-minutes)**
  of that campaign alone. The body repeats Gate 6 (305 s); it is counted once.
  Gate 5's
  failed owner-contract diagnostics and Gate 7's campaign have no duration.
  Do not add these seven runs to a 423 s "full local gate": restore, Debug/Release,
  format, audits, project Unit/coverage and several whole-Unit reruns are separate,
  mostly untimed. These corrections repaired real recovery/security failures;
  neither backend permits skipping their reproductions.
- #1614's [validation](capture-scalar-options-1563.md) and PR body record affected
  tests, Release build, format, guards and 7,090 CameraAgent Unit passes with one
  skip; a final affected 118-case run repairs two fixture failures. Local wall/CPU
  durations are not recorded. Independent review reports **172.964 s**;
  request-to-report was 297 s, including acquisition/relay. Do not count both.
  #1340/#1369 have correction reviews and base sync, but isolated review/queue
  wait and active-author times are not measured.

No release-qualification run belongs to these branch-run totals. Tier C/M local
Integration/fault evidence, `ci.yml` promotion/full qualification, release-image
and exact-artifact deployment acceptance remain separate obligations. We reused
#1059's fault report and #1608's SQL probe; we did not launch full suites merely
to time them. v1 is integration evidence, not production qualification.

## Proposed test-policy delta, with replacement proof

These are **later reviewed changes**, not current waivers. The classifier still
selects local scope; all four CI-control guards and v1 Preflight / Build and Unit
remain. There is no proposed every-PR filesystem matrix.

| Trigger | Retire, reuse or run less often | Replacement invariant and adoption evidence | Cause of potential saving |
| --- | --- | --- | --- |
| Ordinary UI/code/docs with unchanged lifecycle, storage, schema and reference contract | Reuse exact unchanged #1059 fault, #586 object, installer/backend campaign evidence. Do not run unselected `scripts/test:deploy-environment --parallel` or `scripts/test:deployment-installer` gratuitously. Keep focused tests and required gates. | Record classifier range/output, pinned report source/image/tool/topology/scale, and why the changed boundary cannot invalidate it. Any missing relevant identity reopens affected evidence. | Process routing, available on both mechanisms; the 692 s unselected #1340 shard run demonstrates an avoidable instance, not a guaranteed saving per PR. |
| Supported forward schema/config/layout change | Replace the reverse assertion in `PreviousReleaseStores_OpenedByThisRelease_StayReadableByThePreviousRelease` and its old-reader negative controls. Rework `CurrentStores_AreThePreviousReleaseStoresPlusTheStoresThisReleaseAdds` into complete current recovery inventory; do not just delete inventory coverage. Retire `CameraAgentSnapshotRollbackExceptionTests` exemption matching and reverse-reader fixture maintenance once no supported caller needs them. | Forward input fixture reaches candidate schema; failed candidate restores complete paired predecessor; wrong pair/missing key/reference rejected; all externally visible facts conserved. Retain any frozen fixture still needed for supported forward inputs or published archive reading. Existing release labels/preflight checks needed for forward admission remain. | #1606 policy, not Btrfs. Measured reverse-test durations above bound only a small portion; no guaranteed 14 s critical-path win. |
| Lifecycle/admission/backend/layout/durability change | Keep full **affected** recovery/fault campaign; replace post-commit rollback/roll-forward cases in `LifecycleContractTests.SwapUpgrade.cs`, `.StateMigration.cs`, `.RecoveryReads.cs` with no-post-commit-restore/admission cases. Keep pending restore/resume, wrong-instance and missing/corrupt/partial/space failures. | Full logical point and sibling proof, WAL/recovery files, reference hashes, all-writer fence, exact image/config/catalog/key pair, external capsule, durable commit/admission, interrupted selection and CameraAgent protected/unprotected paths. Btrfs adds wrong/nested/unavailable mount, COW headroom and cleanup interruption cases. | Policy removes toggles; backend changes local restore tests only. Copy integrity/sidecar tests remain for archives/copy support; snapshot needs its own selection tests. |
| Release/promotion qualification | Reuse unchanged reports only with explicit applicability; keep `ci.yml` selected Integration/coverage/architecture/publish/migration and exact-candidate installer/release gates. Retain explicit `BackupVaultTests` and `LifecycleContractTests.BackupRestore*` archive/security/credential/retention/restore tests. | Real candidate health and supported topology, complete restore/DR drill, supported forward inputs, original checksums and exact artifact identities. A local snapshot is not off-host backup. | No unconditional saving. Narrow supported topology may reduce a future matrix but is a product support restriction, not free performance. |

The [#1059 negative control](artifact-history-1059-fault-evidence.md#explicitly-unqualified-acceptance)
and current `CameraAgentStateSnapshotTests` demonstrate why metadata verification
and broad store exclusions cannot replace these invariants. Old checkpoint/backup
reader compatibility is separate from old binaries reading newly migrated state.

## Deployment, ownership and reliability costs

Current CameraAgent operations run as its Docker-capable service account, with
the existing narrowly scoped preparation helper for protected directories.
Option 2 preserves existing Linux ext4 layouts and external-root ownership
checks; completing reference pins and mutable-file inventory is real new work.
The installer publishes Linux x64/ARM64 targets; this study runs only x64.
Current host policy checks one mount path, not a filesystem UUID, and explicit
restore binds the recorded Docker daemon/root. A Btrfs capability check must
therefore verify the actual unit/mount, rather than trusting existing host policy
as snapshot authentication. It does not extend the supported hardware envelope.
Local copied DB corruption can be detected before replacement, but copies on
the same disk still do not provide media-loss protection. Full archives and
off-host retention remain operator responsibilities.

Current copying costs scale with selected DB bytes, and restore adds a staged
copy plus verification. `EnsureUpgradeStorage` budgets four times selected DB
bytes plus the carry journal, image archive and 256 MiB headroom; that is an
estimate, not reserved capacity. `CameraAgentStateSnapshotRetention.PruneAsync`
is called by the explicit backup path, not by every successful image upgrade.
Unreferenced checkpoint bytes can therefore accumulate between backups. Option 2
must implement safe post-commit owned-point cleanup, not assume existing retention
is already an upgrade-only lifetime. Option 3 similarly retains changed extents
until the pending point is safely deleted; COW does not make growth free.

Btrfs option 3 deliberately targets **one controlled Linux instance unit**, not
every current deployment. It needs a privileged install/provisioning step and
supported kernel/tools/mount configuration; routine snapshot commands should use
the least required authority after provisioning. Root filesystem conversion,
Pi media/USB/NVMe, bind mounts, nested units and external stores need explicit
support decisions and qualification. No per-upgrade root grant is assumed.
An ext4 installation remains valid but cannot claim this protection; it either
uses a separately supported copy implementation or takes the warned manual path.
Choosing manual fallback reduces automated protection and restoration confidence
there; that is exposed, not hidden in a cost score. No snapshot after failed
creation means no automatic predecessor restart on modified state.

For either option, uncertain restoration remains stopped with exact diagnostic
and recovery-only instructions. Recovery must not depend on a damaged candidate's
schema or an unreadable provider floor. Journals, receipt/capsule and immutable
image/catalog references live outside restored roots. Stale snapshot cleanup
cannot erase an active pending point. Space exhaustion can affect a sibling on
a shared filesystem; instance restore isolation does not imply capacity isolation.
Snapshots do not remove off-host disaster recovery, key custody or archive tests.

## LogicHost: SQL choice is a separate decision

There is **no delivered LogicHost upgrade runtime**. Do not project CameraAgent's
retained rollback complexity, downtime or measured subsecond swaps onto it.
All coherent lifecycle/cutoff work below is estimated future work for
#1244/#1245, with exact-candidate acceptance in #540/#541.

| Choice | Evidence and advantage accepted | Disadvantage accepted / forgone benefit | Reversal condition and confidence |
| --- | --- | --- | --- |
| COPY_ONLY + existing offline object recovery; selected | #255 actual isolated restore/CHECKDB and canonical facts; #586 object markers/interrupted restore; reuse existing toolkit. COPY_ONLY preparation does not alter the differential base. | DB copying scales with DB size; complete object copy in today's tool scales with retained bytes. Coherent SQL/object/config/Identity/OpenIddict/key/catalog cutoff, recovery journal and admission still must be implemented. Native snapshot's potentially smaller DB pause is forgone. No measured whole-application RTO. | Medium architectural confidence, low latency confidence. Reverse if DB backup/restore dominates a stated real cutoff budget and native revert plus post-revert full backup yields lower total cost on the actual supported topology. |
| Native SQL snapshot/revert + **same** object/config recovery; not selected now | #1608's three 4 MiB Developer-edition trials: create 101–122 ms; revert 308–359 ms; CHECKDB/semantic verification 168–217 ms. Neighbor intact, incompatible table removed. Genuine DB-local simplification; no current COPY_ONLY latency comparison. | Sparse files grew from 16 KiB to about 5.18 MB after mutation. Every post-revert log backup failed (4214/3013); a new full backup reseeded the next trial. Additional snapshot/file eligibility, space, permissions and log-chain repair support. Objects/config remain separate. Existing off-host backup still required. | Feasible SQL primitive, low confidence in net recurring savings. A matched COPY_ONLY versus native whole-application cutoff/restore trial, including reseed and external objects, is the smallest resolving experiment. Unknown does not mean no benefit. |

The DBA/service boundary is external to the LogicHost filesystem in shared SQL
topologies. Neither Btrfs on the app host nor a native SQL snapshot makes object
generations, config and key rings atomic with SQL. Target only the application
database and instance's object unit, never shared server/sibling state. Keep
#1608's audited restore authority boundary and service-owned paths; runtime
does not receive sysadmin. Native snapshot eligibility must be proved for the
actual edition/topology, rather than inferred from the Developer probe.

The SQL numeric evidence above is inherited, not rerun. Microsoft documents
[COPY_ONLY preparation](https://learn.microsoft.com/en-us/sql/relational-databases/backup-restore/copy-only-backups-sql-server?view=sql-server-ver16)
and [revert's broken log chain and reseeding](https://learn.microsoft.com/en-us/sql/relational-databases/databases/revert-a-database-to-a-database-snapshot).
Neither ordinary restore nor keeping COPY_ONLY guarantees arbitrary log-chain
continuity; #1244/#1245 must verify the resulting recovery fork and DBA procedure.
The policy can eliminate a proposed mandatory *off-host full archive per upgrade*,
but existing object-copy tooling still needs a completed verified local copy
unless persistent generation pins are implemented and qualified separately.

## One-time versus recurring cost and sensitivity

No active-author-hours ledger exists for this sample. Common #1606 completion,
initial qualification and migration are not measured. Option 2 pays them plus
copy/file/capsule completion; option 3 pays them plus provisioning, conversion,
selection and fallback qualification, while potentially avoiding some copy work.
LogicHost's SQL/object orchestration is common future effort. These differences
cannot honestly be represented as measured saved engineering hours.

Use separate currencies and a simple sensitivity model:

- **Machine time:** one avoided 423 s installer campaign saves 7.05 machine-minutes;
  one avoided 692 s unselected shard campaign saves 11.53. At an *assumed* five
  applicable changes/month, one such avoidance/change saves 35.25 or 57.67
  machine-minutes/month. They are alternative examples, not additive critical
  paths. A backend swap does not automatically avoid either campaign.
- **Engineering/support time:** let `E` be net extra Btrfs implementation,
  conversion and qualification hours after subtracting copy work avoided;
  `d` net developer/support hours saved per applicable change after maintaining
  new topology/fallback tests. Break-even changes = `E/d` when `d > 0`; at
  `d <= 0` there is no recurring-time break-even. Illustrative **estimates**, not
  bids: `E=16/40/80 h`, `d=0.1/0.5/1 h` yields 16–800 changes; the middle case
  is 80 changes, 16 months at five/month. If copy completion is costlier than
  Btrfs (`E < 0`) and ongoing cost also falls, Btrfs wins immediately. This
  explicitly allows reversal on complexity rather than installation effort alone.
- **Upgrade runtime:** `U × (copy pause − snapshot pause)` gives lifetime pause
  saved for `U` upgrades. Real pauses are unknown. If a future paired trial showed
  a 0.1 s gain and six upgrades/year, that is 0.6 s/year; a 30 s gain is 180 s/year.
  These assumed examples cannot monetize outage risk or be equated to staff hours.
  Failure probability × recovery duration and operator actions also matter and
  are unmeasured; no reliability score hides them.

Observed activity is two lifecycle PRs with six and eight branch runs and nine
local gate records in #1369, not evidence for the five/month assumption. Review
iterations exposed repeated candidate-schema/counter and archive-credential
hazards; removing unnecessary post-commit recovery can reduce those obligations,
but explicit archives retain many. Evidence reuse and a smaller pending protocol
have the clearest near-term recurring-work benefit. A filesystem-specific unit
may still win on local recovery simplicity at larger mutable-state scale; record
that uncertainty until the narrow integrated experiment distinguishes it.

## Disposition and validation boundary

#1511/#531 and CameraAgent #1059/#1060/#1300, LogicHost #1244/#1245/#589/#591
consume this comparison alongside #1606–#1608 before committing overlapping
designs. The choice is **simplify current primitives first; retain a reversible,
controlled snapshot alternative**. It clears the reassessment gate, not any
implementation acceptance. Unrelated features, clean installation, release
verification and strict existing-behavior CLI parity remain independent.

This PR changes evidence/planning only (Tier A, deep durability review,
`profile=development-v1`). Exact-range classifier, focused document/artifact
checks, control guards and final v1 CI are recorded in its PR ledger. Existing
mandatory gates, delivered behavior, topology support and previous evidence
remain in force. No backend rollout, live upgrade, hardware access, general
framework, release qualification or current CI waiver is claimed.
