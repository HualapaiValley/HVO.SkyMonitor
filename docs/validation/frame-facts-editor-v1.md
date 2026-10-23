# Frame Facts #1387 software acceptance

Target: `development/v1`; hardware-free integration only. The accepted design is
[the owner checkpoint](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1386#issuecomment-6298821591).
#1386's other panels, #1388, space-weather facts, physical cameras, Pi deployment
and main/release promotion remain separate.

## Parent acceptance across all four slices

| #1387 requirement | Delivered contract and receipt | Current slice D boundary |
| --- | --- | --- |
| Any supported token in any of four independent corners | A [#1550 / PR #1556](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1550#issuecomment-6353935671): 71 parser/evaluator cases; B [#1551 / PR #1579](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1387#issuecomment-6355775591): retained catalogue/context and presets, 53 new cases | Four actual textareas, per-corner typed options, complete bounded token/format/source catalogue; every scalar/format exercised against each corner form |
| Missing values and stale/conflicting status, placeholder/drop-line | [Template v1](../contracts/frame-facts-template-v1.md) and [retained context v1](../contracts/frame-facts-retained-v1.md); no live fallback | Shared evaluator preview; all-missing default, identity-checked frozen file, drop-line/status interaction and rejected snapshot tests |
| Markdown-like emphasis and per-corner styling, dark panels | C [#1552 / PR #1603](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1552#issuecomment-6367648732): opt-in producer, packaged faces, deterministic shared paths, synthetic italic/underline | Font, RGB/hex, milli-pixel size, independent templates/missing policy and spacing; grammar help and Plex-bold rejection |
| Validation and bounds | A source/rendered limits; C layout admission and atomic optional-layer failure | UI and service checks reject grammar, format, colour/font/size, source/line/expansion bounds and quadrant overflow; former preview is cleared on edit |
| Stable CameraAgent/LogicHost rendering and prior revision reconstruction | C retained payload/snapshot identities, legacy golden/default pins, actual central ingest/retrieval with independent facts/scene bindings (six Integration cases after the reproduced F1 correction) | No renderer, payload schema, ingest or identity algorithm changes. Current shared Frame Facts regression selection retains those contracts; actual VirtualSky product and frozen editor output are compared byte-for-byte |
| Explicit versioned upgrade and existing revision lifecycle | A/B converters remain pure; C rich alias is never auto-included; graph JSON remains storage | Whole-draft before/after review, compile-before-accept, cancel, explicit save, validation, activation and rollback. Pure conversion preserves source graph, unknown JSON, legacy environment defaults and variants. No automatic activation |
| Screenshots and operator usability | C shared layout/raster cost receipt at W1/W6 retained unchanged | Real authenticated Kestrel/VirtualSky host and T3 collaborative Chromium; before/after, desktop, 390/320 px, labels, keyboard focus, authorization and actual retained facts/product receipt; see evidence below |

The slice-C receipt records non-green native full-qualification/coverage gates
with exact-base reproduction. That limitation is retained; its v1 integration
pass is not a claim that full qualification was green. This slice does not
repeat unchanged performance harnesses or claim new throughput improvements:
producer, evaluator, layout and compositor implementations are unchanged.
The editor adds bounded transient form/preview work and no queue or durable store.

## Reproduce the browser boundary

`FrameFactsEditorPreviewHostTests.ServeVirtualFrameFactsEditorForCollaborativeBrowser`
starts a real local Kestrel/Identity/SQLite host with the existing browser fixture,
VirtualSky seed 106, nine-star catalog fixture, 1936×1216 Mono16, zero read/shot
noise, 5 ms exposure, gain 1, three-second minimum start interval, rolling window
one and durable projected scene/preview/facts/layer/manifest products. Central
integration is disabled. It uses an issue scratch root and disposes its runtime
root on exit. It exports only the selected retained products and receipts.

Run under the host heavy-gate lock using SDK 10.0.402, no shared compilation or
node reuse, an unset production-catalog variable and invalid Docker endpoint:

```bash
unset HVO_PRODUCTION_CATALOG_BUNDLE
HVO_FRAME_FACTS_BROWSER_EVIDENCE="$(hvo scratch HVO.SkyMonitor 1553 b)" \
  DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  flock -o /tmp/hvo-heavy-gate.lock timeout 1500 \
  dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj \
  --configuration Release --no-build -nodeReuse:false -p:UseSharedCompilation=false \
  --filter FullyQualifiedName~FrameFactsEditorPreviewHostTests
```

Use the address from `browser-host.json` with T3 `preview_status`, `preview_open`
and `preview_navigate`. Sign in with the fixture owner from
`CameraAgentKestrelFixture` (test-only credentials), then follow the
[operator runbook](../runbooks/frame-facts-corner-templates.md). Complete the
browser checklist below and create `browser-complete` in that registered
scratch directory. The test asserts legacy and rich products were actually
produced, re-reads the original retained layer and requires its bytes to match,
and requires the registry to finish on the original revision after rollback.
Elapsed time is bounded to 20 minutes; a missing completion marker fails.

The first run exposed an existing Named graphs UI mismatch: rollback was offered
for retired rows, while the store requires a validated configured/basic or
previously activated revision. `RollbackEligibilityMatchesTheDurableStoreForConfiguredAndPreviouslyActivatedRevisions`
was added and run failing before correcting the UI eligibility/help. The service
and store are unchanged. The fixture's initial storage/query setup failures and
the first receipt's unsuccessful rollback remain in the scratch logs; they are
not counted as passing acceptance.

The first 320 px probe found `scrollWidth=364` (file input and compiled diagram).
The correction confines the native file input and makes the diagram scroll
inside its panel; final measurements and screenshots use the compiled styles.

## Current evidence

Final command/counts, classifier range and browser artifacts are recorded here
when the local candidate gate completes. Evidence is retained under registered
issue scratch; selected portable screenshots/products accompany this receipt.
