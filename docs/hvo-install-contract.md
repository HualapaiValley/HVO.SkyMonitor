# HVO Product Installation and Release Contract

Status: **draft 4**, 2026-10-18. It includes the owner's decisions of 2026-10-16 and 2026-10-18 (section 10). The
owner accepted draft 4's decisions on 2026-10-18. Owner: roys.

This contract is the one install and release experience shared by the HVO products. It covers HVO.RoofController
and HVO.SkyMonitor: CameraAgent now, and LogicHost when its installer lifecycle exists. Each product implements it in its own
repository; it does not create a shared codebase. RoofController 4.1.1 already implements most of it, so where this
document says "as RoofController does", its implementation is the reference. Conformance work is tracked in each
repository's epic. Where a rule says **Today**, it records how each product meets it, and the issue that brings a
product that does not.

Product-specific safety rules stay product-specific and are never weakened by this contract. Examples are the roof
controller's verified Stop and idle-only redeploy, and CameraAgent's capture pause and state-compatibility gate.

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
    `created` is not an RFC 3339 time. SkyMonitor's CameraAgent image carries `version` and `revision` only (#1359).
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
2. A release PR. It renames `## [Unreleased]` in `CHANGELOG.md` (Keep a Changelog) to the version and adds a non-empty
   `docs/upgrade-notes/X.Y.Z.md`.
3. An annotated tag `vX.Y.Z` (or `vX.Y.Z-rc.<n>`) on the release branch. That is the default branch, except in
   SkyMonitor, whose default branch is the `development/v1` integration branch and whose release branch is `main`.

The release workflow then:
- checks that the tag matches the version, is on the release branch, and has its CHANGELOG section and upgrade notes;
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
release candidates. An integration pre-release follows them except in these ways:
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

