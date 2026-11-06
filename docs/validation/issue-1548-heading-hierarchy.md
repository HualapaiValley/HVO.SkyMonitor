# Exact capture heading hierarchy — issue #1548

Target profile: `development-v1`. Risk: Tier B, standard review. This is
integration/accessibility evidence, not production qualification or promotion.

The shared viewer defaults to a native H1 capture heading on standalone detail.
Current Sky and scoped archive detail request H3 beneath the camera H1 and
section H2. Viewer scene layers and provenance use the next level; capture-time
profiles use the following level. The parameter accepts 1–4, keeping every
heading within H1–H6. Existing Bootstrap size classes preserve typography.
Heading text, region names, archive/scope focus IDs, capture identity, and
image/layer lifetime remain consistent.

## Reproduction and scope

Before production edits, the two composed `HeadingTree_` tests failed: archive
and Current Sky expected H3 capture / H4 subsections but received H1 / H2.
The standalone H1 / H2 control passed (2 failed, 1 passed). Tests cover the
actual consumer components, native heading names/levels, optional capture-time
profiles at every supported root level, invalid bounds, and retained viewer
presentation/image key/control state on a level change.

Implementation source: `65874b13f7e3a884d5f70730bd90e1e196b1e20c`.
Before initial review, merge `c4e26f8f` synchronized target
`4441c5ed8dca43a67ab7a2f130d336dabb4956c5`; it preserved the upstream CameraAgent
changes and reconciled category totals without changing this implementation.
The source/test diff fingerprint against that target is
`3bd65b31993541eb6ca3378269c144b98ec579abdd09bb2fcc12fce7c2164bdb` (SHA-256).

## Local candidate evidence

Selector: `scripts/ci:classify pull_request origin/development/v1 HEAD` on the
implementation and synchronized range:

```text
mode=full complete=false logichost=true
deployment=false shared=false cameraagent=false combined=false delivery=false
secret_scan_contract=false release_scripts=false
```

Tier B uses the affected LogicHost tests/build/format and explicit browser
acceptance below. The final protected profile is Development v1 Preflight and
Build and Unit; the full qualification workflow was not dispatched.

Heavy gates use `flock -o /tmp/hvo-heavy-gate.lock timeout ...`,
`-nodeReuse:false -p:UseSharedCompilation=false`. Unit runs unset
`HVO_PRODUCTION_CATALOG_BUNDLE` and use
`DOCKER_HOST=unix:///tmp/hvo-no-docker.sock`. Browser runs use the isolated
Testcontainers SQL Server/Redis/Mailpit fixture and Playwright Chromium.

- Release builds of the LogicHost Unit and Integration project graphs: passed,
  zero warnings/errors.
- `dotnet test tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj --configuration Release --filter 'TestCategory=Unit'`:
  **843 passed, 1 existing provider-specific skip, 844 total**, including the
  exact-viewer, Current Sky, archive and viewer-lifetime suites.
- Supplemental LogicHost Integration project positive Unit selection: **18 passed**.
- `Archive_ExactSameTimeReplacementCaptureBackFocusResponsiveAndDenial`:
  **1 passed**. Exact scoped archive heading tree, stable archive focus target,
  equal-time capture identity, keyboard/touch detail navigation, browser Back,
  return focus, content-loss retry, and membership denial.
- `CurrentSky_ExactPreviewKeyboardTouchResponsiveAndReconnectDenial`: **1 passed**.
  Current Sky and standalone heading accessibility trees, stable keyboard refresh
  focus, touch disclosure, four responsive viewports, exact preview identity and
  real circuit reconnect denial after membership revocation.
- Affected C# format verification: passed for the viewer, consumer tests and
  browser acceptance projects.
- `git diff --check`, `scripts/docs:audit-operations`,
  `scripts/ci:shell-syntax`, `scripts/test:ci-classification`,
  `scripts/test:coordination-guard`, `scripts/test:pr-review-tools`: passed;
  all four CI-control guards were rerun after target synchronization.

Both browser cases assert accessibility-tree/native heading levels, desktop
1440×900, portrait 390×844 and 320×700, short landscape 844×390, and no
horizontal overflow. This is browser accessibility evidence, not an operating
system screen-reader session. No query, image algorithm, I/O or processing hot
path changes; no performance claim or new benchmark is needed for this tier.

Raw logs and archive screenshots/JSON are retained in this issue's registered
`heading-evidence` scratch. The owning issue/PR ledger records the location.
