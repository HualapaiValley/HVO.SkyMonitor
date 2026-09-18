# Requirements-Driven CI and Deployment Target Architecture

Issue #877. Parent epic #876. This document is the approved current-state evidence map and target design that must precede any change to protected CI or deployment authority. It writes design and evidence only; it does not rewrite CI and does not translate the deployment harness.

## 1. Current-State Gate Inventory

`.github/workflows/ci.yml` triggers on `main` and `release/**` and defines eighteen jobs. `Required CI` aggregates sixteen of them through `scripts/ci:require`, which is the actual merge authority: a job that is skipped by selection must be proven intentionally skipped rather than silently absent.

| Job | Runner | Selector | Role |
| --- | --- | --- | --- |
| Change Classification | hosted | not draft | Emits the selection plan consumed by every other job |
| Catalog Contracts | hosted | `mode == full` | Produces the `hyg-v42-contracts` artifact consumed by four jobs |
| Quality | hosted | always, inner steps `mode == full` | Format, package audit, shell syntax, CI-control guards |
| Deployment Contracts | self-hosted | `deployment == true` | Catalog, shard, and installer deployment gates |
| Build | self-hosted | `mode == full` | Warning-clean Debug and Release solution build |
| Unit Tests | self-hosted | `complete == true` | Complete Unit selection and category audit |
| Integration Tests | self-hosted | `complete == true` | Testcontainers SQL Server, Redis, MinIO, Mailpit |
| Architecture & Publish | self-hosted | `mode == full` | Architecture rules and publish verification |
| CameraAgent Migrations | self-hosted | `mode == full` | Canonical migration check |
| LogicHost Migrations | self-hosted | `mode == full` | Canonical migration check |
| Coverage Policy | hosted | `mode == full` | Coverage configuration policy |
| Shared Libraries | self-hosted | `complete == false && shared` | Component lane |
| CameraAgent Component | self-hosted | `complete == false && cameraagent` | Component lane |
| LogicHost Component | self-hosted | `complete == false && logichost` | Component lane |
| Combined Protocol & Integration | self-hosted | `complete == false && combined` | Component lane |
| Delivery Component | self-hosted | `complete == false && delivery` | Component lane |
| Coverage | self-hosted | `complete == true` | Aggregates Unit, Integration, Architecture evidence |
| Coverage Badges | self-hosted | `ref == main` | Publishes badge gist |
| Required CI | hosted | always | Merge authority over the plan and results |

The classifier emits thirteen outputs: `mode`, `complete`, `shared`, `cameraagent`, `logichost`, `combined`, `delivery`, `deployment`, `deployment_catalog`, `deployment_shards`, `deployment_installer`, `branch`, `line`. `mode` and `complete` are independent: `mode` selects the full solution matrix, `complete` selects the complete test matrix versus component lanes.

### Measured baseline

Run `35300523583`, a complete-matrix pull request run, wall clock 19 minutes:

| Job | Duration | Class |
| --- | --- | --- |
| Integration Tests | 15.9m | controlling path |
| Quality | 12.3m | hosted, parallel |
| Unit Tests | 8.2m | self-hosted |
| Build | 6.9m | self-hosted |
| Architecture & Publish | 5.5m | self-hosted |
| Coverage | 2.1m | dependent aggregation |
| CameraAgent Migrations | 1.5m | self-hosted |
| LogicHost Migrations | 1.3m | self-hosted |
| Catalog Contracts | 0.3m | hosted |
| Coverage Policy, Change Classification, Required CI | 0.1m each | hosted |

Integration alone sets the floor. No reordering, caching, or runner change reaches a ten-minute pull-request target while Integration stays on the pull-request critical path in its current form. This is the central finding: the legacy pipeline is slow because of what it requires per pull request, not primarily because of how it is scheduled.

The measured Development v1 line, by contrast, completes hosted Preflight in 8-9s and self-hosted Build and Unit in about 3m50s, because it requires exactly two checks.

### Duplication versus required isolation

Restore and Release build occur in Build, Unit, Integration, Architecture, both migration jobs, and every component lane. This is genuine duplication of compute, but it is also the current provenance model: each job independently reconstructs its inputs, so no job trusts another job's mutable output. Removing duplication therefore requires an explicit immutable-build contract, not an incremental cache. `#852` is the correct pilot for that and must not be generalized before it reports.

