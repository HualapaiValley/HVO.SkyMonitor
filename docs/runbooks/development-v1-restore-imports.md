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

The proof runs one replay on a private snapshot of the committed base using
the pinned SDK and the runner's normal NuGet sources and package cache. It
compares the generated files, package sources and content with the reviewed
certificate. It uses the installed SDK from `DOTNET_INSTALL_DIR` when set, or
the SDK beside the `dotnet` executable on `PATH`. The private snapshot keeps
working-tree files and preexisting restore output out of the proof. The check
records the exact endpoints and the proof decision in its retained artifact.
Full validation runs when a required SDK, package, import or proof is missing,
unexpected or different. Build and Unit keeps the same required check name.

Hosted Preflight checks the committed certificate and source inventory and
warns if that check fails. It can validate source binding without the SDK.
Build and Unit also checks the observed SDK, package graph and generated
imports before skipping the ordinary workload. Pushes and merge groups always
run the full workload.

## Refresh and review

The certificate becomes trusted after HIGH-tier review and merge into
`development/v1`. A certificate in a PR cannot approve that PR's workload
exemption. Refresh the inventory and the certificate together when the SDK,
projects, inputs, packages or generated imports change.

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

Run `scripts/test:ci-classification` and `scripts/test:development-v1-ci`. Run
`scripts/test:ci-docs-restore-import` under the SDK and lock above for the
offline positive and negative proof. If the certificate cannot be reproduced,
the Build and Unit artifact records the reason while full validation remains
in effect. The mandatory maintenance gate is tracked in #1996.
