# Issue #1021: generated products library

## Scope and acceptance boundary

Tier B presentation work on the CameraAgent Products page.
Branch point: `171d6b5736c8d0bdf11d96f03c3bbd7af1a1ffba`, `development/v1`.

Before this change, `/archive/products` was only the retained outputs table:
one row per retained output, filtered by role, kind, availability and recipe.
It could not show what the nightly automations had generated for a day.

The operator decided the scope on 2026-10-04:
- A card is one daily (sunrise-to-sunrise) final product: a star trail or a
  keogram for one evaluated period, with its hourly counts and a link to the
  day. Hourly products, and periods still in progress, stay on the observing
  day page.
- The retained outputs table stays as a second view of the same page
  (`Generated products | Retained outputs`). Old links that carry its role,
  kind, availability, recipe, cursor or page size open it unchanged.
- An evaluated period without a nightly product is a placeholder card. It shows
  the recorded reason and no image.

What is unchanged:
- The page shows only what #993 recorded.
- It never starts, retries or regenerates a product.
- No capture image or stock image stands in for a product.
- Time-lapses stay "not yet generated" until #1130.
- Generation progress is not recorded, so "Running" is not a status choice.
- The retained outputs table, its query and its paging are untouched. Storage
  availability is shown only there, and is never treated as generation state.
- The calendar, the observing day page, the endpoints and the existing
  projections are untouched apart from shared helpers made reachable.

## Implementation

- **Store read.** `INightlyProductCatalog.ListLibraryAsync` lists daily final
  evaluations of `SunriseDay` source windows, newest report date first, under
  the store's read gate.
  - A page shows at most 31 report dates; the page uses 12. A single observing
    day is never paged.
  - The report dates come from `nightly_windows` through the date index, limited
    to one more date than the scan.
  - They are classified from the same date summary the archive calendar reads:
    a daily product is Produced, hourly products without one are Partial, and
    neither is Not produced.
  - Without a status filter, every candidate date matches, so the scan is the
    page size. With one, a page examines at most 62 report dates. When it
    stops there before filling, `SearchedThrough` names the oldest date
    examined and Older products continues below it.
  - Each shown summary is joined to its daily evaluation, chosen as the
    observing day page chooses it. A choice that disagrees with the summary is
    a torn read and fails with `InvalidDataException` rather than showing a
    mismatched card.
- **Service.** `CameraAgentNightlyProductUiService.ListLibraryAsync` maps the
  page and the store's bound errors onto the operator result kinds. A bound
  error reads "outside the generated products library bound".
- **Page.** `ProductsPage` switches between the two views with `?view=retained`
  (`ProductsPage.RetainedPath`). A query that carries any retained parameter
  (role, kind, availability, recipe, cursor or page size) opens the retained
  view. The generated view never reads retained outputs.
- **Library view.** `GeneratedProductsView`:
  - shows the "Archive / multi-source derivatives" eyebrow, the "Generated
    products" heading and intro, and "Manage automations";
  - has Product type, Status and Observing day filters plus "Show all days".
    They are URL state (`type`, `status`, `day`, `before`), so a filter, the
    page position and Back all round-trip;
  - disables time-lapse, daily summary and running, and explains why in a note
    the controls reference. A link that names one of them lists nothing and
    says why. A malformed value does the same rather than querying;
  - shows cards in a grid of 3 columns, then 2 at 1100px and below, then 1 at
    650px and below. Rows are equal height.
- **Card.** Each card shows:
  - the exact preview inside a fixed 16:10 box at its own aspect ratio
    (`object-fit: contain`), linked to the product detail;
  - or, without a product, a placeholder with the kind icon and the recorded
    reason. A preview that fails becomes "Preview unavailable" and still links
    to the product;
  - the title, the kind and the state, the state description and the source
    period when another period is also recorded;
  - sources, the frame span (or the period span) in the period's own time zone,
    the state chip and the hourly counts;
  - the observing day link and "Automation: {definition} · revision N".
- **Empty states, in order.** "No older generated products" on an older page,
  with Newest products. "No generated products match these filters" with Show
  all products and, for a day, Open the observing day. "No generated products
  yet" with Manage automations. An older page never claims that no product
  matches.
- **Loading and errors.** Loading has its own notice. A failure says generated
  products are unavailable and offers Try again; invalid input is informational
  and access denial redirects.
- **Return links.** A retained output's detail page returns to the retained
  view, because the bare products path is now the library.

## Private-host evidence

