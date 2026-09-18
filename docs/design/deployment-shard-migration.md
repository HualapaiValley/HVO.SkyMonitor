# Deployment Contract Shard Migration

Issue #875, parent #876, under the target architecture in
[ci-deployment-target-architecture.md](ci-deployment-target-architecture.md)
section 3.4. This is the start-gate deliverable: the scenario-to-owner map and the
staged plan. It moves no shard behaviour.

## 1. What the harness is

`scripts/test:deploy-environment` is 5,998 lines in one Bash file with four
distinct responsibilities:

| Responsibility | Lines | Content |
| --- | --- | --- |
| Coordinator and options | 1-346 | shard list, bounded worker pool, `setsid` process groups, leader-stop detection, progress/stall/hard watchdogs, fault injection for coordinator tests |
| Shared fixture | 347-1638 | temp roots, fake `ssh`/`curl`/`docker`/`stat`/`grep`/`git` shims, inventory writer, deploy runners, phase recovery exercisers, 123 `FAKE_*` knobs |
| Shard bodies | 1639-5993 | ten `if shard_enabled NAME` blocks, 4,355 lines, 1,128 assertions |
| Serial epilogue | 5994-5998 | end-of-run message for `SELECTED_SHARD=all` |

Every shard body sources the production deployment libraries under
`scripts/deploy/*.sh` (fourteen files) and runs them against fake hosts, so the
harness is simultaneously the only test of that shell and a large consumer of it.

### Measured shape (run 35280447884, main, 8 workers on 16 CPUs)

| Shard | Lines | Assertions | Docker calls | Fake knobs | Wall |
| --- | --- | --- | --- | --- | --- |
| existing-down | 196 | 61 | 13 | 14 | 991s |
| measure | 291 | 45 | 0 | 5 | 753s |
| existing-catalog-up | 1023 | 143 | 0 | 2 | 556s |
| smoke | 171 | 37 | 0 | 13 | 450s |
| bootstrap-credentials | 115 | 21 | 0 | 3 | 332s |
| deploy-services | 692 | 218 | 16 | 20 | 289s |
| bootstrap-authority | 189 | 51 | 20 | 4 | 199s |
| prepare-images | 417 | 139 | 20 | 15 | 123s |
| partial-prepare | 391 | 68 | 5 | 9 | 77s |
| preflight | 1884 | 345 | 44 | 25 | 49s |

Lane wall 992s, set entirely by `existing-down`. Note that wall does not track
line count: the smallest shard is the slowest because it waits on lifecycle
phases the fixture stands up serially. Three recent main runs: 930s, 975s, 992s.

### Coupling that constrains the split

Shard blocks are not independent. Six top-level variables are assigned in one
block and read in another (`deploy_case`, `output`, `evidence`, `lock_path`,
`east_config`, `bootstrap_state`); `deploy_case` is read by seven shards.
Functions defined inside the `preflight` and `partial-prepare` blocks
(`stage_lifecycle_*`, `prepare_lifecycle_fixture`, `exercise_phase_*`,
`create_partial_prepare_case`) are called from six other shards. In serial mode
(`SELECTED_SHARD=all`) this works because blocks execute in file order; in
`--shard NAME` mode each shard re-runs the whole file with the other blocks
skipped, and the `if [[ "$SELECTED_SHARD" == NAME ]]; then stage_lifecycle_*`
lines at each block head exist to rebuild the state a skipped predecessor would
have left. That per-shard re-staging is where the wall time goes.

The consequence for migration: a shard cannot be cut out of the file until the
lifecycle staging it depends on has a home outside the file.

## 2. Scenario-to-owner map

Ownership is assigned by what each shard actually proves, not by where its
lines sit today.

