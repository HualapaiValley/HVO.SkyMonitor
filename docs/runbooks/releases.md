# Releases

A final release, `vX.Y.Z`, or a release candidate, `vX.Y.Z-rc.<n>`, ships one product version from a commit on `main`
([install contract](../hvo-install-contract.md) section 3,
[#1310](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1310)). The installer, the CameraAgent image and the
release's files carry that one version; the approved star catalog keeps its own version and is attached as a file.
Integration pre-releases, `vX.Y.Z-dev.<n>`, follow [prereleases.md](prereleases.md) instead.

A release is trusted by `SHA256SUMS` and the build-provenance attestations the release workflow makes, as a pre-release
is (contract section 4). The key-signed manifests and indexes, their Key Vault signing job and the trust root the
installer embedded were retired by the owner on 2026-10-16; nothing in a release is signed with a key of ours.

## The product version

`VersionPrefix` in `Directory.Build.props` is the only product version: the version the next release carries.
`scripts/release:version` reads it.

- A build given no version is the development build of it, `X.Y.Z-dev`, so a workstation, CI or development image
  build says `X.Y.Z-dev+<commit>`, and a development CameraAgent image's version label is empty.
- A release's builds are given the release's version as `-p:Version` (`scripts/release:assets`, and the image through
  `HVO_VERSION`): `X.Y.Z`, `X.Y.Z-rc.<n>` or, for a pre-release, `X.Y.Z-dev.<n>`.
- `Directory.Build.targets` refuses a build given any other version, or an informational version other than the
  version (error `HVO1310`). A `VERSION` environment variable sets the version too, because MSBuild reads environment
  variables as properties: unset it if a build is refused for a version you did not pass.
- The release workflow's gate refuses a tag that names another version (`scripts/release:version --tag`).

## Before every promotion

Before marking **any** `development/v1` → `main` promotion pull request ready, including a release candidate or
a promotion without a release tag, merge `main` back into `development/v1` through a **content-free sync PR** if
`main` is not already its ancestor. The previous promotion's merge commit lives on `main` alone.
`promote-main.yml` tolerates that history when preparing a draft, but `main`'s strict up-to-date protection still
blocks merging it. Keep the promotion draft until this prerequisite is complete; do not relax branch protection.

This is an **operator/coordinator runbook step**, using the operator's authenticated `gh` session and Git push
credentials. Do not run it with the workflow's `GITHUB_TOKEN`: its pushes do not trigger downstream push workflows,
and PR workflow runs may be suppressed or require approval. In particular, the sync must produce its own green
Development v1 push run before promotion. See
[GitHub's token event rules](https://docs.github.com/en/actions/concepts/security/github_token#when-github_token-triggers-workflow-runs).
Having `promote-main.yml` create the sync would need contents-write permission (its App currently has contents-read)
and an explicit way to obtain those checks. The manual step needs no new secrets, permissions, or settings.

Use Bash in a clean, disposable **release-preparation worktree**, with no other work in it. Run `gh auth status`
first and use the signed-in operator, without workflow-token environment overrides. The block below captures both
branch tips, merges the captured `main` tip normally, and refuses conflicts or **any file change** before committing
or pushing. Its proof is tree equality with the captured pre-sync `development/v1`, not an empty PR file list.
Choose a new `sync_branch` on each attempt; do not reuse or force-push a prior attempt.

<!-- promotion-sync-prepare -->
```bash
(
  set -euo pipefail
  [[ -z "$(git status --porcelain)" ]] || { echo "Use a clean release-preparation worktree." >&2; exit 1; }
  git fetch origin main development/v1
  development="$(git rev-parse origin/development/v1)"
  stable="$(git rev-parse origin/main)"
  before_tree="$(git rev-parse "$development^{tree}")"
  if git merge-base --is-ancestor "$stable" "$development"; then
    echo "main is already an ancestor of development/v1; no sync PR is needed."
    exit 0
  fi

  sync_branch="chore/sync-main-${stable:0:8}-${development:0:8}"
  git switch -c "$sync_branch" "$development"
  if ! git merge --no-ff --no-commit "$stable"; then
    git merge --abort
    echo "Refusing sync: merge conflict; investigate main-only changes separately." >&2
    exit 1
  fi
  if [[ "$(git write-tree)" != "$before_tree" ]]; then
    git merge --abort
    echo "Refusing sync: the merged tree changes development/v1 files." >&2
    exit 1
  fi
  git commit -m "Merge main into development/v1 before promotion"
  [[ "$(git rev-parse HEAD^1)" == "$development" ]] || {
    echo "Refusing sync: first parent differs from the captured development/v1 head." >&2
    exit 1
  }
  [[ "$(git rev-parse HEAD^2)" == "$stable" ]] || {
    echo "Refusing sync: second parent differs from the captured main head." >&2
    exit 1
  }
  [[ "$(git rev-parse 'HEAD^{tree}')" == "$before_tree" ]] || {
    echo "Refusing sync: committed tree differs from the pre-sync development/v1 tree." >&2
    exit 1
  }

  # Stop if either branch moved while preparing the proof.
  git fetch origin main development/v1
  [[ "$(git rev-parse origin/development/v1)" == "$development" ]] || {
    echo "Refusing sync: development/v1 moved during preparation; restart from fresh tips." >&2
    exit 1
  }
  [[ "$(git rev-parse origin/main)" == "$stable" ]] || {
    echo "Refusing sync: main moved during preparation; restart from fresh tips." >&2
    exit 1
  }
  printf 'Pre-sync development=%s main=%s tree=%s sync=%s\n' \
    "$development" "$stable" "$before_tree" "$(git rev-parse HEAD)"
  git push origin "HEAD:refs/heads/$sync_branch"
  gh pr create --repo HualapaiValley/HVO.SkyMonitor --draft --base development/v1 --head "$sync_branch" \
    --title "Sync main into development/v1 before promotion" \
    --body "History-only sync. Pre-sync development: $development; main: $stable; tree: $before_tree. The sync commit has exactly that development tree. Merge with a merge commit to preserve main ancestry."
)
```
<!-- /promotion-sync-prepare -->

Record the printed SHAs and tree in the sync PR's ledger. If a tree check fails, do not resolve it by taking
`ours`, resetting files, or making an empty commit: that would hide main-only content instead of proving a normal
merge changes nothing. Investigate that content separately. If the branch tips moved, restart from fresh tips and
repeat the proof; an earlier tree proof does not cover a different base.

The sync follows the ordinary draft review and finalization procedure. Before merging, verify its head, its
pre-sync target SHA and `main` SHA still match the recorded proof, both Development v1 required checks are green,
and the sync tree still equals that pre-sync target tree. If either protected branch moved, return to draft and
repeat the procedure against the new tips. **Merge with a merge commit**, never squash or rebase, so `main` becomes
an ancestor of `development/v1`. Confirm the merged development tree equals the recorded pre-sync tree, then wait
for the successful **Development v1 push run on the new development head**.

Fetch again and run the [CI ancestry gate](ci-pipeline.md#promotion-ancestry-prerequisite). Review the promotion's
updated source head, record the current source/target SHAs and the sync proof, and only then mark the promotion
ready for its planned qualification window. Its `Required CI` must pass on that head. If `main` moves again, repeat
the ancestry gate and, when needed, the sync before proceeding.

## Cutting a release

Only the owner pushes `v*` tags; everything after the tag is the workflow's, until the owner approves publishing.

1. **Version.** If `VersionPrefix` is not the version to release, change it first, in its own pull request.
2. **Release pull request.** Rename `## [Unreleased]` in `CHANGELOG.md` to `## [X.Y.Z] - YYYY-MM-DD`, start a new empty
   `## [Unreleased]` above it, and add `docs/upgrade-notes/X.Y.Z.md`: what someone upgrading must do, never empty.
   The release tool copies every release's upgrade notes up to this one's into `release.json`, and the release page
   shows this version's CHANGELOG section and notes (`scripts/release:notes`). A release candidate needs neither.
3. **Promote** the release pull request's commit to `main`, the release branch, following
   [Before every promotion](#before-every-promotion) before marking its promotion pull request ready.
4. **Tag** a commit on `main` with an annotated tag, and push only that tag:

   ```bash
   git fetch origin main
   git tag -a v0.9.0 <commit> -m 'SkyMonitor 0.9.0'
   git push origin v0.9.0
   ```

5. **Follow the Release run** in Actions. Once both install tests are green, review the draft release: its files, its
   notes and its image. Then approve the **Release / Publish** job in the `production-release` environment. GitHub
   holds the job until a required reviewer approves; nothing is published before that.

A release candidate is cut the same way with a `vX.Y.Z-rc.<n>` tag, from `n = 1`. It is published as a GitHub
pre-release and never becomes the latest release.

### What the workflow does

`.github/workflows/release.yml` runs on a pushed `vX.Y.Z` or `vX.Y.Z-rc.<n>` tag. The workflow defaults to
`contents: read`; each job asks only for what it needs:

| Job | Runs on | Permissions | What it does |
| --- | --- | --- | --- |
| Gate | GitHub-hosted | `contents: read`, `actions: read` | `scripts/release:gate` admits the tag, read-only. The tag names the product version (`scripts/release:version --tag`), is annotated and names the commit. The commit is `main`'s head or behind it. A final release has its CHANGELOG section and upgrade notes (`scripts/release:notes --check`). No published release has the tag. `scripts/release:check-environment` finds a required reviewer on `production-release`. Every GitHub read is strict (`scripts/lib/github-api.sh`): a read that fails, is cut short or is malformed stops the gate, naming the read, and only GitHub's own 404 means a tag or environment does not exist |
| CI | The CI workflow's runners | `contents: read` | The whole CI workflow (`ci.yml`), called with `workflow_call`, qualifies the tagged commit. A called run sees the push of the tag, so it runs in full, as on `main` |
| Build | GitHub-hosted | `contents: read` | Only after CI passed: `scripts/release:assets` builds every file from the tagged commit with the release's version, as for a pre-release, and refuses a final release without its notes |
| Draft | GitHub-hosted | `contents`, `packages`, `id-token` and `attestations: write` | As the pre-release draft job: checks the files name the tagged release (and a final release's `release.json` carries its notes), `scripts/release:check-image-labels`, `scripts/release:prerelease-check unreleased`, pushes the image under its version tag alone (`scripts/release:push-image`), attests the image and every file and verifies the image's attestation, creates a **draft** release with `--latest=false` (a pre-release for a release candidate), and reads its files back |
| Install and Upgrade (linux-x64, linux-arm64) | `ubuntu-24.04`, and `[self-hosted, linux, ARM64, hvo-skymonitor-arm64]` (`github-runner-pi-01`) | `contents: read`, `packages: read` | Tag pushes only. `scripts/test:release-install` installs a throwaway instance from the draft's offline archive for the platform, moves it to a stand-in image, upgrades it by digest from the registry, checks that a second upgrade is refused, and uninstalls, leaving the runner as it found it ([prereleases.md](prereleases.md#what-the-workflow-does) describes the test). Both platforms must pass |
| Publish | GitHub-hosted, in `production-release` | `contents: write`, `actions: read`, `attestations: read` | Held until a required reviewer approves. `scripts/release:check-environment` checks the environment still requires a reviewer. Then, as the pre-release publish job does, it checks the draft still holds exactly the tested files with this workflow's attestations, and the image's attestation on record. `scripts/release:publish` publishes the draft and decides latest (below), in the release-wide `release-promotion` concurrency group |
| Latest Image | GitHub-hosted | `contents: read`, `packages: write` | Only when publishing made the release the latest release, in the `release-promotion` group too: `scripts/release:tag-latest` checks the image's version tag still names the release's digest and copies it to `latest` |

### latest

Publishing makes a release the latest release, and moves the image's `latest` tag to its image, only when it is the
newest final release: a final `vX.Y.Z` higher, by version number, than every published final release
(`scripts/lib/releases.sh`). A release candidate, a pre-release, or a fix to an older version leaves both where they
are. GitHub would otherwise make any release it publishes the latest, so `scripts/release:publish` always says which.

Every run's Publish and Latest Image jobs share one release-wide concurrency group, `release-promotion`, so two
releases never decide or move latest at once, whatever order their tags were pushed in. Inside it, each script reads
the releases again immediately before it writes and decides from that read. Afterwards, the latest release must be
this release where it moved and not this release where it did not, and neither the latest release nor the image's
`latest` may be older than the newest published final release, read again: if one is, the job fails, naming the
release that should be latest.

### When a run fails

- **Gate, CI or Build:** nothing was pushed or drafted. Fix the cause on `development/v1`, promote it, and tag the
  fixed commit with the next version or release candidate. A failed tag names no release; whether to delete it is the
  owner's choice.
- **Draft, after the image push:** the version tag is in the registry. Do not reuse the version:
  `release:push-image` never moves a tag to another image. Cut the next release candidate or patch version.
- **Install and Upgrade, or Publish:** the draft exists and is unpublished. Retry a transient failure, such as the
  arm64 runner being offline, with **Re-run failed jobs**, which reuses the same files and draft. Otherwise delete the
  draft, keeping its tag (`gh release delete v0.9.0 --repo HualapaiValley/HVO.SkyMonitor --yes`), and cut the next
  version.
- **Latest Image:** the release is published. **Re-run failed jobs** moves `latest` again; it checks the release is
  still the newest final release first.
- **Cancelled while waiting for `release-promotion`:** GitHub keeps only one job waiting in a concurrency group, so a
  Publish or Latest Image job queued behind a waiting one cancels it before it writes anything. Once the other
  release's promotion finishes, **Re-run failed jobs** on the cancelled run.

A full re-run stops at the draft job while the earlier attempt's draft exists: a release is never replaced.

### Owner settings this relies on

The workflow changes no repository or environment setting; these are the owner's to apply:

- **`production-release` environment** (Settings, Environments). As of 2026-10-18 it is set as below: it requires the
  owner's approval, administrators cannot bypass it, and only `v*` tags can use it. The release gate refuses every
  release while it requires no reviewer:
  - **Required reviewers:** the owner (`RoySalisbury`).
  - **Prevent self-review:** off while the owner is the only reviewer, because the owner both pushes the tag and
    approves. Turn it on once another reviewer is added.
  - **Allow administrators to bypass configured protection rules:** off, so an approval is always needed.
  - **Deployment branches and tags:** "Selected branches and tags", with the tag rule `v*`, so only a release tag's run
    can use the environment.
  - No environment secrets or variables are needed. The retired signing job's Azure variables
    (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_KEY_VAULT_NAME`, `AZURE_RELEASE_KEY_NAME`,
    `AZURE_RELEASE_KEY_VERSION`), and the federated credential and Key Vault key behind them, are no longer used; the
    owner may retire them.
- **The `release tags` ruleset** (id 25302808) on `refs/tags/v*`: only repository administrators create, update or
  delete a `v*` tag, so only the owner starts a release, and a tag is never moved. It is active.
- **Runners.** The CI workflow's self-hosted `hvo-qualification` runners must accept jobs from the Release workflow's
  runs, which call it, if their runner group limits the workflows it serves. The arm64 install test needs
  `github-runner-pi-01` as [prereleases.md](prereleases.md#owner-settings-this-relies-on) describes: egress to
  `ghcr.io`, memory cgroups, and `docker buildx` 0.8 or later. The x64 install test runs on a GitHub-hosted runner.
- **The `skymonitor-cameraagent` package** must let this repository's workflows write (they push the version and
  `latest` tags with the workflow token) and read it; its visibility is as for pre-releases.

## Checking a release

```bash
version=0.9.0
sha256sum --check --strict --ignore-missing SHA256SUMS
gh attestation verify hvo-sky-install-linux-arm64 --repo HualapaiValley/HVO.SkyMonitor \
  --cert-identity "https://github.com/HualapaiValley/HVO.SkyMonitor/.github/workflows/release.yml@refs/tags/v$version" \
  --deny-self-hosted-runners
gh attestation verify "oci://ghcr.io/hualapaivalley/skymonitor-cameraagent:$version" --repo HualapaiValley/HVO.SkyMonitor \
  --cert-identity "https://github.com/HualapaiValley/HVO.SkyMonitor/.github/workflows/release.yml@refs/tags/v$version" \
  --deny-self-hosted-runners
```

`install.sh` checks the installer and `SHA256SUMS` against their attestations the same way when `gh` is signed in, and
the installer checks every file it uses against `SHA256SUMS` and `release.json`
([Release Install](deployment-installer.md#release-install)). An upgrade refuses a release older than the installed
one; only `rollback` goes back. Install and upgrade a host from a release as
[prereleases.md](prereleases.md#installing-a-pre-release-on-skymonitor01) shows for a pre-release, with this
workflow's identity in the attestation check.

## Building a release locally

`scripts/release:assets` is what the workflow runs, and it builds the same files locally without pushing or
publishing anything, as [prereleases.md](prereleases.md#building-a-release-locally) describes. Its `--version` must be
the product version or a pre-release of it, and a final version needs the release's CHANGELOG section and upgrade
notes:

```bash
version="$(scripts/release:version)-rc.1"
scripts/release:assets --version "$version" --platforms linux/amd64 --builder default \
  --catalog-bundle "$HVO_PRODUCTION_CATALOG_BUNDLE" \
  --output /tmp/hvo-release --image-oci /tmp/hvo-release.oci.tar
scripts/test:release-install --release /tmp/hvo-release --upgrade-source archive
```

`scripts/test:release-scripts` proves the gate's, the environment check's, the publish's and the latest tag's
decisions with fake `gh` and `skopeo` commands, and the workflow's shape: CI before the build, both install tests
before publishing, the environment only on the publish job, the `release-promotion` group on the publish and latest
jobs alone, and write permission only where it is needed.
