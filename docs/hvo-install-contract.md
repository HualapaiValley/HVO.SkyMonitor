# HVO Product Installation and Release Contract

Status: **draft 3**, 2026-10-16. It includes the owner's decisions of 2026-10-16. Owner: roys.

This contract is the one install and release experience shared by the HVO products. It covers HVO.RoofController
and HVO.SkyMonitor: CameraAgent now, and LogicHost when its installer lifecycle exists. Each product implements it in its own
repository; it does not create a shared codebase. RoofController 4.1.1 already implements most of it, so where this
document says "as RoofController does", its implementation is the reference. Conformance work is tracked in each
repository's epic.

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

## 4. Release assets

Every release carries assets with these names and kinds:

| Asset | Contents |
| --- | --- |
| `hvo-<product>-install-<rid>` | The installer: single-file, self-contained, per runtime ID (`linux-arm64`, `linux-x64`, `osx-arm64` where supported) |
| `hvo-<product>-<rid>` | The runtime client, when the product has one |
| `install.sh` | The entry point, with the release version baked in |
| `<image>-<version>-linux-<arch>.oci.tar` | An offline image archive per architecture |
| `release.json` | The machine-readable release manifest |
| `SHA256SUMS` | Covers every asset |
| `CHANGELOG.md` section and upgrade notes | In the release body and in `release.json` |

- **Images** are pushed to `ghcr.io/hualapaivalley/<image>` with tag `<version>`, plus `latest` for the newest final release,
  and are always referenced by digest. Image names: `roof-controller`, `roof-hat-emulator`, `skymonitor-cameraagent`,
  and later `skymonitor-logichost`.
- **`release.json`** follows RoofController's schema 1:
  - `version`, `tag`, `prerelease`, `commit`, `created`, `repository`;
  - `images.<name>` with `repository`, `tag`, `digest`, `reference` and `platforms`;
  - cumulative `upgradeNotes[]`;
  - `assets[]` with name, kind, platform, size and sha256.

  It adds `images.<name>.offlineArchives[]` and `stateContracts` (section 6).
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
  - The release environment requires the owner's approval to publish.
  - Installers refuse an older version on `upgrade`; only `rollback` goes back.

## 5. Installer

### Entry point

```bash
curl -fsSL https://github.com/HualapaiValley/<repo>/releases/latest/download/install.sh | bash -s -- [installer arguments]
```

`install.sh` does the following. RoofController's implements all of it except installing from offline image archives, which HVO.RoofController#127 adds:
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
  not contact the registry in that mode. This is new for both products; RoofController's `--from` still pulls images from
  GHCR until HVO.RoofController#127;
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
  - Each component declares a state contract (for example `cameraagent-state-v2` with raw-ingress schema 15) in its
    image labels and in `release.json`.
  - If the contract is unchanged, upgrade and rollback are a pure swap. Nothing is copied and no operator assertion is
    needed.
  - A schema change must stay readable by the previous release for one release: add first, remove in a later
    release. Rollback is then still a swap.
  - When a release cannot meet that, it declares a breaking contract change. The installer then snapshots only the
    small databases before migrating (SQLite online backup, or a native database backup), and rollback restores that
    snapshot. The installer says plainly that changes made after the upgrade are lost.
  - **Bulk append-only data is never copied by an upgrade:** frames, archives, derived products, catalogs. It is only
    protected from deletion while the upgrade runs. Copying it does not scale; one CameraAgent instance holds hundreds
    of GB at steady state.
- **Full backup** of everything is the separate `backup` command, run on the operator's schedule. It is not a hidden
  step of every upgrade.

## 7. Container resources

- **Defaults.** Every product container the installer creates is limited to at least **2 vCPUs and 4 GiB of memory**,
  with a PID limit. Operators raise the limits as needed.
- **Setting them.** Limits are given at install, through flags or `--answers`. They are kept in the install record,
  kept across upgrade and rollback, and can be changed with a re-run.
- **Host check.** The installer checks before installing that the host and Docker can enforce the limits and that the
  host has the capacity.
- **Roof controller.** Its limits additionally need evidence that the HAT and relays fail safe (motor drive
  de-energised) when the controller process is killed.

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
| Config root | `/etc/hvo-roof` | `/etc/hvo-sky` |
| Data root | `/var/lib/hvo-roof` | `/var/lib/hvo-sky` |

## 10. Decisions

Owner decisions of 2026-10-16:

1. **Signed manifests:** SkyMonitor's key-signed manifests and indexes are **retired**. Section 4's trust model is the only
   one.
2. **Bulk data in upgrades:** upgrades **never copy bulk files** (section 6). Full backups are the explicit `backup`
   command. SkyMonitor #1044's scope narrows to that command.
3. **Container resources:** at least 2 vCPUs and 4 GiB, adjustable by the operator (section 7).
4. **Naming:** `hvo-sky-install` and `hvo-sky`, confirmed (section 9).
5. **File locations:** RoofController's flat style, named after the binary, is the pattern: `/etc/hvo-roof` and
   `/etc/hvo-sky`. RoofController does not migrate (HVO.RoofController#132, closed). SkyMonitor renamed its paths before its
   first release (#1313).
