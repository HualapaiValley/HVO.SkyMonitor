# Issue #1148: nightly product thumbnails and details viewer

## Scope and acceptance boundary

Tier B presentation follow-up to #1138 on the CameraAgent observing-day page.
Branch point: `cf309114d87373f57d57374928685cc868a9d9c4`, `development/v1`.

The #1138 cards were wide and uneven:
- A product image filled only part of its card.
- The time-lapse sample player took a full row.

They are replaced by compact thumbnail tiles. Each tile opens a viewer with the
larger image and the product's details, so more artifact kinds can join the row
later without widening it.

What is unchanged:
- The page still shows only what #993 recorded.
- It never starts, retries or regenerates a product.
- No capture image stands in for a product.
- The time-lapse remains "not yet generated" until #1130.
- The configured sample stays labeled and is never counted as a product.
- The calendar, the nightly detail page, the endpoints and the projections are
  untouched.

## Implementation

- **Tiles.** Products sit in an `auto-fill` grid (15rem minimum, 9.5rem at
  650px and below) with equal-height rows (`grid-auto-rows: 1fr`), so every
  tile has the same width and height.
  - The media area is a fixed 16:10 box. The preview keeps its own aspect ratio
    inside it (`object-fit: contain`).
  - The title, the recorded facts, the state chip and the hourly summary sit
    below the media. The chips are pinned to the tile's bottom edge.
- **Opening a tile.** A tile is a button (`aria-haspopup="dialog"`) when its
  viewer adds something: the product's larger image, or the hours behind a
  missing nightly product. Other tiles are not interactive.
- **Time-lapse tile.** With a configured sample, the tile reads "Play sample"
  and opens the player in the viewer. Without one it stays an inert icon tile.
- **Viewer.** One `LargeImageViewer` instance per page serves every tile. It
  gains:
  - `Eyebrow` and `CloseLabel` parameters;
  - a `Media` fragment for content other than an image, such as the sample
    player or a stated failure. A `Source` image takes precedence;
  - a `Details` panel beside the canvas, or below it at 850px and narrower;
  - an `OnImageError` callback.

  The size modes ("Fit image" and "100%") are offered only for an image.
- **Details panel.** For a produced product the panel shows:
  - the state and its description;
  - admitted frames, segment products composed, encoded size and total
    integration;
  - the hourly products, moved here from the card;
  - the "Product detail" and "Download preview" links.

  For the sample it shows the not-generated and sample chips, the not-counted
  statement, the media type and the declared size.
- **Narrow screens.** The fitted image is capped at 70dvh and the body
  scrolls, with the media row first and the details row sized to its content.
  The whole image and every details link stay reachable.
- **Failures.** A preview that fails in a tile or in the viewer marks that
  product failed. Both then state "Preview unavailable", show no image and keep
  the download link. The handler captures the product at render time, so a late
  error is attributed to the product that raised it.
- **Navigation and focus.** Navigating to another date closes the viewer.
  Escape and the close button return focus to the tile that opened it.

## Private-host evidence

The products are the #993 full-day harness products recorded in
`issue-1138-nightly-product-adoption.md`:
- a nightly star trail, 640 × 640;
- a nightly keogram, 1,441 × 629;
- report date 2026-10-02, America/Phoenix.

The images are VirtualSky output, not actual sky.

The private loopback host on `127.0.0.1:5138` used:
- a copy of the harness runtime, configured with the harness's own site record
  (`issue-993-qualified-site`, source `test`);
- automation disabled;
- no installed instance, installer or camera hardware.

On the default #1138 host copy, the products belong to another site record. The
page therefore correctly lists them as "Other period only", which offers no
thumbnail to open.

Both builds are Release publishes:
- the branch point `cf309114` (before);
- `53df0ee05301e5f1ab5da07142dd1458b4d10c51` (after).

The configured sample is the synthetic 1280 × 1280 H.264 MP4 test pattern
(`84e6fad9…c711`). It is labeled as a sample.

