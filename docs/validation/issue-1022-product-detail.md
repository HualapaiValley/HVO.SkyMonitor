# Issue #1022: exact-artifact Product Detail

## Scope and acceptance boundary

Tier B product-page port over the existing bounded retained-output/detail APIs;
deep review for authorization, asynchronous navigation and exact media identity.
Branch point: `cb5d7e3f63c69e85f0f079ef63c687fbb7e5f818`, `development/v1`.
Prototype authority: `product.html`, `archive-pages.js::initializeProductDetail`
and `renderRunningProductDetail`, and the product-detail rules in
`docs/prototypes/pipeline-operations/archive-pages.css`.

The page presents an exact retained output. An ordinary capture processing output
is not relabeled as a nightly derivative. Nightly generation remains #993;
time-lapse video is separately tracked in #1130 (FFmpeg). No generation or
regeneration action is simulated. Unknown provenance stays explicitly unavailable.

## Implementation

- The shared layout supplies the Products-active archive subnavigation once.
- A substantial media viewer sits beside the evidence inspector. Native JPEG,
  PNG and WebP images use the exact artifact content; supported video uses native
  controls on the actual bytes. Packed/linear Preview, Combined, Calibrated and
  AnnotatedPreview roles use the existing exact-artifact reconstruction endpoint.
- The viewer uses `contain`, preserving every image edge and aspect ratio.
  Fit, 100% image/preview, fullscreen, Escape and focus return retain artifact
  identity. Preview enlargement is labeled preview; native-resolution original
  bytes remain separately downloadable.
- A narrow `inline=true` content request is permitted only for explicitly
  supported passive image/video media. Default downloads and unknown/raw/metadata
  remain attachments, with unchanged checksums, private headers, owner authorization,
  byte ranges and `nosniff`.
- Failed, missing, quarantined and non-displayable bytes retain evidence and an
  explicit unavailable reason. They never substitute another capture or product.
- The inspector distinguishes generation state from storage availability and
  total integration from elapsed observation span. Generation history,
  recipe/checksum/output identity, ordered sources and retained predecessors
  reflect the existing immutable records. Unretained/truncated sources stay visible.
- Current product contracts lack complete nightly window, geometry/time-axis,
  automation/run and executor-route metadata. The page states that absence; a
  keogram is shown as actual pixels without invented axes/event markers. The #993
  product contract can add qualified metadata for later adoption.
- The generation API does not expose an authorized successor-generation action;
  Regenerate and exact generation-run navigation remain unavailable with reasons.
- Reads are generation-, cancellation- and exact-artifact-fenced. Revocation clears
  protected media and blocks repeated redirect-triggered reloads. Fullscreen
  callbacks are artifact-bound and cleaned up on navigation/disposal.

## Native same-artifact evidence

Genuine paused capture #42, artifact `7aab3fa1-b5db-8381-b110-52705b9749d6`, saved
`operator-stack-v1` AnnotatedPreview, packed RGB image. Source capture identity:
`252fcd46-45aa-8682-bafb-b5bb9b564218`. Seven recorded immediate sources are
immutable base/layer artifacts, not seven separate exposures. The retained
output's total integration is 5 seconds; an elapsed seven-frame window is not inferred.

Source capture/recipe facts and pixels are unchanged. Authenticated private
preview on LAN port 5135 is run from a copied paused state under
`/home/roys/.hvo-ui-1022` with redirected settings/configuration paths. No live
installed instance, installer, camera hardware or protected evidence was changed.
Original accepted previews remain available.

Host evidence: `/home/roys/.cache/hvo/1022/`:

- `before-{1440,390,320}.png` / `before.json`: exact retained-artifact baseline
  using the previous private native host, matching the unchanged branch-point page.
- `after-{1440,390,320}.png` / `after.json`: same artifact after the port;
  decoded image is contained and document scroll width equals viewport width.
- `interactions.json`: keyboard fullscreen on the actual 1440×900 viewport,
  native-size source identity preserved, Escape focus return, and failed-image
  removal with artifact evidence preserved; no browser page errors.

No-login allowlisted review captures on the bounded evidence server:
`http://192.168.2.45:5133/product-detail-{1440,390,320}.png`.

## Validation

- CameraAgent Debug warning-clean build: zero warnings/errors.
- Focused ProductDetail/ArchivePages tests: 22 passed, no skips.
- Eight new Unit cases cover exact encoded/packed media selection, missing/error
  handling, running-output truth, wrong requested identity, stale responses,
  revocation and local return URL restrictions. Inventories/runbook are aligned.
- Existing artifact content Integration case is expanded to verify supported
  inline bytes/checksum and rejection of inline for raw/unknown types.
- Native browser qualification checks exact artifact source, fullscreen/fit/100%,
  keyboard/focus, identity-preserving media failure and 1440/390/320 bounds.
- Dedicated Manual `OwnerProductDetailPresentationAcceptanceAsync` exercises a real
  VirtualSky fixture artifact, not a production catalog/science qualification.
- Classifier-selected affected candidate gate and independent review results
  belong in the issue/PR ledger on their exact commits. No complete Manual suite,
  performance improvement or generated nightly/video acceptance is claimed here.

## Observability and performance

No new worker, background queue or scientific image algorithm. Runtime records
verified production catalog identity and preserved ingress/product reconciliation
without missing/quarantined captures. Existing authorized content telemetry is
unchanged. Comparative benchmark/metric/trace manifests are N/A for this bounded
UI/media-delivery change; media identity, byte checksums, responsive bounds and
failure semantics are the relevant evidence.

Page-specific operator visual acceptance is pending.
