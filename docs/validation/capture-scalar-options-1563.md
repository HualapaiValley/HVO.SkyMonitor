# Capture scalar options evidence (#1563)

Date: 2026-10-23. Tier B, `profile=development-v1`, SDK 10.0.402.
Implementation candidate: `9b2bc0cc`. The subsequent target sync to
`758208bc` adds only the #1607/#1608 recovery studies and documentation; it has
no overlap with this implementation. The initial independent review covers
the full PR diff against that synchronized base.

## Selector and local checks

`scripts/ci:classify pull_request 758208bc412bc3f8abaa4badc92b2641d8e2b35d HEAD`
selects `mode=full`, `complete=false`,
`shared/cameraagent/logichost/combined/delivery=true` and
`deployment/secret_scan_contract/release_scripts=false`.
The shared boundary receives the Processing tests, both hosts receive the
Release solution build, and central graph/architecture tests cover host
boundaries. This is v1 integration evidence; no full qualification was requested.

| Local gate | Result |
| --- | --- |
| Release solution build, `-warnaserror` | Passed, zero warnings/errors |
| Processing `TestCategory=Unit` | 829 passed |
| CameraAgent broad `TestCategory=Unit` | 7,090 passed, one expected platform skip, two new fixture failures corrected below |
| CameraAgent affected options, graph, UI, virtual-cloud and connection-pool contract rerun, with build and `-warnaserror` | 118 passed, zero failures/skips |
| LogicHost `TestCategory=Unit&FullyQualifiedName~ProcessingGraph` | 148 passed |
| Architecture `TestCategory=Unit` | 32 passed |
| Changed-file `dotnet format --no-restore --verify-no-changes` | Passed |
| Shell syntax and classification, coordination, PR-review-tool control guards | Passed; repeated after the documentation-only target sync |

Heavy checks use `flock -o /tmp/hvo-heavy-gate.lock timeout ...`, with
`-nodeReuse:false -p:UseSharedCompilation=false` for builds/tests. Unit runs
unset `HVO_PRODUCTION_CATALOG_BUNDLE` and use
`DOCKER_HOST=unix:///tmp/hvo-no-docker.sock`.

The initial execution fixture created a new raw artifact ID for each context,
which correctly changed product identity. It now pins one captured artifact
across executions. Its unnecessary global connection-pool teardown also failed
the parallel-test contract and was removed. Both original failing tests pass in
the 118-case rerun. Unchanged successful suites were retained rather than
repeated. The platform skip is `InitializeOnMacOsRejectsBeforeRuntimeAccess`.

## Behavior and retained revisions

- `ProcessingNodeOptionsTests.CaptureScalars.cs` pins exact aliases/versions,
  defaults, strict JSON types, duplicate-member rejection, identifier bounds,
  unknown-member preservation and byte-identical unchanged edits.
- `ScalarPanelsExecuteRegisteredGraphTogglesRoutesAndRetainedRevisionDeterministically`
  executes the registered graph against one deterministic VirtualSky capture.
  Disabling nodes suppresses quality output, telemetry and cloud publication;
  changing the quality variant changes output identity while preserving payload
  checksum. Restoring the retained graph reproduces the original product bytes,
  identity and plan identity. Cloud facts remain `Simulated` with stable IDs.
- `QualityRouteDraftRequiresValidationAndActivationAndRollbackReconstructsRetainedOutput`
  uses disposable SQLite/filesystem state and the existing coordinator. Save
  remains a draft, activation before validation is rejected, explicit activation
  changes routing and rollback restores the retained revision. Original source
  options and order remain intact.
- bUnit and service tests exercise no-op saves, restored absent defaults,
  invalid input after a valid preview, denied access, source preservation,
  explicit node enable state and accessible field labels/help.

No runtime algorithm, publication rule, persistence layout, profile default,
producer version or recipe meaning changes. Performance benchmarking is not
applicable to this configuration/editor slice.

## Browser and accessibility inspection

The [retained screenshot](capture-scalar-options-1563.png) shows the actual
bUnit-rendered graph page, exported by
`ScalarPanelsRetainBrowserFixtureAndExplicitFalseWithoutRewritingSource`, with
the shipped CSS and App theme. This is a rendered component/service fixture;
interaction and persistence are verified by the tests above, not by a live host.

T3 browser inspection found three distinct named regions: `image-quality`,
`capture-telemetry` and `simulated-cloud-observations`. All four new inputs have
labels and existing `aria-describedby` targets. There are no duplicate IDs.
Telemetry retains its explicit unchecked state; quality shows
`image-quality-v1`; simulated cloud qualification is visible; and the unchanged
draft's save button is disabled. No browser console or network errors occurred.

This is a bounded partial delivery of #1386. That issue remains open.
