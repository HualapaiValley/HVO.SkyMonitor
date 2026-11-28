# HVO Product Installation and Release Contract

Status: **draft 4 with approved SkyMonitor recovery revision**, 2026-10-23. The owner accepted draft 4 on
2026-10-18 and revised SkyMonitor recovery in #1606 on 2026-10-23 (sections 6 and 10). Owner: roys.
The revision is a target contract, not a claim that its runtime implementation or qualification has shipped.

This contract is the one install and release experience shared by the HVO products. It covers HVO.RoofController
and HVO.SkyMonitor (CameraAgent). Each product implements it in its own
repository; it does not create a shared codebase. RoofController 4.1.1 already implements most of it, so where this
document says "as RoofController does", its implementation is the reference. Conformance work is tracked in each
repository's epic. Where a rule says **Today**, it records how each product meets it, and the issue that brings a
product that does not.

Product-specific safety rules stay product-specific and are never weakened by this contract. Examples are the roof
controller's verified Stop and idle-only redeploy, and CameraAgent's capture pause and state contract gate.

## 1. Versions

- **One product version per repository.** It is semantic versioning, held once as `VersionPrefix` in
  `Directory.Build.props`. The build refuses a `-p:Version` that is neither that prefix nor a pre-release of it. A
  script (`build/version.sh` in RoofController) refuses a tag that does not match.
- **Build kinds:**

  | Build | Version |
  | --- | --- |
  | Workstation | `X.Y.Z-dev` |
  | CI | `X.Y.Z-ci.<run>` |
  | Integration pre-release | `X.Y.Z-dev.<n>` (SkyMonitor `development/v1` only) |
  | Beta | `X.Y.Z-beta.N` (SkyMonitor `main`) |
  | Release candidate | `X.Y.Z-rc.<n>` |
  | Release | `X.Y.Z` |

