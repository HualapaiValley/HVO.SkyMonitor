# LogicHost UI Design Studies

These static files explore the public Sky Network and authenticated Network
Operations experiences for LogicHost. They use synthetic observatories, images,
events, and operational data. Proposed capabilities are not implementation
evidence.

From the repository root, serve the studies with:

```bash
node --env-file=.env docs/design/logichost/serve-mockups.mjs
```

Then open `http://localhost:4174/`.

## Initial Page Set

- `network.html`: public landing page with network activity, featured stations,
  verified events, imagery, and editorial news.
- `observatories.html`: public observatory directory using published location
  precision and safe station state.
- `observatory-volcano.html`: public station profile with multiple cameras and
  deliberately released products.
- `events.html`: public verified-event feed with publication authority shown.
- `app-dashboard.html`: registered-user network dashboard across accessible and
  followed observatories.
- `app-observatory.html`: owner view of one multi-camera observatory.
- `app-camera.html`: stable logical-camera view with CameraAgent-derived
  workflows and immutable installation history.
- `app-captures.html` and `app-capture-detail.html`: bounded central archive,
  artifact-origin variants, authenticated retrieval, provenance, and jobs.
- `app-processing.html`: read-only inherited global defaults, an owner-managed
  observatory override, scoped jobs, output policy, and reprocessing without
  CameraAgent acquisition controls.
- `app-members.html`: observatory-wide membership and invitation administration.
- `account.html`: existing human-identity concepts and account-owned API keys.

## Product Boundaries

- Anonymous pages consume separate allowlisted public projections. They never
  reuse protected DTOs or expose exact coordinates, internal IDs, diagnostics,
  raw artifacts, credentials, or unpublished records.
- Registered users receive richer safe network summaries and community features:
  follows, bookmarks, subscriptions, and notifications. Comments are excluded.
- Observatory membership is explicit and observatory-wide. Roles are Viewer,
  Manager, and Owner; multiple Owners are supported.
- Invited Viewers have read-only access and may download raw artifacts through a
  future short-lived, audited authorization flow.
- Public profile visibility, operational membership, location disclosure, and
  content publication are independent policies.
- The public directory uses an interactive global map in the target design. Pins
  plot only observatory-approved region or approximate coordinates. Correlation
  lines belong on verified event details, not on the general directory map.
- MapLibre is the selected map client. The study provides a labeled vector map
  and a satellite raster toggle, opens at a complete world extent, and requires
  provider attribution. Production tile-provider licensing and capacity must be
  confirmed. Google Maps remains a contingency decision only; there is no active
  Google integration or key configuration in this study.
- A logical camera survives CameraAgent replacement. Installation identities and
  registration history are immutable and never silently reused.
- Curated homepage placements coexist with automatically surfaced verified
  events, but automatic inclusion requires observatory publication consent.
- Future public and authenticated APIs use the same projections and authorization
  model. The account study includes scoped API-key management; implementation of
  the API and credential lifecycle remains future work.

## Visual Direction

The studies retain the established HVO dark shell, image handling, status
vocabulary, and accessibility conventions. LogicHost adds a violet network
accent, editorial typography on public pages, and denser scope-aware navigation
inside Network Operations so the central host is visually distinct from one
CameraAgent station.