**Today:** SkyMonitor cuts pre-releases this way (`.github/workflows/prerelease.yml`, #1309). Its final releases are
#1310. RoofController cuts no integration pre-releases; its release candidates follow the final-release path.

## 4. Release assets

Every release carries assets with these names and kinds:

| Asset | Kind | Contents |
| --- | --- | --- |
| `hvo-<product>-install-<rid>` | `installer` | The installer: single-file, self-contained, per runtime ID (`linux-arm64`, `linux-x64`, `osx-arm64` where supported) |
| `hvo-<product>-<rid>` | `cli` | The runtime client, when the product has one |
| `install.sh` | `install-script` | The entry point, with the release version baked in |
| `<image>-<version>-linux-<arch>.oci.tar` | `image-archive` | An offline image archive per architecture |
| `<catalog version>.bundle.tar.gz` | `catalog` | A data package the release is tested with, under its own version (section 1): SkyMonitor's approved star catalog, today `hyg-v4.2-p3-s2-r1.bundle.tar.gz` |
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
  and are always referenced by digest. Image names: `roof-controller`, `roof-hat-emulator`, `skymonitor-cameraagent`,
  and later `skymonitor-logichost`.
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
  | SkyMonitor | `cameraAgent` (`skymonitor-cameraagent`), later `logicHost` (`skymonitor-logichost`) |

  - Reason: RoofController's released installers already read `images.controller`, while SkyMonitor has published
    only integration pre-releases, so this choice changes the product that has no released readers. A component's key
    also outlives a rename of its image.
  - **Today:** RoofController uses these keys. SkyMonitor uses the image name, `images.skymonitor-cameraagent` (#1359).
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
  - Installers refuse an older version on `upgrade`; only `rollback` goes back.

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
| `rollback` | Return to the release recorded as previous |
| `backup` / `restore FILE` | A root-only, self-verifying archive of the product's own data, with a manifest |
| `uninstall [--purge --confirm HOST --backup\|--no-backup]` | Remove what was installed; `--purge` also removes data |
| `status` | What is installed here, its versions and health |

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

- **Before upgrading**, the installer prints the upgrade notes of every release between the installed and the target
  version. It refuses an older target and points to `rollback`.
- **Swap:**
  1. The previous image or container is kept.
  2. The product-specific safety gate runs (verified Stop and roof idle; capture paused and drained).
  3. The new version starts and must pass its health check.
  4. If it fails, the previous version is restored automatically.
- **Rollback** goes back exactly one release.
- **State:**
  - Each component declares a state contract in its image labels and in `release.json`'s `stateContracts`. Its shape
    is given below.
  - If the contract is unchanged, upgrade and rollback are a swap: no operator assertion is needed, and a rollback
    needs no restore. A product may still copy its small stores on every upgrade, and restore them, as the copies
    below allow (D2).
  - A schema change must stay readable by the previous release for one release: add first, remove in a later
    release. A rollback then still needs no restore.
  - When a release cannot meet that, it declares a breaking contract change. The installer then snapshots only the
    small databases before migrating (SQLite online backup, or a native database backup), and rollback restores that
    snapshot. The installer says plainly that changes made after the upgrade are lost.
  - **Bulk append-only data is never copied by an upgrade:** frames, archives, derived products, catalogs. It is only
    protected from deletion while the upgrade runs. Copying it does not scale; one CameraAgent instance holds hundreds
    of GB at steady state.
- **Full backup** of everything is the separate `backup` command, run on the operator's schedule. It is not a hidden
  step of every upgrade.

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
- **A breaking change** is a target that writes a schema, for some store, newer than the installed release reads
  (`target.writes > installed.reads.max`). A rollback to the installed release would then meet a store it cannot read.
  Any other change keeps rollback a swap.
- Reason: per-store numbers let the installer decide "swap" or "breaking" by comparing two `release.json` files, with
  no knowledge of the product's schemas.
- **Today:** RoofController declares this shape in `build/release-assets.py` and in its controller Dockerfile's label
  `io.hvo.roof.state-contracts`, which its release tests check agree. Its installer compares contracts before an
  upgrade and a rollback. SkyMonitor
  declares its state as one contract name (`io.hvo.skymonitor.state-compatibility`, `cameraagent-state-v2`) plus
  boundary labels, and treats any changed boundary label as a state migration (#1340). `release.json` omits
  `stateContracts` (#1359).

**The SkyMonitor mapping.** Component `cameraAgent`, with one store for each SQLite database an instance keeps:

D1's target is a whole-number schema for each store in `stateContracts`. The table gives the marker each store keeps
its schema in today, and what is declared:

| Store | Database (under the instance's state root) | Schema marker today | Declared today |
| --- | --- | --- | --- |
| `identity` | `identity/cameraagent_identity.db` | The applied EF Core migrations in `__EFMigrationsHistory` (their number is the store's schema) | `io.hvo.skymonitor.identity-migration` names the newest, `20260827053715_InitialIdentity`: writes 1, reads 1 to 1 |
| `rawIngress` | `raw/journal/raw-ingress.db` | `PRAGMA user_version` | `io.hvo.skymonitor.raw-ingress-schema` 15 and `raw-ingress-migration` `canonical-v13-v14-to-v15`: writes 15, reads 13 to 15 |
| `artifactOutbox` | `outbox/artifact-outbox.db` in each storage root (`raw`, `archive`, and every FileStorage root) | The `artifact_outbox_schema.version` column | Not declared |
| `fleetStatus` | `raw/.fleet/fleet-status.db` | The `fleet_status_metadata.schema_version` column | Not declared |
| `environmentalOutbox` | `raw/.environment/environmental-observation-outbox.db` | `PRAGMA user_version` | Not declared |
| `executionEvidenceOutbox` | `raw/evidence/execution-evidence-outbox.db` | The `execution_evidence_schema.version` column | Not declared |
| `timeLapses` | `raw/.time-lapses/time-lapses.db` | `PRAGMA user_version` | Not declared |
| `nightlyProducts` | `raw/.nightly-products/nightly-products.db` | `PRAGMA user_version` | Not declared |
| `localAutomations` | `raw/.automation/local-automations.db` | `PRAGMA user_version` | Not declared |

- These are the stores the state snapshot of #1340 copies.
- The star catalog is a data package (section 1), not a store. The catalog labels keep the versions CameraAgent
  accepts: `io.hvo.skymonitor.catalog-contract` and `catalog-manifest-version`.
- Declaring every store's schema in `stateContracts`, from these markers, is outstanding conformance work (#1359).

### Copies of the small stores

Decision D2. Before an upgrade can change a small store, a restorable copy of it exists.
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
  - SkyMonitor snapshots its SQLite stores only when the candidate declares a state migration (#1340). It pauses and
    drains capture and stops the instance first, then takes the snapshot. Its rollback restores the snapshot the
    upgrade it undoes took, and says what is lost.
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
  - **Today:** RoofController behaves this way (HVO.RoofController#151). SkyMonitor's install refuses such a host (its
    host prerequisites). Its upgrade refusal and rollback warning are #1357.
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
| Images | `roof-controller`, `roof-hat-emulator` | `skymonitor-cameraagent`, `skymonitor-logichost` |
| Image keys (`release.json`, `stateContracts`) | `controller`, `hatEmulator` | `cameraAgent`, later `logicHost` |
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
   upgrade with no override, and a rollback proceeds with a loud warning (section 7). SkyMonitor's upgrade and rollback
   part is #1357.
7. **Draft 4's decisions** D1, D2 and D5–D10, below, are accepted as written. SkyMonitor's resulting gaps (the
   `cameraAgent` image key, `stateContracts` and the `created` label) are #1359.

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
