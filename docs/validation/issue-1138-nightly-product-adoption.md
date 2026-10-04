# Issue #1138: nightly product adoption on the archive pages

## Scope and acceptance boundary

Tier B adoption of the #993 nightly product catalog by the CameraAgent archive
pages. Deep review covers authorization, exact media identity and the
presentation of recorded facts. Branch point:
`1204a0feb99ee2d70bd9216ef7b593e7777b9ce4`, `development/v1`.

The pages show only what #993 recorded:
- daily and hourly evaluations;
- current products, with their lineage, recipe options, automation runs and
  algorithms.

They never start, retry or regenerate a product, and no capture image stands in
for a product. Time-lapse generation is #1130 and is not claimed here. Until it
exists:
- the time-lapse is always reported as not generated;
- the calendar never lights its T badge;
- an optional configured sample is labeled as a sample.

The #1022 exact-artifact viewer at `/archive/products/{artifactId}` is
unchanged. Nightly products have their own route.

## Implementation

- **Read-only projections.** `SqliteNightlyProductStore` gains three bounded,
  read-only projections:
  - A calendar date summary of at most 62 dates and 8,192 evaluated windows.
  - A day listing that joins every recorded evaluation to its current products.
  - A product presentation, holding:
    - the exact detail and its recorded algorithms;
    - the distinct lineage frame count, within the window-candidate bound;
    - the keogram time axis and gaps;
    - other current outputs for the same span.

  A final keogram's axis is its retained planned axis. A segment keogram's axis
  is rebuilt from its retained gap options and lineage. Other outputs are other
  definitions or revisions current for the span; no predecessor or successor
  is inferred.
- **Endpoints.** Authenticated `OperationsReadV1` endpoints serve those
  projections. Preview and provenance media:
  - accept `GET` and `HEAD`;
  - use the published SHA-256 as a strong ETag and also return it in
    `X-Content-SHA256`;
  - answer `If-None-Match` with 304 and support byte ranges;
  - are `private, no-cache` with `Vary: Cookie` and `nosniff`;
  - return an attachment with `?download=1`.
- **Calendar.** In the sunrise-period view, S (star trail) and K (keogram)
  report one of these states:
  - produced;
  - hourly only;
  - evaluated without a product;
  - pending;
  - not generated;
  - unavailable.

  A produced evaluation is preferred, so an older rejection never hides a
  product. The legacy noon view hides the badges, explains why and links to the
  sunrise view.
- **Day page.** The page shows one card per kind from the recorded daily
  evaluation, with its hourly evaluations listed under it:
  - A produced card shows the exact preview and links to the nightly detail page.
  - An unproduced card states the recorded disposition and reason.

  Final cards count the segment products they composed; frame counts come from
  the lineage on the detail page. When the store cannot be read, the page says
  the nightly status is unavailable rather than showing products as missing.
- **Time-lapse player.** `TimeLapsePlayer` reserves the declared aspect ratio
  and uses native video controls. An animated image loads only when asked,
  because it cannot be paused. Media that fails or is unsupported is reported
  as such.

  The sample (`CameraAgent:TimeLapseSample`) is off by default and requires a
  fully qualified path. It is served at an authenticated
  `/api/v1/operations/time-lapse-sample`, labeled "not generated from this
  night" and never counted as a product. No large sample is committed.
- **Nightly product detail.** `/archive/products/nightly/{productId}` shows:
  - the exact published product;
  - its source period, product window and admitted frame span;
  - selection, lineage and direct-source counts, and total integration;
  - recipe, variant, automation definition and run, and algorithms;
  - other current outputs.

  A planned keogram axis is linear in clock time and hatches its gaps. A segment
  axis labels only actual frame columns. A keogram without a recorded axis draws
  no markers. Long source and gap lists name how many they omit. Narrow axes
  hide clock labels that would overlap. A span crossing midnight is dated.
- **Harness override.** `HVO_ISSUE993_REPORT_DATE` lets the #993 full-day harness
  qualify a sunrise period that has already closed on the reviewing host. The
  site, recipe and gaps are unchanged.

## Private-host evidence

Products come from the #993 full-day harness on `d64ec725`, run with
`HVO_ISSUE993_REPORT_DATE=2026-10-02` (America/Phoenix). The sunrise period ran
from 2026-10-02 13:31:18Z to 2026-10-03 13:32:05Z and used the production
catalog `hyg-v4.2-p3-s2-r1`.

The capture was VirtualSky at a one-minute cadence:
- 1,391 sources;
- a ten-minute leading omission, a thirty-minute internal omission and a
  ten-minute trailing omission.

The images are virtual, not actual sky. The harness passed in 6 m 5 s, with
`evidence.json` SHA-256
`90abefa72bae89f286d8dc40d5d76810ed12776b71797d343992bf7d6370a97e`.

The resulting products:

