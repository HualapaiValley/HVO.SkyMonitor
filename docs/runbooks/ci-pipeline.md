# CI Pipeline Runbook

This runbook describes the required current-head checks in `.github/workflows/ci.yml` and their local equivalents.

This is the **full qualification** profile for main/release and explicitly
requested qualification. Ordinary PRs targeting `development/v1` use the
[short v1 profile](../development-v1.md), including after a base sync. Classifier
output does not by itself authorize an extra full-workflow dispatch for a v1 PR.
Whole-repository ShellCheck remains in Quality; exhaustive category discovery
remains in Build. Both are intentionally absent from the short workflow.

## Development v1 Preflight Budget

**Development v1 / Preflight** has a four-minute (240-second) job timeout,
per the owner decision in #1365. This supersedes the three-minute (180-second)
budget retained by #1333 when the secret-scanner contract became conditional.
PR #1364 took 173 seconds with the contract selected; the new budget provides
67 seconds of headroom for that measured run. Ordinary PRs remain near one minute.
All checks, their order and selection rules, job names and required-check names
remain unchanged.

## Promotion ancestry prerequisite

Before **every** promotion from `development/v1` to `main` becomes ready, including promotions without release
tags, follow [Before every promotion](releases.md#before-every-promotion). A merge-commit promotion leaves a commit
on `main` that development lacks. The workflow's behind-check tolerates verified promotion merges, but that does
not satisfy `main`'s strict up-to-date protection; `Required CI` and branch protection stay unchanged.

The operator/coordinator opens the sync PR with authenticated Git/`gh`, proves its normal merge tree equals the
**pre-sync development tree**, and takes it through review, both Development v1 required checks, and a merge-commit
merge. A conflict or changed tree stops the sync. Workflow `GITHUB_TOKEN` pushes do not start downstream push CI,
and PR workflow runs may require approval; the operator step avoids relying on that token or adding secrets or
permissions. Wait for the green Development v1 **push run on the merged development head**, then verify ancestry:

<!-- promotion-sync-ready -->
```bash
(
  set -euo pipefail
  git fetch origin main development/v1
  git merge-base --is-ancestor origin/main origin/development/v1 || {
    echo "Keep the promotion draft: sync main into development/v1 first." >&2
    exit 1
  }
  git rev-parse origin/main origin/development/v1
)
```
<!-- /promotion-sync-ready -->

Record these source/target SHAs and the sync tree proof with the promotion's updated-source review. If `main` was
already an ancestor, record the no-sync result instead. Mark the promotion ready only after this prerequisite;
then full `ci.yml` qualification supplies `Required CI` on the reviewed head. Recheck when either branch moves.
A green sync PR is integration evidence and does not replace promotion qualification.

## Required Checks

| Check | Enforced behavior |
| --- | --- |
| **Change Classification** | Fail-closed selection of the full matrix for pushes and behavior-affecting pull requests or reduced mode for explicitly allowlisted documentation/developer-environment pull requests. |
| **Catalog Contracts** | Full-mode hosted build and smoke validation of the exact HYG v42 production catalog contracts, retained as a one-day workflow artifact. Skipped in classified reduced mode. |
| **Quality** | The full-history [secret scan](#secret-scan), workflow lint, syntax and documentation audits, the Unit-selection completeness control, lightweight environment/classification contracts, and Compose validation. Full mode also enforces formatting, package vulnerability/deprecation policy, and pinned .NET tools; manual dispatch additionally validates the historical Phase 14 acceptance inventory. Reduced mode does not restore or audit application packages it cannot affect. |
| **Deployment Contracts** | Deployment-relevant pull requests run the coordinator watchdog/failure contracts and current campaign-shape contracts, plus only the affected exhaustive catalog, split-host, or installer suite selected by the classifier. Main/release/manual runs execute every exhaustive suite. Otherwise its planned `skipped` result is required. |
| **Build** | Warning-clean solution Debug and Release builds plus complete, disjoint behavioral category discovery. Never component-scoped, so no component plan can hide a warning or a category-count drift. Skipped only in classified reduced mode. |
| **Unit Tests** | 10606 Unit cases with an intentionally invalid Docker endpoint and per-project TRX/Cobertura paths, plus four TRX-only supplemental runs (see [Supplemental Unit selections](#supplemental-unit-selections)) so every project that owns Unit cases is executed. Runs only for the complete solution plan; component plans run their own per-project commands inside their selected lanes including each lane’s owned supplemental Unit selection (#1261). |
| **Integration Tests** | 851 Integration-category cases across SQLite, filesystem object storage, SQL Server, Redis, Mailpit, forwarded-header, host integration, and the 7 repository graph/provider-boundary/publish cases in Architecture & Publish. LogicHost coverage includes clean/current-layout initialization, idempotency, schema, locking, and permission behavior. Runs only for the complete solution plan; component plans run the same per-project commands inside their selected lanes. The LogicHost assembly runs in its own lane beside the other five assemblies because it is the measured controlling path (#850); commands, results directories and TRX names are unchanged. |
| **Architecture & Publish** | Both category selections of the architecture project, and membership is the project's own category discovery rather than any narrower reading of "boundary": the 30 Unit-category boundary cases, including the host `Dockerfile`, fault-matrix discovery, and CameraAgent event-ID uniqueness contracts, and the 7 Integration-category repository graph/provider-boundary/MSBuild/publish cases; plus retained host publish manifests and self-contained installer publishes with SHA-256 manifests for Linux x64 and ARM64. Never component-scoped, so no component plan can skip the architecture or host-publish boundary. |
| **CameraAgent Migrations** | Exactly one canonical initial migration source for CameraAgent Identity plus zero pending CameraAgent EF model changes, built from the CameraAgent project root. Runs for every full-mode head. |
| **LogicHost Migrations** | Exactly one canonical initial migration source for LogicHost plus zero pending LogicHost EF model changes, built from the LogicHost project root. Runs for every full-mode head. Unreleased legacy-schema convergence is not supported by either host. |
| **Coverage Policy** | Schema validation of every aggregate and component coverage baseline plus rejection of any pull request that lowers a baseline rate, raises its tolerance, or drops a baseline file floor relative to the merge target. Runs for every full-mode head. |
| **Shared Libraries** | Component lane. Project-root restore/build of the shared test and runner project roots, the shared Unit and Integration selections, a `linux-x64` ProcessingRunner publish with a checksum manifest, and the shared component coverage baseline over 9 slots. |
| **CameraAgent Component** | Component lane. Project-root restore/build of the CameraAgent host, runner, and test project roots, the CameraAgent Unit and Integration selections, CameraAgent and `linux-x64` ReplayRunner publishes with checksum manifests and an opposite-host assembly check, and the CameraAgent component coverage baseline over 5 slots. |
| **LogicHost Component** | Component lane. Project-root restore/build of the LogicHost host and test project roots, the LogicHost Unit and Integration selections, the LogicHost publish with checksum manifest and an opposite-host assembly check, and the LogicHost component coverage baseline over 2 slots. |
| **Combined Protocol & Integration** | Component lane. Project-root restore/build and execution of the only suites allowed to compose both hosts, covering registration, identity, artifact transfer, fleet contracts, and other cross-host behavior, plus the combined component coverage baseline over 2 slots. |
| **Delivery Component** | Component lane. Project-root restore/build of the deployment CLI project root and the deployment CLI/distribution test roots, which also build the deployment contracts and release-tool projects they reference, against the exact production catalog contract; a `linux-x64` installer publish with a checksum manifest; and the delivery component coverage baseline over 2 slots. |
| **Coverage** | Aggregate rollup for the complete solution plan: exact source-path and branch merge of 23 expected reports, checked-in aggregate non-regression, and risk-file floors. The Coverlet 10.0.1 baseline is 84.3690% line and 66.3253% branch coverage. Component plans enforce their own baselines inside their lanes instead. |
| **Required CI** | One current-head aggregate whose expected job results are derived from the validated classifier plan. It fails when any expected check fails, times out, is canceled, is missing, is unexpectedly skipped, or is unexpectedly run, and it rejects the plan itself when the plan is incomplete, self-inconsistent, or invalid for the event. |

Each test invocation owns a category/project-specific result directory and TRX name. Coverage rejects any report count other than the expected 23, preventing missing or overwritten evidence.

Change Classification, Catalog Contracts, Quality, Coverage Policy, and Required
CI run on pinned `ubuntu-24.04` hosted runners. Deployment Contracts, Build, Unit
Tests, Integration Tests, Architecture & Publish, both migration checks, every
component lane, Coverage, and Coverage Badges remain on the labeled self-hosted
runners and also require `hvo-qualification`. Two x64 runners stay without that
label so ready `development/v1` checks are not queued behind a qualification
fan-out; see `docs/runbooks/ci-runner-capacity.md`. This allocation keeps the long
deployment harness off hosted minutes. Quality performs .NET setup, restore,
formatting, and package audit only for full-mode changes.

## Release Script Tests

`scripts/test:release-scripts` proves the integration pre-release scripts with fake `gh`, `skopeo`, `docker` and
`dotnet` commands ([prereleases.md](prereleases.md), #1309). Its fake `gh` answers as `gh api --include` prints, and
can fail after a complete-looking 200, so the tests prove every GitHub read (`scripts/lib/github-api.sh`) refuses a
transport failure, a 404 that is not GitHub's, and an empty, repeated or malformed body, before and after publishing.
It then runs `scripts/test:install-sh`, which pipes the release's `install.sh` into bash as `curl ... | bash` does,
with fake commands (`tests/fixtures/install-sh/`), and proves its checks, `--check`, `--from DIR`, the online
download of the release's small files, the verification of each file, the catalog, the exit codes and the hand-over ([deployment-installer.md](deployment-installer.md#installsh), #1311). Its tests run in
parallel. It always runs in Full Quality. **Development v1 /
Preflight** runs it after the secret scanner contract, reading the same classifier step, when the `release_scripts`
output selects it. On a PR that output is `true` when the change touches `scripts/release:assets`,
`scripts/release:check-image-labels`, `scripts/release:push-image`, `scripts/release:prerelease-gate`,
`scripts/release:prerelease-check`, `scripts/release:publish-prerelease`, the final-release scripts
(`scripts/release:version`, `scripts/release:notes`, `scripts/release:gate`, `scripts/release:check-environment`,
`scripts/release:publish` and `scripts/release:tag-latest`, #1310), `scripts/test:release-install`,
`scripts/test:release-scripts`, the libraries those scripts source (`scripts/lib/deployment-installer-qualification.sh`,
`scripts/lib/github-api.sh` and `scripts/lib/releases.sh`), `src/HVO.SkyMonitor.Deployment.Cli/install.sh`,
`scripts/test:install-sh`, anything under `tests/fixtures/install-sh/`, `.github/workflows/prerelease.yml`,
`.github/workflows/release.yml`, the product version (`Directory.Build.props`), anything under
`tools/HVO.SkyMonitor.Deployment.ReleaseTool/`, or the wiring that runs it (`.github/workflows/development-v1.yml`,
`scripts/ci:classify`). Deleting one of those paths selects it too. Pushes, missing commits, empty ranges, failed or
malformed diffs, missing or non-regular tree entries, missing outputs and classifier-step failures all run it: only a
successful, explicit `false` skips it. It takes a few seconds.

## Secret Scan

`scripts/ci:secret-scan` scans the Git history for committed credentials with
[gitleaks](https://github.com/gitleaks/gitleaks) 8.30.1 and its default rules
(#1321). It runs in **Development v1 / Preflight** and in **Quality**, so every
`development/v1` and `main` pull request and push is scanned inside a check that
branch protection already requires. Draft `development/v1` pull requests run it
too, because Preflight runs on drafts.

Run the contract locally at any time with `./scripts/test:ci-secret-scan`.
It always runs on `development/v1` pushes and in Full Quality. On a PR,
Preflight runs it when the classifier's `secret_scan_contract` output selects
it for changes to any of these inputs: the scanner (including its pinned
gitleaks version/checksums), its contract test, `.gitleaks.toml`,
any path matching `*.gitattributes` or `*.gitleaksignore` at the root or any
depth, either scanner workflow (`development-v1.yml` or `ci.yml`), or the
classifier itself. Pushes retain the classifier's non-PR full-mode convention.
Missing commits, empty ranges, failed or malformed diffs, missing/non-regular
tree entries, missing outputs, and classifier-step failures run the contract
test too; a classifier-step failure still fails Preflight. The history scan
remains unconditional and precedes classification. Preflight keeps the
classifier's key=value diagnostic line in its log and suppresses its full
job-summary block.

- **Scope.** Every commit reachable from the checked-out commit, which is the
  pull request's merge commit or the pushed head. It reads the lines each commit
  added, so a credential that a later commit deleted is still found.
  - A merge is read as the difference between Git's own re-merge of its two
    parents and the committed result (`git log --diff-merges=remerge`, Git 2.36
    or newer). Whatever a conflict resolution or any other change made in the
    merge added is scanned.
  - A type change, such as a symlink replaced by a regular file, is read like
    any added file. gitleaks does not read deleted or binary files.
  - A file that Git shows as a binary diff gives gitleaks no text, even when
    its new content is text: a binary file turned into text, even by deleting
    the NUL bytes around a line it already held, a binary conflict that a merge
    resolved as text, or a text file that `.gitattributes` marks as binary
    (`-diff`). When the new blob is text by Git's own rule (no NUL byte in its
    first 8000 bytes), the script scans its whole content, not only the lines
    its diff adds.
    - The script writes each such file as a creation diff, from a temporary
      index, under its own commit's header, and gitleaks reads that synthetic
      history in git mode through a stand-in `git`.
    - So each finding carries the real commit and the file's path, and the
      exceptions apply to it exactly as to any other finding. A finding
      reported twice, identical in every field, is counted once.
    - A file that `.gitattributes` marks as binary is therefore read in full at
      every commit that changes it.
  - Every text line in a scanned tree is therefore either a line some commit's
    text diff added or part of a file scanned in full. Only content that is
    binary by Git's rule is not read.
  - A merge of more than two parents is refused, because Git shows no diff for
    one.
  - Other branches in the clone are not scanned; their own pull requests scan
    them.
- **Pinning.** The script downloads the release archive for Linux x64 or ARM64
  or macOS ARM64 or x64, checks it against the SHA-256 pinned in the script, and
  checks `gitleaks version`. To update gitleaks, change `GITLEAKS_VERSION` and all
  four checksums from the release's checksums file, then rerun both commands
  below. `HVO_GITLEAKS_CACHE_DIR` keeps the archive between runs and is verified
  on every use. `HVO_GITLEAKS_BIN` names a binary for another platform; only its
  version is checked.
- **Completeness.** Both workflows check out the full history (`fetch-depth: 0`),
  and the script refuses a shallow clone.
  - Before the scan, the script lists every reachable commit from the same
    patches gitleaks reads. It counts the commits that added text, meaning a hunk
    in a file the commit does not delete; that is when gitleaks counts a commit.
  - The other commits added no text: empty commits, deletions, pure renames,
    mode changes, empty or binary files, and merges whose resolution added
    nothing. The result line reports them separately.
  - The scan fails if gitleaks reports a different number of commits or logs any
    error, so a partial, cut-short or empty scan cannot pass. A problem git
    reports while gitleaks reads the history, such as a malformed
    `.gitattributes` line, fails it too.
  - gitleaks scans whatever its `git log` writes and notes a failed or killed
    git only in its debug log, so a reader that stopped part way through a
    commit would still be counted. gitleaks therefore reads through a stand-in
    `git` that appends a completion record, signed with a nonce for this run,
    only after its whole output was written: the real `git log` must exit 0,
    and the files scanned in full are recorded commit by commit with their
    file and byte counts. A record that is missing or differs in any way fails
    the scan, and gitleaks must report reading every byte of the files scanned
    in full.
  - Configuration cannot hide content. `--root` shows the root commit under
    `log.showRoot=false`. The other `git log` options pin textconv and external
    diff drivers, the `a/` and `b/` prefixes, `diff.relative`, colour, rename
    detection, the diff algorithm and the commit header format, and
    `GIT_CONFIG_COUNT` pins `core.bigFileThreshold`, `core.quotePath` and
    `core.commitGraph=false`. Both override every configuration file, the
    repository's `.git/config` included, which `GIT_CONFIG_GLOBAL=/dev/null`
    would not.
  - The history cannot be cut short or swapped.
    - Every inherited `GIT_*` and `GITLEAKS_*` variable is removed, so
      `GIT_DIR`, `GIT_OBJECT_DIRECTORY`, `GIT_GRAFT_FILE`, `GIT_SHALLOW_FILE`
      or `GIT_CONFIG_PARAMETERS` cannot redirect Git.
    - Grafts are off (`GIT_GRAFT_FILE=/dev/null`), and a grafts file in the
      repository is refused outright.
    - Replacement refs are ignored (`GIT_NO_REPLACE_OBJECTS=1`), and commit
      parents are read from the commits, not from the local commit-graph
      cache.
    - Alternates need nothing: objects are named by their content, so an
      alternate can only supply an object, never change one, and a missing
      object fails the scan.
- **Output.** The script prints its own counts and, for a finding, the rule,
  path, line and commit.
  - The matched text is never printed. gitleaks' report holds it, unredacted,
    so the script can check what it prints. The report stays in the script's
    private temporary directory and is removed on exit.
  - A rule ID or path that holds part of a credential is withheld:
    - the finding's whole match, or any run of 8 characters of any finding's
      credential. That is about 48 bits, enough to narrow a search; shorter
      runs are mostly the fixed prefixes that rules match on, such as `ghp_`,
      and ordinary words;
    - or anything gitleaks finds when it reads the rule IDs and paths as text.
      That covers a credential in a file name or a custom rule ID that is not
      itself a finding. It reads them with gitleaks' default rules, which the
      scanned repository cannot change, and always again with
      `.gitleaks.toml`, as gitleaks parses it, for its custom rules, so the
      repository can only add to the check. Inline `gitleaks:allow` comments
      are ignored, and gitleaks runs in the private directory, so no
      `.gitleaksignore` file applies. If either read fails, or does not report
      reading every byte, every rule ID and path is withheld.
    - The check weighs every finding gitleaks reported, before identical ones
      are counted once, so two credentials on one line are both weighed.
  - No tool's diagnostics are shown, because git and gitleaks can quote
    `.gitleaks.toml`, `.gitattributes` or scanned text: a malformed expression
    makes gitleaks panic and print it, and git quotes a malformed attribute
    name. A failure is described in the script's own words.
  - A finding's path is printed only when it is printable ASCII; any other
    path is replaced by a placeholder. The commit is always printed, so a
    withheld finding can still be traced.
  - Exit status 0 means no findings, 1 findings, and 2 that the scan could not
    run.
- **Exceptions.** `.gitleaks.toml` is the only place to record one. Each entry
  gives its reason and names its rules, its paths, and the historical commits it
  covers, so it cannot hide a finding in a later commit. Inline `gitleaks:allow`
  comments are ignored. The history scan refuses a root `.gitleaksignore`;
  the contract also refuses every tracked `*.gitleaksignore` file at any depth.
  Changes to those paths select the contract on PRs, so nested files cannot
  bypass that check. `scripts/test:ci-secret-scan` enforces this refusal and
  exception shape whenever it runs as described above. History is never rewritten:
  revoke or rotate a real credential, then record its historical commit. An entry
  names commits, so it holds only for merge-commit merges; a squash or rebase
  merge lands different commits. gitleaks matches a file in a merge's conflict
  resolution as `b/<path>`, so an entry for one must match that form; the scan
  prints the plain path. A file scanned in full is matched by its plain path,
  and an entry for it names each commit that changes it.

The first full scan, on 2026-10-16 at `43ef6d4a`, reported 28 findings in 1,871
commits. Five reviewed entries cover them. One holds true positives that are
inert: Data Protection key rings from local development runs, committed in
November 2025, expired on 2026-02-12, and removed from the tree. The rest are an
Application Insights connection string (a resource identifier, not a secret,
removed from the tree on 2026-07-12 and now only in history), test-only values,
PEM header literals and synthetic AWS-shaped vectors in the processing-graph
secret filter, and generic-rule matches on ordinary words.

Run both checks directly for local validation, including to force the contract test:

```bash
./scripts/ci:secret-scan
./scripts/test:ci-secret-scan
```

On home-dev-01 at `06cf385b`, the script accounted for all 2,474 reachable
commits. gitleaks scanned the 1,956 that added text (95 MB of patches), 79 of
them merge resolutions, and the script took 5.6 s in total with a cached
archive. Git shows 28 added or changed files in the history as binary diffs;
each one's new blob is binary, so no file was scanned in full. The contract
test's 62 cases took about 45 s.

The contract test runs the real scan against fixture repositories with an
obviously fake credential assembled at run time. It fails if any output shows
that credential or holds a character that is not printable ASCII. Its cases
cover:

- a credential a later commit deleted, an inline `gitleaks:allow` comment, and
  another branch;
- credentials that only a conflict resolution, or a merge to a file neither
  side changed, added;
- a symlink replaced by a file holding a credential;
- text that Git shows as a binary diff: a binary conflict resolved as text, a
  binary file replaced by text (with a file read in both passes reported once),
  a file that `.gitattributes` marks as binary, and clean text that replaced a
  binary file;
- files scanned in full: a credential line a binary file already held, kept as
  it turns to text, before and after a later deletion; a merge resolution that
  keeps a line of the binary side; a binary file renamed as it turns to text;
  and a commit-scoped exception that covers exactly its commit and path;
- a grafts file with Git's warning off, a caller's `GIT_GRAFT_FILE` and
  `GIT_DIR`, and a commit-graph file edited to drop a parent;
- withheld metadata: files named after the credential, part of it, or another
  credential, also beside a `.gitleaksignore` and with `gitleaks:allow` in the
  name; rule IDs that hold a credential; a `.gitleaks.toml` that redefines a
  default rule, and custom credential formats under `[[rules]]`, `[["rules"]]`
  and `[['rules']]`; two credentials on one line; and the check of rule IDs
  and paths failing, or either of its reads getting part of the names;
- readers that do not finish: the history's `git log` exiting or killed part
  way through a commit, and the files scanned in full cut off, killed, short of
  a file, or without this run's completion record;
- a credential only in the root commit under `log.showRoot=false`, and again
  under hostile repository configuration and a replacement ref;
- output: a malformed `.gitattributes` attribute name, git failing before the
  scan, and a revision, each holding the credential, and a finding in a path
  with a control character;
- seven commit shapes that add no text;
- refusals: an octopus merge, a malformed `.gitleaks.toml` expression, a scan
  cut short, git failing under gitleaks, a shallow clone, a `.gitleaksignore`
  file, a wrong gitleaks version, and a tampered archive.

## Base-Image Updates

Dependabot covers every repository Dockerfile through the `docker` directories
in `.github/dependabot.yml`: the devcontainer, Stellarium, CameraAgent, LogicHost,
ProcessingRunner, and TimeLapseBenchmark images. Updates are weekly and grouped
per dependency across all directories (`group-by: dependency-name`), so identical
image tags keep the same digest wherever they occur.

Dependabot updates `FROM` lines. CameraAgent pins its external images on the
`dotnet-sdk` and `dotnet-aspnet` alias stages; its build/runtime stages select
those aliases through `DOTNET_SDK_IMAGE` and `DOTNET_ASPNET_IMAGE`. The ARM64
advisory build passes resolved native references through the same arguments.

- `dotnet/aspnet` receives patch releases and digest refreshes. A new major
  version is ignored, because it is a target-framework migration.
- `dotnet/sdk` receives digest refreshes of the pinned tag only. `global.json`
  requires that SDK exactly (`rollForward: disable`), so a new SDK is a manual
  change to `global.json`, every SDK-bearing Dockerfile, and the other SDK pins
  together.

Every Development v1 Preflight, including drafts, runs
`./scripts/ci:base-images`. This offline check enumerates tracked and non-ignored
new files named `dockerfile` or `containerfile`, case-insensitively, including
`X.*` and `*.X` variants, while excluding `*.dockerignore`. The Dependabot
coverage contract uses the same discovery. The check requires SDK FROM tags
to equal `global.json`, requires identical image tags to share one digest, and
requires every external FROM to carry a full `sha256` digest. Earlier stage
aliases inherit their checked base; global ARG defaults are resolved before
checking FROMs. UTF-8 BOMs are decoded before checking parser directives and
instructions. Unknown instructions, unresolved arguments and unsupported FROM
forms fail closed.
There are no pinning exceptions. Build-time argument overrides are outside this
source check; the ARM64 harness separately resolves its native references.
`./scripts/test:development-v1-ci` includes offline mismatch/alignment fixtures
and verifies that every checked-in Dockerfile directory is listed in Dependabot.
No Docker daemon, image pull, SDK invocation, or registry access is needed.

Development v1 CI does not build images; full qualification's Deployment
Contracts job exercises the CameraAgent and LogicHost images. ProcessingRunner
has no dedicated CI image build. Build affected images locally before merging a
base-image pull request, including ProcessingRunner when its base changes:

```bash
docker build -f src/HVO.SkyMonitor.CameraAgent/Dockerfile .
docker build -f src/HVO.SkyMonitor.LogicHost/Dockerfile .
docker build -f src/HVO.SkyMonitor.ProcessingRunner/Dockerfile .
```

## Coverage Badge Publication

The main-branch aggregate Coverage job publishes its line and branch percentages
to the public
[`HVO.SkyMonitor coverage badges`](https://gist.github.com/RoySalisbury/aec5c0f8e0741d964da6859ec4466740)
Gist. The README renders `coverage-line.json` and `coverage-branch.json` through
the Shields endpoint. The Gist must contain only public aggregate metrics; never
publish paths, credentials, test output, or other repository data there.

Badge publication uses two repository-level GitHub Actions settings:

| Setting | Kind | Value and ownership |
| --- | --- | --- |
| `GIST_TOKEN` | Secret | Dedicated token owned by the Gist account. Use either a fine-grained token with only the `Gists: read and write` user permission or an OAuth token whose only scope is `gist`; record its expiry or rotation due date. |
| `COVERAGE_GIST_ID` | Variable | `aec5c0f8e0741d964da6859ec4466740`; this identifier is public and is not a credential. |

The owner recovery copy of `GIST_TOKEN` is
`HVO-SkyMonitor--GitHub--GistToken` in the RBAC-enabled `hvo-central-kv` Azure
Key Vault. The Gist ID is mirrored there as
`HVO-SkyMonitor--GitHub--CoverageGistId`. GitHub Actions does not read either
value directly from Key Vault: the badge job intentionally has no Azure login,
repository permission, or access to the release-signing identity. Use the Azure
and GitHub settings portals to copy the current Key Vault secret version into
the repository Actions secret without placing it in a command argument, shell
history, log, or issue comment. Install the public identifier with:

```bash
gh variable set COVERAGE_GIST_ID \
  --body aec5c0f8e0741d964da6859ec4466740 \
  --repo HualapaiValley/HVO.SkyMonitor
```

The publication step is best-effort because `Required CI` is the protected
code-quality gate. Missing configuration emits a warning and summary. When
configured, the job sends one atomic Gist API request for both files. Connection,
transfer, and retry limits bound that request to less than the job's five-minute
backstop; a request failure is tolerated and followed by a diagnostic warning
and summary. The token is supplied to `curl` through standard input rather than
the process argument list on the shared runner, and the invocation disables
ambient curl configuration before reading that credential. Treat any badge-job
warning or failed publication step as stale-badge evidence and repair it before
relying on the displayed percentages. A runner or workflow cancellation can
still cancel the overall run and is an infrastructure failure, not a
badge-publication result.

Rotate the token make-before-break: create the replacement with the same narrow
permission or scope, create a new Key Vault secret version, update the GitHub
Actions secret from that version, rerun the badge job, verify both Gist files
changed, and only then revoke the old token. Verify the rendered endpoints and
the underlying JSON without exposing the token:

```bash
curl --fail --silent --show-error \
  https://gist.githubusercontent.com/RoySalisbury/aec5c0f8e0741d964da6859ec4466740/raw/coverage-line.json |
  jq --exit-status '.schemaVersion == 1 and (.message | endswith("%"))'
curl --fail --silent --show-error \
  https://gist.githubusercontent.com/RoySalisbury/aec5c0f8e0741d964da6859ec4466740/raw/coverage-branch.json |
  jq --exit-status '.schemaVersion == 1 and (.message | endswith("%"))'
```

## ARM64 Advisory

`.github/workflows/cameraagent-arm64.yml` supplies native Linux ARM64 evidence
on a dedicated runner selected by
`[self-hosted, linux, ARM64, hvo-skymonitor-arm64]`. It runs from protected
`main`, by daily schedule, or by default-branch `repository_dispatch`; it does
not execute pull-request or branch-selectable manual-dispatch code and is not
aggregated by `Required CI`.

The workflow builds the canonical production catalog contract on hosted x64,
then performs a native CameraAgent Release build, Docker-disabled Unit subset,
`linux-arm64` publish/ELF validation, native container build, catalog install,
and bounded VirtualSky smoke on ARM64. It retains checksums, image identity,
host resource/thermal samples, installation verification, capture evidence,
SQLite checks, logs, and cleanup state for 30 days. Provisioning, isolation,
updates, recovery, decommissioning, and promotion criteria are maintained in
[`cameraagent-arm64-ci.md`](cameraagent-arm64-ci.md).

## Categories

The category audit requires every discovered case to belong to exactly one primary behavioral category. Current declared inventory is `Unit=10606`, `Integration=851`, `Manual=260`, `Soak=1`, `External=0`, and `Hardware=1`. Whole-solution discovery verifies those declarations.

These totals and the Unit/Integration rows in [Required Checks](#required-checks) are not hand-maintained pins: `./scripts/docs:audit-operations` sums the per-project inventories and fails when this runbook disagrees with them, while the Build check's category audit proves those inventories match actual discovery. Each test project owns its expected counts in `tests/<project>/test-categories.json`, a flat object with exactly the six categories as non-negative integers; the engine in `scripts/test-categories/Program.cs` owns no counts. A count change is therefore a change inside the owning project directory and selects that project's component lane, while editing the engine still selects the complete solution matrix. A project without an inventory, an inventory without a project, or a malformed, duplicate, unknown, or stale category all fail the audit. Update the owning inventory and this runbook in the same change.

`External` is implemented by the pinned, networkless Stellarium workflow rather than an empty MSTest check. The accelerated `Soak` case and real-duration soak are independently selectable in `.github/workflows/cameraagent-soak.yml`. The Hardware case remains separately selectable and is not published as a CI check until a suitable device runner exists.

## Local Validation

```bash
dotnet tool restore
dotnet restore HVO.SkyMonitor.v9.slnx
dotnet build HVO.SkyMonitor.v9.slnx --no-restore --no-incremental --configuration Debug -warnaserror
dotnet build HVO.SkyMonitor.v9.slnx --no-restore --no-incremental --configuration Release -warnaserror
dotnet format HVO.SkyMonitor.v9.slnx --no-restore --verify-no-changes
./scripts/package:audit
dotnet run --project scripts/test-categories/HVO.SkyMonitor.TestCategoryAudit.csproj --configuration Release
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter "TestCategory=Unit"
dotnet test HVO.SkyMonitor.v9.slnx --no-build --configuration Release --filter "TestCategory=Integration"
```

Use the exact per-project commands in `.github/workflows/ci.yml` when producing
coverage evidence; solution-level TRX names are not collision-proof. Run the
per-lane equivalents in [Component Selection](#component-selection) when a
change is component-scoped.

The exhaustive deployment harness runs on main/release/manual CI and on pull
requests that change split-host inputs. It defaults to the original serial
all-contract mode for local validation:

```bash
./scripts/test:deploy-environment
```

Its closed shard inventory is `preflight`, `prepare-images`, `partial-prepare`,
`existing-catalog-up`, `bootstrap-authority`, `bootstrap-credentials`, `smoke`,
`measure`, `existing-down`, and `deploy-services`. Inspect it with
`./scripts/test:deploy-environment --list-shards` (read from the closed inventory in `scripts/deploy/shards.json`, which the harness validates against its shard bodies under `scripts/deploy-contracts/` before dispatch), or run one isolated shard with
`./scripts/test:deploy-environment --shard NAME`. Main/release/manual CI and
affected pull requests use `./scripts/test:deploy-environment --parallel`; each
child creates an independent temporary fixture. The coordinator defaults to the
smaller of the shard count,
`nproc`, and eight local child processes. `DEPLOY_TEST_MAX_PARALLEL=1` through
`8` can lower that bound. Every child runs in its own session. Fail-fast and
signal cleanup send TERM to the complete process group, wait a bounded grace
interval, escalate surviving groups to KILL, and reap stopped leaders. Queued
shards are marked as not started and the complete failed-shard log is printed.

Every coordinator invocation creates a collision-safe directory beneath
`TestResults/deployment-contracts/`. GitHub directories include run and attempt
identity; local directories use a local identity, UTC timestamp, coordinator
PID, and random suffix. Concurrent invocations never clear or share these
directories. Each contains per-shard `.log`, `.status`, and leader PID evidence,
and the Deployment Contracts artifact retains every invocation directory even
when the job fails.

Run the lightweight closed-CLI and injected coordinator-failure contracts
without executing the nine full shards:

```bash
./scripts/test:deploy-environment-cli
```

Manual dispatch retains the historical Phase 14 acceptance inventory contract:

```bash
./scripts/test:phase14-acceptance
```

Deployment-relevant pull requests retain the lightweight coordinator and two
current deployment orchestration contracts:

```bash
./scripts/test:deployment-logichost-outage-contract
./scripts/test:deployment-normal-flow-contract
```

The historical Phase 14 component/source importers are reproducibility tools,
not current protected gates. Run them only when changing or reproducing that
closed evidence campaign.

Run the path-classification, component-plan, and aggregate-protection contract
tests when changing CI orchestration, the component map, the seam rule, or the
reduced-mode allowlist. They cover representative shared-only, CameraAgent-only,
LogicHost-only, combined, delivery, documentation-only, and CI-change diffs,
`Required CI` failure aggregation and cancellation, and the complete fallback
matrix:

```bash
bash ./scripts/test:ci-classification
```

That suite, `scripts/test:coordination-guard` and `scripts/test:pr-review-tools`
are the CI-control guards, and they are part of the local candidate gate in
`AGENTS.md` rather than protected-CI-only checks. They are Docker-free and need
no build. Run them on the base-synced head too, before requesting the base-sync
review: a base-sync review verifies that the merge preserved both sides'
behaviour, which is the wrong instrument for a classifier lane that each side
satisfied separately and their union does not.

Use a fresh result root for every collection. Before merging, require exactly one report from each of the 23 category/project slots as shown in `.github/workflows/ci.yml`; never merge every historical GUID directory under a reused result root. Merge those 23 explicit reports once with the pinned ReportGenerator tool, then enforce and publish that same canonical result:

```bash
patterns=(
  'TestResults/unit/astronomy/*/coverage.cobertura.xml'
  'TestResults/unit/imaging/*/coverage.cobertura.xml'
  'TestResults/unit/processing/*/coverage.cobertura.xml'
  'TestResults/unit/catalog-sqlite/*/coverage.cobertura.xml'
  'TestResults/unit/deployment-cli/*/coverage.cobertura.xml'
  'TestResults/unit/deployment-distribution/*/coverage.cobertura.xml'
  'TestResults/unit/agent-core/*/coverage.cobertura.xml'
  'TestResults/unit/common/*/coverage.cobertura.xml'
  'TestResults/unit/fleet-contracts/*/coverage.cobertura.xml'
  'TestResults/unit/test-support/*/coverage.cobertura.xml'
  'TestResults/unit/architecture/*/coverage.cobertura.xml'
  'TestResults/unit/cameraagent/*/coverage.cobertura.xml'
  'TestResults/unit/cameraagent-acceptance/*/coverage.cobertura.xml'
  'TestResults/unit/logichost/*/coverage.cobertura.xml'
  'TestResults/unit/cameraagent-logichost/*/coverage.cobertura.xml'
  'TestResults/integration/cameraagent-storage/*/coverage.cobertura.xml'
  'TestResults/integration/cameraagent-standalone/*/coverage.cobertura.xml'
  'TestResults/integration/astronomy/*/coverage.cobertura.xml'
  'TestResults/integration/logichost/*/coverage.cobertura.xml'
  'TestResults/integration/cameraagent/*/coverage.cobertura.xml'
  'TestResults/integration/cameraagent-logichost/*/coverage.cobertura.xml'
  'TestResults/architecture/*/coverage.cobertura.xml'
)
reports=()
for pattern in "${patterns[@]}"; do
  mapfile -t matches < <(compgen -G "$pattern" || true)
  if [[ "${#matches[@]}" -ne 1 ]]; then
    printf 'Expected one coverage report for %s, found %s\n' "$pattern" "${#matches[@]}" >&2
    exit 1
  fi
  reports+=("${matches[0]}")
done
dotnet reportgenerator "-reports:$(IFS=';'; echo "${reports[*]}")" -targetdir:coverage-report -reporttypes:"Html;TextSummary;MarkdownSummaryGithub;Badges;Cobertura"
./scripts/coverage:enforce --merged coverage-report/Cobertura.xml
```

Pending-model checks use the pinned `dotnet-ef` tool:

```bash
dotnet ef migrations has-pending-model-changes --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj --startup-project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj --context HVO.SkyMonitor.CameraAgent.Data.ApplicationDbContext --configuration Release --no-build
ConnectionStrings__skymonitordb="Server=127.0.0.1,1433;Database=ModelCheck;User Id=sa;Password=Model_check1!;TrustServerCertificate=True" dotnet ef migrations has-pending-model-changes --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj --startup-project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj --context HVO.SkyMonitor.LogicHost.Data.ApplicationDbContext --configuration Release --no-build
```

Each split migration job first requires exactly one non-designer timestamped
migration source file in its own EF context, through
`./scripts/ci:canonical-migration <cameraagent|logichost>`. A model change replaces that canonical migration
and snapshot; adding a second migration fails CI during the pre-release period.

## Protection And Review

Protect `main` with the stable `Required CI` check and require branches to be
current before merge. GitHub enforces current-head CI and conversation
resolution; independent review is an additional repository-process requirement,
not an approving-review branch-protection rule. The merge-method, branch-deletion
and release-tag settings the install contract requires await the owner's
approval; see [Repository Settings](../development-v1.md#repository-settings). The complete review-provider,
correction-cap, finalization-lock, and merge procedure is defined by
`.agents/skills/pr-lifecycle/SKILL.md`.

Open implementation PRs as drafts and keep them draft while review corrections
converge and the target branch is finally synchronized, with required base-sync
review if the target advanced or recorded unchanged-base proof otherwise.
Draft PR events skip the expensive CI plan and publish an intentionally failing
`Required CI`, so branch protection remains fail-closed; returning a PR to draft
also cancels its superseded in-progress run. Only the PR holding the
repository-wide finalization lock may perform final synchronization, transition
to ready, run protected CI, and merge. Marking that final reviewed head ready
triggers the classifier-selected CI plan, and a successful `Required CI` on that
exact head and current target base satisfies the gate. A diagnosed
infrastructure failure may rerun the same unchanged SHA. Stale, canceled,
timed-out, failed, skipped, or absent checks do not satisfy `Required CI`.

The aggregate is fail-closed for classification inputs and job results, but a workflow running from a pull request cannot be an independent trust boundary against an author who maliciously rewrites that workflow or its CI helper scripts. Independent review of `.github/workflows/**` and `scripts/ci:*` remains part of this repository's solo-maintainer protection model. Repositories accepting untrusted workflow changes require a separately trusted required workflow or mandatory reviewer policy.

## Pull Request Selection

The workflow runs the classifier-selected CI plan only for non-draft pull
requests targeting `main` or `release/**`. Draft events run only the fail-closed
`Required CI` result. The
workflow responds to the `ready_for_review` transition so a reviewed draft
receives current-head CI. A lightweight classifier uses the
pull request's base and head commits and selects reduced mode only when every
changed path is an added or modified member of this allowlist:

- `docs/**`, except the production bundle inputs `docs/catalog/hyg-v42-attribution.md` and `docs/catalog/hyg-v42-license.md`
- `.agents/skills/*/SKILL.md`; supporting scripts and other resources fail
  closed to the complete matrix
- `.devcontainer/**`
- `.vscode/**`
- `.github/prompts/**`
- `README.md`, `AGENTS.md`, `CLAUDE.md`, `CHANGELOG.md`, and `THIRD-PARTY-NOTICES.md`
- `.github/copilot-instructions.md` and `.github/pull_request_template.md`
- `tools/asi-capture/README.md`
- one-level `src/*/README.md` and `tests/*/README.md`
- `tests/fixtures/catalog/SOURCE.md` and `tests/fixtures/stellarium/SIMBAD_ENDPOINTS.md`
- `scripts/test:devcontainer`
Reduced mode still runs lightweight **Quality** and **Required CI**. Quality
resolves the devcontainer definition after unsetting GitHub-token and Git-identity
variables and suppresses the resolved output because future configuration may
contain secrets. It does not set up .NET, restore/format the solution, build
catalog artifacts, or run the package audit for paths excluded from
application/package behavior. It
intentionally skips Catalog Contracts, Build, Unit Tests, Integration Tests,
Architecture & Publish, Coverage Policy, both migration checks, every component
lane, and Coverage. Reduced mode is the only plan in which the never-component-scoped
gates do not run, because no executable contract is affected. Documentation-only reduced pull requests also skip
Deployment Contracts. `Required CI` accepts those skipped results only when
classification and Quality succeeded, the deployment plan is explicitly `false`,
and the plan selects neither the complete matrix nor any component lane.

Deployment selection is independent from full/reduced mode. A full-mode pull
request with ordinary application or test changes runs the existing full build
and test matrix but skips Deployment Contracts. A deployment-relevant pull
request runs the lightweight deployment gate plus the affected exhaustive suite
selected by `deployment_catalog`, `deployment_shards`, or
`deployment_installer`. Main, `release/**`, and manual runs select all three. The
closed deployment path map is:

- `.github/workflows/ci.yml`, `.dockerignore`, `.env.template`, `docker-compose.apps.yml`, and `global.json`
- `scripts/ci:classify`, `scripts/ci:require`, `scripts/test:ci-classification`,
  `scripts/release:*`, and `scripts/lib/**`
- `scripts/deploy:environment` and `scripts/deploy/**`
- `scripts/test:deploy-environment`, `scripts/test:deploy-environment-cli`, `scripts/test:deployment-installer`, `scripts/test:deployment-logichost-outage-contract`, and `scripts/test:deployment-normal-flow-contract`
- `scripts/catalog:*`, `scripts/catalog/**`, and `scripts/infra:operation-lock`
- `deploy/**`
- `tests/fixtures/catalog/hyg-v42-bright-stars.sqlite`
- `src/HVO.SkyMonitor.CameraAgent/Dockerfile` and `src/HVO.SkyMonitor.LogicHost/Dockerfile`
- `src/HVO.SkyMonitor.Deployment.Cli/**`, `src/HVO.SkyMonitor.Deployment.Contracts/**`, `tests/HVO.SkyMonitor.Deployment.*.Tests/**`, and `tools/HVO.SkyMonitor.Deployment.ReleaseTool/**`
- `src/HVO.SkyMonitor.TerminalUi/**`, which is compiled into the installer: it selects the installer suite, which runs the published installer on a terminal (`scripts/lib/published-installer-terminal.py`)
- `.github/workflows/release.yml` and `.github/workflows/prerelease.yml`
- `src/HVO.SkyMonitor.CameraAgent/cameraagent.sample.json`
- `src/HVO.SkyMonitor.CameraAgent/virtual-asi174.full.json` and `src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json`

The classifier emits `mode`, `deployment`, and the three exhaustive-suite outputs
with reasons in the step summary. Missing commits, failed or malformed diffs,
and empty change sets fail closed to every suite. Deletions, renames, type
changes, and missing or non-regular entries use the affected path to select a
suite while still forcing `mode=full` and `deployment=true`. `Required CI` requires Deployment
Contracts to be `success` exactly when deployment is `true`, and `skipped`
exactly when it is `false`; failed, canceled, missing, or mismatched results are
rejected in either plan. The same derivation applies to every other gate: a job
that is `success` when the plan expected `skipped` is rejected exactly like a
job that is `skipped` when the plan expected `success`, so neither a stale plan
nor an edited job condition can turn an unselected lane into a silent pass.

Pushes to `main` or `release/**` and manual dispatches always use the complete
solution matrix and exhaustive deployment contracts.

## Component Selection

Full mode is refined by six further classifier outputs. `complete` selects the
solution-wide Unit Tests, Integration Tests, and aggregate Coverage jobs;
`shared`, `cameraagent`, `logichost`, `combined`, and `delivery` select the
component lanes. A lane runs only when `complete` is `false`, so the complete
matrix and the lanes never duplicate the same evidence, and `Required CI`
derives each job's expected result from that plan rather than from a hard-coded
list.

Six gates are deliberately never component-scoped and run for every full-mode
head: **Catalog Contracts** (the exact production catalog bundle both the Unit
job and the Delivery lane consume), **Build** (warning-clean solution builds and
the behavioral category audit), **Architecture & Publish** (repository graph,
provider boundary, host publish evidence, and both category selections of the
architecture project), **Coverage Policy** (baseline non-regression), and both
**CameraAgent Migrations** and **LogicHost Migrations**. The migration gate is
split per host so each failure is attributable to one EF context, but neither
half is component-selected: a delivery-only or documentation-adjacent plan still
runs both. **Quality** already runs in every mode and keeps the package
vulnerability/deprecation audit on every full-mode head.

The closed component map is keyed on the exact project directory segment, so
sibling names such as `HVO.SkyMonitor.CameraAgent.Tests` and
`HVO.SkyMonitor.CameraAgent.LogicHost.Tests` can never match by prefix:

| Component | Owned paths |
| --- | --- |
| `shared` | `src/` and `tests/` project directories for `AgentCore`, `Astronomy`, `Imaging`, `Processing`, `ProcessingRunner`, `ProcessingRunner.Contracts`, `Catalog.Sqlite`, `Common`, `Fleet.Contracts`, and `TestSupport` |
| `cameraagent` | `src/HVO.SkyMonitor.CameraAgent`, `CameraAgent.Cli`, `CameraAgent.Common`, `CameraAgent.Modules.Zwo`, `CameraAgent.Replay`, `CameraAgent.ReplayRunner`, and `tests/HVO.SkyMonitor.CameraAgent.{Tests,AcceptanceTests,IntegrationTests}` |
| `logichost` | `src/HVO.SkyMonitor.LogicHost` and `tests/HVO.SkyMonitor.LogicHost.{Tests,IntegrationTests,TestInfrastructure}` |
| `combined` | `tests/HVO.SkyMonitor.CameraAgent.LogicHost.{Tests,IntegrationTests}` |
| `delivery` | `src/HVO.SkyMonitor.Deployment.*`, `tools/HVO.SkyMonitor.Deployment.ReleaseTool`, and `tests/HVO.SkyMonitor.Deployment.*.Tests` |

Deployment inputs that are not project directories — `deploy/**`,
`docker-compose.apps.yml`, `.env.template`, `scripts/deploy*`,
`scripts/catalog*`,
`scripts/infra:operation-lock`, `scripts/test:deploy*`,
`scripts/test:deployment-*`, the HYG v42 attribution and license inputs, and
`tests/fixtures/catalog/hyg-v42-bright-stars.sqlite` — are deliberately **not**
mapped to the delivery component. Test code reads several of them from the
repository root at run time, which no project file records: for example
`SqlOperationsIssue255Tests` and `DatabaseInitializationAcceptanceTests` read and
assert `deploy/sql/*.sql`, and seven test projects across every component load
the catalog fixture. Narrowing them would silently drop the suites that assert
them, so they keep their independent deployment-contract selection and otherwise
select the complete matrix, exactly as before component scoping.

The classification rules applied to that map are:

- A shared contract change selects `shared` plus every consuming lane:
  `cameraagent`, `logichost`, `combined`, and `delivery`. Delivery is included
  because `HVO.SkyMonitor.Deployment.Cli` and the release tool consume
  `AgentCore` and `Catalog.Sqlite`.
- A CameraAgent change selects `cameraagent`, and adds `combined` when the path
  can change an exported protocol or integration seam.
- A LogicHost change selects `logichost`, and adds `combined` under the same
  seam rule.
- A combined fixture change selects `combined` plus both host lanes.
- A delivery change selects `delivery`; deployment-contract selection stays
  independent and still runs the Deployment Contracts gate.
- CI, coverage, package, architecture, classifier, toolchain, and solution
  inputs select the complete matrix, and so does any path outside every
  component boundary. **The default is the complete matrix**, so a new
  top-level directory or a new project fails closed rather than silently
  running a narrow plan.
- Documentation-only pull requests stay in reduced mode only through the
  existing allowlist, and only while no allowlisted path is compiled, embedded,
  or copied into a project. Seven allowlisted paths are included by project
  files today — `THIRD-PARTY-NOTICES.md` and six `docs/validation/*.json`
  signal manifests — and each leaves reduced mode for the complete matrix. The
  remaining 234 allowlisted paths still classify reduced.

Seam selection is an inverse allowlist. A host path is treated as an exported
protocol or integration seam unless it is explicitly host-private, so a new host
directory fails closed into combined coverage. The host-private set is:

- `src/HVO.SkyMonitor.CameraAgent/{Components,Properties,wwwroot}/**`, except `Components/Account/**`, which wires the identity endpoints the combined suite exercises, and `Properties/AssemblyInfo.cs`, whose `InternalsVisibleTo` grants are what let the combined suites compile
- `src/HVO.SkyMonitor.CameraAgent.Common/{Background,Diagnostics,Frames,Gallery,Imaging,Logging,Properties,Reflection,Scheduling,SkyMap}/**`, except `Properties/AssemblyInfo.cs`, whose `InternalsVisibleTo` grant is what lets `CameraAgent.LogicHost.Tests` compile
- `src/HVO.SkyMonitor.CameraAgent.{Modules.Zwo,Replay,ReplayRunner}/**`
- `src/HVO.SkyMonitor.LogicHost/{Components,Properties,wwwroot}/**`, with the same `Components/Account/**` and `Properties/AssemblyInfo.cs` exceptions
- every `tests/**` path except `tests/HVO.SkyMonitor.LogicHost.TestInfrastructure/**`, which is the non-test fixture project both the LogicHost and combined suites compose

Everything else inside a host project, including its project root files,
`Configuration`, `Controllers`, `Endpoints`, `Http`, `Authentication`,
`Authorization`, `Security`, `Services`, `Data`, `Hosting`, `Infrastructure`,
`Middleware`, `Models`, `Capture`, `Environmental`, `Transients`, `Modules`,
`Fleet`, `Upload`, `Deployment`, `DeploymentLocation`, `Operations`, `Options`,
`RawIngress`, `Storage`, `Telemetry`, and `DependencyInjection`, adds the combined lane.
The real evidence-exporter combined harness consumes `StoragePressureState`, so
storage changes must reach that lane alongside CameraAgent coverage.

The host-private set is not merely asserted. `scripts/test:ci-classification`
extracts every `HVO.SkyMonitor.CameraAgent.Common.*` and
`HVO.SkyMonitor.LogicHost.*` reference from the two combined test projects —
plain, `global`, `static`, aliased, and fully qualified alike — and fails when
any namespace those suites reference maps to a host-private directory, so the
list cannot drift away from what the combined lane actually guards. The check
covers the `HVO.SkyMonitor.CameraAgent`, `HVO.SkyMonitor.CameraAgent.Common`, and
`HVO.SkyMonitor.LogicHost` namespace roots; a host-private file that contributes
to a combined behavior through some other namespace, such as an ASP.NET routing
extension, still needs a deliberate carve-out like `Components/Account/**`. A
second cross-check covers the assembly-visibility graph: any file granting
`InternalsVisibleTo` to a combined suite must select the combined lane, which is
why `Properties/AssemblyInfo.cs` is carved out of the host-private set.

Directory ownership is not the only way a source reaches a project. A file that
another project compiles, embeds, or copies through a relative MSBuild item —
`<Compile Include>`, `<None Include>` with `CopyToOutputDirectory`, and the rest —
also selects the including project's component, wherever in the repository it
lives. `tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/VirtualSkyPipelineTests.cs`
therefore selects the combined lane because the combined integration project
compiles it, and `THIRD-PARTY-NOTICES.md` and `docs/validation/*.json` leave
reduced mode because shipping and test projects copy them.

Link derivation only ever **widens** a plan. A path outside every project
directory still selects the complete matrix, because test code also reads
repository-root files at run time in ways no project file records. The
classifier derives links from the checked-out project files rather than a list,
and skips project references, which the component map already models, and
wildcard includes, whose directories the contract test proves already select the
complete matrix. `scripts/test:ci-classification` copies the real project files
into its fixture repository and asserts that every resolvable link selects at
least what a file in the including project selects.

### Exact Affected-Gate Selection

Every full-mode row below additionally runs the six never-component-scoped
gates: Catalog Contracts, Build, Architecture & Publish, Coverage Policy,
CameraAgent Migrations, and LogicHost Migrations. Quality runs in every mode,
including reduced. The table records only what varies.

| Change | Complete | Lanes | Deployment Contracts |
| --- | --- | --- | --- |
| Allowlisted documentation only | no | none | no — and every full-mode gate above is skipped too; only Quality and Required CI run |
| Shared library or shared test | no | shared, cameraagent, logichost, combined, delivery | no |
| CameraAgent UI, imaging, `Modules.Zwo`, storage, gallery, scheduling, or replay code | no | cameraagent | no |
| CameraAgent seam (`Capture`, `Environmental`, `Transients`, `Modules`, `Fleet`, `Upload`, controllers, identity, configuration) | no | cameraagent, combined | no |
| LogicHost UI code | no | logichost | no |
| LogicHost seam (controllers, services, data, infrastructure) | no | logichost, combined | no |
| `tests/HVO.SkyMonitor.LogicHost.TestInfrastructure/**` | no | logichost, combined | no |
| Combined fixture or combined suite | no | cameraagent, logichost, combined | no |
| `src/HVO.SkyMonitor.Deployment.*`, `src/HVO.SkyMonitor.TerminalUi`, the release tool, its release-validation shell/JQ helpers, or their tests | no for project paths; yes for repository-root scripts | delivery for project paths; complete for repository-root scripts | yes — release validation selects the installer suite |
| `deploy/**`, `scripts/deploy*`, `scripts/catalog*`, or the other non-project deployment inputs | yes | every lane claimed by the complete matrix | yes |
| `THIRD-PARTY-NOTICES.md` or a `docs/validation/*.json` a project copies | yes | every lane claimed by the complete matrix | no |
| `docs/catalog/hyg-v42-attribution.md` or `hyg-v42-license.md` | no | delivery | no |
| CameraAgent or LogicHost `Dockerfile` or host configuration sample | no | that host, combined | yes |
| CI, classifier, coverage, package, toolchain, solution, or architecture-test input | yes | every lane claimed by the complete matrix | yes for the deployment-contract inputs; the rest run every other gate |
| `tests/fixtures/**`, including the shared HYG v42 catalog fixture | yes | every lane claimed by the complete matrix | yes for the HYG v42 fixture |
| Deleted, renamed, type-changed, missing, empty, or unclassifiable path | yes | every lane claimed by the complete matrix | yes |
| Push to `main`/`release/**` or manual dispatch | yes | every lane claimed by the complete matrix | yes |

The complete matrix additionally runs Unit Tests, Integration Tests, and the
aggregate Coverage rollup, which every component plan skips in favour of its
lanes.

Inspect the exact plan for any two commits without pushing:

```bash
./scripts/ci:classify pull_request <base-sha> <head-sha>
```

Run this on the review range before choosing a local gate set, and use what it
returns rather than a reading of the diff, whenever it classifies the range
successfully. `complete` is not a condition on using it: `complete=false` is the
ordinary result for a change confined to one component's paths, and the lane
flags set in that state are the selection being asked for. `complete` governs
whether the complete solution matrix is required, nothing else.
Record its output next to the gate results; a ledger that lists gates without
naming the selector that chose them cannot distinguish a gate that passed from
one that was never selected. The classifier is the only selector here that is
not bounded by the diff, and on a draft pull request it is the only one
available at all, because the `changes` job carries `draft == false` and every
downstream job is gated on its outputs. Measured on PR #756 at head `7f232172`
it returned `mode=full complete=true deployment=false` over 14 paths and would
have named Unit Tests, which owns the test-category audit, on the first head
instead of the eighth.

`scripts/ci:require` uses `declare -A` and needs Bash 4 or newer, and
`scripts/ci:shell-syntax` requires Bash 5.1 and refuses to run under anything
older rather than reporting a pass it did not perform. The macOS system Bash is
3.2, so invoke the Homebrew Bash explicitly there. `scripts/pr:dispatch-review`,
which `scripts/test:pr-review-tools` exercises, additionally requires `flock`,
which macOS does not ship; install it with `brew install flock`.

`scripts/ci:shellcheck` enforces ShellCheck 0.9.0 `--severity=warning` over exactly
the set `scripts/ci:shell-syntax --list0` derives, downloading and
checksum-verifying the pinned Linux x86_64 release unless `HVO_SHELLCHECK_BIN`
names a verified 0.9.0 binary. It runs one process per file with `-x`, except
`scripts/test:deploy-environment`, which is linted without `-x` because it
sources the lifecycle library and twelve shard bodies and analyzing that union
in one process measured 16.8 GB; every file it sources is a derived target in
its own right and is linted with `-x` on its own. Peak per-process RSS is about
2.9 GB and the whole gate about 80s. Info and style diagnostics are deliberately
not enforced (273 info, 3 style at the time #873 raised severity to warning); every
retained `disable` directive states the fact that makes it correct at its site, and
file-scope directives are used only for libraries whose readers are other files.
`scripts/test:ci-shellcheck` is its contract test and proves a new warning fails and
an info-only file passes.

Reproduce one lane locally with the same scripts CI runs:

```bash
dotnet tool restore
./scripts/ci:component-build cameraagent
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build --configuration Release --filter "TestCategory=Unit&TestCategory!=Integration&TestCategory!=Manual&TestCategory!=Soak&TestCategory!=External&TestCategory!=Hardware" --settings tests/coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults/unit/cameraagent --logger "trx;LogFileName=unit-cameraagent.trx"
./scripts/ci:component-publish cameraagent
./scripts/coverage:component cameraagent
```

Use the exact per-project `dotnet test` invocations in `.github/workflows/ci.yml`
for the remaining slots of the selected lane; `scripts/coverage:component
--list-slots <component>` prints the exact result directories a lane must
produce, and the lane fails when a slot is missing, duplicated, or borrowed from
an unselected lane. The split migration checks and the coverage policy have
direct local equivalents:

```bash
./scripts/ci:canonical-migration cameraagent
./scripts/ci:canonical-migration logichost
./scripts/coverage:policy pull_request main
```

Component baselines live beside the aggregate baseline as
`scripts/coverage/baseline.<component>.json`. Each one carries its own aggregate
rate over the files its lane measures plus its own critical-file floors, so a
regression in one component cannot be masked by another component's coverage.

The CameraAgent, LogicHost and delivery lanes measure only the assemblies they
own, as the classifier's project lists assign them: `HVO.SkyMonitor.CameraAgent`
and `HVO.SkyMonitor.CameraAgent.*`, `HVO.SkyMonitor.LogicHost`, and
`HVO.SkyMonitor.Deployment.*` with `HVO.SkyMonitor.TerminalUi`.
`scripts/coverage:component` passes each lane's set to ReportGenerator as
`-assemblyfilters`. Coverlet instruments every HVO assembly in a test project's
output, including the shared assemblies a host references, so without the
filter shared code the host's tests never call would count against the host's
floor. #1408's orbital contracts took the LogicHost lane below its floor that
way (#1420). Shared code is measured by the shared lane and the aggregate
rollup, with one gap: the `ProcessingRunner` and `ProcessingRunner.Contracts`
files LogicHost's tests reach are not in the shared lane, because the
ProcessingRunner suite is not a coverage slot (below). The aggregate rollup
still measures both, and the combined lane measures the contracts. The shared
and combined lanes keep every assembly their reports carry: the shared suites
reference only shared assemblies, and the combined baseline floors the shared
contracts both hosts compose. A filter that drops a floored file, or leaves a
report with no classes, fails the lane rather than passing it.

A lane merges fewer reports than the aggregate rollup, so its measured rates are
its own. To keep component gating from relaxing risk coverage, `Coverage Policy`
requires that every file floor in `scripts/coverage/baseline.json` is matched or
exceeded by the component baseline that a change to that file selects. A
component floor may sit below the aggregate only when the lane provably cannot
reach it, and only when the entry records a `belowAggregateReason`. One entry
does today: `HVO.SkyMonitor.AgentCore/ArtifactManifestV2.cs` reaches 0.90 branch
coverage only in the union of every report, so the shared lane records 0.85 with
that reason while the aggregate rollup keeps enforcing 0.90 on `main`.

`TestResults/unit/processing-runner` is deliberately not a coverage slot: the
aggregate rollup has never counted it, and changing the aggregate slot set is
outside component gating. The Shared Libraries lane still runs that suite as a
gate; only its coverage report is discarded, exactly as the Unit job does today.

## Supplemental Unit selections

`tests/Directory.Build.props` compile-links `TestSettings/ConnectionPoolClearContract.cs` into every test project, so each assembly checks its own pool-clear usage. In four assemblies whose remaining cases are Integration or Manual, those four contract cases are the only Unit cases, and the Unit job once omitted the assemblies because their names do not read as Unit projects (#1255):

| Project | Results directory |
| --- | --- |
| `HVO.SkyMonitor.CameraAgent.IntegrationTests` | `TestResults/unit/cameraagent-integration` |
| `HVO.SkyMonitor.LogicHost.IntegrationTests` | `TestResults/unit/logichost-integration` |
| `HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests` | `TestResults/unit/cameraagent-logichost-integration` |
| `HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests` | `TestResults/unit/catalog-sqlite-performance` |

The Unit job runs them in a separate step with the same positive Unit filter, with a TRX logger and no coverage collection. The step sets `DOCKER_HOST=unix:///tmp/hvo-no-docker.sock` instead of the job's endpoint because the `LogicHost.IntegrationTests` assembly hook skips its Testcontainers fixture only for that value; under the job endpoint it would start SQL Server and Redis containers on any host with a reachable Docker daemon. They are deliberately not canonical coverage slots: the aggregate rollup's exact-path slot list is unchanged, so no duplicate or incompatible Cobertura report reaches the canonical merge, and the retained `unit-test-evidence` artifact gains only their TRX files.

When the classifier returns `complete=false`, each owning component lane also runs its four compile-linked Unit cases: shared uses Catalog.Sqlite.PerformanceTests, CameraAgent uses CameraAgent.IntegrationTests, LogicHost uses LogicHost.IntegrationTests, and combined uses CameraAgent.LogicHost.IntegrationTests. Delivery owns none. These separate steps use the exact Docker-disabled endpoint and TRX-only slots listed above, before the existing component upload; Integration, canonical coverage slots, thresholds and artifact roots are unchanged. The shared component build now includes the Catalog performance test root; the other three assemblies were already lane build roots.

`./scripts/ci:unit-selection` derives the expectation from the inventories instead of from this list: every `tests/<project>/test-categories.json` with `Unit > 0` must be selected by exactly one Unit-job `dotnet test` command that uses `--no-build`, Release, `--filter "$UNIT_FILTER"` and a TRX logger `unit-<slot>.trx` under its own `TestResults/unit/<slot>`; the command's step must match a closed grammar (a `- name:` opening line, an optional `env:` mapping holding only `DOCKER_HOST`, one `run: |` block of `dotnet test tests/<project>.csproj` lines that use only `--no-build`, `--configuration Release`, `--filter "$UNIT_FILTER"`, `--settings`, `--collect`, `--results-directory` and the TRX `--logger`) so an `if:` or `continue-on-error:` key in any spelling, a reindented step, a heredoc or shell operator, a step-level `UNIT_FILTER` override, or an option such as `--list-tests` that stops a line executing the cases is rejected, as is anything around the step that changes what it runs (workflow or job `defaults:` or `shell:`, workflow or job env beyond `DOTNET_*` and `DOCKER_HOST`/`UNIT_FILTER`, an earlier step exporting through `GITHUB_ENV` or `GITHUB_PATH` other than the catalog bundle path, a selection placed after `Bind Unit evidence`/`Upload Unit evidence`, or an upload path other than `TestResults/unit/`), a command that selects a project owning no Unit cases, a slot shared by two commands, a slot that is a canonical Coverage slot without collecting coverage, and supplemental coverage collection or `--settings`, or a supplemental step without the Docker-disabled endpoint, all fail. Job-level `env:` and workflow-level `env:` must hold only plain single-line `KEY: value` entries (a quoted key, a flow mapping or a folded continuation under `UNIT_FILTER` is rejected), and a `defaults` key in any spelling is rejected anywhere in the workflow and a `shell` key anywhere in the Unit job.

Bounded guarantee: the control proves that, for each inventory-derived Unit-bearing project, the full `unit` job holds one supported selecting command whose arguments and report boundaries (positive filter, TRX slot, coverage, Docker endpoint) are checked, ahead of the evidence bind and upload. It is not proof of arbitrary future shell execution, of whole-job artifact membership or publication (an upload condition, a cleanup step, a second upload), of installed catalog availability, or of checkout trust (a `ref:` input). Those behaviors are the workflow's existing, unchanged checkout, catalog-preparation, evidence-binding and upload steps and are covered by Required CI and review.

For those four component supplements, the same control checks exactly one supported TRX-only selecting command with the complete positive Unit filter, Release/`--no-build`, its distinct result directory and TRX name, the exact `unix:///tmp/hvo-no-docker.sock` endpoint and the closed step grammar. It checks that the command follows the named component build and precedes the named component upload, and that `scripts/ci:component-build <component> --list-projects` includes the owned assembly. List mode reads the same root array used by restore/build; a recorder control proves the real invocations agree. These are bounded selection/build-plan/report checks, not whole-job checkout, prerequisite or publication assurance; #1265 retains those separate follow-ups.

Quality runs it beside `./scripts/test:ci-unit-selection`, whose negative controls prove that each way of dropping a selection fails. Both are Docker-free and need no build:

```bash
./scripts/ci:unit-selection
./scripts/test:ci-unit-selection
```

## Failure Triage

- Formatting or package failures name the command, package, version, project, and reviewed allowlist status.
- Category failures name uncategorized, multiply categorized, unknown, or count-drifted tests.
- Migration failures identify the host context with pending model changes.
- Coverage failures name exact source paths, covered/valid counts, observed rates, and required floors.
- Integration failures retain separate project/category TRX and Cobertura evidence for 30 days.
- Deployment failures identify the failed shard in coordinator output; inspect the retained shard log and status marker before rerunning only that shard locally.

### Exact scoped delivery-proposal read (#1270)

The dedicated `ProcessingGraphProposalReadServiceTests` Unit cases and
`ProcessingGraphProposalReadTests` Integration cases own this read slice.
Discover actual categories from the built Release assemblies before changing
`test-categories.json`; the inventories are counts of discovered cases.

```bash
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj --no-build --configuration Release --filter "FullyQualifiedName~ProcessingGraphProposalReadServiceTests"
DOCKER_HOST=unix:///var/run/docker.sock dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj --no-build --configuration Release --filter "FullyQualifiedName~ProcessingGraphProposalReadTests"
```

Integration uses the existing fixture's real HTTP/TestServer and SQL Server.
Its faithful large-history case uses one initial LogicHost retrieval and one
valid acceptance per proposal, followed by 10,000 alternating activation/rollback
facts for the selected retained proposal and another 10,000 across 200 unrelated
proposals (402 initial retrieval/acceptance facts). The fixture compiles a valid
CameraAgent graph, preserves its exact revision/local/hash identities, validates
every acceptance and lifecycle transition against the delivered writer rules,
and enforces issuance and monotonically recorded UTC ordering. The concurrent
append goes through the real delivery acknowledgement service. Direct bounded
batch seeding keeps setup outside the observed read.

After one warm-up, it records three product SELECTs, the actual existing fact
index rows examined/returned (and clustered-key lookups), response bytes and one
observed HTTP duration for the latest 50 plus one sentinel. Separate explicitly
synthetic duplicate-retrieval rows test adversarial equal-timestamp ordering;
those rows are excluded from faithful workload cost. Fixture startup and seeding
are outside the observation; this is bounded-read evidence, with no percentile,
speedup, full-history snapshot or production qualification claim. Raw SQL plan
XML and observed values are retained in TRX output with the exact source/DLL/PDB
provenance in the issue/PR evidence archive.
