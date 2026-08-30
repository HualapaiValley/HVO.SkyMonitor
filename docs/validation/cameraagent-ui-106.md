# CameraAgent UI Acceptance Evidence

Issue #106 introduced the authenticated local CameraAgent operations, system,
quarantine, and gallery experience over durable server-side read services.
RM-014 extends that foundation with the image-led current view, presentation-first
capture detail, accessible large-image viewing, and bounded visual archive. It
does not add physical camera controls, SDK-specific tuning, hardware evidence,
or central LogicHost UI behavior.

## Opt-In Gate

Browser and performance acceptance live in the separate
`HVO.SkyMonitor.CameraAgent.AcceptanceTests` assembly. The selected browser and
gallery-performance classes are categorized `Manual` and do not participate in
the default Unit or Integration selections. Run the complete issue gate from
the repository root:

```bash
./scripts/test:cameraagent-ui --install-browser
```

The install option downloads the Chromium revision pinned by
`Microsoft.Playwright.MSTest`; omit it after that revision is installed. The
runner performs a warning-as-error Release build and then runs the CameraAgent
browser and gallery-performance acceptance classes. Other manual hardware,
standalone, and evidence harnesses in the assembly have separate entry points.
The browser test becomes inconclusive with an explicit install command when the
pinned executable is absent.
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

The focused RM-014 browser scenarios additionally prove:

- `/` is an authenticated image-led Current sky route while `/operations`
  retains technical health and controls;
- current-image stage selection, stale and unavailable states, enlargement,
  Escape close, focus restoration, and responsive image containment;
- archive card fallback, lazy loading, exact advanced filter and cursor state,
  separate detail/enlargement actions, and isolated preview failure/retry state;
- presentation-first capture detail, stage comparison, bounded previous/next
  navigation, technical disclosure, and the shared accessible large viewer.

## Performance Method

`GalleryReadModelRecordsIssue106AcceptanceEvidenceAsync` uses production raw
ingress and capture-processing SQLite schemas plus the production gallery read
service. It seeds 1,000 and 10,000 metadata records in a prepared transaction,
with one raw and one derived preview artifact per capture, valid manifest-v2
checksums and lineage, representative evidence origins, and representative
processing outcomes. The issue-441 schema records equivalent synthetic
predecessor and candidate runs.

For each history size, first, middle, later, filtered, and detail reads run at
concurrency 1, 10, and 50. The test records response bytes, median and p95
latency, process CPU, allocations, working set, RSS, output checksum, SQLite
version, and EXPLAIN query plans. In addition to these direct SQLite/read-service
measurements, the same 1,000 and 10,000 galleries are injected at the production
service boundary of a real loopback Kestrel CameraAgent. A real owner cookie is
obtained through the local Identity login form, and authenticated gallery HTTP
and browser-rendered requests run at 1, 10, and 50 sessions while recording API
and rendered bytes, median, p95, throughput, CPU, allocations, RSS, working set,
and per-session growth. The candidate also measures completion of all 50
deterministic per-card preview failures without retries. The harness proves:

- exact descending traversal without gaps or duplicate captures;
- bounded 50-item pages and responses no larger than 512 KiB;
- pre-cancelled reads terminate within two seconds;
- p95 remains at or below five seconds and working-set growth remains at or
  below 256 MiB for every measured scenario;
- rendered pages remain at or below 1 MiB and cumulative working-set growth
  remains at or below 32 MiB per browser session;
- later-page latency and allocation do not scale linearly with total history;
- keyset query plans use the expected durable indexes;
- 20 sequential and 50 concurrent requests for one unchanged preview perform
  one encode and return identical bytes;
- every preview-failure page contains exactly 50 failed cards and zero retry
  actions before interaction.

Generated evidence is ignored by Git and written to:

```text
TestResults/issue-441/baseline/cameraagent-gallery-performance.json
TestResults/issue-441/candidate/cameraagent-gallery-performance.json
```

The JSON records exact `HEAD`, whether the worktree is dirty, a deterministic
SHA-256 over the tracked binary diff plus sorted untracked path/content hashes,
the environment, workload, thresholds, SQL plans, correctness invariants,
measurements, cache behavior, and privacy disposition. Raw JSON remains ignored;
retain its checksum with the issue or PR evidence.

The accepted predecessor baseline is based on commit
`1e2bab3f1c82fff45b5826900465c4ef2f5b07f3` plus the benchmark-only harness
correction identified in the JSON. Its evidence SHA-256 is
`9f45522b56f52c083daf12e4fdd537997871c98b72ad78ca65dfeb80ab755d93`.
The candidate evidence SHA-256 is
`79662386c1f0789ff2d5d47852b7f0958fd51dc00c4a259e28ba6f07e9bba427`,
with revision fingerprint
`41E94D3F1FB6A9F7DEA76C20AC126F20D80A1E67476EE7C9C074FB9E8E5746D3`.
Documentation-only changes after that measurement do not invalidate the
measured path under the performance protocol.

At concurrency 50, candidate browser-render p95 was 2,669.9025 ms for the 1K
history and 2,765.8572 ms for 10K, compared with baseline 2,899.6764 ms and
2,978.5488 ms. Candidate cumulative working-set growth was about 1.24 MiB and
1.97 MiB per session respectively. The candidate-only completed preview-failure
p95 was 942.296 ms for 1K and 707.2654 ms for 10K, with about 1.95 MiB and
2.01 MiB cumulative growth per session. No unexplained material regression was
observed.
Fixed synthetic identities may appear in SQL evidence;
credentials, payload content, lease tokens, and internal paths must not.

## Residual Boundaries

The browser gate checks one isolated owner session and deterministic responsive
viewports; it is not a browser compatibility matrix or a visual-regression
suite. Performance numbers are acceptance bounds for the recorded machine and
SQLite workload, not production capacity claims. Release evidence intended for
review should be regenerated from the candidate head and its JSON checksum
retained with the test log.
