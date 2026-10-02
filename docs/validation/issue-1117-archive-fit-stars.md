# Issue #1117: Archive containment and catalog-star verification

## Scope and candidate

Baseline: `6bcf4c94060c0e45cfed74287b3f314a319cbc32` on `development/v1`.
Tier B: one Archive scoped-CSS correction and qualification of the already merged
#522 renderer. No image algorithms, catalog limits, captured facts, or historical
artifacts change. The Archive link supplies the parent CSS scope; `::deep img`
reaches the `RetainedPreviewImage` child while retaining the existing 16:10 box
and `object-fit: contain` behavior.

## Same-capture browser evidence

Authenticated native preview, genuine capture #42 (`252fcd46-45aa-8682-bafb-b5bb9b564218`),
1936×1216 selected presentation. Before evidence is retained under
`~/.cache/hvo/1035/archive-follow-up-*`; after measurements and screenshots are
indexed by `~/.cache/hvo/1117/archive-after.json` on home-dev-02.

| Viewport | Before image width | After image box | Card height after | Horizontal overflow |
| --- | ---: | --- | ---: | --- |
| 1440×900 | 1936 px | 444.51×277.81 px | 514.03 px | None |
| 390×844 | 1936 px | 367.20×229.50 px | 465.72 px | None |
| 320×844 | 1936 px | 297.20×185.75 px | 444.22 px | None |

Every image computes `object-fit: contain` and `aspect-ratio: 16 / 10`.
At 320 px the existing 208 px minimum slot height remains, with black space below
the image. The complete native image fits without stretching or cropping.
A browser image-error event removes the image and shows “Image preview unavailable /
Capture facts retained”; the same capture-detail link and accessible reason remain.
Focusing that link and pressing Enter opens the exact capture #42 detail route.
Existing focused tests cover missing previews, failure handling, and artifact identity.

## Historical pixels versus current renderer

Capture #42 predates #522. Its recorded full HYG 4.2 catalog has 119,625 rows,
SHA-256 `B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2`.
Its bounded scene contains 2,000 stars plus seven solar-system objects. The scene
is daytime: 2026-10-01 17:51:56.3948194 UTC, Sun altitude approximately 45.34°.
The preserved local configuration explicitly sets the old 2,000-result bound;
loading the full catalog does not mean rendering every catalog row.

The retained Mono8 display base already contains stars at the recorded positions:
Sirius peaks at 146/255, Capella at 64, Arcturus at 48, and Vega at 43, with local
annular median zero. Samples use radius 3 px about each projected position and
a background annulus of radii 6–9 px. Those small, often faint features shrink
further in card/full-frame display. The observation is not evidence of an unloaded
catalog. These are measurements of historical output, not a claim that the old
renderer applied the current daylight policy.

## Current catalog-backed day/night qualification

The retained probe at `~/.cache/hvo/1117/pixel-probe/` calls the real
`VirtualSkyCameraModule` from the candidate worktree with the same 1936×1216 rig,
observer (35.5599378°, -113.9119818°, 520 m), gain 150, seed 2025, magnitude-zero
flux 300 electrons/s, Bortle class 3, and ASI174 model/black level 64.
It verifies the production catalog row count and checksum above, selects a bounded
32,768 maximum candidate count (the integrated default), and keeps magnitude 6.5.
It retains raw bytes, complete scene/provenance/algorithm metadata, and SHA-256
for both raw and display outputs. No fixture stars or measured associations are added.

`FixedSceneUtc` explicitly records celestial start time; actual request UTC is
separately retained. Night start is 2026-10-01 08:00:00 UTC, not the acquisition
clock. The module uses the midpoint of each exposure for the scene. Display PNGs
apply the existing `Mono16DisplayStretch` default percentile/asinh transfer to
raw bytes, with no annotation layer. They are module qualification artifacts,
not new durable Archive captures or replacements for capture #42.

| Case | Exposure | Sun altitude | Admitted stars | Pixel evidence |
| --- | ---: | ---: | ---: | --- |
| Day at historical scene time | 1 s | +45.3396° | 0 | Current daylight/background admission rejects stellar signals |
| Fixed night | 1 s | -56.6979° | 4 | All four have positive local raw contrast; centroid error ≤0.187 px |
| Fixed night | 20 s | -56.6891° | 162 | All 162 have positive local raw contrast; brightest 15 centroid error ≤0.106 px |

Centroids are positive-background-subtracted pixel-edge centers inside radius 3 px;
background is the median in the 6–9 px annulus. This bounded diagnostic verifies
bright-star alignment, not general detector accuracy or #1103's measured-star work.
For the 20 s image, Vega peaks at raw code 600 against local background 66; Capella
847 against 67; Rigel 509 against 66. Their display peaks are 255. The renderer's
SNR threshold remains 5; the candidate/query/work limits stay bounded.

Reproduce on this host with:

```bash
dotnet run --project /home/roys/.cache/hvo/1117/pixel-probe/PixelProbe.csproj --configuration Debug
```

Retained evidence: `day-1s`, `night-1s`, and `night-20s` each have `.json`, `.raw`,
`.mono8`, and `.png` files under `~/.cache/hvo/1117/`; `pixel-measurements.json`
and `historical-pixels.json` contain the position/contrast checks. These private
host artifacts and the original preview state are preserved. A full copy of the
preview state was taken before restart; capture remains paused. No installed
instance was modified. No performance claim is made for this CSS-only change.

## Validation and acceptance

- CameraAgent Debug build with `-warnaserror`: passed, zero warnings/errors.
- Focused `GalleryPageTests|RetainedPreviewImageTests`: 25 passed, zero skipped.
- Candidate classifier-selected gates and independent exact-range review are
  recorded in the issue/PR ledger once complete.
- Operator acceptance of Archive fit and the catalog-star preview remains required
  before this follow-up can be completed.
