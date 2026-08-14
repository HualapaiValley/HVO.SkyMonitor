# Deferred Phase 14 Evidence Campaign

## Authority And Scope

Issue #305 owns post-milestone Phase 14 evidence import, remaining real fault
campaigns, aggregation, and measured optimization. Issue #318 defines this
contract. Neither issue reopens or claims the closed Virtual-First Platform
Completion milestone, `GATE-P14`, or `DONE-007`.

The machine-readable definition is
`deploy/split-host/acceptance/phase14-evidence-contract.json`. The existing
`phase14-scenarios.json` remains the revision-hashed runtime inventory and is not
extended with post-milestone state.

This definition slice executes no scenario, imports no artifact, changes no
product or database behavior, and performs no optimization.

## Inventory Reconciliation

The schema-v2 runtime inventory contains 111 distinct rows, not 110. Commit
`a2e61a1bbff5decf454151ddae9eef72934d6113` added
`raw-boundary-payload-partially-written` as literal process-kill
coverage. It is not interchangeable with `raw-boundary-payload-written` and must
not be dropped or merged.

The definition contract binds the canonical inventory SHA-256 and maps every row
exactly once:

| Disposition | Rows | Meaning |
| --- | ---: | --- |
| `accepted-current-head` | 0 | Reserved for an already admissible immutable artifact; none is promoted by this definition. |
| `source-family-import` | 103 | The inventory names a `test:` source; a later importer must produce sanitized revision-bound evidence. |
| `real-campaign` | 2 | `normal-flow` and `logichost-network-outage` have supported executable harnesses. |
| `deferred` | 2 | `permanent-upload-rejection` and `network-failure` lack supported campaign harnesses. |
| `excluded` | 4 | External, soak, Stellarium, and future-hardware rows remain separate gates. |

`executionClass` remains runtime inventory metadata and is not a disposition.
In particular, ten `real-campaign` inventory rows cite test evidence and remain
source-family imports until an admissible artifact exists.

## Source Families

The contract maps every `test:` reference to exactly one project family. It pins
product behavior to main revision
`ee117c1e8cf3e04998825d366da663e16c2b95ed` and tree
`17a0ad69452d710c82847f9a515022de05ebfaa5`; later importers cannot substitute
newer product behavior. Evidence executes from a separately reviewed clean
descendant harness revision/tree whose complete diff is confined to the exact
test, recorder, importer, contract, CI, and documentation allowlist. Standard
test families run each unique fully qualified method once from that harness in
Release configuration. The CameraAgent acceptance family
uses the complete five-trial `scripts/test:cameraagent-standalone-211` harness and
its pinned catalog and collector inputs. The index and acceptance bundle bind
the reviewed collector digest and validated production catalog manifest/database
digests and length. Retained test assemblies use a deterministic relative source
path map and are rejected if they contain the canonical repository path. There
are 25 unique methods across the
103 rows:

| Family | Project | Rows | Unique methods |
| --- | --- | ---: | ---: |
| `cameraagent-tests` | `tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj` | 75 | 18 |
| `cameraagent-acceptance` | `tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj` | 6 | 1 |
| `logichost-integration` | `tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj` | 22 | 6 |

A test citation or passing method-level TRX alone cannot become `passed`. Many
methods exercise several fault points inside one loop, so `;case=` must exactly
match each fragment's retained selector and never identifies a TRX data row.
Import requires one explicit
passed entry per scenario, the versioned sanitizer and admissibility validator
named by the contract, exact requested revision/tree binding, immutable TRX and
source-evidence inputs, and verified lengths and SHA-256 values. Each digest
binds bytes at an explicit safe relative path. The separately hashed source
evidence file must parse to the exact `entries` array embedded in the bundle, so
no digest is self-referential or ambiguous. Standard imports rebuild with
`NoIncremental=true` after checking the clean reviewed harness revision/tree and
retain that build provenance with the assembly digest. Scenario evidence remains
bound to the pinned product revision/tree. The issue-211 harness performs its own
build while continuously checking the same captured worktree fingerprint.
Every method bundle also requires an exact `trialResults` array. Standard methods
retain one `method` result at `trial-results/method.trx`. The acceptance method
retains all five sanitized results at `trial-results/trial-1.trx` through
`trial-results/trial-5.trx`; its compatibility `result.trx` is byte-identical to
trial 1. The recorder prefixes the three semantic publication observations in
every trial, while the bounded expensive restart, pressure, and shutdown
observations remain in reviewed trial 1, producing 18 independently validated
fragments.
Multiple observations for one selector are accepted only when their assertion
and measurement schemas agree; the artifact then retains an explicit
`observation-count` measurement.

