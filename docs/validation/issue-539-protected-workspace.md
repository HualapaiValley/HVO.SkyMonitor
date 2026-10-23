# Protected LogicHost workspace acceptance (#539)

This is the final software integration acceptance for RM-018's protected
workspace, on `development/v1`, starting at
`523d43d9a36fbf7f3d7bfff47d29a74909061fd7`. Tier B; deep independent review
for authorization, stale-role actions and navigation interactions. The protected
profile is **Development v1 / Preflight** and **Development v1 / Build and Unit**.
It does not qualify production capacity, release artifacts, deployment or #531.

All owning stories are delivered: discovery #855, bounded reads #1236/#1237,
scope navigation #1238, Current Sky/archive/viewer #1239, imported inspection
#1240 and central operations #1241. Importer #538 and exact reconciliation #1554
are delivered. The latest owner-approved
[remaining-scope instruction](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/539#issuecomment-6363083377)
requests integration acceptance over these contracts. The static
[handoff](../prototypes/pipeline-operations/LOGICHOST-HANDOFF.md) supplies interaction
language; its historical missing-contract and start-order statements are not
the current delivery state.

## Scope and acceptance crosswalk

Paths below are under `src/HVO.SkyMonitor.LogicHost/` and
`tests/HVO.SkyMonitor.LogicHost.{Tests,IntegrationTests}/` respectively. Test
classes identify executable checks, rather than treating a merged child as
proof that every original acceptance row was covered.

| Original scope or acceptance | Delivered contract and executable evidence |
| --- | --- |
| One protected network/observatory/camera shell; path identities, navigation, breadcrumbs and tabs | `Components/Shared/OperationsScopeShell.*`; `OperationsScopeBrowserAcceptanceTests`; new `ProtectedWorkspaceJourneyTests` follows all four workspace sections for one camera. |
| Current Sky is the latest eligible complete retained presentation, with independent capture/receipt/system freshness | `Services/LogicalCameraArchiveReadService*`, `Components/Shared/CurrentSkyView.*`; `LogicalCameraArchiveIntegrationTests`, `CurrentSkyViewTests` and `CurrentSkyBrowserAcceptanceTests`. Bounded continuation explicitly describes an unfinished search; refresh starts a fresh search. |
| One authorized layered viewer and bounded keyset archive | `ExactCaptureViewer.*`, `CameraArchiveView.*`; `ExactCaptureViewerTests`, `CameraArchiveViewTests`, `LogicalCameraArchiveIntegrationTests` and `CameraArchiveBrowserAcceptanceTests`. List pages replace 25 rows; detail retains at most 50 artifact records, with truncation visible. No totals or automatic history drain. |
| Imported CameraAgent evidence is immutable and separate from central authority | `ImportedExecutionInspector.*`, `ImportedExecutionInspectorReadService*`; `ImportedExecutionInspectorTests`, `ExecutionEvidenceIntegrationTests.Inspector.cs`, `ImportedExecutionBrowserAcceptanceTests`. New journey compares canonical bytes, payload hashes and receipt times before/after central publication and a role race; imported executions do not populate the central execution list. |
| Exact executions, attempts, inputs, outputs, artifacts, reasons, availability, provenance and lineage | Imported inspector plus `CentralOperationsWorkspace.*` and their bounded read services; imported/central component, SQL inspector and browser tests. Missing facts stay unavailable. The comparison selects an explicit imported unit and central execution UUID, never role/order-based pairing. |
| Retrieval, validation, acceptance/rejection, staging, activation, rollback and heartbeat remain distinct | `CentralOperationsWorkspaceTests.DerivedStagingAndStoredFactsKeepSeparateTimesAndReasons`, `ProcessingGraphProposalReadTests`, `CameraOperationsReadTests`. Stored delivery facts retain their timestamps/reasons; derived staging says its timestamp was not recorded; heartbeat supplies no activation evidence. |
| Recovery/reprocessing preserves received evidence and existing central semantics | `CentralOperationsWorkspaceAuthorityTests.ScopedJobRecoveryUsesRealAuthorityFencesAndPreservesSameJobAttemptHistory`, `CentralOperationsWorkspaceTests.RealJobInspectorRendersIndependentChildContinuationsAndSameJobVersusSuccessorLanguage`, replay tests. Same-job requeue creates another attempt; reprocessing/replay creates central successor work. No received-evidence edit action exists. |
| Event review and central operations are authorized and bounded | `CentralOperationsWorkspaceAuthorityTests.EventReviewUsesAllContributorAuthorityAndExactPreconditionsThroughProductionServices`, central component tests, `CentralOperationsWorkspaceBrowserAcceptanceTests`. Event review uses the exact assessment, strong ETag and idempotency key; every contributor is authorized. Every nested displayed collection has a 25-item bound and explicit truncation. |
| Publication is an explicit release of one exact artifact | `CentralOperationsWorkspaceAuthorityTests.ArtifactPublicationIsExactAuditedAndRejectsForeignIdentitiesAndContinuationReuse`, component publication cases, new journey. The journey records a release for one exact SQL artifact, excludes its expired sibling, rejects a stale Owner withdrawal after demotion, then permits withdrawal after authority is restored. |
| Every read/mutation rechecks current resource-level observatory authority; cross-observatory requests fail closed | `OperationsScopeShellTests`, `LogicalCameraArchiveIntegrationTests.CurrentSky_ServiceAndEndpointsIntersectCredentialScopeMembershipAndRetainedInstallations`, `CameraOperationsReadTests.Http_ViewerReadsEveryCameraCollectionWhileNonMembersAndOtherScopesAreDenied`, importer inspector and central authority tests. New journey mixes observatory A with camera B in all four routes while the actor belongs to both sites; none may disclose either workspace. |
| Missing, expired, partial, quarantined, unauthorized or unavailable bytes stay metadata-only and never borrow another image | Archive/Current Sky component state matrices; archive SQL eligibility and artifact-retrieval tests; imported observation state cases. New journey moves from a visible exact Preview to unrelated imported execution evidence without rendering or linking a substitute image. Scope denial clears protected metadata too; unavailable bytes retain only authorized metadata. |
| Desktop, narrow portrait and short landscape; no horizontal overflow | Each section's existing Manual browser class and new journey use 1440×900, 390×844, 844×390 and 320×700. The journey checks overflow and the dark theme for each section at each size. |
| Keyboard, touch, screen-reader, focus restoration | Existing section browser classes cover keyboard/touch detail navigation and return focus; new journey uses keyboard and touch tabs, checks the current section and heading focus, and records accessibility trees. This is Chromium accessibility-tree evidence, not an OS screen-reader session. |
| Reconnect, authentication loss and role/state races | `OperationsScopeBrowserAcceptanceTests`, `CurrentSkyBrowserAcceptanceTests` (real websocket reconnect), imported/central browser and component race cases. New journey races a rendered publication control with a SQL role downgrade and revalidates the same exact capture after offline/online. Pending-read tests reject stale authentication/scope results. |
| bUnit, endpoint/service and authorization evidence | Full affected LogicHost Unit selection plus focused SQL classes listed below. These cover scoped credentials, revoked membership, protected cursor binding, current state and exact identity separately from the browser's cookie-based flow. |
| Representative large-history resource costs | Source-pinned child evidence below: archive 1k/10k captures, imported 1k/10k units, central 1k/10k histories and exact events with 100/1k reviews/notifications. Explicit bounds apply to materialized UI evidence; physical SQL reads/canonical parsing can grow with retained data. |
| Exclusions | This PR changes tests, inventory and acceptance documentation only. No CameraAgent/settings, persistence/schema/protocol, renderer, anonymous site, unbounded totals, execution-concurrency inference, deployment or release work. |

## Reused evidence and source applicability

- [Archive acceptance](issue-1536-archive.md), merged by
  [PR #1547](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1547) at
  `3e118ee8a69a05794bbe6904368e33fc37b6f132`. Since then, the archive markup
  and browser assertions received the heading accessibility correction, including
  heading-level composition in the viewer code-behind. Archive read services,
  presentation selection and the measured cost method are unchanged. Rerun the
  current archive browser class, including its cost case.
- [PR #1583](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1583), clean
  reviewed head `8c1085c1cf41da7edc89b9cee941ca92e5f7d931`, contains the imported
  inspector's 1k/10k SQL/response/browser/heap evidence and source/assembly
  identities. Imported inspector components, services and Manual browser
  harness are unchanged between its merge `fce3f89b` and this baseline.
- [PR #1584](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1584), clean
  reviewed head `210a5abd92f2b321618ad2542518d3440a019eab`, contains the central
  operation 1k/10k and exact-event 100/1k measurements. Its components,
  services and browser/authority tests are unchanged between merge `6ea815f2`
  and this baseline. Canonical document parsing remains proportional to the
  exact document; bounded UI projection is not constant-cost parsing.
- [Current Sky lifetime evidence](logichost-current-sky-revalidation.md)
  explicitly retains the extra presentation read on an authenticated on-screen
  notification. It makes no two-read claim for that path. Current Unit and real
  websocket browser tests cover the corrected heading/lifetime composition.

These are retained child measurements with declared source applicability, not
new measurements on the parent's candidate. No performance-sensitive product
path changes here. Fresh targeted current-source checks cover composition and
authorization; the PR ledger records their immutable candidate and results.

## Reproduction

Use pinned SDK 10.0.402, Release, Docker-backed disposable SQL Server/Redis/Mailpit
and real filesystem object-store fixtures, and repository Playwright Chromium.
Keep artifacts under ignored `TestResults/issue-539/`, then retain them outside
the disposable worktree before cleanup. Heavy work is serialized:

```bash
flock -o /tmp/hvo-heavy-gate.lock timeout 600 dotnet build \
  tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false

# After committing, capture clean source and loaded assembly SHA-256 identities.
export HVO_EVIDENCE_REVISION="$(git rev-parse HEAD)"
export HVO_ISSUE539_EVIDENCE="$PWD/TestResults/issue-539"
flock -o /tmp/hvo-heavy-gate.lock timeout 900 env -u HVO_PRODUCTION_CATALOG_BUNDLE \
  dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --no-build --configuration Release --filter 'FullyQualifiedName~ProtectedWorkspaceJourneyTests'
```

Also run the affected LogicHost Unit selection with
`DOCKER_HOST=unix:///tmp/hvo-no-docker.sock`, the existing section browser
classes in fresh test hosts, and the focused SQL archive/inspector/central
authority classes. The classifier selects local lanes; the four CI-control
guards, affected formatting, docs audit and category inventory checks accompany
them. Whole-repository ShellCheck and exhaustive solution discovery remain
full-qualification gates. The protected v1 profile does not claim those gates.

`journey.json` records source cleanliness, assembly hashes, exact identities,
browser version and each section's accessibility tree for all four viewports;
PNG screenshots and Playwright diagnostic attachments accompany its TRX.
Results and review/protected-CI evidence are appended to the PR ledger.

## Local integration results (2026-10-23)

The functional candidate was committed clean at
`4b7fc4135ce142699d90354d1218028515a2a02c`. The selector was
`scripts/ci:classify pull_request 523d43d9a36fbf7f3d7bfff47d29a74909061fd7 HEAD`:
`mode=full complete=false logichost=true`, with every other component and
deployment flag false. This selected affected LogicHost evidence under the
Development v1 protected profile; no full workflow was dispatched.

| Local gate | Result |
| --- | --- |
| Release builds of LogicHost.Tests and LogicHost.IntegrationTests, `-warnaserror -nodeReuse:false -p:UseSharedCompilation=false` | Both passed, zero warnings/errors. |
| LogicHost.Tests, `TestCategory=Unit`, invalid Docker endpoint | 937 passed; one existing provider-specific skip (`AmbiguousDelete_IsNotAReachableStateForThisProvider`). |
| SQL filter: `LogicalCameraArchiveIntegrationTests`, all `ExecutionEvidenceIntegrationTests`, `CameraOperationsReadTests`, `CentralOperationsWorkspaceAuthorityTests`, `ProcessingGraphProposalReadTests`, `ArtifactRetrievalTests` | 69 passed. |
| New `ProtectedWorkspaceJourneyTests` | One passed; 16 screenshots, four viewport accessibility trees, exact image identity, three unchanged imported units and exact release/withdrawal audit records retained. |
| Fresh-process Current Sky, archive (including 1k/10k cost case), scope, imported functional and central functional browser selections | Six passed. Imported/central large-history cost reruns were unnecessary because their cited source remained unchanged. |
| Affected IntegrationTests Manual discovery | 53 actual cases, matching the project inventory; aggregate declared Manual inventory is 278. |
| Four CI-control guards, operations documentation audit, source/test crosswalk and `git diff --check` | Passed. All 17 named test classes and explicit methods exist. |

Every test invocation used Release `--no-build --no-restore`, an explicit filter,
TRX output and `env -u HVO_PRODUCTION_CATALOG_BUNDLE`. The Unit invocation also
set `DOCKER_HOST=unix:///tmp/hvo-no-docker.sock`. Heavy work ran sequentially
inside `flock -o /tmp/hvo-heavy-gate.lock timeout 9000 python3 .../candidate-gates.py`;
the retained `results.json` records each exact command, environment selection,
exit code, source commit and elapsed time. Each browser selection started a
fresh test process and disposable fixture.

The initial build on `d459e5c6` failed CA1869 for uncached serializer options;
`4b7fc413` caches them and the rebuild passed. Affected format verification then
reported ten whitespace diagnostics in the new JSON initializer. The final
correction changes only those line breaks and this results record; affected
formatting, clean-commit rebuilds and the journey are revalidated after that
commit. Their exact final source/assembly identities and results belong to the
retained final gate ledger. Unchanged Unit, SQL and section-browser results above
remain attributed to `4b7fc413`, rather than being relabeled as final-head runs.

Artifacts are retained outside the disposable checkout at
`/home/roys/.t3/evidence/HVO.SkyMonitor/issue-539`, including failed attempts,
TRX, screenshots, accessibility trees, source/assembly fingerprints, child-source
applicability checks and `SHA256SUMS`. The older archive cost JSON has a null
legacy `Source` field; `section-command-source.json` and the exact command/TRX
ledger bind that run to the clean candidate and binary SHA-256 identities.
Archive costs describe a bounded 25-item page and exact 1936×1216 Preview; they
do not claim constant SQL work or a timing improvement against an equivalent
baseline. Imported/central costs remain the source-pinned child evidence above.
This is software integration evidence, without production/release qualification
or an OS screen-reader claim. Parent-owned independent review and protected CI
remain separate gates.
