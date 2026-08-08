# Issue 151 Split-Host Deployment Candidate Evidence

## Scope

Issue #151 adds a resumable control-host deployment workflow for explicit
LogicHost, CameraAgent, and optional shared-service targets. It covers read-only
preflight, owner-controlled runtime preparation, immutable multi-architecture
image distribution, catalog installation, controlled database initialization,
provisioning-gated startup, supported device bootstrap, deterministic smoke and
measurement workloads, continuity refusal, sanitized evidence, and exact
preserve/delete teardown.

The existing local-development `scripts/infra:*` workflow remains unchanged.

## Candidate

- Base revision: merged issue #255 at
  `49ffc182c12cbe347e80756735024bcabd367b1e`.
- Candidate revision: `a865429`.
- Inventory contract: schema v4 with explicit SSH hosts, Docker contexts,
  architectures, runtime roots, immutable image distribution, catalog identity,
  public/internal authorities, service namespaces, secret references, and
  workload/teardown policy.
- Local orchestration evidence uses fake SSH, Docker, transfer, registry, HTTP,
  SQL, Redis, and MinIO transports that execute the shipped remote shell bodies.
  It covers both registry and archive image distribution, existing and deployed
  services, multiple CameraAgents, all phase failpoints, continuity refusal,
  W0 checksum/lineage/trace proof, one-operation W1 execution, and exact cleanup.

## Review

- Independent functional review completed with no actionable findings.
- Independent concrete hazard review completed with no actionable findings.
- Corrections covered Docker/SSH target correlation, crash-safe phase commits,
  one-time envelope recovery, least-privilege service setup, secret-file cleanup,
  persistent capture/outbox/fleet continuity, deterministic workload boundaries,
  raw artifact checksum identity, workload-correlated traces, and resumable
  run-owned teardown.

## Local Validation

- Tool and solution restore: passed.
- Warning-clean Debug and Release builds: passed with zero warnings and errors.
- Formatting verification: passed.
- Package audit: passed with no vulnerabilities and the reviewed deprecation
  allowlist unchanged.
- Category audit: `Unit=1755`, `Integration=533`, `Manual=75`, `Soak=1`,
  `External=0`, and `Hardware=1`.
- Deployment/failpoint contract: passed.
- Documentation audit, Bash syntax, ShellCheck, JSON/schema checks, Compose model
  checks, and actionlint: passed.
- Unit selection with an invalid Docker endpoint: the pre-review 1,754-case gate
  passed; the added trace-key regression passed in its focused three-case suite,
  bringing current discovery to 1,755. Protected replacement CI owns the full
  corrected-head Unit gate.
- Integration evidence: CameraAgent storage 145/145, central 357/357,
  CameraAgent host 21/21, architecture/publish 6/6, and standalone acceptance
  4/4. The aggregate run initially produced the separately known transient local
  owner `403`; the exact case and complete four-case acceptance project passed
  immediately as replacement evidence.
- CameraAgent and LogicHost pending-model checks: passed with no changes.
- Representative workload hook: the deployment contract executes the pinned W0
  profile and a one-operation W1 path with separate warm-up/measured boundaries,
  exact sequence/artifact identity, drain, checksum, provenance, and trace facts.
  Sustained W1/W2 and physical-host resource measurements remain operator-run
  evidence rather than a claim from fake transports.

## Real-Host Disposition

The configured amd64 host `hvo-docker` was reachable and reported Docker
29.5.2, Compose 5.3.1, `x86_64`, eight CPUs, and approximately 20 GB memory.
Neither configured ARM64 target was reachable during candidate validation:

- `devpi5`: SSH banner exchange timed out/reset at `192.168.1.8:22`; its Docker
  context could not connect.
- `allsky01`: `192.168.2.169:22` returned no route to host; its Docker context
  could not connect.

Therefore this candidate does not claim the issue's clean real amd64/ARM64
split-host smoke, production catalog transfer, real registry/archive transfer,
or real preserve/delete teardown. Those checks require an available ARM64 host,
owner-provided deployment secrets/certificates, and an approved production
catalog bundle. Protected CI can validate repository behavior but cannot replace
that environment-owned acceptance evidence.

## Performance

The orchestration records phase duration, transfer bytes/checksums, target
resources, queue/backlog state, exact warm-up/measured capture ranges, and
correctness/drain facts. No production throughput or ARM64 resource conclusion
is claimed without the real-host run. The simplest sequential transfer and
per-target phase design remains the default; no unexplained local build or test
regression was observed.
