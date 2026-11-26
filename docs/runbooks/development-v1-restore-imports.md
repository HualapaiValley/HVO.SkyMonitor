# Development v1 restore-import certificate

The [Development v1 CI contract](../development-v1.md#required-ci) requires
successful hosted Preflight and the exact `Development v1 / Build and Unit`
check. For a ready PR, Build and Unit reads the classifier, inventory and
restore certificate from the verified first parent of the checked-out merge.
It audits both the exact PR tip and the exact merge tree against that parent.
The two endpoints must agree before a restore proof can authorize the fast path.

The [source inventory](../../scripts/ci:docs-inventory) records the audited
solution, project and import blobs plus SDK pins, NuGet configuration, package
lock files and Git attributes relevant to the snapshot. The
[restore certificate](../../scripts/ci:restore-import-certificate.json) binds
that inventory to the Linux x64 SDK directory, each project's resolved graph and
generated `.nuget.g.props` and `.nuget.g.targets`, and each resolved package's
source, archive and expanded payload. SDK declarations in imported files,
explicit SDK imports, linked documents and ambiguous paths still fail the
source audit.

The SDK digest covers `sdk/<version>`. Workload manifests in `sdk-manifests/`,
installed `packs/`, and workload installation state sit outside that digest.
The replay detects their effects on the resolved graph or generated imports.

## Runner prerequisites and evidence

Before the regular SDK setup step runs, the Linux x64 runner needs Python 3.12
or later and .NET SDK 10.0.402. Put `dotnet` on `PATH` or set
`DOTNET_INSTALL_DIR` to its installation directory. The expanded SDK directory
must match the reviewed digest.

The proof runs one replay on a private snapshot of the committed base using
the pinned SDK and the runner's normal NuGet sources and package cache. It
compares the generated files, package sources and content with the reviewed
certificate. The private snapshot keeps working-tree files and preexisting
restore output out of the proof. GNU `timeout` runs the proof in its own process
group and sends TERM after 30 seconds. If the monitored process is still
running five seconds later, `timeout` sends KILL. When `timeout` returns,
Build and Unit sends KILL to any remaining group members before attempting
scratch cleanup. A cleanup error still allows the decision to complete.
On proof failure, the job gives the elapsed time back to the full-validation
workload budget within the 22-minute hard job timeout. Full validation also
runs when a required SDK, package, import or proof is missing, unexpected or
different.

For the first ready docs-only PR after rollout, confirm `runFull=false` in
the retained `classification.md` artifact. It records the exact endpoints,
decision, `restoreProofExitCode`, and `restoreProofElapsedSeconds` when the
proof is attempted.

Hosted Preflight runs `./scripts/ci:classify --check-docs-inventory HEAD` as a
mandatory failing gate on every PR, push and merge group; full `CI / Quality`
enforces the same check. Without the SDK, it validates file modes, audit rules,
source inventory bytes, certificate schema, `sourceSha256`, the committed
`global.json` SDK version pin, and the committed restored project set (solution
projects plus the coverage-gate project when present). SDK-backed full restore
replay (`./scripts/ci:classify --verify-docs-restore`) additionally validates
the observed Linux x64 SDK binary digest, package sources and archive/payload
digests, and each project's resolved graph and generated `.nuget.g.props` and
`.nuget.g.targets` imports. Build and Unit runs that replay against the trusted
base before skipping the ordinary workload, and full-mode `CI / Quality` runs
both `scripts/test:ci-docs-restore-import` and `./scripts/ci:classify
--verify-docs-restore HEAD` under the pinned `.NET` SDK. Merge groups always
run the full workload; pushes to `development/v1` also run the full workload
unless Preflight verifies that a merge-queue-generated push matches an exact
SHA that already passed the Development v1 merge-group workflow (Refs #2066).

## Refresh and review

The certificate becomes trusted only after HIGH-tier review and merge into
`development/v1`. A certificate updated in a PR cannot approve that same PR's
workload exemption. Any PR that changes the pinned SDK, restored project set,
audited build inputs, NuGet packages, or generated imports must refresh
`scripts/ci:docs-inventory` and `scripts/ci:restore-import-certificate.json`
together before merge.

Any change to an audited build input, `global.json` SDK pin, or restored project
set makes the certificate stale (project files, props and targets, package pins,
`global.json`, and `.gitattributes` are all covered). For stale, drifted, or
missing certificates caught by the SDK-free check, hosted Preflight and full
`CI / Quality` fail with
`::error::Documentation CI build inventory is stale or unsafe; refresh scripts/ci:docs-inventory and scripts/ci:restore-import-certificate.json: …`
until the refresh is committed. For unsafe build inputs (such as a tracked
symlink, submodule, `.user`/`.rsp` file, or escaped XML), the check fails with
`::error::Documentation CI build inventory is stale or unsafe; remove the unsafe input or update the audit policy under HIGH-tier review: …`
because refreshing the certificate fails with the same audit error until the
unsafe input is removed or the audit policy is changed under HIGH-tier review.
Drift in the observed Linux x64 SDK binary, resolved NuGet packages, or
generated `.nuget.g.props`/`.nuget.g.targets` imports is caught by SDK-backed
`--verify-docs-restore HEAD` in full-mode `CI / Quality` (failing the job) and
by the trusted-base `--verify-docs-restore "$base"` replay during
`Development v1 / Build and Unit` classification of docs-only PRs. Even when a
base tree carries a stale, drifted or unsafe certificate, Build and Unit
continues to fail closed and select full validation; it never produces a
skipped-green required result (Refs #1996).

From a clean candidate commit on Linux x64, use the pinned stable SDK and the
same NuGet sources as Build and Unit. The published Linux x64 SDK distribution
and the runner's expanded SDK directory must match the certificate digest.
Capture both outputs in a private temporary directory before copying them into
the candidate.

```bash
(
  set -euo pipefail
  capture_dir="$(mktemp -d "${TMPDIR:-/tmp}/hvo-restore-certificate.XXXXXXXX")"
  trap 'rm -rf "$capture_dir"' EXIT
  ./scripts/ci:classify --print-docs-restore-source HEAD > "$capture_dir/inventory"
  checksum="$(sha256sum < "$capture_dir/inventory")"
  MSBUILDDISABLENODEREUSE=1 flock --close /tmp/hvo-heavy-gate.lock \
    python3 -I scripts/ci/restore-imports.py capture HEAD "${checksum%% *}" \
    > "$capture_dir/certificate.json"
  cp "$capture_dir/inventory" scripts/ci:docs-inventory
  cp "$capture_dir/certificate.json" scripts/ci:restore-import-certificate.json
)
```

Review the source inventory, manifest and build-input diff together. Reconcile
SDK bytes with the approved distribution and resolved packages with their
listed sources and archive digests. Inspect the generated import paths and
their recursively imported package build logic for reads of eligible documents,
including implicit items and globs. Reproduce the capture byte-for-byte and
check the exact committed result with `./scripts/ci:classify
--verify-docs-restore HEAD` under the same lock. The expected result is
`restore_imports_verified=true`.

Run `./scripts/ci:classify --check-docs-inventory HEAD`,
`scripts/test:ci-classification` and `scripts/test:development-v1-ci`. Run
`scripts/test:ci-docs-restore-import` under the SDK and lock above for the
offline positive and negative proof. If a committed certificate cannot be
reproduced against the pinned SDK and NuGet sources, full-mode `CI / Quality`
fails `--verify-docs-restore HEAD` on the checked-out commit (a PR carrying a
refreshed certificate passes `CI / Quality` before merge), while
`Development v1 / Build and Unit` checks `--verify-docs-restore "$base"` on the
trusted base branch—failing closed to full validation and recording the reason
in its classification artifact until the refreshed certificate is reviewed and
merged into `development/v1`. Full-mode
`CI / Quality` runs `scripts/test:ci-docs-restore-import` and
`./scripts/ci:classify --verify-docs-restore HEAD` automatically under its
pinned `.NET` SDK (`Run SDK-dependent contract checks`, Refs #1996), so hosted
Preflight stays SDK-free and the required Build and Unit workload keeps its
existing scope and `1080s` budget.
