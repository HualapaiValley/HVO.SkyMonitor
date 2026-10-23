# Calibration scalar options (#1566)

This Tier B configuration slice exposes the three runtime-effective calibration
controls through shared Processing descriptors and the existing generic editor.
Both exact current `Calibration` step versions are registered:
`linear-normalization-none-v1` and `reference-calibration-linear16-v2`.
Recipes, model/master semantics, persisted formats and implementation identities
are unchanged. Refs #1386, which remains open.

## Contract and lifecycle evidence

`ProcessingNodeOptionsTests.Calibration.cs` covers defaults, exact version
resolution, the three case-sensitive strategy values, strict types, duplicate
members and output-route limits. Lossless scalar edits preserve nested model
JSON, unknown fields, compatibility members and absent/default semantics.
Unsupported nested edits are rejected.

The factory tests compare descriptor defaults to runtime options, resolve the
actual version for every strategy, preserve unchanged source bytes and effective
plan hashes, and retain model JSON while changing the output route. The existing
server rejects a synthetic strategy without its model, a model under either
other strategy, unknown strategy values, invalid compatibility-property bounds,
and reference strategies over Mono8 input. No unused compatibility property is
presented as an effective control.

Real disposable coordinator evidence creates a draft, rejects activation before
validation, keeps the old revision active until explicit activation, changes the
calibration output route, and rolls back to the retained revision. Source revision
JSON remains unchanged. Existing synthetic-reference store fixtures verify
restart-stable model/master identities and deterministic correction. The shared
host conformance fixture exercises the edited route against retained reference
bytes, checks ordered lineage and profile identity, and reconstructs the original
product identity after restoring retained options. The source is also compiled
by the isolated LogicHost test project, preserving the host dependency boundary.

## Browser and accessibility

The [retained screenshot](calibration-options-1566.png) shows the actual bUnit
component markup with shipped Bootstrap, theme and scoped CSS. This is a rendered
component/service fixture; real coordinator and recipe tests provide lifecycle
and execution evidence.

Chrome inspection at desktop width and 390×844 found no horizontal overflow,
duplicate IDs or console warnings/errors. All three controls have associated
labels and help text. Keyboard Tab moves from **Enable calibration** to
**Calibration strategy**. Save starts disabled. The strategy menu contains only
the three current values, and nested model contents are absent from the controls.
bUnit additionally verifies denied access, stale-preview rejection after invalid
scalar or strategy/model input, changed strategy saves, unchanged saves and
restored defaults. Saving issues no activation command.

T3 preview navigation worked, but snapshot capture timed out twice with
`Preview automation snapshot timed out after 15000ms.` The screenshot and responsive
inspection therefore use the supported Chrome fallback. Exported HTML, phone
image and test results are retained in registered local issue evidence.

## Candidate validation

The selector is `scripts/ci:classify pull_request
2f4eace674ff2c145ff765ac025c55382afe74d1 HEAD`: `mode=full`, `complete=false`,
`shared/cameraagent/logichost/combined/delivery=true`, with deployment and its
sublanes, secret-scan-contract and release-scripts false. Target profile is
**development-v1 Preflight + Build and Unit**; no full qualification workflow is
requested. Local scope follows Tier B's focused and affected boundary checks.

Release solution build and the final affected CameraAgent rebuild passed with
zero warnings/errors. All four CI-control guards, the operations documentation
audit, and changed-source formatting passed.

| Affected Unit selection | Passed |
| --- | ---: |
| Processing | 885 |
| CameraAgent editor, graph/service/endpoint and calibration fixtures | 345 |
| Combined host conformance | 81 |
| LogicHost graph and conformance | 186 |
| Architecture | 32 |

All 1,529 executions passed with no failures or skips. The PR ledger records the
classifier output and immutable reviewed head. Detailed commands and TRX files
remain in registered local issue evidence.

Heavy builds/tests use `flock -o /tmp/hvo-heavy-gate.lock timeout ...`, disable
node reuse/shared compilation, unset `HVO_PRODUCTION_CATALOG_BUNDLE` and use the
invalid Docker endpoint. Physical scratch TMPDIR avoids macOS symlink rejection
in existing fixtures. No hot path, persistence protocol, telemetry or worker
behavior changes, so no new canonical performance run is required. Existing
execution outcomes and calibration failure reasons remain the runtime signals.
No physical rig, installed profile or release qualification is claimed.
