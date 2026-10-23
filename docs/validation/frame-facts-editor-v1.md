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

Software candidate `ab869b16416934387b8c7675c167e9973999d44c` follows the initial
candidate `5b904d5dd8cef6773367fb8f53d8e14da3ccf000`; the final handoff adds only
this evidence ledger. Base is `3686e19a57b4c9c9ea0d4324755278c288827661`.

The selector was `scripts/ci:classify pull_request <base> <candidate>`, recorded
on both immutable ranges. Full PR output is `mode=full`, `complete=false`,
`shared=cameraagent=logichost=combined=delivery=true`, with deployment, all three
deployment subprofiles, secret-scan-contract and release-scripts false.
The correction range `5b904d5d..ab869b16` selects only CameraAgent, also
`mode=full`, `complete=false`. That selector preserved the unchanged shared,
combined and central evidence and selected the affected UI rebuild/tests.
Protected profile remains the two development-v1 checks, pending parent
lifecycle work; no full-qualification dispatch ran.

| Local gate | Result |
| --- | --- |
| SDK/tool/solution restore and complete Release solution build | Pass, SDK 10.0.402; 0 warnings/errors at 5b904d5d |
| Full solution format verification | Pass at 5b904d5d |
| Affected CameraAgent Release rebuild and format verification after async corrections | Pass at ab869b16, warning-clean |
| Editor/form/conversion/service/facts-builder Unit selection | 80 passed, 0 failed/skipped at ab869b16 |
| Shared Frame Facts and typed-options Unit selection | 201 passed, 0 failed/skipped; relevant source unchanged through ab869b16 |
| Combined-host Unit selection | 74 passed, 0 failed/skipped; source unchanged through ab869b16 |
| Central retained-presentation lineage Unit selection | 20 passed, 0 failed/skipped; source unchanged through ab869b16 |
| Real Kestrel/VirtualSky collaborative-browser Manual case | 1 passed in each of two complete runs; same test case, zero skips |
| Shell syntax, CI classification, coordination and PR-review guards | All four passed on each candidate; shell syntax covered 203 files under Bash 5.2 |
| Documentation/inventory audit and diff whitespace | Pass |

Every build/test command used the shared host `flock -o` lock and timeout,
`-nodeReuse:false` and `-p:UseSharedCompilation=false`. Unit and virtual-host
runs unset `HVO_PRODUCTION_CATALOG_BUNDLE` and used the invalid Docker endpoint.
The ledger is 375 distinct selected Unit cases plus one Manual case executed
twice; it does not claim the entire 13,438-case Unit inventory ran locally.
Inventories add 33 Unit cases and one Manual case. Full v1 Unit execution and
independent exact-range deep review remain with the parent.

Two async boundaries received additional test-first checks after the browser
pass. `PendingFactsUploadCannotRelabelAMissingPreviewAsLoadedFacts` failed
before disabling preview during input validation and publishing its snapshot
on the renderer context. A queued older file cannot replace a newer snapshot.
`InFlightCompilationCannotRestoreAPlanOrConversionAfterTheDraftChanges` failed
in both modes before generation checks discarded stale plan/conversion results.
All four new cases pass in the final 80-case selection. These changes leave
the ready-state native rendering and existing positive browser receipt intact.
The red TRXs are retained alongside the passing runs.

[The portable gate receipt](frame-facts-editor-v1/local-gate-receipt.json)
contains exact selectors, counts and executable source checksums.
[The browser receipt](frame-facts-editor-v1/browser-receipt.json) records
cancel/apply focus, all four templates/styles, invalid-format/line-bound
rejection, anonymous login and viewer denial, immutable save, unchanged reopen,
validation/activation/rollback, 390/320 px measurements and empty browser console.
[The screenshot receipt](frame-facts-editor-v1/screenshot-receipt.json) records
a complete second pass using the same frozen input, with settled viewport
captures of the controls and full preview.

The frozen editor JPEG and actual rich VirtualSky product both hash to
`B1E048662DC1AB342BFDCE01AD896BD580D9C7E2016A9794106B5A33E8652C53`.
The snapshot identity is
`30D11ACA5B32E93DCFD6A1632783F66AB9AA75668DD453A44A14B86750AC904C`.
The original retained legacy layer re-read after activation/rollback remains
byte-identical, SHA-256
`E49E9B986BD01AC8557E9EDDA30FA41CB70B4E13FD855B86B4430FA8310C28B8`.
The second pass also verified its original layer unchanged and restored its
original registry selection. Neither pass rewrote prior output.

Portable evidence:

- [Before legacy presets](frame-facts-editor-v1/before-legacy.png) and
  [whole-draft conversion review](frame-facts-editor-v1/conversion-review.png).
- [Top corners](frame-facts-editor-v1/after-top-corners.png),
  [bottom corners](frame-facts-editor-v1/after-bottom-corners.png),
  [frozen preview](frame-facts-editor-v1/frozen-preview.png),
  [390 px](frame-facts-editor-v1/narrow-390.png) and
  [320 px](frame-facts-editor-v1/narrow-320.png).
- Actual retained [legacy layer](frame-facts-editor-v1/retained-legacy-payload.json),
  [legacy raster](frame-facts-editor-v1/retained-legacy-render.jpg),
  [rich facts](frame-facts-editor-v1/retained-rich-facts.json),
  [rich layer](frame-facts-editor-v1/retained-rich-payload.json) and
  [rich raster](frame-facts-editor-v1/retained-rich-render.jpg).

Full commands, logs and TRXs remain in registered scratch
`/tmp/hvo-skymonitor-1553-implementation.RuxHYi`; browser receipts/products
also remain in the registered `1553-b.WFNc2T`, `1553-c.QYQ6Fe` and
`1553-d.tLgfzt` roots. All isolated fixture runtime directories were disposed,
both owned browser tabs were closed, and no issue-owned container, test database
or host process remains. The shared heavy-gate lock file, build caches and all
evidence artifacts are retained. No claim, PR, deployment or promotion action
was performed by the implementation task.

## Independent review correction F1

The initial deep review found that valid JSON with a non-object root escaped
the upload handler's validation errors. Twelve helper/component cases first
failed on the reviewed code for arrays, null, numbers, booleans and strings.
The reader now rejects those roots before accessing object properties. The
component cases replace a valid preview, verify inline rejection and missing
evidence, edit the draft, render again and successfully load another valid file.
The affected editor/service selection passes 92 cases, including all twelve
reproductions, with zero failures/skips. Valid snapshot rendering and retained
product semantics are unchanged; the original browser/output receipts remain
applicable. The correction adds twelve Unit cases to the declared inventory.
