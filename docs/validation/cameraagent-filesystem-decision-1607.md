# CameraAgent filesystem snapshot decision (#1607)

2026-10-23. Source: `a20d606ea764b2001409984980708f410a001d23`
(`development/v1`, including #1606). This is a bounded keep/change study, not
backend implementation or production qualification.

## Decision

**Keep the current filesystem/layout and selected-SQLite checkpoint machinery
as the implementation baseline. Do not adopt a filesystem snapshot backend.**
The benefit screen found no demonstrated net reduction in total upgrade/recovery
work that justifies provisioning and maintaining a different filesystem. This
clears #1607's ordering decision for overlapping #1059, #1060 and #1300 work;
those stories still own their acceptance and supported forward inputs.

Keeping the mechanism does **not** mean that today's selected-store snapshot
already meets #1606. Complete mutable-file/config/identity/key coverage, pinned
physical references, an all-writer fence, exact checkpoint/image/catalog pairing,
and durable protected/unprotected decisions remain required by the
[approved contract](../hvo-install-contract.md#skymonitor-approved-target-upgrade-time-checkpoint-recovery).
The [implementation map](../planning/skymonitor-upgrade-recovery.md) records these
gaps. No downgrade, scheduled snapshot, reverse migration or new routine CI
campaign follows from this decision.

## Delivered baseline credited

[PR #1340](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1340#volume-timings-before-and-after)
(#1314) removed automatic bulk-copy upgrades. Its existing `UpgradeVolumeTests`
uses real filesystem and SQLite operations with fake Docker, health and lifecycle
clients. Historical observations at a 200,000-row journal and 2.02/6.03/20.02 GiB
of retained raw/archive/derived files were respectively:

| Retained payload | Unchanged upgrade | Migrating upgrade | Migrating rollback | Process peak RSS |
| --- | --- | --- | --- | --- |
| 2.02 GiB | 0.13 s | 1.05 s | 0.24 s | 118 MB |
| 6.03 GiB | 0.13 s | 0.12 s | 0.22 s | 120 MB |
| 20.02 GiB | 0.16 s | 0.11 s | 0.22 s | 122 MB |

These are inherited individual host observations, not repeated percentiles or
live container readiness measurements. The original report identifies its
source, fixture and 1 CPU/1 GiB limits; its cgroup peak includes setup page cache.
No filesystem candidate receives credit for these delivered improvements.
[PR #1369](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1369) (#1353)
separately delivered explicit backup/restore. Full archives are not an automatic
prerequisite for each upgrade.

Current `CameraAgentStateSnapshot.cs` uses SQLite online backup, self-contained
copies, integrity/schema/row-count checks, SHA-256 manifests, durable directory
publication, staged restoration and stale-sidecar removal. The current
[runbook](../runbooks/deployment-installer.md#swap-upgrade-and-database-snapshots)
also records configured-root outbox discovery and the maintenance/deletion
barrier protecting physical files a restored DB can reference. Those safeguards
remain credited; capture pause alone is not proof that every writer is fenced.

[PR #1154](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1154) delivered
metadata/fact process-interruption evidence for #1059. Its
[physical-product negative control](artifact-history-1059-fault-evidence.md#explicitly-unqualified-acceptance)
shows metadata verification can pass despite a missing referenced product.
Therefore it cannot substitute for physical payload hashes or complete recovery;
#1059 remains open for its broader authoritative-history work. No rerun of that
unchanged campaign is needed to decide whether to add a filesystem here.

## Current-source checks and reproduction

The unchanged volume harness is
`tests/HVO.SkyMonitor.Deployment.Cli.Tests/UpgradeVolumeTests.cs`, SHA-256
`2d42d998bf0494e7793f6c3b1dbed93e51f4a28b1e41fb295753d8d6aefd408e`.
It still calls the actual lifecycle manager and SQLite checkpoint code; the
current snapshot source hashes to
`af8c3fc2315ea8b316abc99f99f328b4db1322754fca609ad11f397e8afc3ddd`.
The lifecycle has evolved since #1340, so its historical timings are credited
observations, not asserted current-source latency guarantees.

To reproduce a bounded current-source observation with the pinned SDK, create
an issue-owned scratch directory and use the existing harness:

```bash
hvo scratch HVO.SkyMonitor 1607 volume
# Set study_root to the directory returned above (not an existing instance).
flock -o /tmp/hvo-heavy-gate.lock timeout 900 \
  env -u HVO_PRODUCTION_CATALOG_BUNDLE \
  DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  HVO_UPGRADE_VOLUME_SOURCE="$study_root/volume" \
  HVO_UPGRADE_VOLUME_GIB=0.0625 \
  HVO_UPGRADE_VOLUME_REPORT="$study_root/report.json" \
  dotnet test tests/HVO.SkyMonitor.Deployment.Cli.Tests/HVO.SkyMonitor.Deployment.Cli.Tests.csproj \
    --configuration Release -nodeReuse:false -p:UseSharedCompilation=false \
    --filter 'FullyQualifiedName~UpgradeVolumeTests'
```

The harness generates a random payload tree when the source is absent, seeds
a 200,000-row synthetic SQLite journal, copies setup outside its operation
timings, and reports four lifecycle outcomes, seconds and process/cgroup peaks.
Inspect every outcome for `completed` and every `backups` count for zero: the
harness catches operation exceptions into its report, so a green test alone is
insufficient. It reports whole fake-client operations, not isolated writer pause
or real restore-to-ready. Exact dataset hashes and repeated current-source
measurements would need recording if used to justify a later adoption.

**Actually run for this study:** source/layout/test inspection, exact harness
and snapshot hashes, all four CI-control guards, operations documentation audit,
local link-target validation and whitespace checks. No .NET test or volume
measurement completed here: the optional focused regression command was
canceled while still waiting for the shared heavy lock after the benefit screen
concluded. No current-source runtime result is claimed. The baseline measurements
above are explicitly reused from #1340, as #1607 permits. The evidence-only
change is Tier A, `profile=development-v1`; the exact-range classifier output and
protected Preflight / Build and Unit results are recorded in the PR ledger.

## One-alternative benefit screen

The simplest promising alternative considered was one Btrfs subvolume per UUID
instance, containing its mutable `config` and `state`, with recovery records
outside it. No alternative filesystem was formatted, mounted or benchmarked.
The screen stopped before device setup; no missing runner is being treated as a
failed experiment or as evidence that Btrfs is slower.

Btrfs can share unchanged extents and capture a larger file set without copying
all payloads. Its [documented snapshot boundary](https://btrfs.readthedocs.io/en/stable/btrfs-subvolume.html#nested-subvolumes)
omits nested subvolume contents. Its subvolumes also share filesystem capacity.
Consequently a product-wide snapshot would have the wrong sibling/journal scope,
and per-instance adoption would add recovery-unit provisioning, nested/external
storage inventory and rejection, space/cleanup policy and interruption-safe
selection. These are engineering costs inferred from the documented semantics
and the [current layout](../runbooks/product-instance-layout.md#upgrade-recovery-boundary-approved-target),
not measured performance defects.

A filesystem snapshot would still need the same all-writer fence, semantic
health decision, immutable catalog/image pins, physical-reference proof and
non-rewindable fact treatment. Current excluded provider stores and best-effort
counter carry cannot simply be rewound with the rest of a subvolume. Completing
these contracts remains work for either mechanism. SQLite copies already avoid
bulk copying, so an instantaneous snapshot is not evidence of a useful total
upgrade-time reduction. There is no measured pause budget missed by DB copying
in the evidence used here.

No credible net advantage survived this screen. Under the owner's simplicity
stop rule, testing ext4/LVM or OpenZFS, constructing a backend matrix, or adding
filesystem-specific fault CI would add work without resolving a remaining
selection question. The decision is conservative and reversible by later
specific evidence; it is not a claim that all backends are equivalent.

## Remaining uncertainty and qualification boundary

The local harness uses synthetic stores and fake admission, Docker and health
clients. Its synthetic DB path/hash fields do not prove reference closure against
the generated payload tree. Real writer pause, container boot/readiness, semantic auth/persistence
health, physical power loss, Pi/USB/NVMe latency/durability, large DBs and the full
#1606 recovery set are unqualified. Snapshot creation alone cannot settle those.
No Btrfs checkpoint/write-overhead/retained-space/cleanup metrics, sibling restore,
nested-storage negative control or filesystem fault matrix was run or claimed.
The surviving-alternative acceptance steps in #1607 are therefore not selected.

Future recovery work should first close the inventory/fence/reference/pairing
gaps using existing primitives and immutable-file pins, without introducing a
new authoritative layout here. If a named workload later shows that copying
selected DBs or completing mutable-file coverage dominates end-to-end downtime
or complexity, reopen that specific decision with a guarded disposable probe.
Any adopted backend must then prove two-instance isolation, WAL/reference/hash
negative controls, unavailable/partial/wrong-mount/out-of-space failures,
interruption/resume, and the warned unprotected branch that remains stopped
following changed-state failure. Existing behavior and simulated tests do not
establish that new installer feature.
