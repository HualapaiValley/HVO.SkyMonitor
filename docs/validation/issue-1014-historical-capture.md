# Historical Capture Review Candidate

Date: 2026-09-25. Issue #1014. Operator visual acceptance is pending.

## Scope

Historical capture detail uses the accepted Current Sky component and scoped
styles, rather than a second dashboard implementation. The parent owns the
route-selected capture, filtered neighbours, safe archive return URL, and
technical evidence. The shared workspace owns stage selection, verified layers,
ordered source lineage, saved presentations, and the recorded run link.

Archived mode never reads Current Sky or starts its polling timer. Retry only
retries that capture's optional layers. Missing/mismatched capture identities do
not substitute another record. The exact-capture service shares the bounded
preview validator with the live service, preserving the distinction between the
linear stage identity, retained display derivative, and display-reference policy.

No persistence schema, astrometry, registration, detector, or lifecycle-tooling
changes are included. The live site on port 5130 was not changed.

## Review Instance

- URL: `http://192.168.2.45:5131/gallery/3837135d-3bbf-8275-b8b6-9f5e3cf41142`
- Instance: `c357af1c-27ac-442a-b272-86ff6f25c5ac`.
- Capture: sequence 28, `3837135d-3bbf-8275-b8b6-9f5e3cf41142`.
- Before image: `05d83a81954307805910ddbf12c8d6e477fffa1a4bb74b90a3e293d304036d32`.
- Candidate image: `f7748640123050eb674d4c2b0fd1efcb3758672349089d0e8ede3ffe62de16d1`.
- Candidate source: `f6faeaf9b5aac8da16c69bc4594cbac9a31afe19`.
- Default resources: 1 CPU / 1 GiB; healthy, zero restarts at verification.
- Owner login verified after the in-place change. No credentials in this record.

The review instance first ran the deployed baseline. After pinning a processed
capture and recording baseline screenshots it was upgraded using the reviewed
installer, retaining that same small data set for comparison. No live data was
copied. Saved-stack verification produced one immutable successor artifact:
`8d802411-8e93-8f3b-9682-6bcfd25509d6`; its authenticated content download succeeded.

## Visual Evidence

Local evidence: `/tmp/opencode/1014-evidence/`.

- `pinned-capture.json`: original capture and artifact IDs.
- `before-{1440,390}.png`, `after-{1440,390}.png`: same capture before/after.
- `prototype-history-{1440,390}.png`: prototype `dashboard.html?capture=84219`.
- `comparison-{1440,390}.png`: before, after, prototype side by side.
- `layout-comparison.json`: measured element bounds and font sizes.
- `interactions.json`: exact-capture interactions and layout measurements.
- `fullscreen-raw.png`: selected raw stage enlarged without another fetch.

| Measurement | 1440 x 900 | 390 x 844 |
| --- | --- | --- |
| Viewer width difference from prototype | 0 px | 0 px |
| Inspector width difference from prototype | 0 px | 0 px |
| Image stage width/height difference | 0 / 0 px | 0 / 0 px |
| Viewer/inspector left-position difference | 0 / 0 px | 0 / 0 px |
| Heading font size (prototype and candidate) | 56 px | 32 px |
| Horizontal page overflow | 0 px | 0 px |

Total card heights differ intentionally: real source IDs/display provenance are
retained, the unavailable astrometry card remains visible per operator direction,
and additional unavailable controls remain in place. Historical breadcrumb and
adjacent navigation remain above the shared workspace. The prototype's capture
84219 and its science fixtures are illustrative, not substituted into production.
This is not a claim of identical whole-page pixels or operator acceptance.

Browser checks passed: exact heading remains after 18 seconds while acquisition
continues; no live-age indicator; stage selection; toggle off, stage round-trip,
and defaults restore; saved-stack submission/download; previous/back navigation;
exact replay target; raw fullscreen/source preservation/exit/focus; technical
downloads; not-found identity; no browser errors. Missing preview and no-display
states are covered by component and browser-fixture acceptance tests.

## Validation

Selector: `scripts/ci:classify pull_request
151a3b6b6333894ec334522fb99fcb0fff6cdf65 f6faeaf9b5aac8da16c69bc4594cbac9a31afe19`.
Result: `mode=full complete=true deployment=true shared=true cameraagent=true
logichost=true combined=true delivery=true`. Deleting the obsolete detail-page
JS module triggers the classifier's full-matrix rule.

- SDK 10.0.401; tool restore, solution restore, Debug/Release `-warnaserror` builds,
  format verification, package audit, and four CI-control guards passed.
- Category audit: Unit 3969, Integration 672, Manual 117, Soak 1, Hardware 1.
- Focused current/history/auth component tests: 68 passed.
- CameraAgent Unit: 2040 passed, one existing skip; invalid Docker endpoint.
- Solution Unit with coverage: other projects passed; the outer terminal deadline
  interrupted the CameraAgent project. That project was rerun separately with
  the same category/settings/collector and passed (2040 plus one existing skip).
- Solution Integration with coverage: 669 passed initially; three LogicHost
  evidence tests rejected binaries stamped with the preceding source revision.
  Rebuilt on candidate HEAD and reran those exact three: all passed. No product
  correction or weakening of the evidence guards was required.
- `OwnerCaptureDetailPresentationAcceptanceAsync`: passed in a real Chromium
  session against its isolated Kestrel fixture. The test exits fullscreen before
  resizing, as required by Chromium's window-bounds protocol.

Logs: `/tmp/opencode/1014-{debug,release-final,format-final,unit-solution,
unit-cameraagent-coverage,integration-solution,integration-correction,browser-test2}.log`.

## Remaining Boundaries

Camera/optics/site and scientific feature gaps match the accepted live dashboard.
Observation span is not inferred from endpoint exposure. Unresolved lineage
sources keep their artifact identity and an unavailable placeholder. Page-local
archive text search cannot be reconstructed by the neighbour query, so adjacent
links are disabled for that case while Back to archive retains the exact URL.
No new automatic replay, delivery, or publication operation is performed.
