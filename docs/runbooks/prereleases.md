# Integration Pre-releases

An integration pre-release, `vX.Y.Z-dev.<n>`, publishes a commit on `development/v1` so a real host can install and
upgrade from it before a release exists ([install contract](../hvo-install-contract.md) section 3,
[#1309](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1309)). It carries the release assets of contract
section 4. It is a GitHub pre-release, needs no CHANGELOG section, and never moves `latest`.

Final releases, `vX.Y.Z` and `vX.Y.Z-rc.<n>` from `main`, are
[#1310](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1310)'s; until then
[release-distribution.md](release-distribution.md) describes the retired signed design.

## What a pre-release carries

| File | Contents |
| --- | --- |
| `hvo-sky-install-linux-x64`, `hvo-sky-install-linux-arm64` | The self-contained installer. `--version` prints `<version>+<commit>` |
| `skymonitor-cameraagent-<version>-linux-amd64.oci.tar`, `...-linux-arm64.oci.tar` | The CameraAgent image for one platform, as an OCI archive `docker image load` reads |
| `<catalog version>.bundle.tar.gz` | The approved catalog bundle, under its own version: today `hyg-v4.2-p3-s2-r1.bundle.tar.gz` |
| `install.sh` | The entry point, once [#1311](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1311) adds it. Until then a pre-release has none |
| `release.json` | The release manifest, RoofController's schema 1 (contract section 4) |
| `SHA256SUMS` | The SHA-256 of every other file, `release.json` included |

The CameraAgent image is also pushed to `ghcr.io/hualapaivalley/skymonitor-cameraagent:<version>`, for both platforms.
`release.json` names it by digest, and the offline archives hold exactly that image's platform manifests. No `latest`
tag is ever pushed.

Every file and the image carry a GitHub build-provenance attestation. It names the workflow
`.github/workflows/prerelease.yml` at the tag, the commit, and a GitHub-hosted runner. Together with `SHA256SUMS` it is
the only trust model (contract section 4). There are no signatures and the installer has no embedded keys.

## Cutting a pre-release

Only the owner pushes `v*` tags. Everything after the tag is the workflow's.

1. Pick a commit on `development/v1` whose **Development v1** push run is green, with both **Development v1 /
   Preflight** and **Development v1 / Build and Unit** successful. The branch head is the usual choice. If its run
   is still going, wait for it to finish.
2. Pick the version. `X.Y.Z` is the release the work is heading for, and `<n>` counts that release's pre-releases from
   1. A version is never reused, even after a failed run.
3. Create an **annotated** tag on the commit, and push only that tag:

   ```bash
   git fetch origin development/v1
   git tag -a v1.2.0-dev.3 <commit> -m 'SkyMonitor 1.2.0-dev.3'
   git push origin v1.2.0-dev.3
   ```

4. Follow the **Integration Pre-release** run in Actions. When it is green, the release page shows a pre-release
   that is not marked latest.

### What the workflow does

The workflow defaults to `contents: read`. Each job asks only for what it needs:

| Job | Runs on | Permissions | What it does |
| --- | --- | --- | --- |
| Gate | GitHub-hosted | `contents: read`, `actions: read` | `scripts/release:prerelease-gate` checks several things. The tag is `vX.Y.Z-dev.<n>`, annotated, and names the commit. The commit is the `development/v1` head or behind it. The commit's newest Development v1 push run, and both its required jobs, succeeded. No release has the tag. Nothing is built if any check fails |
| Build | GitHub-hosted | `contents: read` | `scripts/release:assets` builds every file from the tagged commit. The image is built once, for both platforms, into one OCI archive, and the offline archives are split from it |
| Draft | GitHub-hosted | `contents`, `packages`, `id-token` and `attestations: write` | Refuses if a release or draft already has the tag. `scripts/release:push-image` pushes the archive under its version tag, refuses a tag the registry already has for another image, and checks the registry serves the archive's digest. The job then attests the image and every file, verifies the image's attestation, creates a **draft** pre-release with `--latest=false`, and reads its files back |
| Install and Upgrade (arm64) | `[self-hosted, linux, ARM64, hvo-skymonitor-arm64]` (`github-runner-pi-01`) | `contents: read`, `packages: read` | Tag pushes only. `scripts/test:release-install` checks the draft's files and then works on a throwaway instance. It installs from the arm64 offline archive, moves the instance to a stand-in image, upgrades it from the release by digest from the registry, and checks that a second upgrade is refused. Then it uninstalls. However far it got, it removes the containers, the stand-in image, the release image it loaded or pulled (registered before the install, unless the runner held it already) and its temporary root. The registry login lives in a per-run Docker configuration that is deleted afterwards |
| Publish | GitHub-hosted | `contents: write`, `attestations: read` | Checks the draft still holds exactly the files that were tested. Checks each file's attestation names this workflow, tag and commit, and that the image's attestation is on record. Then `scripts/release:publish-prerelease` publishes the draft as a pre-release with `--latest=false` and checks `releases/latest` did not move. Only GitHub's 404 counts as there being no latest release: a read that fails or names no tag stops before publishing, or fails the job after it |

The self-hosted runner never holds a token that can write. It runs only after the gate has admitted a commit that is
already on `development/v1`. The install test uses its own instance ID, container, network and port (55420), and a
temporary product root. It removes the images it added and leaves alone the ones that were there before.

### When a run fails

- **Gate or build:** nothing was pushed or drafted. Fix the cause on `development/v1`, then tag a new commit with the
  next `<n>`. The failed tag names no release; whether to delete it is the owner's choice.
- **Draft, after the image push:** the version tag is in the registry and may name an image no release lists. Cut the
  next `<n>`. Do not reuse the version, because `release:push-image` never moves a tag to another image.
- **Install and Upgrade or Publish:** the draft exists and is unpublished. A transient failure, such as the runner
  being offline, can be retried with **Re-run failed jobs**, which reuses the same files and draft. Otherwise, delete
  the draft, keeping its tag (`gh release delete vX.Y.Z-dev.<n> --repo HualapaiValley/HVO.SkyMonitor --yes`), and
  cut the next `<n>`.

A full re-run stops at the draft job while the earlier attempt's draft exists. This is deliberate: a release is never
replaced.

### Owner settings this relies on

These settings are the owner's; the workflow cannot change them:

- **The `release tags` ruleset** (id 25302808, contract section 2) is active on `refs/tags/v*`. Its creation, update,
  deletion and non-fast-forward rules admit only repository administrators, so only the owner creates a
  `vX.Y.Z-dev.<n>` tag, and once pushed a tag cannot be moved. The workflow relies on that: only the owner starts it,
  and the gate then also requires a green `development/v1` commit.
- **The arm64 runner's egress** must reach `ghcr.io` and `pkg-containers.githubusercontent.com`. The install test pulls
  the image from there, so a blocked runner fails every pre-release at that job, before anything is published.
- **The `skymonitor-cameraagent` package's visibility.** A new GHCR package is private, so a host must sign in to
  `ghcr.io` before it can pull by digest. The offline archives need no sign-in.

## Installing a pre-release on skymonitor01

These steps install or upgrade the Raspberry Pi `skymonitor01` (linux-arm64) from a published pre-release. Follow the
operations manual's change-management rules for a live host. An upgrade pauses capture while the image changes.

1. Download the arm64 files and the catalog into an owner-only folder:

   ```bash
   version=1.2.0-dev.3
   release="$HOME/hvo-releases/$version"
   mkdir -p -m 700 "$release"
   gh release download "v$version" --repo HualapaiValley/HVO.SkyMonitor --dir "$release" \
     --pattern release.json --pattern SHA256SUMS --pattern hvo-sky-install-linux-arm64 \
     --pattern "skymonitor-cameraagent-$version-linux-arm64.oci.tar" --pattern '*.bundle.tar.gz'
   ```

2. Check the files. `--ignore-missing` skips the files you did not download:

   ```bash
   cd "$release"
   sha256sum --check --strict --ignore-missing SHA256SUMS
   for file in *; do
     gh attestation verify "$file" --repo HualapaiValley/HVO.SkyMonitor \
       --cert-identity "https://github.com/HualapaiValley/HVO.SkyMonitor/.github/workflows/prerelease.yml@refs/tags/v$version" \
       --deny-self-hosted-runners
   done
   chmod 755 hvo-sky-install-linux-arm64
   ./hvo-sky-install-linux-arm64 --version    # 1.2.0-dev.3+<commit>; compare the commit with release.json
   ```

   If `gh` is not on the host, download and verify on an admin workstation and copy the folder over. Run the
   `sha256sum` check again on the host.

   The installer unpacks its SQLite library under `~/.net`, or `$DOTNET_BUNDLE_EXTRACT_BASE_DIR` when set, each time it
   starts, so run it as a user whose home only that user can write
   ([host prerequisites](deployment-installer.md#host-prerequisites)).

3. Extract the catalog bundle:

   ```bash
   mkdir -m 700 catalog
   tar -xzf hyg-v4.2-p3-s2-r1.bundle.tar.gz -C catalog
   ```

4. Install, or upgrade the existing instance. Use the release's own installer. `--release` takes the folder's
   absolute path:

   ```bash
   # A new instance, from the offline archive (the default image source).
   ./hvo-sky-install-linux-arm64 cameraagent install \
     --release "$release" \
     --catalog-bundle "$release/catalog/hyg-v4.2-p3-s2-r1.bundle" \
     --friendly-name "SkyMonitor 01" --bind-address 127.0.0.1 --port 5130 \
     --latitude 35.2 --longitude -114.1 --elevation 800 --time-zone America/Phoenix

   # The installed instance. Upgrade notes print before anything changes.
   ./hvo-sky-install-linux-arm64 cameraagent upgrade --instance-id <uuid> --release "$release"
   ```

   To pull the image by digest instead of loading the offline archive, add `--image-source registry`. The host must
   then be signed in to `ghcr.io` while the package is private.

5. Check the result, and keep the rollback command at hand:

   ```bash
   ./hvo-sky-install-linux-arm64 status --instance-id <uuid>
   # The release record, under the product root (/var/lib/hvo-sky by default):
   cat /var/lib/hvo-sky/cameraagents/<uuid>/state/deployment/image-distribution.json
   # Only if the upgrade has to be undone:
   ./hvo-sky-install-linux-arm64 cameraagent rollback --instance-id <uuid>
   ```

An instance installed from an `--image-ref` has no release record. Its first upgrade from a release shows every
upgrade note up to the target and says that the installed version is not recorded. From then on, the installer
refuses an upgrade to an older version: only `rollback` goes back.

The installer refuses a release folder whose files disagree, and changes nothing when it does. It refuses when:

- `release.json` is not the file `SHA256SUMS` lists;
- the two files list different assets or SHA-256s;
- an offline archive differs from the size or SHA-256 they give;
- the release does not publish this host's architecture;
- the image Docker resolves is not one the release names, or is not labelled with the release's version and commit.

[deployment-installer.md](deployment-installer.md#release-install) lists every check.

## Building a release locally

`scripts/release:assets` is what the workflow runs. A local build writes the same files without pushing or publishing
anything. It needs a clean checkout at the commit it builds.

```bash
scripts/release:assets --version 1.2.0-dev.0 --platforms linux/amd64 --builder default \
  --catalog-bundle "$HVO_PRODUCTION_CATALOG_BUNDLE" \
  --output /tmp/hvo-release --image-oci /tmp/hvo-release.oci.tar
scripts/test:release-install --release /tmp/hvo-release --upgrade-source archive
```

A single-platform build holds one offline archive, and its `release.json` names that platform's manifest digest, because
buildx writes no index over a single platform. A build for both platforms needs QEMU or an arm64 builder. To test a
digest install, start a throwaway registry on this host and rebuild into a fresh folder with
`--image-repository localhost:<port>/hualapaivalley/skymonitor-cameraagent`. Push that build's archive with
`scripts/release:push-image --insecure <archive> localhost:<port>/hualapaivalley/skymonitor-cameraagent:<version>`,
then run `scripts/test:release-install --install-source registry --upgrade-source registry` against it.

`scripts/test:release-scripts` proves the gate's and the push script's decisions with fake `gh` and `skopeo` commands.
CI's Quality job always runs it. **Development v1 / Preflight** runs it when the classifier's `release_scripts` output
selects it: on a pull request that changes the release scripts or the library `scripts/test:release-install` sources,
`.github/workflows/prerelease.yml`, the release tool,
or the Preflight wiring that runs it (`development-v1.yml`, `scripts/ci:classify`), on every push, and whenever the
classification failed or is incomplete ([ci-pipeline.md](ci-pipeline.md#release-script-tests)).
