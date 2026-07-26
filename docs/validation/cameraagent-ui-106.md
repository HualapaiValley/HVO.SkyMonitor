# CameraAgent UI Acceptance Evidence

Issue #106 provides the authenticated local CameraAgent operations, system,
quarantine, and gallery experience over durable server-side read services. It
does not add physical camera controls, SDK-specific tuning, hardware evidence,
or central LogicHost UI behavior.

## Opt-In Gate

Browser and performance acceptance live in the separate
`HVO.SkyMonitor.CameraAgent.AcceptanceTests` assembly. The assembly and every
test are categorized `Manual` and do not participate in the default Unit or
Integration selections. Run the complete issue gate from the repository root:

```bash
./scripts/test:cameraagent-ui-106 --install-browser
```

The install option downloads the Chromium revision pinned by
`Microsoft.Playwright.MSTest`; omit it after that revision is installed. The
runner performs a warning-as-error Release build and then runs all Manual tests
in the acceptance assembly. The browser test becomes inconclusive with an
explicit install command when the pinned executable is absent.
The development container installs that pinned revision during post-create and
retains it in the `hvo-skymonitor-playwright` volume across rebuilds.

The acceptance fixture starts the production CameraAgent host on an ephemeral
Kestrel TCP port. Identity, Data Protection keys, raw ingress, processing,
artifact storage, configuration, and catalog state are isolated under a
per-run temporary directory. Its deterministic `RandomImage` profile exercises
real capture, durable ingress, processing, preview, and storage paths without
claiming physical evidence.

## Browser Coverage

`OwnerOperationsGalleryAndResponsiveAcceptanceAsync` proves these boundaries:

- anonymous pages redirect to login, unauthenticated APIs return 401, and an
  authenticated non-owner receives Access Denied;
- the owner can inspect capture state and durable operational health, confirm
  pause/resume, dismiss confirmation with Escape, regain trigger focus, and see
  terminal receipts;
- deterministic malformed legacy fixtures populate bounded artifact quarantine;
  the owner pages with opaque cursors and completes one abandon confirmation,
  including Escape dismissal and trigger-focus restoration;
- a gallery of real durable captures supports URL-backed origin filtering,
  bounded cursor paging, detail navigation, and preservation of the filtered
  return URL;
- previews and downloads require the owner cookie, return non-empty content,
  and vary responses by `Cookie`;
- every visible gallery image decodes, no preview response fails, detail images
  use `object-fit: contain`, and images remain bounded by the viewport;
- operations, quarantine, gallery, detail, and system routes have no horizontal overflow at
  1440x900, 820x1180, 390x844, 844x390, and 320x700;
- visible links, buttons, and images have text or accessible names, gallery
  inputs have labels, and each route retains one main landmark and heading;
- rendered text excludes fixture secrets, lease/client-secret names, Data
  Protection paths, and the fixture's internal storage root.
- computed foreground/background contrast for visible primary text, status, and
  control pairs is at least 4.5:1 at every responsive route and viewport.

These deterministic assertions cover the issue's keyboard, focus, labels,
landmarks, image sizing, contrast, and responsive acceptance boundaries. A full
axe rule scan is not part of this gate; scoped component tests and manual visual
review remain necessary for rules outside these deterministic checks.

## Performance Method

`GalleryReadModelRecordsIssue106AcceptanceEvidenceAsync` uses production raw
ingress and capture-processing SQLite schemas plus the production gallery read
service. It seeds 1,000 and 10,000 metadata records in a prepared transaction,
with one raw and one derived preview artifact per capture, valid manifest-v2
checksums and lineage, representative evidence origins, and representative
processing outcomes.

For each history size, first, middle, later, filtered, and detail reads run at
concurrency 1, 10, and 50. The test records response bytes, median and p95
latency, process CPU, allocations, working set, RSS, output checksum, SQLite
version, and EXPLAIN query plans. In addition to these direct SQLite/read-service
measurements, the same 1,000 and 10,000 galleries are injected at the production
service boundary of a real loopback Kestrel CameraAgent. A real owner cookie is
obtained through the local Identity login form, and authenticated gallery HTTP
requests run at 1, 10, and 50 clients while recording API bytes, median, p95,
throughput, and response checksum. It also proves:

- exact descending traversal without gaps or duplicate captures;
- bounded 50-item pages and responses no larger than 512 KiB;
- pre-cancelled reads terminate within two seconds;
- p95 remains at or below five seconds and working-set growth remains at or
  below 256 MiB for every measured scenario;
- later-page latency and allocation do not scale linearly with total history;
- keyset query plans use the expected durable indexes;
- 20 sequential and 50 concurrent requests for one unchanged preview perform
  one encode and return identical bytes.

Generated evidence is ignored by Git and written to:

```text
TestResults/issue-106/working-tree/cameraagent-gallery-performance.json
```

The JSON records exact `HEAD`, whether the worktree is dirty, a deterministic
SHA-256 over the tracked binary diff plus sorted untracked path/content hashes,
the environment, workload,
thresholds, SQL plans, correctness invariants, measurements, cache behavior,
and privacy disposition. The baseline is explicitly `NotApplicable` because no
predecessor CameraAgent endpoint or UI exists; acceptance compares 1K versus
10K scaling and declared thresholds rather than inventing baseline values.
Fixed synthetic identities may appear in SQL evidence;
credentials, payload content, lease tokens, and internal paths must not.

## Residual Boundaries

The browser gate checks one isolated owner session and deterministic responsive
viewports; it is not a browser compatibility matrix or a visual-regression
suite. Performance numbers are acceptance bounds for the recorded machine and
SQLite workload, not production capacity claims. Release evidence intended for
review should be regenerated from the candidate head and its JSON checksum
retained with the test log.
