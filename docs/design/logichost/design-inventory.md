# LogicHost Design Inventory

## Information Architecture

### Public Sky Network

- Home
- Observatory directory
- Observatory public profile
- Verified events
- News and explainers
- Sign in / registration

### Network Operations

- All-accessible dashboard
- Observatory overview
- Camera overview
- Captures and raw artifacts
- Events and central review
- Processing and jobs
- Health and notifications
- Members and publication settings
- Account, subscriptions, and future API access

## Access Vocabulary

| Audience | Read access | Mutations |
| --- | --- | --- |
| Visitor | Deliberately released public projections | None |
| Registered User | Public data plus safe operations and subscriptions | Personal follows/bookmarks |
| Viewer | Full protected read, including audited raw download | None |
| Manager | Protected operations | Camera, processing, and review actions |
| Owner | Full observatory access | Membership, visibility, publication, registration, destructive settings |
| Platform Editor | Curated public release surfaces | Feature, suppress, order, and editorial publishing |

Roles apply to the observatory and all of its cameras. Camera-level overrides are
not part of the initial design.

## Required Contracts Before Blazor Conversion

- Observatory membership with auditable Viewer, Manager, and Owner grants.
- Separate public profile audience and operational access policy.
- Independent region, approximate, and exact location disclosure.
- Observatory publication consent and per-record release/withdrawal authority.
- Interactive MapLibre directory using backend-produced, allowlisted public
  location projections, complete-world initial bounds, named markers, and
  standard/satellite styles. Tile-provider licensing, capacity, and attribution
  are required. Google Maps remains a contingency decision only and has no active
  integration in this design.
- Public-safe observatory, camera, image, weather, event, and product projections.
- Stable logical Camera separate from immutable CameraAgent installation identity.
- Explicit installation replacement/assignment history.
- Bounded public and protected archive queries with opaque content resolution.
- Audited short-lived authorization for Viewer raw-artifact downloads.
- Follow, bookmark, subscription, and notification preferences.
- Curated placement records plus eligibility rules for automatic verified-event
  inclusion.
- Future API clients and keys scoped to the same audience and observatory grants;
  API keys cannot widen the issuing user's effective permissions.

## Safety Notes

- Exact coordinates, owner identity, device identifiers, registration IDs,
  credentials, queue details, storage paths, and diagnostics are protected.
- Public pages must remain truthful about image, weather, and station freshness.
- Central verified events must not be inferred from CameraAgent candidate or
  delivery state.
- A public event composed from multiple stations needs an explicit sanitized
  release that does not reveal a private contributor.
- Automatic homepage selection only considers verified, released, non-withdrawn
  records from observatories that opted into public inclusion.
- API responses need dedicated versioned projections rather than serialization of
  UI or persistence models.