| Product | Facts |
| --- | --- |
| Nightly keogram `ea7d2cb0-bdbb-8e64-8e55-8236267672a6` | 1,441 × 629. Planned axis of 1 min per column, with 1,391 of 1,441 columns holding frames and 3 gaps. 44 segment products, 1,391 lineage frames, 3h 30m integration. Preview SHA-256 `1696B2C8…E264`, identical to the harness `keogram.jpg`. |
| Nightly star trail `1aa0e4a7-3297-8a11-a6e0-080d28f86516` | 640 × 640. 18 segment products, 566 dark leaf frames, 3h 08m integration. |
| Keogram segment part 1 `6adf1cc6-f6d5-862d-b815-17cf3599f9c5` | 32 frames, 06:41–07:12 local. Actual axis with no gaps, 39 ms integration. |

The browser evidence comes from a private loopback host on `127.0.0.1:5138`
running a Release publish of `33cbc454`. It used:
- a copy of the harness runtime;
- automation disabled;
- no installed instance, installer or camera hardware.

The later commit changes only documentation and the test inventory. Evidence is
in `/home/roys/.cache/hvo/1138/`, captured by a Playwright tool (SHA-256
`4992664f…6d29`) in Chromium 153.0.8010.12 at 1440, 390 and 320 widths:

| Index | Content |
| --- | --- |
| `pages.json` | 29 screenshots and 18 checks. |
| `sample-webm.json` | 3 screenshots and 3 checks of the WebM sample. |
| `sample-mp4.json` | 3 screenshots and 3 checks of the MP4 sample. |
| `sample-gif.json` | 3 screenshots and 3 checks of the GIF sample. |

Every index has zero browser errors, no horizontal overflow and no clipped
controls. Each records the full head SHA, the capture UTC and a SHA-256 for
every screenshot.

What the indexes show:
- **Calendar and day page.** Calendar and day-page cards render at every width.
  The legacy noon view shows no nightly cards and offers the switch link. An
  unknown product ID shows "not found" with a return link.
- **Detail pages.** The keogram, star trail and segment-part detail pages show
  the facts above.
- **Keogram preview headers.** Retrieved with an owner cookie:
  - 200, whose body SHA-256 equals both the ETag and `X-Content-SHA256`;
  - 206 for `bytes 0-1023/64501`;
  - 304 for `If-None-Match`;
  - `attachment` for `?download=1`;
  - 200 for provenance, with checksum `510C3F4C…AFB5` matching its body.

  A cookieless request context received 401 for both preview and provenance.
- **Samples.** All three are synthetic test patterns labeled as samples. The
  calendar T badge stays unlit.

  | Sample | SHA-256 | Result |
  | --- | --- | --- |
  | 1280 × 1280, 30 fps, 12 s VP9 WebM | `6c8145f8…10a6` | Plays at `readyState` 4 |
  | 1280 × 1280, 30 fps, 12 s H.264 High MP4 | `84e6fad9…c711` | Plays at `readyState` 4 |
  | 480 × 480 GIF | `0fcb0947…2e89` | Loads at 480 × 480 when asked |

## Limitations

- The harness produces daily finals only. Hourly evaluation rendering is covered
  by bUnit tests, not by private-host evidence.
- Full-page screenshots repeat the sticky header where Chromium stitches them.
- Store reads share the store's existing gate with generation, so a page read
  can wait for a running generation step.
- Reading the nightly projections on an agent with no products creates the
  empty #993 store schema, as #993 generation already does.
- Real time-lapse size, codec and container choices await #1130. The player
  accepts `video/mp4`, `video/webm`, `image/gif`, `image/webp` and
  `video/x-ms-wmv`, and reports media the browser cannot decode.

## Validation

- **Tests.** There are 33 new Unit cases; the CameraAgent inventory is now
  `Unit=3277`, and the CI runbook totals are aligned. They cover:
  - projections over a real SQLite store: bounds, preference, axis
    reconstruction and other outputs;
  - endpoints: validators, ranges, `HEAD`, download disposition and 404/400;
  - sample configuration and serving;
  - calendar badges;
  - day-page cards and hours;
  - the player;
  - the detail page.

  The test-category audit and `scripts/docs:audit-operations` pass.
- **Gate.** `scripts/ci:classify` on `1204a0fe...` selected `mode=full
  complete=false cameraagent=true combined=true`. The classifier output, the
  four CI-control guards and the selected lane results are recorded in the PR
  ledger on their exact commits.
- **Review.** Independent review results are recorded in the PR ledger.

## Observability and performance

There is no new worker, background queue or image algorithm. Reads are bounded
by the limits above and fail explicitly beyond them. Media responses stream the
published bytes and are revalidated by checksum.

Comparative benchmarks are not applicable to this bounded read and presentation
change. Media identity, byte checksums, responsive bounds and failure semantics
are the relevant evidence.

Operator visual acceptance of the pages is pending.