- **Stamping.** Every assembly carries `InformationalVersion` = `<version>+<40-character commit>`. Every image carries the
  OCI labels `org.opencontainers.image.version`, `.revision` and `.created`, never inherited from a base image.
  - **`org.opencontainers.image.created` is required** (decision D9). It is the release's `created` time, the same value
    as `release.json`'s `created`, as an RFC 3339 UTC time such as `2026-10-17T18:00:00Z`. The release checks refuse an
    image without it, as they refuse one without `version` or `revision`.
  - Reason: it dates an image without trusting a registry or a file time, and a release reproduces it from
    `release.json`. A base image's `created` would date the base, not the release.
  - **Today:** RoofController sets it from `ROOF_CREATED` and `build/check-image-labels.sh` refuses an image whose
    `created` is not an RFC 3339 time. SkyMonitor's CameraAgent image carries it too, set from `HVO_CREATED` to
    `release.json`'s `created`, and its release checks and installer refuse an image without it or with another value
    (#1359, #1364).
- **Where the version shows:** `--version` on every binary, the application's System page and footer, its startup log
  line, its info/health API, and the image labels. CI asserts `--version` equals `<version>+<commit>`.
- **Data packages keep their own train.** Data that changes on its own cadence is versioned and released separately from the
  product version, and the product declares which data versions it accepts. The current case is SkyMonitor's star catalogs.

## 2. Branches and checks

- The default branch is protected. It has required status checks, merge commits only, and branches are deleted on
  merge.
- Release tags (`v*`) are protected by a ruleset.
- GitHub Actions are pinned by commit SHA. Dockerfile base images are pinned by digest, and Dependabot updates both.
- CI runs a secret scan over history and a documentation link check.

## 3. Cutting a release

1. A version-bump PR.
2. A release PR for the final release. Use `scripts/release:changelog` to assemble the pending fragments and any
   remaining Unreleased entries into the version section in `CHANGELOG.md`. Add a non-empty `docs/upgrade-notes/X.Y.Z.md`.
3. An annotated tag `vX.Y.Z`, `vX.Y.Z-rc.<n>` or (for SkyMonitor) `vX.Y.Z-beta.N` on the release branch. That is the
   default branch, except in SkyMonitor, whose default branch is the `development/v1` integration branch and whose
   release branch is `main`.

The release workflow then:
- checks that the tag matches the version and is on the release branch, and that a final release has its CHANGELOG
  section and upgrade notes;
- calls the CI and scenario workflows (`workflow_call`) as gates, so the tested binaries are the shipped binaries;
- builds and pushes the images, collects CI's binaries, and writes `release.json` and `SHA256SUMS`;
- creates a **draft** GitHub release, in an environment that needs the owner's approval;
- installs from the draft on linux-x64 and linux-arm64 (end-to-end job);
- waits for a person to publish the draft.

Publishing moves `latest` (the image tag and `releases/latest`) only for the newest final `X.Y.Z`. A release
candidate, a pre-release or a fix to an older line never moves it.

**Integration pre-releases** (`vX.Y.Z-dev.<n>`) can be cut from an integration branch for smoke testing. They carry the
same assets, are marked as GitHub pre-releases, need no CHANGELOG section, and never move `latest`.

**The pre-release exception** (decision D8). The approval and the end-to-end job above are for final releases and
release candidates and SkyMonitor betas. An integration pre-release follows them except in these ways:
- **The human step is the owner's tag.** An annotated `vX.Y.Z-dev.<n>` tag that only the owner can push, under the
  release-tag ruleset, on a commit the integration branch's required checks passed. There is no environment approval.
  The workflow publishes the tested draft itself.
- **The end-to-end job installs and upgrades on linux-arm64 only**, the platform the integration hosts run. The
  linux-x64 files are checked by `SHA256SUMS` and their attestations, not installed.
- **A failure publishes nothing.** A transient failure is re-run on the same draft. Otherwise the draft is deleted and
  the next `<n>` is cut. A tag is never moved and a release is never replaced.

Reason: an integration pre-release exists to put a branch head on a real host quickly. The owner's tag is already a
deliberate act on a reviewed, green commit, and a second approval would add a wait without adding a check. A final
release is what operators install from `latest`, so it keeps the approval and both platforms.

**Today:** SkyMonitor cuts integration pre-releases this way (`.github/workflows/prerelease.yml`, #1309). Its final
releases, betas and release candidates follow the release path above (`.github/workflows/release.yml`, #1310), with
approval in its `production-release` environment. RoofController cuts no integration pre-releases; its release
candidates follow the final-release path.

## 4. Release assets

Every release carries assets with these names and kinds:

| Asset | Kind | Contents |
| --- | --- | --- |
| `hvo-<product>-install-<rid>` | `installer` | The installer: single-file, self-contained, per runtime ID (`linux-arm64`, `linux-x64`, `osx-arm64` where supported) |
| `hvo-<product>-<rid>` | `cli` | The runtime client, when the product has one |
| `install.sh` | `install-script` | The entry point, with the release version baked in |
| `<image>-<version>-linux-<arch>.oci.tar` | `image-archive` | An offline image archive per architecture |
| `<catalog version>.bundle.tar.gz` | `catalog` | An independently versioned data package (section 1): the default HYG 4.4 star bundle and `openngc-v20260501-p6-s5-r1.bundle.tar.gz` for deep-sky objects. Star and deep-sky selections use independent catalog lifecycle commands |
| `THIRD-PARTY-NOTICES.md` | `notices` | The third-party notices of what the release ships, the installer's bundled packages among them (SkyMonitor, #1317) |
| `release.json` | | The machine-readable release manifest |
| `SHA256SUMS` | | Covers every asset |
| `CHANGELOG.md` section and upgrade notes | | In the release body and in `release.json` |

- **The asset `kind` vocabulary** (decision D7). Each entry of `release.json`'s `assets[]` names one of these kinds:

  | Kind | Asset | `platform` | Products |
  | --- | --- | --- | --- |
  | `installer` | The installer | its runtime ID | both |
  | `cli` | The runtime client | its runtime ID | RoofController (`hvo-roof`); SkyMonitor with `hvo-sky` (#1317) |
  | `install-script` | `install.sh` | `null` | RoofController; SkyMonitor (#1311) |
  | `image-archive` | An offline image archive | the runtime ID of its architecture (`linux-x64` for `amd64`) | both |
  | `catalog` | A data package | `null` | SkyMonitor |
  | `notices` | `THIRD-PARTY-NOTICES.md` | `null` | SkyMonitor (#1317) |
  | `compose`, `deploy-script`, `kiosk`, `linux-auth`, `mac-app` | RoofController's own files: its Compose file, deploy script, kiosk, Linux authentication worker and Mac app | as written | RoofController |

  - A kind is lowercase: a letter, then letters, digits and `-`.
  - A shared kind means the same in every product.
  - A product-specific kind is listed in this table before a release uses it.
  - `release.json` and `SHA256SUMS` describe the assets, so they are not entries of `assets[]`.
  - Reason: an installer and `install.sh` select files by kind, so a kind that means two things, or one no reader knows,
    would let a release be read two ways.
  - **Today:** both products write only kinds in this table.
- **A data package asset** (kind `catalog`) is the version of that data the release is tested with. It is an ordinary
  asset: listed in `SHA256SUMS`, attested and checked like the others. The installer does not take it from the release
  by itself: the operator, or `install.sh`, extracts it and passes it to the installer (SkyMonitor:
  `--catalog-bundle`). The product still declares which data versions it accepts, so a newer data package can be
  installed without a product release.
- **Star catalog rollback** is an instance operation.
  `hvo-sky-install catalog rollback` restores the previous package revision of the installed star lineage.
  It refuses when there is no previous revision or capture processing still has pending work or retries.
  Later releases must preserve this rollback contract. Independent OpenNGC selection has its own
  previous-selection record.
- **Platform names** (decision D5). A name about an image uses the OCI architecture, `amd64` or `arm64`:
  - the offline archive's file name, `<image>-<version>-linux-<arch>.oci.tar`;
  - `images.<key>.platforms`;
  - `offlineArchives[].platform`, as `linux/amd64` or `linux/arm64`.

  A name about a binary uses the .NET runtime ID, `linux-x64`, `linux-arm64` or `osx-arm64`: the binary's file name
  and every asset's `platform`, an image archive's included.
  - Reason: each name is the one the tool that reads it uses, `docker load` and OCI for images, and `dotnet publish`
    for binaries. Renaming either side would add a translation that both products already avoid.
  - **Today:** both products name their files this way.
- **Images** are pushed to `ghcr.io/hualapaivalley/<image>` with tag `<version>`, plus `latest` for the newest final release,
  and are always referenced by digest. Image names: `roof-controller`, `roof-hat-emulator`, and `skymonitor-cameraagent`.
- **`release.json`** follows RoofController's schema 1:
  - `version`, `tag`, `prerelease`, `commit`, `created`, `repository`;
  - `images.<key>` with `repository`, `tag`, `digest`, `reference` and `platforms`;
  - cumulative `upgradeNotes[]`;
  - `assets[]` with name, kind, platform, size and sha256.

  It adds `images.<key>.offlineArchives[]` and `stateContracts` (section 6).
- **Image keys** (decision D6). `images.<key>` names the component, not the image. The key is a lower-camel-case
  logical name, and the image name is in `repository`. `stateContracts` uses the same keys:

  | Product | Keys |
  | --- | --- |
  | RoofController | `controller` (`roof-controller`), `hatEmulator` (`roof-hat-emulator`) |
  | SkyMonitor | `cameraAgent` (`skymonitor-cameraagent`) |

  - Reason: RoofController's released installers already read `images.controller`, while SkyMonitor has published
    only integration pre-releases, so this choice changes the product that has no released readers. A component's key
    also outlives a rename of its image.
  - **Today:** both products use these keys. SkyMonitor's `release.json` names `images.cameraAgent` (#1359, #1364).
- **Upstream images** (#1966). `images.silo` names the stock Silo image a Silo-layout CameraAgent runs. HVO does not
  build or convey it, so it differs from a component image:
  - it has `"supplier": "upstream"`, the upstream `repository`, release `tag`, index `digest` and `reference`;
  - `platforms[]` are objects with `platform`, `manifestDigest` and `configDigest`;
  - it adds `revision`, `license` and `source`, and has no `offlineArchives`;
  - the release tool writes it from the installer's own pin. A reader that does not use it ignores it, so earlier
    installers read such a release unchanged;
  - a Silo-layout install refuses a release that names no Silo image or another one, before changing anything.
- **Upgrade-note text** follows one terminal-safety rule. The tool that writes `release.json` applies it, and so does
  the installer that reads it. CRLF is normalized to LF first. After that, a note is
  refused if it holds a Unicode control character (Cc) other than LF and TAB, a format character (Cf), a line or
  paragraph separator (Zl, Zp), a private-use character (Co) or a noncharacter. The refusal names the release version
  and the first such code point as `U+XXXX`, never the text. A release whose notes break the rule is not written, and
  an installer does not print it.
- **Trust:**
  - `SHA256SUMS` plus a GitHub build-provenance attestation for every asset and image, verifiable with
    `gh attestation verify`, is the only trust model.
  - SkyMonitor's key-signed manifests and indexes (ES256, Azure Key Vault, embedded trust root) are retired (owner
    decision, 2026-10-16).
  - The release environment requires the owner's approval to publish, except for the integration pre-releases of
    section 3.
  - Installers refuse an older version on `upgrade`. CameraAgent's `rollback` loads the retained
    previous image on the baseline contract (section 6).

## 5. Installer

### Entry point

```bash
curl -fsSL https://github.com/HualapaiValley/<repo>/releases/latest/download/install.sh | bash -s -- [installer arguments]
```

`install.sh` does the following. RoofController's implements all of it, offline image archives included (`--from DIR`,
HVO.RoofController#127):
- is wrapped in a function, so a truncated download runs nothing;
- refuses to run without a baked-in version;
- checks everything before changing anything:
  - platform and architecture;
  - `curl`, `tar` and `sha256sum`;
  - free space;
  - clock skew;
  - registry reachability;
  - Docker version;
  - the product's own prerequisites, such as I2C for the roof controller and Docker memory, CPU and PID limit
    support for CameraAgent;
- can install Docker or enable a prerequisite only when asked (`--install-docker` and the like);
- with `--check`, stops after the checks;
- with `--from DIR`, takes the release files, offline image archives included, from a folder instead of GitHub. It must
  not contact the registry in that mode. RoofController's `--from` loads the offline archives and pulls nothing from
  GHCR (HVO.RoofController#127); SkyMonitor's `install.sh` is #1311;
- verifies the downloaded installer against `SHA256SUMS`, against the attestation when `gh` is signed in, and against
  its own `--version`;
- hands its remaining arguments to the installer.

### Command surface (`hvo-<product>-install`)

| Command | Meaning |
| --- | --- |
| *(none)* / `install` | Install; interactive on a terminal, otherwise from flags or `--answers` |
| `upgrade [--version V]` | Upgrade to the latest release or to `V` (section 6) |
| `rollback` | RoofController returns to the release recorded as previous. CameraAgent starts the retained previous image on the same state contract (section 6). |
| `backup` / `restore FILE` | A self-verifying archive of the product's own data, with a manifest, that only its owner can read (below) |
| `uninstall [--purge --confirm HOST --backup\|--no-backup]` | Remove what was installed; `--purge` also removes data |
| `status` | What is installed here, its versions and health |

- **Who owns a backup.** Each product keeps its archives readable by the account that already holds the data they copy,
  and by no one else:
  - RoofController's are root-only: `sudo hvo-roof-install backup` runs as root and keeps them in `/var/backups/hvo-roof`.
  - SkyMonitor's are **owner-only, the service account's** (owner decision, 2026-10-18, decision 8). `backup` and
    `restore` run as the instance's service account, not started with `sudo`, and refuse to run as anyone else. The
    archives are `0600` in `<product-root>/backups`, a `0700` folder that account owns. All archive and data handling
    stays with the account.
  - **The single exception:** before changing anything, they may call the installer's existing folder-preparation
    helper, the one `install` uses, only to create two folders owned by the service account: `<product-root>/backups`,
    and an absent instance root when restoring after a purge. A restore leaves the machine install record to
    `reinstall`.
  - Reason: SkyMonitor's service account already owns every live store and secret a backup copies, and it is in the
    `docker` group, which is root on the host in all but name. A root-only archive would add almost no protection, and
    running as root was the riskiest code of the backup. The difference from RoofController stays.
- **Programs on PATH** (#1385). A release-based SkyMonitor CameraAgent install and upgrade put the host's
  `hvo-sky-linux-<arch>` in `~/.local/bin/hvo-sky`, with `/usr/local/bin` as the next choice only when writable without
  sudo. The default folder order matches RoofController; SkyMonitor chooses the first writable folder rather than
  adding RoofController's `cli.folder` setting. The release's installer goes in both `/usr/local/sbin/hvo-sky-install`
  and `~/.local/bin/hvo-sky-install`. Both kinds are checked against release.json and SHA256SUMS, published atomically
  with mode `0755`, reported by `--plan`, and projected into `install-programs.json` beside the install record. Upgrade keeps replaced files as
  `.previous`. The retained programs belong to the pending checkpoint and image pair described in section 6.
  - SkyMonitor keeps per-image program sets and cached files under the instance's deployment state. The machine
    install-record sidecar is their summary, consistent with D10. It uses the same atomic write and
    file and directory synchronization as the schema-2 machine record. An image-only install has no
    release programs to publish. Shared command paths take the version last published from a verified release.
  - SkyMonitor removes a last-owner runtime client on uninstall and restores it on reinstall. Unlike RoofController,
    it retains the installer for a preserved instance's `reinstall`; purge removes the last-owner installer copies
    and `.previous`, after the deletion is durably completed. Interrupted program cleanup remains resumable from
    retained operation evidence. Shared commands remain while another instance needs them. Both current and
    `.previous` files are checked against publication records before replacement; unowned or externally changed files
    are preserved and reported, as on removal. These differences follow SkyMonitor's preserved-instance lifecycle.
  - Destination folders and their ancestors must be owned by the installing user or root and have no group/world
    write permission, including destinations kept in existing records. Production checks reach the filesystem root;
    admitted isolated roots map every destination below their own virtual root. New directories receive explicit safe
    modes. Publication keeps the verified stage open and checks its device/inode at the renamed destination. A durable
    intent records the old and desired checksums before either slot changes, so an interrupted publication can resume
    without accepting an unowned `.previous`.
  - A completed install re-run also publishes missing release commands and then settles both record views,
    without redeploying an otherwise unchanged container.
  - After publication, command hints use an existing durable installer path. SkyMonitor completes its current
    operation without RoofController's installer handover; subsequent commands execute the newly installed release.
- **Common flags:** `--plan` (read-only; prints what would be created, changed, unchanged or refused), `--answers FILE`
  (strict JSON with a schema version; unknown members are errors), `--json`, `--version` and `-h`/`--help` (prints on
  stdout and exits 0).
- **Wizard:** on a terminal, a Terminal.Gui wizard in the HVO Dark theme, which skips pages with nothing to ask. Without a
  terminal it asks nothing.
- **Exit codes:**

  | Code | Meaning |
  | --- | --- |
  | 0 | Done (with `--plan`: can be carried out) |
  | 1 | A step failed; re-running continues |
  | 2 | Command line or answers invalid, or a question had no one to answer it |
  | 3 | Refused and nothing changed |
  | 130 | Interrupted |

- **Secrets** are never accepted on the command line and never written to answers, install records or logs. They are
  given interactively, from a file flag (`--*-password-file`), or generated.
- **Idempotent:** a second run with the same answers prints "Nothing to change".
- **Install record:** `/etc/hvo-<product>/install.json` for machine roles, `~/.config/hvo-<product>/install.json` for user
  roles. It holds no secrets: `version`, `previousVersion`, `rolledBackFrom`, and the roles or instances installed.
- **Which record is authoritative** (decision D10). Every fact about an installation has one authoritative record:
  what is installed, its release, its previous release and its limits.
  - A product either makes the install record that record, or keeps per-instance records authoritative and writes the
    install record as a view of them.
  - A view is written after the records it reflects and is rebuilt from them. Nothing decides anything from it, and a
    view that disagrees with them is the one that is wrong.
  - Reason: a fact kept in two authoritative places drifts after an interrupted operation. A product with several
    instances per host (SkyMonitor) commits each instance's change in that instance's own records, under its own lock,
    while the machine-level file is one per host.
  - **SkyMonitor's install record is a summary view** (owner decision of 2026-10-18), at `/etc/hvo-sky/install.json`
    under the flat `hvo-sky` paths (decision 5, #1331).
    - The per-instance records stay authoritative. The instance manifest names the running and previous images.
      #1343's release records own the installed version: `<instance root>/state/deployment/image-distribution.json`,
      with the previous image's record in `image-distribution.records.json`.
    - At every settle, after those records are written, each instance's entry reads `version`, `previousVersion` and
      `rolledBackFrom` from the release records, by image ID.
    - The entry adds `lastImageChange`: whether the image last changed by upgrade or by rollback. That decides whether
      the previous image's release is `previousVersion` or `rolledBackFrom`.
  - **Today:** RoofController's install record is authoritative, and its upgrade, rollback and uninstall read it.
    SkyMonitor's view is #1315.
- **Data** goes under `/var/lib/hvo-<product>/`, or a product root the operator chooses through a root-owned host policy.
- `<product>` is the short name used in the binaries: `roof` or `sky` (section 9).

## 6. Upgrade and rollback

### CameraAgent state contract baseline

**0.10.0-beta.1** is the first release of the clean-install state contract `cameraagent-silo-state-v1`.
The CameraAgent image declares the contract in
`io.hvo.skymonitor.state-contract`. The installer records it in the instance manifest and in the
installed image identity. The manifest, installation journal, installation result and machine
install record use schema 2. The image lifecycle operation journal uses schema 4, and the
database checkpoint manifest uses schema 2.

A pre-baseline Silo instance may already have the same identifier in its storage binding. Installer
admission requires the baseline record schemas and the new OCI label in the retained image identity.
The binding and individual SQLite schema numbers do not establish installer admission on their own.

A site moving from a release before 0.10.0-beta.1 provisions a fresh instance on empty storage. Install and
lifecycle preflight refuse records or images outside this contract and direct the operator to a
fresh install. They do not adopt earlier state, resume its journals, or apply its checkpoints.
The [cutover plan](planning/cameraagent-silo-cutover.md) governs site rollout.

The baseline keeps clean install, installation verification, data-preserving uninstall, reinstall,
and purge with exact instance confirmation. Later builds that declare the same contract can use
`cameraagent upgrade` and `cameraagent rollback` through the retained Compose and image identities.
Until 0.10.0-beta.1 ships, fold schema changes into the initial EF Core migration and baselined SQLite
stores owned by [#2179](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/2179). From 0.10.0-beta.1
onward, later releases must maintain upgrade, reinstall, migration and rollback support from this
baseline. A schema or state-contract change after shipment requires an explicit, verified transition.

Before replacing an image, the installer pauses and drains capture, stops the writers and records
a verified checkpoint of the small stores for recovery before commit. A failed or interrupted
operation is recovered against its own authenticated checkpoint and exact predecessor records.
An operation past commit resumes its settlement. The operator can use `cameraagent rollback` to run
the retained previous image against the same live contract and keep post-upgrade data. The
[installer runbook](runbooks/deployment-installer.md#checkpoint-and-image-recovery) describes the
recovery procedure and checkpoint boundaries.

### RoofController policy

RoofController retains its own upgrade notes, state checks and one-release rollback policy.
Its installer verifies Stop and roof idle before replacing the image and restores the previous
version if candidate health fails.

### State contracts

Decision D1. `stateContracts` names each component by its image key (section 4). For each store the component keeps,
it gives the newest schema a release may write, and every schema it reads:

```json
"stateContracts": {
  "controller": {
    "identity": { "writes": 3, "reads": { "min": 1, "max": 3 } },
    "settings": { "writes": 1, "reads": { "min": 1, "max": 1 } }
  },
  "hatEmulator": {}
}
```

- **Shape:** `stateContracts.<component>.<store>` is `{ "writes": W, "reads": { "min": A, "max": B } }`. Each is a
  whole number from 1, with `A ≤ W ≤ B`, and the release reads every schema from `A` to `B`. A component that keeps no
  state has `{}`. The component's image carries the same object in its `io.hvo.<product>.state-contracts` label, and the
  release checks refuse a release whose label and `release.json` differ.
- **A store** is one durable store the component writes: a database, or a set of files with one schema version. A
  store's schema is the version that store records about itself, never one the installer infers.
- For RoofController, a target writes beyond the previous reader's range when
  `target.writes > installed.reads.max`. Its installer uses these values to distinguish a same-state
  swap from a transition requiring a checkpoint. SkyMonitor publishes its per-store schema inventory
  in the same shape. CameraAgent lifecycle admission uses the baseline identifier and the candidate's
  catalog, Identity and raw journal declarations.

CameraAgent keeps the local environmental observation journal at
`raw/.environment/environmental-observations.db` under the instance state root.

### SkyMonitor Silo layout (#1966)

The CameraAgent Silo layout ([cutover contract](planning/cameraagent-silo-cutover.md)) is the default
for a clean install. Omitting `--storage-layout` or specifying `--storage-layout silo` selects it.
The interactive installer uses the same defaults and carries explicit Silo image and archive options into its answers.

- Its instance manifest declares component schema `cameraagent-silo-install-v1` and Compose template
  `cameraagent-silo-compose-v1`. It supports `status`, `first-resume`, `silo-verify`, `uninstall`,
  `purge`, `reinstall`, `upgrade`, `rollback`, `catalog select` and `catalog rollback`. Backup and disaster recovery of the Silo store are
  tracked in [#1972](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1972).
- The manifest records the pinned stock Silo image and the data root's filesystem and device. It also records the
  bucket, store and binding identities, the writer key's ID and the exact versions and digests of the instance marker and
  fence. It holds no secret.
- The stock Silo image is pinned by release, index digest and each platform's manifest and config digests. The
  installer and the release tool share that pin, and `release.json` names it as `images.silo` (section 4). HVO does not
  ship the image. An offline site stages a `docker save --platform` archive of the pinned reference, which the installer
  verifies blob for blob before loading. [The runbook](runbooks/cameraagent-silo-runtime.md) gives the licence
  rationale, who provisions and verifies the image, and the offline route.
- The CameraAgent image declares `io.hvo.skymonitor.first-boot-contract=cameraagent-first-boot-pause-v1`; a Silo-layout
  install refuses an image without it.
- The data-root filesystem must keep the larger of 10 GiB and 5 % free. `silo-verify` reports `ready`, `degraded` (only
  the optional archive is unreachable) or `not-ready`. It exits 3 when not ready, and `first-resume` then refuses.
- An optional archive store is verified read-only over HTTPS, chained to an operator authority for the endpoint's
  name, with a signed versioning read. Plain HTTP is refused, and there is no lab exception. The manifest records its
  endpoint, bucket, authority and server-certificate SHA-256 and access key ID, never the secret.
- A re-run of a completed Silo-layout install with the same inputs verifies the instance and changes nothing. With
  other inputs it refuses and names `silo-verify`. Use the lifecycle commands for admitted image changes.
- `silo-verify`, `first-resume` and a completed re-run verify the instance as it runs now. They correlate its records
  before any authenticated call, require the running CameraAgent on its recorded image and this daemon's own platform
  child of the Silo pin, and require the private network to hold only the instance's two containers. They read the
  binding back from CameraAgent's own network namespace.
- An interrupted Silo-layout install resumes from an owner-only, durable bootstrap intent and writer credential record.
  It adopts only what this instance's own attempt created, and refuses any other or ambiguous state before the store
  is touched.

### Copies of the small stores

Decision D2. Before an image change can affect a small store, a restorable copy of it exists.
- **When to copy.** A product copies at least when the target declares a state change. It may copy on every upgrade.
- **Rollback.** A rollback restores the copy of every store the release it returns to cannot read. It may restore
  every copied store. It says which changes made since the upgrade are lost, before it changes anything.
- **What is copied.** Only the small stores of section 6's state contracts. Bulk data is never copied (above).
- **Full backups** are the `backup` command (section 5).
- Reason: a copy is what makes a breaking change recoverable. Copying only small stores keeps an upgrade's time and
  disk independent of how much the product has captured.
- **Today:**
  - RoofController takes a backup of its own data, as its `backup` command does, as the first step of every
    upgrade, before it puts the release's installer in place; that data is a few small files. Its
    `rollback --restore-backup` puts back the stores the release before cannot read, after asking (or with `--yes`).
  - SkyMonitor pauses and drains capture and stops its writers before checkpointing the selected SQLite stores
    and protected records. The checkpoint recovers an interrupted image change. An explicit rollback within
    the baseline contract runs the retained image on live state.
  - SkyMonitor's `backup` command is #1353; RoofController has one.

## 7. Container resources

- **Per-product limits.** Every product container the installer creates has a CPU, memory and PID limit. The defaults
  and floors are set per product, because the products' work differs by an order of magnitude (owner decisions of
  2026-10-16 and HVO.RoofController#149):

  | Container | Default | Floor | **Today** |
  | --- | --- | --- | --- |
  | SkyMonitor CameraAgent | 2 vCPUs, 4 GiB, 256 PIDs | 2 vCPUs, 4 GiB | Meets it (#1324) |
  | RoofController controller | 1 vCPU, 2 GiB | 1 vCPU, 1 GiB | Meets it (HVO.RoofController#151) |

  The operator raises a limit as needed, and an installer refuses one below the floor.
- **Setting them.** Limits are given at install, through flags or `--answers`. They are kept in the install record
  (section 5), kept across upgrade and rollback, and changed by re-running the installer with new limits (the re-run
  of section 5).
  - **Today:** RoofController changes them by a re-run. SkyMonitor keeps them in the instance manifest and refuses a
    re-run with different limits; the re-run is #1315.
- **Hosts that cannot enforce the limits** (owner decision of 2026-10-18, "match Roof fully"). Before it installs or
  upgrades, the installer checks that Docker enforces the CPU, memory and PID limits and that the host has the CPUs.
  - **Install and upgrade refuse** a host that cannot enforce them, and change nothing. There is no override.
  - **A rollback proceeds.** It is the recovery path, so it applies the limits Docker can enforce and gives a loud
    warning on standard error and in the logs. Everything else that refuses an install still refuses a rollback.
  - Reason: an unbounded container can starve the host it shares, but refusing a rollback could leave an operator
    with no way back from a failed release.
  - RoofController applies that policy to rollback. SkyMonitor applies it to pending-upgrade recovery starts
    and records rollback warnings in the operation's install log. On a host without CPU quota support,
    the recovery model withholds the CPU limit and keeps the other recorded limits. The instance retains its
    full resource settings.
- **Roof controller.** Its limits are on by default because its container's supervisor turns the drive relays off
  whenever the controller exits or crash-loops (HVO.RoofController#138):
  - It writes relay-off and verifies the register by reading it back.
  - If the read-back fails, it reports the relays as unverified and warns that the drive may still be running. It then
    retries, and holds the controller stopped until a read-back shows every relay off.
  - A kill of the whole container, which ends the supervisor too, is outside this protection.

## 8. Runtime client and settings

Each product with operator settings provides `hvo-<product>` (CLI) and `hvo-<product> ui` (terminal UI), as RoofController
does with `hvo-roof`. It sits beside the installer: the installer owns lifecycle, the client owns runtime.

- **Commands:** `status [--watch]`, `info` (versions), `config show|get|set|diff|apply`, `restart`, `login`/`logout`,
  `ui`, plus product verbs. Every command has `--json` and documented exit codes.
- **Transport:** the product's HTTP API only. It authenticates with an API key or a password session; the credentials
  file is `0600` in a `0700` folder; environment variables override it; secrets never go on the command line.
- **Settings catalogue:**
  - Every operator setting is described once: group, type, range, required role, safety class, whether it applies
    after a restart, and whether it is secret.
  - The API serves it, and the web UI, CLI and terminal UI build their forms from it.
  - Validation is on the server, and a value overridden by environment or secret files is shown as read-only with its
    source.

## 9. Naming

| | RoofController | SkyMonitor |
| --- | --- | --- |
| Installer | `hvo-roof-install` | `hvo-sky-install` |
| Runtime client | `hvo-roof` | `hvo-sky` |
| Images | `roof-controller`, `roof-hat-emulator` | `skymonitor-cameraagent` |
| Image keys (`release.json`, `stateContracts`) | `controller`, `hatEmulator` | `cameraAgent` |
| Config root | `/etc/hvo-roof` | `/etc/hvo-sky` |
| Data root | `/var/lib/hvo-roof` | `/var/lib/hvo-sky` |

## 10. Decisions

Owner decisions of 2026-10-16:

1. **Signed manifests:** SkyMonitor's key-signed manifests and indexes are **retired**. Section 4's trust model is the only
   one.
2. **Bulk data in upgrades:** upgrades **never copy bulk files** (section 6). Full backups are the explicit `backup`
   command. SkyMonitor #1044's scope narrows to that command.
3. **Container resources:** set per product and adjustable by the operator (section 7). CameraAgent gets at least
   2 vCPUs and 4 GiB. The roof controller gets 1 vCPU and 2 GiB, with a floor of 1 vCPU and 1 GiB
   (HVO.RoofController#149). This revises draft 3's single 2 vCPU / 4 GiB minimum for every product.
4. **Naming:** `hvo-sky-install` and `hvo-sky`, confirmed (section 9).
5. **File locations:** RoofController's flat style, named after the binary, is the pattern: `/etc/hvo-roof` and
   `/etc/hvo-sky`. RoofController does not migrate (HVO.RoofController#132, closed). SkyMonitor renamed its paths before its
   first release (#1313).

Owner decisions of 2026-10-18:

6. **Hosts that cannot enforce the container limits:** match RoofController fully. Both products refuse install and
   upgrade with no override, and a rollback proceeds with a loud warning (section 7). SkyMonitor met its upgrade and
   rollback part in #1357.
7. **Draft 4's decisions** D1, D2 and D5–D10, below, are accepted as written. SkyMonitor met the resulting rules (the
   `cameraAgent` image key, `stateContracts` and the `created` label) in #1359 (#1364). The CameraAgent
   baseline uses the admission rules in section 6.

- **D1. State contracts:** RoofController's per-component, per-store `writes` and `reads` shape, with the SkyMonitor
  mapping (section 6).
- **D2. Small-store copies:** a product copies at least when the target declares a state change, and may copy on every
  upgrade. A rollback restores what the older release cannot read (section 6).
- **D5. Platform names:** OCI architectures (`amd64`, `arm64`) for images, .NET runtime IDs (`linux-x64`) for binaries
  and asset platforms (section 4).
- **D6. Image keys:** logical lower-camel-case component keys (`controller`, `cameraAgent`), shared with
  `stateContracts` (section 4).
- **D7. Asset kinds:** the vocabulary of section 4, with `catalog` for data packages.
- **D8. Pre-release exception:** the owner's tag is the human step, and the end-to-end job is linux-arm64 only
  (section 3).
- **D9. `org.opencontainers.image.created`:** required (section 1).
- **D10. Authoritative record:** one authoritative record per fact; the install record is either it or a view of
  per-instance records (section 5). SkyMonitor keeps `/etc/hvo-sky/install.json` as a summary view, written after the
  per-instance records, which stay authoritative. #1343's release records own the installed version. At every settle
  the view reads `version`, `previousVersion` and `rolledBackFrom` from them, and adds `lastImageChange`.

Owner decision of 2026-10-18, on #1353:

8. **SkyMonitor backups are owner-only:** the instance's service account's, not root's (section 5). `backup` and
   `restore` run as that account, not started with `sudo`, and keep archives `0600` in its own `0700` folder,
   `<product-root>/backups`. The account already owns every live store and secret and is in the `docker` group, so a
   root-only archive adds almost no protection. Refined the same day: before changing anything they may use the
   installer's existing folder-preparation helper only to create that folder and an absent instance root, both the
   account's, and a restore leaves the machine install record to `reinstall`. RoofController keeps running its backups
   as root. SkyMonitor met this in #1353 (#1369).

Owner decision of 2026-11-27, on #1964:

9. **0.10.0-beta.1 starts fresh.** The CameraAgent lifecycle uses the
   [baseline contract](#cameraagent-state-contract-baseline) for install, image transitions and checkpoint recovery.
