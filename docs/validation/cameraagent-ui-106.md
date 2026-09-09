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
browser, gallery-performance, and archive-performance acceptance classes. Only
the browser and gallery-performance classes guard the pinned Chromium; the
archive-performance class needs no browser and runs on any host, so a non-empty
selection alone never proves the browser cases executed. Other manual hardware,
standalone, and evidence harnesses in the assembly have separate entry points.
The browser tests become inconclusive with an explicit install command when the
pinned executable is absent, and the runner refuses that outcome: it writes a
TRX, requires the selection to have recorded results, requires every recorded
result to be `Passed`, and otherwise exits non-zero naming each non-passing test
and, separately, the reasons the tests recorded. That assertion lives in
`scripts/lib/trx-evidence.sh`, shared with the ARM64 CI runner, and is anchored to
the `UnitTestResult` element rather than to the line: one result per line is a
habit of a particular writer and not a property of the format, and a line-oriented
check reads a collapsed TRX wrongly in both directions. `scripts/test:trx-evidence-contract`
gates that behaviour against fixtures that vary the serialisation as deliberately
as the content. Omitting `--install-browser` on
a host without the pinned revision is therefore a hard failure rather than a run
that reports success having executed almost nothing.
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

The focused RM-014 browser and component scenarios additionally prove:

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
- working-set growth remains at or below 256 MiB for every measured scenario;
- p95 remains at or below five seconds for every measured scenario. A met bound
  is asserted whatever the host was doing: contention biases a deadline against
  the pass, so a browser p95 that clears five seconds on a busy host cleared it
  with less machine available than an idle run had, and is the stronger of the
  two results. Only a missed browser bound is conditional. When a browser family
  misses its bound and the one-minute load average sampled before the workload
  exceeds 0.40 per core, the miss is refused as unattributable rather than
  reported as a regression, the run reports Inconclusive, and a refusal document
  is written beside the evidence. Under contention the browser families move by
  more than 40% while the SQLite and Kestrel families stay flat, so a slow
  product and a busy host produce the same red and the run cannot tell them
  apart (see #774 and #785);
- rendered pages remain at or below 1 MiB;
- cumulative working-set growth remains at or below 32 MiB per browser session,
  under the same directional condition as the p95 claim above: a met bound is
  asserted at any load, and a missed bound above the ceiling is refused together
  with the p95 rather than adjudicated separately;
- later-page latency and allocation do not scale linearly with total history;
- keyset query plans use the expected durable indexes;
- 20 sequential and 50 concurrent requests for one unchanged preview perform
  one encode and return identical bytes;
- every preview-failure page contains exactly 50 failed cards and zero retry
  actions before interaction.

The directional condition above reaches only the two bounds that are asserted.
Every other timing in this evidence, the Kestrel p95 included, is recorded and
compared to nothing, so host load moves the number and moves no verdict. For
those the pre-workload load is published beside the measurement in the
environment block rather than used to refuse the run, because refusing would
discard evidence to protect a conclusion nobody drew. A deadline enforced by a
timeout or a cancellation token rather than by an assertion is the same
directional shape wearing a different mechanism and is not covered.

The browser-render workload substitutes deterministic valid one-pixel images so
that its latency and memory numbers isolate server rendering, component state,
and session concurrency rather than image transfer or decode variance. Separate
production-service measurements cover preview validation, encoding, cache, and
concurrency; focused browser acceptance verifies that real authorized previews
load and decode. The evidence does not claim end-to-end image-transfer latency.

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
`8393a10b4c7a92e35ae740101a3d3bd476380b0336b918127a4a97b67ae8b609`,
with revision fingerprint
`333652BC2DBB049959A86F75D70D71B879580DA843888C8CCF45146CBDA1AC74`.
Documentation-only changes after that measurement do not invalidate the
measured path under the performance protocol.

At concurrency 50, candidate browser-render p95 was 2,852.0874 ms for the 1K
history and 2,062.6439 ms for 10K, compared with baseline 2,899.6764 ms and
2,978.5488 ms. Candidate cumulative working-set growth was about 1.45 MiB and
1.16 MiB per session respectively. The candidate-only completed preview-failure
p95 was 469.1274 ms for 1K and 506.8519 ms for 10K, with about 2.61 MiB and
1.71 MiB cumulative growth per session. No unexplained material regression was
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
