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
- The inspector distinguishes current producing-step state from the committed
  artifact and its storage availability, and total integration from elapsed
  observation span. Current step attempts/times do not establish this artifact's
  generation history or finality. Other retained outputs of the same step are
  labeled earlier, later or same-time by commitment, without assuming a version,
  predecessor or successor relationship. Ordered immutable sources and
  unretained/truncated sources stay visible.
- Current product contracts lack complete nightly window, geometry/time-axis,
  automation/run and executor-route metadata. The page states that absence; a
  recipe name alone does not establish nightly product type or axes. The #993
  product contract can add qualified metadata for later adoption.
- The generation API does not expose an authorized successor-generation action;
  Regenerate and exact generation-run navigation remain unavailable with reasons.
- Reads are generation-, cancellation- and exact-artifact-fenced. Revocation clears
  protected media and blocks repeated redirect-triggered reloads. Fullscreen
  callbacks are artifact-bound. A returned JS controller owns listeners directly,
  so cleanup works after the DOM element has been detached or failed. Native
  browser click/keyboard activation requests fullscreen without a server round
  trip; unsupported fullscreen has an explicit disabled reason.
- The new inline opt-in applies only to artifact content, not replay downloads.

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
- `after-{1440,390,320}.png` / `after.json`: initial implementation evidence,
  superseded by the corrected-head capture set. Early pre-commit captures are not
  exact-head acceptance evidence.
- `correction-{1440,390,320}.png` / `correction.json`: corrected checked-out head,
  with capture UTC, full head SHA and worktree-diff fingerprint in the index.
  The decoded image is contained and document scroll width equals viewport width.
- `correction-interactions.json`: keyboard fullscreen on the actual 1440×900 viewport,
  native-size source identity preserved, Escape focus return, and failed-image
  removal with artifact evidence preserved; no browser page errors. Also verifies
  listener release after failed media and detached-viewer navigation, and the
  explicit unsupported-browser fullscreen reason.

No-login allowlisted review captures on the bounded evidence server:
`http://192.168.2.45:5133/product-detail-{1440,390,320}.png`.

## Validation

- CameraAgent Debug warning-clean build: zero warnings/errors.
- Focused ProductDetail/ArchivePages tests after corrections: 23 passed, no skips.
- Nine new Unit cases cover exact encoded/packed media selection, missing/error
  handling, current-step versus immutable artifact state, sibling-output ordering,
  unknown executor, wrong requested identity, stale responses, revocation and
  local return URL restrictions. Inventories/runbook are aligned.
- Existing artifact content Integration case is expanded to verify supported
  inline bytes/checksum and rejection of inline for raw/unknown types. Replay
  content retains attachment disposition even when `inline=true` is requested.
- Native browser qualification checks exact artifact source, fullscreen/fit/100%,
  keyboard/focus, identity-preserving media failure and 1440/390/320 bounds.
- Dedicated Manual `OwnerProductDetailPresentationAcceptanceAsync` exercises a real
  VirtualSky fixture artifact, not a production catalog/science qualification.
- Classifier-selected affected candidate gate and independent review results
  belong in the issue/PR ledger on their exact commits. No complete Manual suite,
  performance improvement or generated nightly/video acceptance is claimed here.
  The initial Tier B local gate is focused/affected evidence; the classifier also
  selects the combined protocol lane, whose full suites and component coverage
  are required on the protected current-head CI run. Targeted artifact endpoint
  tests are not presented as a full combined-lane local pass.

## Observability and performance

No new worker, background queue or scientific image algorithm. Runtime records
verified production catalog identity and preserved ingress/product reconciliation
without missing/quarantined captures. Existing authorized content telemetry is
unchanged. Comparative benchmark/metric/trace manifests are N/A for this bounded
UI/media-delivery change; media identity, byte checksums, responsive bounds and
failure semantics are the relevant evidence.

Page-specific operator visual acceptance is pending.
