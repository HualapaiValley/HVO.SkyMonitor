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
- `/operations` retains the existing technical dashboard and links to schedule,
  calibration, environment, transients, system, device, and quarantine tools.
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
2. **Combined**: an available combined/stacked artifact.
3. **Calibrated**: an available calibrated artifact.
4. **Raw**: an available raw artifact rendered through an authorized preview,
   never direct packed bytes in an image element.

An unavailable selected stage is never silently replaced under the same label.
The UI either retains the prior valid selection or explains the unavailable
stage. `Processed` is a presentation label, not a new artifact role.

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
