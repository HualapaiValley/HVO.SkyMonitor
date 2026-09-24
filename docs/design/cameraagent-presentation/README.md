# CameraAgent Presentation Checkpoint

Status: accepted `RM-014/#440` design checkpoint, delivered through PR #453
and recorded by epic #438. These static
studies use synthetic imagery and representative CameraAgent facts. They are
design evidence, not production behavior, processing authority, or an anonymous
publication decision.

## Decision Summary

- `/` becomes the authenticated, image-led **Current sky** view.
- `/gallery` remains the bounded local archive, led by imagery and common
  browsing; technical filters move behind an advanced disclosure.
- `/gallery/{captureId}` leads with the image, stage availability, acquisition
  story, and previous/next navigation. Existing lineage and diagnostics remain
  available in a technical disclosure.
- `/operations` is the overview of the Operations workspace (#517): a grouped
  section sidebar reaches camera and rig, sky map and catalog, device
  registration, capture schedule, calibration, environment, pipeline summary,
  automations, data and storage, quarantine and recovery, and system. The
  retained `/schedule`, `/calibration`, `/environmental`, and `/system` routes
  alias their workspace sections. Transients live under the archive.
- Opening capture details and opening the large-image viewer are separate,
  explicitly labeled actions.
- The latest valid image remains visible during daylight, delay, staleness, or
  disconnection. Image freshness and system state are always reported
  independently.

Production implementation remains authenticated and CameraAgent-local. Public,
kiosk, or anonymous access requires a separate threat model and approval.

The static studies use `operations-study.html` as a labeled route placeholder;
it documents the production `/operations` destination without reproducing or
redesigning the existing technical dashboard.

## Studies

- [Current sky](home.html)
- [Archive](gallery.html)
- [Capture detail](capture-detail.html)
- [Large-image viewer](large-viewer.html)
- [Technical workspace route placeholder](operations-study.html)

Each study contains exact CSS-pixel desktop (`1440 x 900`), narrow portrait
(`390 x 844`), and short landscape (`844 x 390`) frames. The study wrapper may
scroll horizontally on a smaller browser rather than scaling a frame and hiding
responsive defects. Production acceptance still uses those actual browser
viewports.

## Current Route Audit

| Route | Current implementation | Decision |
| --- | --- | --- |
| `/`, `/operations` | `Components/Pages/OperationsPage.*` | Preserve at `/operations`; #439 supplies the new `/`. |
| `/gallery` | `Components/Pages/GalleryPage.*` | Preserve bounded cursor paging; #441 changes hierarchy. |
| `/gallery/{captureId}` | `Components/Pages/GalleryDetail.*` | Preserve evidence and downloads; #442 makes presentation primary. |
| Technical routes | `Components/Pages/` | Remain in the technical workspace without broad redesign. |

The existing dark theme, focus treatment, `object-fit: contain`, reduced-height
rules, authorization boundary, and 44-pixel coarse-pointer targets remain the
visual and accessibility foundation.

## Prior Study Audit

The historical study now preserved by the `cameraagent-ui-mockups-archive` tag
was reviewed as research, not current implementation authority. This checkpoint
recovers the **Observatory Window** image-first hierarchy, separately aged image
and environmental facts, last-valid-image behavior, bounded frame browsing, and
the synthetic day/night imagery. It rejects that study's anonymous-public
assumption and does not adopt its calendar, events, time-lapse, keogram,
dewarping, product scheduling, configuration, or runtime-supervisor proposals.
Those broader concepts have no authority in #440.

Issue #514 later authorized a bounded subset on its own terms: an observing
calendar of local-noon-to-noon nights with retained capture and local
candidate counts, day detail through the existing capture filters, product
list and detail views over outputs CameraAgent actually retains, and local
candidate list, calendar, and detail views. Time-lapse, keogram, registered
stacking, product scheduling, and any new generation remain unauthorized.

## Image Hierarchy

Presentation labels select only artifacts that actually exist and can be safely
displayed:

1. **Processed**: an available display derivative such as the current
   `AnnotatedPreview` or `Preview`; the exact backing role remains visible in
   technical details.
2. **Combined / Live mean**: an available linear arithmetic mean, not an exposure
   sum or a registered stack.
3. **Calibrated**: an available calibrated artifact.
4. **Raw**: an available raw artifact rendered through an authorized preview,
   never direct packed bytes in an image element.

An unavailable selected stage is never silently replaced under the same label.
The UI either retains the prior valid selection or explains the unavailable
stage. `Processed` is a presentation label, not a new artifact role.

### Capture-Bound Comparison Display

Current Sky keeps the linear Combined artifact as the stage/source and download
identity. Its display may use a unique, retained `encoded-preview` derivative
whose only source is that Combined artifact. An incomplete or ambiguous artifact
list cannot establish uniqueness. The loaded layered hero must name that same
derivative in its manifest base before presenting it as the shared base; pending
layers and all-overlays-off use the same unannotated display base.
Once the layer service confirms that no overlay manifest was retained, a legacy
Processed selection shows its retained flattened annotated artifact instead.
Failure to read existing structured layers is not proof of absence and retains
the explicit unannotated fallback. Pending reads never claim that layers loaded.
This remains true when the comparison derivative cannot qualify: use the
available Combined artifact's own preview and its fallback policy, or show no
image if Combined is unavailable. Baked annotated pixels require confirmed
manifest absence, not merely a false structured-layer availability flag.
Losing the layered DOM or changing its base invalidates verification; same-capture
recovery must bind the replacement DOM and reapply the saved layer selection
before enabling overlays or saving. Both standard and layered images are capped
at their native size, container width and 70vh, including small retained frames.

For Raw and Calibrated, Current Sky requests
`/api/v1/operations/artifacts/{id}/preview?displayReference={retained-preview-guid}`.
Only settings are shared: pixels always come from `{id}`. The server validates
the reference payload/sidecar and journal recipe/output identity, the supported
`encoded-preview-v1` recipe and its recorded `options.parameters`, source
selector, same-capture provenance, compatible dimensions/format, and finite,
ordered percentiles/positive bounded asinh strength. A reference may point to a
packed display image or JPEG; these are not stretched again. Linear targets use
one transfer over their own full-frame histogram before bounded resizing.
Combined and the loaded layered hero use the same policy-qualified derivative
URL (D as its own `displayReference`), so validation, ETags and displayed bytes
agree. JPEG headers are compared with the bounded manifest dimensions and format
before scanline validation. Null source selector variants retain their wildcard
semantics; a specified variant must match every source role, including Raw.

This is the same percentile policy applied independently to each image, **not a
locked transfer curve**. Calibration None remains no correction, not a display
stretch. Without a reference the existing global preview defaults are unchanged.
Gallery/Archive's unvalidated projection remains own-artifact only. A failed
comparison reference causes Current Sky to abandon the shared policy for all
comparison stages and describe the own-artifact fallback instead.

The optional query accepts exactly one nonempty GUID in `D` format, never client
JSON or arbitrary transfer parameters. Malformed input returns a fixed `400`;
unusable/incompatible reference evidence returns a fixed `409`. Existing owner
authorization, source/output bounds and cancellation still apply. Request flights
are registered by artifact/reference GUID before evidence validation. Only
overlapping requests share that validation; later requests revalidate evidence.
Generation flights and the cache include the verified immutable policy identity.
The ETag combines response-byte checksum and policy identity; responses are
private/no-cache and include `X-Display-Policy` when a reference is used.
`X-Display-Operation` and the stage projection distinguish per-image stretch,
encode-only Mono8/RGB, and encoded passthrough; reference selection alone does
not imply that normalization was applied.

## State Matrix

| State | Presentation copy and behavior |
| --- | --- |
| Loading | Reserve the image canvas and say “Loading the latest sky view.” |
| Empty | Say “No sky image is available yet”; do not expose queue language. |
| Capturing | Show the latest useful image, capture time, and image age. |
| Daylight | Say “Daylight standby” and retain the last valid image with its age. |
| Delayed | Say the next image is taking longer than usual; retain the last image. |
| Stale | State the last image time and age independently of connectivity. |
| Offline | State that CameraAgent is disconnected while retaining any historical image. |
| Partial stage | Disable unavailable stages with an explanation; do not relabel a fallback. |
| Failed stage | Explain that the stage could not be created and offer truthful available stages. |
| Missing image | Preserve safe capture facts and say the image is no longer available. |
| Unsupported | Say the capture cannot be displayed in the browser; retain authorized technical download. |

## Interaction Contract

- Archive image/title activates capture detail. A separate **View large image**
  button opens the viewer.
- Home provides distinct **Open capture details** and **View large image**
  actions; neither is hover-only.
- Previous and next capture controls preserve the archive return context and
  have disabled boundary states.
- Stage selection changes only the image stage and reports availability.
- The large viewer is a named modal dialog. Initial focus moves to Close, focus
  remains inside, Escape closes, and focus returns to the invoking control.
- The close target is at least `44 x 44` CSS pixels. The image remains
  aspect-correct; native-size overflow permits deliberate pan/scroll.
- Touch pinch zoom may enhance the viewer, but baseline use does not require it.
  Horizontal swipes do not change captures because they conflict with panning
  and assistive technology.

The static viewer studies model three invocation contexts: desktop closes to
capture detail, portrait closes to Current sky, and short landscape closes to
Archive. Production uses a dialog rather than navigation and restores focus to
the exact invoking control.

## Content Boundaries

The default presentation may show capture time/age, exposure/integration,
available stage, evidence origin when simulated, sensor temperature when useful,
and temporally associated environmental context with its own freshness. Queues,
lanes, heartbeat internals, checksums, recipe IDs, nodes, storage, and full
lineage belong in `/operations` or technical disclosure.

Representative study data follows current component fixtures and the standalone
VirtualSky profile: a `3552 x 3552` simulated all-sky capture, five-second
exposure, gain 82, local sensor temperature, and separately aged environmental
context. Values illustrate hierarchy and are not live evidence.

## Component Boundaries

CameraAgent-local components should separate presentation navigation, state
notice, image freshness, system state, capture image, stage selector, image
actions, large viewer, previous/next navigation, capture summary, archive card,
common browse controls, advanced filters, and technical disclosure. They remain
in the CameraAgent host, not `HVO.SkyMonitor.Common`.

## RM-004 Deferrals

This checkpoint does not define projected-scene identity, layer descriptors,
layer ordering, grouped SVG, masks, renderer/style identity, caching,
materialization, or materialization lineage. Those remain `RM-004`, especially
#431 and #434. Historical `AnnotatedPreview` remains viewable as a flattened
result. Missing structured layers are reported as unavailable and are never
inferred. #443 may project existing artifacts into the bounded current-image
read model; layered controls begin only after stable RM-004 contracts exist.

## Acceptance Crosswalk

| #440 criterion | Evidence |
| --- | --- |
| Three responsive layouts and representative data | All four linked studies and shared `studies.css` |
| Loading, empty, daylight, stale, offline, partial, failure | State matrix and study state rails/panels |
| Accepted IA/content hierarchy | Decision summary and route audit |
| Click/expand and keyboard/touch behavior | Interaction contract and viewer study |
| Reusable boundaries without processing contracts | Component boundaries and RM-004 deferrals |
| Authenticated baseline | Decision summary and content boundaries |
