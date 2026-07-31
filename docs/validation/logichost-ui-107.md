# LogicHost UI issue #107 validation

Run the opt-in browser gate from the repository root:

```bash
./scripts/test:logichost-ui-107 --install-browser
```

After Chromium is installed, omit `--install-browser` on later runs. The gate builds the LogicHost integration assembly in Release, starts the digest-pinned SQL Server, Redis, MinIO, and Mailpit fixtures, and hosts the production LogicHost entry point on loopback Kestrel.

The browser journey verifies:

- public home, directory, observatory detail, and event routes at the five accepted desktop/mobile viewports;
- one main landmark and heading, named controls, no horizontal overflow, and successful scoped-CSS delivery;
- hidden exact-location, owner identity, object credential, storage-reference, connection-string, and temporary-path leakage sentinels;
- anonymous redirection from `/app` to login and authenticated access to the observatory-scoped processing policy;
- browser page errors and same-origin HTTP 5xx responses remain absent.

The dependency journey separately stops SQL Server, Redis, MinIO, and SMTP for at least 15 seconds. It requires each named health check and real dependency operation to fail without presenting an empty-data success state, then requires the same container, health check, and operation to recover within 60 seconds.

The browser performance gate seeds the exact deterministic `I107-P13-UI-v1` 1K/10K histories and runs five isolated Release processes. Each trial creates 1, 10, and 50 independent authenticated Chromium contexts for every public, archive, operations, events, and authority mix, with 20 warm-up and 200 measured closed-loop server-rendered navigations. It also measures public-preview transfer at 1/10/50 clients, exact W2 full/range retrieval, empty conditional `304` responses, pre-cancelled reads, and a five-warm-up/30-operation W1 encoded-preview recipe run. A separate interactive phase holds 1, 10, and 50 connected sessions for a 15-second settle plus 60 one-second resource samples. The retained candidate summary reports five-trial minimum/median/maximum for p95 latency, response bytes, throughput, CPU, and allocations while each trial enforces the 5-second p95, 15-second maximum, 512-KiB response, and 256-MiB 50-session growth gates.

Evidence is written beneath `TestResults/issue-107/<fingerprint>/<run-id>/`. The fingerprint includes the Git revision, tracked diff, status, and untracked-file checksums. Retained records include sanitized browser, dependency transition, logs, metrics, traces, health, process, SQL, map-request, and cardinality/leak evidence. Screenshots and raw browser traces are not retained because raw-download authorization URLs may contain short-lived credentials.

The canonical runtime-signal contract is `docs/validation/logichost-ui-107-runtime-signals.json`. The UI adds no worker or queue health boundary; existing LogicHost dependency health checks remain authoritative.