| Shard | Proves | Target owner | Retained shell |
| --- | --- | --- | --- |
| preflight | inventory schema, split-host preflight rejection matrix, tampered-registry rejection, legacy login shape, `bash -n` of production scripts | `Deployment.Contracts` inventory and preflight validators; MSTest in `Deployment.Cli.Tests` | `bash -n` syntax gate stays shell (it tests shell) |
| prepare-images | image staging, registry digest binding, isolated/cross-case prepare failures, eight failpoints | `Deployment.Distribution` image staging; MSTest | none |
| partial-prepare | no-container recovery after failed prepare, lock/state cleanup, quoted-path cases | `Deployment.Cli` recovery command; MSTest | none |
| existing-catalog-up | production catalog bundle install, lock blocking, resume failure, legacy path case, fixture transport | `Catalog.Sqlite` install contract plus `Deployment.Cli` transport; MSTest | `scripts/catalog/*.sh` remain the installer under test; a thin adapter invokes them |
| bootstrap-authority | registry publication as hard barrier, rendered/private bootstrap state, registry-failure recovery | `Deployment.Cli` bootstrap phase; MSTest | none |
| bootstrap-credentials | credential destination rejection, orphan rejection, incomplete-phase marking | `Deployment.Cli` bootstrap phase; MSTest | none |
| smoke | hybrid transient smoke over fake HTTP, thirteen response-shape knobs | `Deployment.Cli` smoke with an in-process fake HTTP server; MSTest | none |
| measure | measure ledger, private-upload registry, strict entry cleanup, four failpoints, runtime-hash staleness | `Deployment.Cli` measure phase; MSTest | none |
| existing-down | same-host lifecycle down, remote private artifact absence, Docker context teardown | `Deployment.Cli` down phase; MSTest for state, one shell adapter for Docker context assertions | Docker-context teardown proof stays shell (it proves the Docker CLI boundary) |
| deploy-services | services up, compose rendering, sixteen Docker calls, three failpoints | `Deployment.Cli` up phase; MSTest for rendering and state, shell adapter for Compose | Compose invocation proof stays shell |

Shared fixture: the fake-tool shims (`ssh`, `curl`, `docker`, `stat`, `grep`,
`git` under `$BIN`/`$REMOTE_BIN*`) become one `DeploymentFakeHost` fixture in
`Deployment.Cli.Tests`, replacing 123 environment knobs with typed options. The
coordinator's process-group supervision, watchdogs and fault injection are
replaced by MSTest's per-test timeouts and the runner's own process model, and
the coordinator's own tests (`scripts/test:deploy-environment-cli`) retire with it.

Deliberately retained shell, in full: `bash -n` syntax gate; catalog installer
invocation adapter; Docker-context teardown adapter; Compose invocation adapter.
Each is a boundary where the shell surface is the thing under test. Everything
else is state, JSON, process and assertion logic that .NET tests better.

## 3. Staged plan

Authority moves last; the shell harness stays the protected gate until each
replacement has matched it.

| Stage | Deliverable | Moves behaviour | Gate |
| --- | --- | --- | --- |
| 0 (this slice) | shard manifest `scripts/deploy/shards.json`; coordinator reads it; `--list-shards` and the CLI contract test derive from it | no | manifest parity with the in-file list |
| 1 (done) | deploy runners, recovery exercisers, prepare-case helpers and lifecycle staging (341 lines) extracted unchanged to `scripts/lib/deploy-test-lifecycle.sh`, sourced by the harness | no | all ten shards pass unchanged: local `--parallel` 844s, same completion order as the CI baseline |
| 2 (done) | each shard body extracted verbatim to `scripts/deploy-contracts/<shard>.sh` (twelve files; `preflight` is three parts because shared fixture code sits between its blocks), sourced from a dispatch line the manifest is checked against; harness 5,702 -> 1,749 lines; classifier learns the directory | no | all ten shards pass: local `--parallel` 857s, same completion order; ShellCheck on the harness 4.9 GB -> 141 MB, largest body 458 MB |
| 3-5 | moved to #893: every shard tests shell production code under `scripts/deploy/*.sh` that has no `Deployment.Cli` counterpart, so typed conversion is phase porting, not harness work | yes | per #893 |

Stages 0-2 are pure restructuring and give #782 most of what it needs. Measured
after stage 2 with ShellCheck 0.9.0 without `-x`: the monolith took 4.9 GB peak
RSS and 8.7s; the harness now takes 141 MB and 0.4s, the largest shard body
(`existing-catalog-up`, 1,032 lines) 458 MB and 1.0s, the lifecycle library 54 MB.
The `-x` source-following cost that drove the 15.4 GB figure is now bounded by
the largest single sourced file rather than by the whole harness.

## 4. Resource baseline

Recorded so stage 5 can claim a measured result rather than an assumed one.

| Measure | Value | Source |
| --- | --- | --- |
| lane wall | 930-992s | three main runs |
| controlling shard | existing-down, 991s | run 35280447884 |
| ShellCheck peak RSS, monolith with `-x` | ~15.4 GB | #782 evidence |
| ShellCheck peak RSS, monolith without `-x` | ~3.98 GB | #782 evidence |
| worker pool | 8 of 16 CPUs | coordinator `max_parallel` |
| Docker calls across all shards | 118 | static count |

Stage 5 acceptance from #875 is measured against this table: no single
multi-thousand-line contract file, every shard independently invocable, wall and
peak RSS per shard and per lane recorded before and after, and ShellCheck able to
cover every retained shell file without a per-file exception.