The products are the #993 full-day harness products recorded in
`issue-1138-nightly-product-adoption.md`:
- a nightly star trail, 640 × 640;
- a nightly keogram, 1,441 × 629;
- report date 2026-10-02, America/Phoenix.

The images are VirtualSky output, not actual sky.

The private loopback host on `127.0.0.1:5138` used:
- a copy of the harness runtime, configured with the harness's own site record
  (`issue-993-qualified-site`, source `test`). Its nightly products store is
  SHA-256 `f2c1f8ad…f7b6`;
- for the empty state, a fresh runtime directory with no frames and no
  recorded evaluation;
- automation disabled;
- no installed instance, installer or camera hardware.

Both builds are Release publishes:
- the branch point `171d6b57` (before);
- `567fb0ca2dc0bc85709bc523567f502ceffed950` (after). The later branch commits
  change only tests and this record.

The prototype is `docs/prototypes/pipeline-operations/products.html`, opened
from the file system.

Evidence is in `/home/roys/.cache/hvo/1021/`. It was captured in Chromium
153.0.8010.12 at 1440 × 900 and 390 × 844, plus 1024 × 768 for the two-column
breakpoint, by:
- a Playwright tool. The first six indexes were taken with SHA-256
  `18aaa5f3…63c1`; the tool then gained the tablet mode (`b6b073d0…49aa`) and
  the date-focus mode (`b2272cde…aaf`), which change no other mode;
- a runner, SHA-256 `c897f306…95e5`.

`manifest.json` (SHA-256 `def30eae…d2d`) records a SHA-256 for every index and
screenshot, and the tool hash behind each index.

| Index | Head | Content |
| --- | --- | --- |
| `before.json` | `171d6b57` | 4 screenshots, 2 checks. |
| `after.json` | `567fb0ca` | 37 screenshots, 20 checks: 9 states at each width, the URL-state check and the keyboard-order check. |
| `tablet.json` | `567fb0ca` | 2 screenshots, 1 check. |
| `failed-preview.json` | `567fb0ca` | 4 screenshots, 2 checks. Every preview request was aborted (8). |
| `empty.json` | `567fb0ca` | 8 screenshots, 4 checks, on the fresh runtime. |
| `date-focus.json` | `567fb0ca` | 4 toolbar screenshots, one per Tab stop of the date input. |
| `prototype.json` | prototype | 4 screenshots, 2 checks. |
| `compare.json` | — | 6 side-by-side composites: prototype/after, before/after and before/retained at each acceptance width. |

There are no browser errors except the 8 expected `net::ERR_FAILED` loads of
the aborted previews. No page overflows horizontally.

### Card measurements

| Width | Columns | Card | Star trail drawn | Keogram drawn |
| --- | --- | --- | --- | --- |
| 1440 | 3 | 447 × 449 | 278 × 278 | 445 × 194 |
| 1024 | 2 | 479 × 453 | 298 × 298 | 477 × 208 |
| 390 | 1 | 369 × 412 | 230 × 230 | 367 × 160 |

Each drawn ratio equals its natural ratio: 1.000 for the star trail and 2.291
for the keogram. The prototype shows 3 columns at 1440 and 1 at 390.

The populated library reads "2 product cards from 1 observing day, newest
first." The cards show:

| Card | Sources | Frames | Automation |
| --- | --- | --- | --- |
| Star trail | 18 segment products | 19:42 2 Oct–05:07 3 Oct | Qualified StarTrail · revision 1 |
| Keogram | 44 segment products | 06:41 2 Oct–06:21 3 Oct | Qualified Keogram · revision 1 |

### States

| State | URL | Result |
| --- | --- | --- |
| Before | `/archive/products` | "Products" retained table, 24 rows. |
| Populated | `/archive/products` | 2 cards, as above. |
| Type | `?type=keogram` | 1 card. |
| Day | `?day=2026-10-02` | 2 cards. |
| Status no-match | `?status=not-produced` | "No generated products match these filters", Show all products. |
| Day no-match | `?day=2026-09-01` | The same, plus Open the observing day. |
| Unrecorded type | `?type=time-lapse` | "Nothing is listed for this filter", reading "Timelapses are not yet generated, so none are listed and nothing is substituted for them.", with Show all products. |
| Older page | `?before=2026-10-02` | "No older generated products", Newest products. |
| Retained | `?view=retained` | "Retained outputs", 24 rows. |
| Legacy link | `?role=Combined` | Opens "Retained outputs" unchanged. |
| Failed preview | `/archive/products` | Both cards read "Preview unavailable" and still link to their product detail. |
| No generated products | `/archive/products`, fresh runtime | "No generated products yet", Manage automations. |