Evidence is in `/home/roys/.cache/hvo/1148/`. It was captured in Chromium
153.0.8010.12 at 1440 × 900, 390 × 844 and 320 × 640, by:
- a Playwright tool, SHA-256 `f1eee0d5…3e97`;
- a runner, SHA-256 `23a60f11…afe8`.

`manifest.json` (SHA-256 `e95c47a9…e0fb`) records a SHA-256 for every index and
screenshot.

| Index | Head | Content |
| --- | --- | --- |
| `before.json` | `cf309114` | 3 screenshots, 3 checks. |
| `after.json` | `53df0ee0` | 18 screenshots, 18 checks. |
| `failed-preview.json` | `53df0ee0` | 18 screenshots, 18 checks. Every preview request was aborted (12). |
| `corrupt-mp4.json` | `53df0ee0` | 18 screenshots, 18 checks, with 512 KiB of random bytes declared `video/mp4` (`546412f7…7502`) as the sample. |

There are no browser errors except the 12 expected `net::ERR_FAILED` loads of
the aborted previews.

### Tile measurements

| Width | Before: card sizes | After: every tile | Star trail drawn | Keogram drawn |
| --- | --- | --- | --- | --- |
| 1440 | 677 × 271 for the two products; 1365 × 572 for the full-row player | 264 × 308 | 164 × 164 | 262 × 114 |
| 390 | 369 × 240 and 369 × 534 | 180 × 319 | 111 × 111 | 178 × 78 |
| 320 | 299 × 240 and 299 × 480 | 299 × 330 | 186 × 186 | 297 × 130 |

Each drawn ratio equals its natural ratio: 1.000 for the star trail and 2.291
for the keogram.

### Viewer, in every after mode at every width

- The viewer is a modal dialog. Focus starts on "Close the product viewer".
- Escape closes it and returns focus to the opening tile's id.
- The canvas never overflows, and the details content fits.
- At 390 and 320, "Product detail" and "Download preview" are visible above the
  footer once the details are scrolled into view.

Results by product and mode:

| Product | Width | Mode | Result |
| --- | --- | --- | --- |
| Star trail | 1440 | after | Image 640 × 640, its natural size, beside the details. |
| Star trail | 390 / 320 | after | Image 390 × 390 / 320 × 320, with the details below. |
| Keogram | 1440 | after | Image 1062 × 464. |
| Keogram | 390 / 320 | after | Image 390 × 170 / 320 × 140. |
| Time-lapse sample | all | after | Eyebrow "Sample, not from this night"; the player reaches `video` and shows its sample caption. |
| Time-lapse sample | all | corrupt-mp4 | The player reaches `failed` and keeps its download. |
| Star trail and keogram | all | failed-preview | Tiles show "Preview unavailable". The viewer shows the stated failure, no image and no size modes, and keeps the facts and download. |

### Capture note

In the before capture, the narrow keogram card screenshot is blank, although
the image had loaded. A probe (SHA-256 `c9db322f…afd6`) found the cause:
- The image had loaded, and its pixel mean was non-zero.
- After a scroll into view and a short settle, the tile rendered correctly
  (`probe-keogram-390.png`).

The blank was therefore the screenshot racing a lazily loaded image's paint.
The after capture re-decodes and settles after scrolling.

## Validation

- Focused bUnit tests, all in `tests/HVO.SkyMonitor.CameraAgent.Tests/Components`:
  - `ObservingDay_SunrisePeriodShowsRecordedNightlyProductsAndALabelledSample`
    (rewritten for tiles and the viewer);
  - `ObservingDay_AFailedPreviewIsStatedInTheTileAndTheViewer`;
  - `ObservingDay_HoursWithoutANightlyProductOpenTheirListWithoutSubstitutingAnImage`;
  - `MediaAndDetailsAreShownBesideTheCanvasOnlyWhileOpen`;
  - `SourceTakesPrecedenceOverMediaAndReportsItsFailure`.

  The CameraAgent component namespace passes 804 of 804.
- The test-category audit passes with `Unit=5753`. CameraAgent Unit is 3289,
  which adds the 4 new cases.
