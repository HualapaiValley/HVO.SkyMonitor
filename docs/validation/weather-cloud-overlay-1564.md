# Weather and cloud overlay configuration evidence (#1564)

Date: 2026-10-23. Tier B, `profile=development-v1`, SDK 10.0.402.
Base: `58164c35` (includes merged #1563). Partial delivery of #1386.

## Selector and validation

`scripts/ci:classify pull_request origin/development/v1 HEAD` selects
`mode=full`, `complete=false`,
`shared/cameraagent/logichost/combined/delivery=true`, and
`deployment/secret_scan_contract/release_scripts=false`.
The shared descriptor's consumers receive a warning-clean Release solution
build, focused Processing/CameraAgent tests, and both-host reconstruction
conformance tests. This is v1 integration evidence; no full qualification or
physical-profile qualification was requested.

Heavy builds/tests use `flock -o /tmp/hvo-heavy-gate.lock timeout ...` with
`-nodeReuse:false -p:UseSharedCompilation=false`. Unit tests unset
`HVO_PRODUCTION_CATALOG_BUNDLE` and use the invalid Docker endpoint
`unix:///tmp/hvo-no-docker.sock`. The local gate results are recorded on the PR.

| Final local gate | Result |
| --- | --- |
| Release solution build, warnings as errors | Passed; zero warnings/errors |
| Changed-file `dotnet format --no-restore --verify-no-changes` | Passed |
| Processing Unit selection | 852 passed |
| CameraAgent affected editor/service/graph/shipped-pipeline selection | 201 passed |
| Both-host conformance Unit selection | 76 passed |
| LogicHost processing-graph Unit selection | 148 passed |
| Architecture Unit selection | 32 passed |
| Shell syntax, CI classification, coordination and PR-review-tool guards | Passed |
| Operations documentation audit and diff whitespace check | Passed |

The final 1,309 selected tests have no failures or skips. The lifecycle fixture
uses a 16-bit raw input and a 2×1 assessment grid appropriate to its 2×2 image;
the real factory continues rejecting unsuitable bit depth and compound inputs.

## Contract and execution checks

- The descriptor pins `WeatherCloudOverlay` / `weather-cloud-overlay-v1` and
  matches every bound runtime default. No producer, recipe or style changes.
  `recipeVersion` remains a required artifact label, including existing custom
  labels; it does not select the recipe implementation. The generic text
  descriptor supports this existing required-but-unbounded field without
  imposing a new graph-identifier limit on it.
- Codec tests cover strict JSON types, both numeric boundaries, duplicate-member
  diagnostics, output-identifier limits, absent/default semantics, untouched
  unknown/out-of-scope members and source-exact unchanged saves.
- bUnit/service tests cover editing all six fields, explicit v2 node enablement,
  invalid input after a valid preview, denied access and unchanged saves.
- The disposable graph-coordinator test rejects missing preview, wrong metadata
  recipe and disabled producers, requires explicit validation before activation,
  changes the active output route, preserves the old revision's source options,
  and rolls back to that revision.
- Both host adapters execute deterministic retained preview/assessment inputs
  with and without a clear reference. Each setting is checked separately:
  boundary thickness with labels hidden; visible versus hidden labels; short and
  zero-length text. Both hosts reproduce identical payload bytes, checksums,
  algorithm identities, lineage and product identities. Restoring the original
  options reproduces the original output identity and bytes. Raw, preview and
  assessment bytes stay unchanged. Missing precipitation and insufficient cloud
  evidence retain their status and null coverage.

The both-host test initially reproduced `processing.invalid-layout` for a valid
12-character budget with hidden labels: the renderer requires already-bounded
text, but the recipe supplied its complete status strings. The recipe now bounds
its three labels before rendering and supplies none when hidden or zero-length.
Default and previously successful outputs retain the same bytes and versions.

These checks modify no persistence protocol or image algorithm. Performance
benchmarking is not applicable to this bounded descriptor/editor slice.

## Browser and accessibility evidence

The [desktop screenshot](weather-cloud-overlay-1564.png) shows the actual
bUnit-rendered `WeatherOverlayDeepLinkEditsVisualFieldsAndRetainsAccessibleBrowserFixture`
page with shipped Bootstrap, theme and scoped CSS. This is a rendered
component/service fixture; bUnit and coordinator tests verify interactions and
persistence, rather than a running physical host.

T3 browser inspection at 1280×800 and 390×844 found no horizontal overflow.
The `weather-cloud-overlay` named region contains all six labelled controls,
each with an existing `aria-describedby` help target. There are no duplicate
IDs. Keyboard Tab advances from the overlay enable checkbox to the artifact
recipe label. The initial save button is disabled. Browser console and network
requests report no errors. The retained desktop screenshot and test-exported
HTML make these checks reproducible; the phone screenshot and complete test
results are retained with the issue's local evidence.