`Catalog Contracts` already demonstrates the target pattern: one producer, an immutable named artifact, four consumers.

## 2. Deployment Surface Inventory

Typed deployment code is `HVO.SkyMonitor.Deployment.Contracts`, `HVO.SkyMonitor.Deployment.Distribution`, `HVO.SkyMonitor.Deployment.Cli`, and `tools/HVO.SkyMonitor.Deployment.ReleaseTool`. `Deployment.Cli` is the sole supported deployment authority.

`scripts/test:deploy-environment` is a 5,998-line Bash harness that is simultaneously a test suite, a fixture factory, a process coordinator, and a fault injector. It defines ten shards: `preflight`, `prepare-images`, `partial-prepare`, `existing-catalog-up`, `bootstrap-authority`, `bootstrap-credentials`, `smoke`, `measure`, `existing-down`, `deploy-services`. It contains its own bounded worker pool with `setsid` process-group supervision, progress files, and injected failure, hang, progress, and descendant-failure modes.

Three properties make this the highest-risk component in the estate:

1. It is authority-adjacent: it exercises publication, rollback, tampering rejection, orphan rejection, and credential bootstrap.
2. It is not analyzable by the standard toolchain: source-aware ShellCheck of this one file peaked near 15.4 GB RSS and twice caused runner shutdown, which is why `#782` was abandoned.
3. Its coordinator semantics are real concurrency logic implemented in the least verifiable language available in the repository.

The target therefore does not attempt to lint this file into compliance. It replaces the coordinator and assertions with typed shards under `Deployment.*` and retains shell only as thin adapters where a shell boundary is the thing actually under test.

## 3. Target Architecture

### 3.1 Versioned gate graph

A single machine-readable graph becomes the source of truth for selection, dependency, ownership, and budget. Local and GitHub adapters consume the same file, so the local candidate gate and protected CI can no longer disagree by construction.

```jsonc
{
  "schemaVersion": 1,
  "gates": [
    {
      "id": "unit",
      "name": "Unit Tests",
      "command": "dotnet test --filter TestCategory=Unit",
      "runner": "self-hosted-x64",
      "events": ["pull_request_ready", "push", "qualification"],
      "selectors": { "components": ["shared", "cameraagent", "logichost"] },
      "needs": ["build"],
      "consumes": [{ "artifact": "release-build", "immutable": true }],
      "produces": [{ "artifact": "unit-trx", "retentionDays": 14, "privacy": "content-free" }],
      "budget": { "wallSeconds": 600, "peakRssKilobytes": 8000000 },
      "required": true
    }
  ]
}
```

Every gate declares event and path selectors, dependencies, consumed and produced artifacts with provenance, a resource budget, and whether it is required. `Required CI` becomes a derived aggregation: it asserts that the gates the graph selected for this event all reported the expected result, and that every unselected gate was unselected by a recorded rule rather than by omission.

### 3.2 Run classes

The legacy pipeline conflates four distinct questions into one pull-request run. The target separates them:

| Run class | Trigger | Target duration | Content |
| --- | --- | --- | --- |
| PR integration | ready pull request | under 10 minutes | Preflight, build, unit, affected component lanes |
| Full integration qualification | scheduled and pre-promotion | unbounded | Complete matrix, Integration, coverage, migrations, architecture |
| Release qualification | release branches and tags | unbounded | Full qualification plus packaging, signing, provenance |
| Deployment qualification | explicit and pre-release | unbounded | Typed deployment shards against disposable targets only |

Integration coverage is not weakened; it is moved off the per-pull-request critical path and onto the qualification classes that gate promotion. This is the only change that makes the operator's ten-minute requirement achievable without deleting evidence.

### 3.3 Immutable build and evidence model

One producer builds Release once per run and publishes it as an immutable, digest-identified artifact. Consumers restore that artifact and never rebuild it. Evidence artifacts are content-free by default, following the model already accepted in `#795`, and carry a declared retention and privacy class in the graph.

### 3.4 Typed deployment shards

Each of the ten shards becomes a typed scenario built on `Deployment.Contracts` and driven through `Deployment.Cli` against disposable targets. The shard coordinator becomes a typed orchestrator with structured results, replacing the Bash worker pool and its injected-fault matrix with testable code. Shell adapters are retained deliberately only where the shell surface is itself the contract under test, and each retained adapter is named in `#875`.

