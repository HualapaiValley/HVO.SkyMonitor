# LogicHost UI issue #107 validation

Run the opt-in browser gate from the repository root:

```bash
./scripts/test:logichost-ui --install-browser
```

After Chromium is installed, omit `--install-browser` on later runs. The gate builds the LogicHost integration assembly in Release, starts the digest-pinned SQL Server, Redis, MinIO, and Mailpit fixtures, and hosts the production LogicHost entry point on loopback Kestrel.

The browser journey verifies:

- public home, directory, observatory detail, and event routes at the five accepted desktop/mobile viewports;
- one main landmark and heading, named controls, no horizontal overflow, and successful scoped-CSS delivery;
- hidden exact-location, owner identity, object credential, storage-reference, connection-string, and temporary-path leakage sentinels;
- anonymous redirection from `/app` to login and authenticated access to the observatory-scoped processing policy;
- browser page errors and same-origin HTTP 5xx responses remain absent.

The dependency journey separately stops SQL Server, Redis, MinIO, and SMTP for at least 15 seconds. It requires each named health check and real dependency operation to fail without presenting an empty-data success state, then requires the same container, health check, and operation to recover within 60 seconds.

The browser performance gate seeds the exact deterministic `I107-P13-UI-v1` 1K/10K histories and runs five isolated Release processes. Each trial creates 1, 10, and 50 independent authenticated Chromium contexts for every public, archive, operations, events, and authority mix, with 20 warm-up and 200 measured closed-loop server-rendered navigations. It also measures public-preview transfer at 1/10/50 clients, exact W2 full/range retrieval, empty conditional `304` responses, pre-cancelled reads, and a five-warm-up/30-operation W1 encoded-preview recipe run. A separate interactive phase holds 1, 10, and 50 connected sessions for a 15-second settle plus 60 one-second resource samples. The retained candidate summary reports five-trial minimum/median/maximum for p95 latency, response bytes, throughput, CPU, and allocations while each trial enforces the 5-second p95, 15-second maximum, 512-KiB response, and 256-MiB 50-session growth gates. Override the default retained baseline root with `HVO_LOGICHOST_UI_BASELINE_ROOT` when validating another reviewed baseline.

Evidence is written beneath `TestResults/issue-107/<fingerprint>/<run-id>/`. The fingerprint includes the Git revision, tracked diff, status, and untracked-file checksums. Retained records include sanitized browser, dependency transition, logs, metrics, traces, health, process, SQL, map-request, and cardinality/leak evidence. Screenshots and raw browser traces are not retained because raw-download authorization URLs may contain short-lived credentials.

The canonical runtime-signal contract is `docs/validation/logichost-ui-runtime-signals.json`. The UI adds no worker or queue health boundary; existing LogicHost dependency health checks remain authoritative.

## Protected scope navigation (#1238)

The **Network** entry in Network Operations opens `/app/network`. Observatory
and logical-camera scope use nested paths:

```text
/app/network/observatories/{observatoryId}
/app/network/observatories/{observatoryId}/cameras/{cameraId}
```

The shell checks current membership and the camera/observatory relationship
before showing either scope's name or its content. Existing observatory
operations and camera installation/capture history remain linked. The richer
Current Sky, Archive, imported inspector and central workspace sections require
their separately accepted implementation stories.

Each scope selector displays one 25-item page. Next/first links replace that
page; browser history retains the page cursors and restores the previously
focused scope link. Navigation cursors are protected and bound to the actor,
list and parent observatory. Invalid or obsolete pages offer an explicit reset.
Authentication changes render the cleared/loading state before waiting for
replacement authorization. Forward scope navigation focuses the completed
heading when there is no valid saved or pagination focus target.
Access is rechecked on navigation, authentication changes, refresh, browser
focus/visibility/online changes, circuit reconnect and a 30-second interval.
Denied or failed checks clear protected labels and child content. Read failures
show a generic retry state; an empty authorized collection has a separate state.

Run the focused component boundary and opt-in real-browser gate with the pinned
SDK and a locally installed Playwright Chromium:

```bash
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj --configuration Release --filter "FullyQualifiedName~OperationsScopeShellTests|FullyQualifiedName~NetworkRouteTests"
dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj --configuration Release --filter "FullyQualifiedName~OperationsScopeBrowserAcceptanceTests"
```

The browser case uses disposable SQL/Redis/Mailpit fixtures and the real Kestrel
host. It exercises 64 observatories and 1K/10K camera scopes, complete-shell SQL
command/reader bounds (including selected scope, choices and installation reads),
URL/history/focus restoration, delayed forward reads, keyboard/touch navigation, accessible
landmarks and names, desktop/portrait/short-landscape layouts, actual websocket
reconnect with access revoked while disconnected, automatic clearing before the
first periodic tick, role access, cross-scope denial and membership removal. Its attached
`issue-1238/acceptance.json` records SQL commands/reader operations, HTML bytes,
journey timings and Chromium heap samples. Set `HVO_EVIDENCE_REVISION` to the
exact source revision or worktree fingerprint for retained candidate evidence.
These scope measurements supplement the canonical #107 workload; they do not
claim archive, image-transfer or multi-client qualification.