On the fresh runtime the retained view shows its existing "No retained products
match" notice, unchanged from `171d6b57`.

### URL state and keyboard

- Choosing Keogram navigates to `?type=keogram`. Produced adds
  `&status=produced`, and the day adds `&day=2026-10-02`. Show all days removes
  the day, and Back restores it with the card shown.
- Tab order at 1440: the two view tabs (44px), Manage automations, Product type,
  Status, the date input's four native stops, Show all days, the first card's
  media link, then its observing day link (44px). Every stop has a visible focus
  indicator. At the date input's fourth stop, Chromium's calendar button, the
  ring is Chromium's own around the icon rather than the input's 3px outline.

### Limits

- The runtime records one evaluated day, 2026-10-02, with both products
  produced. Placeholder cards, Partial, several days, paging and the bounded
  scan notice are therefore covered by the bUnit and store tests below, not by
  screenshots. No store rows were fabricated to stage them.
- Target sizes follow the theme's rule in `wwwroot/css/themes/hvo-dark.css`. At
  1440, with a fine pointer, Manage automations is 40px and the small notice
  actions (Show all products, Open the observing day, Newest products) are 34px.
  At 390 every new control is at least 44px.
- The retained table's row links are 19px both before and after. That table is
  unchanged.
- The space between the archive subnav and the page header is the archive
  layout's existing spacing, present before as well.

## Validation

- `./scripts/ci:classify pull_request 171d6b57 567fb0ca`:
  `mode=full deployment=false complete=false shared=false cameraagent=true
  logichost=false combined=true delivery=false paths=24`. The full mode reason
  is "at least one path is outside the reduced-mode allowlist"; complete is
  false because the change touches only component-scoped paths. The target is
  the Development v1 profile, so no `ci.yml` qualification was dispatched.
- Local gates, all passing at `6bf7e215`:
  - Debug and Release builds with `-warnaserror`;
  - `dotnet format --verify-no-changes`;
  - `./scripts/package:audit`: no vulnerable packages; 6 deprecated
    occurrences, reviewed;
  - `./scripts/ci:shell-syntax`: 154 files, Bash 5.2.21;
  - `./scripts/test:ci-classification`, `./scripts/test:coordination-guard` and
    `./scripts/test:pr-review-tools`;
  - the test-category audit, with `Unit=5783`, `Integration=684`,
    `Manual=167`, `Soak=1`, `External=0` and `Hardware=1`. CameraAgent Unit is
    3319;
  - `./scripts/docs:audit-operations`;
  - the complete Docker-disabled Unit selection: 5783 total, 5776 passed,
    7 skipped, 0 failed. CameraAgent ran 3318 passed and 1 skipped.
- New and changed tests:
  - `NightlyProductProjectionTests`:
    - `Library_ListsDailyFinalsNewestFirstAndClassifiesThemAsTheCalendarDoes`;
    - `Library_FiltersByKindStatusAndDayAndPagesByReportDate`;
    - `Library_StatusFilterStopsAtItsScanBoundAndSaysHowFarBackItSearched`.
  - `GeneratedProductsLibraryTests`:
    - `Library_ShowsEachDailyFinalAsACardWithItsIdentitySourcesSpanAndAutomation`;
    - `Library_FailedPreviewIsAnExplicitPlaceholderThatStillOpensTheProduct`;
    - `Library_FiltersAreUrlStateThatMapsOntoTheLibraryQuery`;
    - `Library_UnrecordedOrMalformedFiltersAreExplainedInsteadOfQueried`;
    - `Library_EmptyStatesTellNoProductsNoMatchAndNoOlderApart`;
    - `Library_BoundedScanIsStatedAndPagingKeepsTheFilters`;
    - `Library_FailureOffersRetryInvalidIsInformationalAndDeniedRedirects`;
    - `Products_RetainedLinksOpenTheRetainedViewUnchanged`;
    - `Products_LibraryViewDoesNotReadRetainedOutputs`.
  - `ProductDetailTests.ReturnLinkOpensTheRetainedOutputsView`.
  - `ArchivePagesTests.NewPages_RedirectToAccessDeniedWhenUnauthorized` now
    covers both views. Commit `6bf7e215` disposes each redirected page before
    the next render. bUnit keeps a rendered page alive and does not route, so
    a calendar page left rendered saw the next query change and redirected
    again from inside that navigation without end. The full Unit run
    overflowed the test host's stack there before the fix.
  - The CameraAgent browser acceptance test now expects the library heading
    and filters, then opens the retained view through the view switch. The
    still-product endpoint test's catalog stub implements the new member.