The versioned redaction policy rejects credential-related fields, exception or
response bodies, absolute paths, service authorities, raw logs/payloads, control
characters, and registered secret fingerprints before hashing or publication.
Allowed transformations replace hosts with roles and paths with relative IDs,
while retaining bounded counts, durations, digests, enum values, and reason
codes.

## Executable Workloads

The only currently supported real campaigns are:

```bash
./scripts/deploy:environment acceptance-run \
  --inventory /absolute/path/inventory.yml \
  --mode isolated \
  --run-id <run-id> \
  --scenario normal-flow

./scripts/deploy:environment acceptance-run \
  --inventory /absolute/path/inventory.yml \
  --mode isolated \
  --run-id <run-id> \
  --scenario logichost-network-outage
```

The contract pins topology, canonical profiles and seed, warm-up and measured
operation counts, 0.04 captures-per-second arrival rate, initial backlog,
concurrency, measured stages, and correctness checks. The outage workload also
pins ten outage captures, a nominal 250-second fault window, and the existing
900-second recovery budget. Each workload also requires the complete evidence
record from `docs/planning/performance-validation.md`: revision, environment,
workload, method, I/O, CPU, memory, latency, throughput, backlog, correctness,
and result/noise disposition.

Evidence source revision is scenario-specific. Test-backed imports remain pinned
to the definition revision above. Real campaigns bind the later clean execution
revision/tree selected by #320 after any missing harness implementation is
reviewed. The campaign index records both per-scenario source provenance and the
separate aggregation revision/tree; it does not falsely force all evidence to
one revision.

The two unsupported campaigns are explicitly `executable: false`. A later issue
must add and validate a real harness before changing their disposition; an
`acceptance-run` command that deterministically rejects a scenario is not an
executable workload manifest.

## Artifact And Status Semantics

Every promoted runtime artifact is owner-only, no larger than 256 KiB, sanitized,
and retains the existing exact schema-v1 shape. A separate campaign-index entry
binds the sanitizer/admissibility identities and artifact schema, byte length,
and SHA-256 without changing runtime artifact compatibility. The index also
binds its definition and inventory hashes, each scenario's runtime-artifact
source revision/tree, and the later aggregation revision/tree. Missing, stale, unhashed,
unsanitized, incompatible, digest-mismatched, or tampered evidence fails closed.
Recorded bytes and committed index generations are immutable. Each index and
owner-only commit envelope is retained beneath
`phase14-evidence/generations/<publication-generation>/`; the envelope binds the
canonical index byte length, SHA-256, and generation. A replaceable
`latest-generation.json` pointer is published last and binds the generation plus
the generation-scoped index and commit paths and digests. Resume validates that
pointer, envelope, and all artifact digests before publishing generation plus
one, without replacing prior generation bytes. The pointer has an explicit
schema-v1 field set, positive lengths/generation, lowercase SHA-256 bindings,
safe generation-scoped paths, owner-only mode 0600, and atomic replacement.

- `not-run`: no admissible artifact has been evaluated.
- `recorded`: immutable bytes exist and their length and digest match.
- `passed`: recorded evidence passed its disposition-specific policy.
- `deferred`: planned under #305 but not executable or admissible yet.
- `excluded`: owned by a separate non-Phase-14 gate.
- `campaign-complete`: every non-deferred, non-excluded row passed; deferred and excluded counts remain visible.
- `parent-complete`: all #305 import, campaign, aggregation, and optimization dispositions are closed.

These definition states do not alter the runtime acceptance ledger. In
particular, issue #318 cannot mark a scenario passed or complete #305.

## Follow-Up Boundaries

Later issues must remain non-overlapping:

1. #319 owns source-family execution/import, sanitization, and immutable artifacts for the 103 test-backed rows.
2. #320 owns execution and immutable artifacts for supported `normal-flow` and `logichost-network-outage`, plus harness support and execution for the two deferred rows.
3. #321 follows both evidence families and owns admissibility, the immutable index, telemetry/resource summaries, and residual risks.
4. #322 follows aggregation and opens focused product issues only where measured evidence justifies a change.

Any discovered product defect becomes a separate correctness issue. Evidence
work must not weaken an existing test or change product behavior merely to make
collection easier.

## Validation

Run:

```bash
./scripts/test:phase14-acceptance
./scripts/test:phase14-component
./scripts/test:phase14-source-import
./scripts/test:phase14-campaign
./scripts/test:phase14-normal-campaign
```

The acceptance contract test verifies the 111-row digest and exact one-rule
coverage, project/FQN family counts, executable workload fields, artifact/status
semantics, and follow-up boundaries. Negative checks reject stale counts or
hashes, unpinned source revisions, unmapped rows, missing sanitizer identity,
incorrect follow-up dependencies, and unsupported campaigns advertised as
executable.