### 3.5 Runner trust and capacity

Hosted runners take cheap, untrusted, fast work: whitespace, workflow lint, policy checks, classification. Self-hosted runners take build, test, Docker, and deployment work. Ready-pull-request capacity is protected from post-merge contention, which is the standing problem `#849` owns.

## 4. Migration Plan

The migration is staged so that authority moves last.

1. Publish the gate graph and schema with no authority; nothing consumes it.
2. Run the graph-derived plan in shadow against the legacy classifier and record every disagreement.
3. Adopt the graph in the local runner adapter, where a mistake is cheap and visible.
4. Pilot immutable build reuse for exactly one consumer (`#852`).
5. Convert deployment shards to typed scenarios incrementally, shard by shard, keeping the shell harness authoritative until each replacement matches.
6. Split run classes and move Integration to qualification.
7. Cut `Required CI` over to graph-derived aggregation only after shadow agreement is clean.

Rollback at every stage is reverting the adapter to the legacy selector, because the legacy classifier stays in place until the final cutover. No stage removes a currently protected behavior; the crosswalk in section 6 is the check on that.

## 5. Disposition Of Existing Issues

| Issue | Disposition |
| --- | --- |
| #792 Skip Quality on post-merge pushes | Superseded. Becomes an event selector in the gate graph rather than a workflow condition. |
| #849 Protect ready-PR runner capacity | Retained. Folds into the runner capacity model in 3.5. |
| #850 LogicHost integration critical path | Retained and elevated. Integration is the measured controlling path. |
| #851 Rebalance deployment shard long tails | Superseded by #875. Rebalancing the Bash coordinator is wasted work if it is being replaced. |
| #852 Immutable same-run build reuse pilot | Retained as the pilot for 3.3. |
| #853 Separate per-project counts from audit code | Retained. Category inventory ownership in the graph depends on it. |
| #873 ShellCheck warning backlog | Retained, reduced scope. Files replaced by typed shards need no warning remediation. |
| #875 Typed deployment shard orchestration | Retained as the central deployment deliverable, absorbing #851. |
| #782 ShellCheck error enforcement | Blocked until #875 lands. The 15.4 GB file is the entire obstacle; enforcement becomes feasible once it no longer exists. |

## 6. Requirements Crosswalk

No current protected behavior may disappear by omission. Every one of the eighteen current jobs is listed individually below, with no grouped rows, so that coverage can be checked mechanically against the job names in `.github/workflows/ci.yml`. This table is also the input to the Required CI aggregation contract: a gate that is not named here is not aggregated there.

| Current job | Target owner | Run class |
| --- | --- | --- |
| Change Classification | gate graph selector | all |
| Quality | preflight and policy gates | PR integration |
| Catalog Contracts | immutable artifact producer | PR integration |
| Build | immutable build producer | PR integration |
| Unit Tests | unit gate | PR integration |
| Shared Libraries | component gate, `shared` selector | PR integration |
| CameraAgent Component | component gate, `cameraagent` selector | PR integration |
| LogicHost Component | component gate, `logichost` selector | PR integration |
| Combined Protocol & Integration | component gate, `combined` selector | PR integration |
| Delivery Component | component gate, `delivery` selector | PR integration |
| Integration Tests | integration gate | full qualification |
| Architecture & Publish | architecture gate | full qualification |
| CameraAgent Migrations | migration gate, CameraAgent | full qualification |
| LogicHost Migrations | migration gate, LogicHost | full qualification |
| Coverage Policy | coverage policy gate | full qualification |
| Coverage | coverage aggregation gate | full qualification |
| Deployment Contracts | typed deployment shards | deployment qualification |
| Coverage Badges | release publication | release qualification |
| Required CI | derived aggregation contract | all |

## 7. Approval Gate

Maintainer approval of this document is required before any child issue changes protected authority. Children may proceed with non-authoritative work (schema, shadow comparison, typed shard conversion, local adapter) before that approval.

Open decisions for the maintainer:

1. Confirm that Integration moves off the per-pull-request path to qualification, since this is the only change that meets the ten-minute target without reducing coverage.
2. Confirm the four run classes and their triggers.
3. Confirm the #851 supersede and the #782 blocked-until-#875 sequencing.
