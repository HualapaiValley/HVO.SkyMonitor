# Evidence reproduction and limits

Source and interpretation: [#1615 decision](../../upgrade-recovery-cost-1615.md).
`volume-result.json` records the actual existing-harness observations and source
hashes. `unit-probe.py` is an executed disposable primitive probe, not product
code or a supported restore utility. `pr-costs.json` retains the observed public
workflow/job/step fields and reduced final manifests. No host names, credentials,
artifact TRX machine paths or raw application logs are retained here.

## Completed PR/workflow accounting

Read the named PR bodies, comments and completed branch runs:

```bash
gh pr view 1340 -R HualapaiValley/HVO.SkyMonitor --json body,comments,createdAt,mergedAt,headRefOid
gh pr view 1369 -R HualapaiValley/HVO.SkyMonitor --json body,comments,createdAt,mergedAt,headRefOid
gh pr view 1614 -R HualapaiValley/HVO.SkyMonitor --json body,comments,createdAt,mergedAt,headRefOid
gh api --method GET repos/HualapaiValley/HVO.SkyMonitor/actions/runs \
  -f branch=feature/1314-swap-upgrade -f per_page=100
gh api --method GET repos/HualapaiValley/HVO.SkyMonitor/actions/runs \
  -f branch=feature/1315-backup-restore -f per_page=100
gh api --method GET repos/HualapaiValley/HVO.SkyMonitor/actions/runs \
  -f branch=agent/1563-1386-slice-d-configure-image-quality-tel -f per_page=100
# Repeat for each immutable run ID retained in pr-costs.json:
gh api 'repos/HualapaiValley/HVO.SkyMonitor/actions/runs/40004104603/jobs?per_page=100'
gh api repos/HualapaiValley/HVO.SkyMonitor/actions/runs/40004104603/artifacts
gh run download 40004104603 -R HualapaiValley/HVO.SkyMonitor \
  -n development-v1-40004104603-1 -D "$study_root/ci-1614"
```

The other final artifact names are `development-v1-39082396293-1` and
`development-v1-39192227439-1`. Downloads can expire; the reduced manifests,
their SHA-256 values and observed stage times remain in `pr-costs.json`.
The `final_artifact_sha256` field hashes the original `manifest.json` bytes,
not the pretty-printed embedded JSON object.

Compute job allocation seconds from `completed_at - started_at` for active jobs,
zero for skipped jobs (the API sometimes reports reversed skipped timestamps).
Compute run critical path from creation to the latest active-job completion,
and initial queue to the earliest active-job start. Do not sum overlapping job
durations as elapsed wall time. All observed runs in each sampled branch are
sequential. PR lifespan is merge time minus creation, not author or review time.

For selected Unit timings, parse TRX using namespace
`http://microsoft.com/schemas/VisualStudio/TeamTest/2010`, join
`TestDefinitions/UnitTest/@id` and `TestMethod/@className` to
`Results/UnitTestResult/@testId`, and retain result name, outcome, duration,
start and end. Sum durations only as an attribution measure: parallel tests
make it unsuitable as an incremental critical-path saving. The original
artifact's `headSha` is a synthetic merge; its verified parents are retained in
`selected-unit-timings.json`.

The #1369 body repeats the same Gate 6 duration and heading; deduplicate that
record before adding distinct measured campaigns. Gate 1b/2b are intermediate
records; nine numbered gates does not mean nine complete matrix timings.
Do not manufacture unknown local durations from comment timestamps.

## Disposable Btrfs setup

Use only a new regular image file in the issue-owned scratch directory. The
actual accepted run used 768 MiB, btrfs-progs 6.6.3 and the mount options retained
in `unit-probe-result.json`. It ran after the three existing volume-harness
trials, using their generated payload tree. `sudo -n` was available for this
new image's mount/unmount, usage inspection and readonly-checkpoint cleanup.
No existing device was formatted and no shared infrastructure was used.

```bash
export study_root # directory returned by hvo scratch, with volume already generated
flock -o /tmp/hvo-heavy-gate.lock timeout 300 bash -c '
  set -euo pipefail
  mkdir -p "$study_root/mount"
  ! mountpoint -q "$study_root/mount"
  test ! -e "$study_root/btrfs-wal.img"
  truncate -s 768M "$study_root/btrfs-wal.img"
  mkfs.btrfs -f "$study_root/btrfs-wal.img"
  sudo -n mount -o loop,user_subvol_rm_allowed \
    "$study_root/btrfs-wal.img" "$study_root/mount"
  trap '\''sudo -n umount "$study_root/mount"'\'' EXIT
  sudo -n chown "$(id -u):$(id -g)" "$study_root/mount"
  /usr/bin/time -v -o "$study_root/unit-probe-time.txt" \
    python3 docs/validation/evidence/issue-1615/unit-probe.py \
      "$study_root/mount" "$study_root/volume" "$study_root/unit-probe-result.json"
'
```

An initial attempt completed the first copy/snapshot restore assertions but
failed deleting the readonly checkpoint as an ordinary user, despite
`user_subvol_rm_allowed` (`Read-only file system`). Its `EXIT` trap unmounted
the image. It produced no accepted result set. The script was corrected to use
an explicit privileged deletion of its owned readonly checkpoint and privileged
usage inspection, and all trials were restarted in a new image. This is
operational evidence of a privilege boundary, not a product recovery defect.

A subsequent pilot restored all six cases but its no-op row update emitted no
WAL for the Btrfs checkpoints. It is excluded from the final timing table.
The final fixture explicitly closes SQLite connections, commits a changing
generation marker, asserts a nonempty WAL before every checkpoint, changes that
marker in the incompatible candidate and checks its exact restored value.

Do not use the probe's two-rename selection or file-copy restore as crash-safe
implementation. It has no admission protocol, durable selection journal,
non-rewindable capsule, external roots, health client or running sibling.
The accepted run asserts SQLite integrity/schema/row count, full ordered row
digest, every referenced payload hash, mutable config/key hashes and a static
sibling marker after each restore, including the committed WAL generation
marker. Neither microprobe is full #1606 recovery.
