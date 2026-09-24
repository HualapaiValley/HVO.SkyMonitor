# Shared Shell Candidate Evidence (#1034)

The initial local candidate below was reviewed with REQUEST CHANGES. See
[Correction Evidence](#correction-evidence) for F1-F4 dispositions and new pixels.
The operator subsequently authorized a draft PR only. Independent correction
review is pending; no ready transition, merge, or live deployment is authorized.

## Source And Isolation

- Owner representative: `opencode:cliproxy:gpt-6-astra:1034-shell-delegate-0924`,
  explicitly delegated by `opencode:cliproxy:gpt-6-astra:1034-main`.
- Worktree: `/home/roys/development/hvo-1034-shell`, branch
  `feature/1034-shared-shell`. All eight inherited modified files are included.
- Baseline and prototype source: `b9da049ed4e1d06f9c73c5652d4ff73e392f067e`.
- Screenshot/code candidate: `8eef6b495f95789bf12c7f8104744432eb38efc3`.
  A subsequent documentation-only evidence commit does not change those pixels.
- Candidate image:
  `sha256:50b6c420e3cd816e9d24e8c7149df9b864f52aa3b1adcfbec038e8739c4fc23a`.
- Browser: `/home/roys/.cache/ms-playwright/chromium-1234/chrome-linux64/chrome`,
  headless Chromium, device scale 1, same host for all three comparisons.
- All runtime work used loopback `127.0.0.1:5234` and the disposable product root
  `/tmp/opencode/1034-shell-evidence/product`. The primary `:5130`, its pointer,
  credentials, configuration and data were not accessed or mutated.
- Baseline instance ends `0001`; final candidate instance ends `0005`, under
  UUID prefix `10340000-0924-4000-8000-`. Intermediate candidates were stopped
  with preserve-by-default uninstall. No state compatibility check was bypassed.
  The final candidate was also uninstalled after evidence capture; its private
  state and the screenshots remain, and no disposable container is left running.
- Equivalent installation inputs: latitude 35.2, longitude -150, elevation 800m,
  `Pacific/Honolulu`, installer `VirtualAsi174Mm` profile, same-night live capture,
  local bundle `/home/roys/hvo/hyg-v4.2-p3-s2-r1.bundle` (119,625 catalog rows).
  Profile SHA-256 `ab58f58aca402daa696740c7f18fe2d8b3402f59513e6cb198c6b1c1e77a2620`
  and schedule SHA-256 `bb3758b944068657d7093b0d715aff114360de02e906908472a228350bac9dc8`
  are identical before/after. Images are genuinely retained 1936x1216 frames with
  verified presentation layers, not mocks or substituted prototype imagery.
- Before and after are different captures and disposable identities, not a
  pixel-difference test of one immutable image. Capture/artifact paths and loaded
  dimensions are recorded in the measurements. No credential/session values are
  included in screenshots, logs, manifests or this document.

## Retained Artifacts

All artifacts are local under `/tmp/opencode/1034-shell-evidence/`.
`manifest.json` binds 54 PNGs and the measurement/scripts to SHA-256 hashes.
The private `product/` and password files are not evidence for publication.

| Artifact | Content |
| --- | --- |
| `{before,prototype,after}-{sky,events,operations,captures,calendar,products}-{1440x900,390x844}.png` | 36 actual viewport screenshots, direct navigation to each destination |
| `comparison-{sky,operations,calendar,events}-{1440x900,390x844}.png` | Eight annotated before/prototype/after sheets, equally scaled native screenshots |
| `after-{shell-menu-panel,operations-sections}-open-390x844.png` | Native modal menu and drawer, focus and background scrim visible |
| `after-{capture-detail,observing-day,product-unavailable,event-unavailable}-{1440x900,390x844}.png` | Direct detail/day route activation; actual retained capture, explicit absent product/event states |
| `{before,prototype,after}-measurements.json`, `layout-comparison.json` | DOM geometry, computed type/color, active destinations, overflow and actual image dimensions |
| `browser.mjs`, `package-evidence.mjs`, `comparison.html` | Reproducible local capture, interaction assertions, comparison renderer and hashing |
| `runtime-summary.json` | Final candidate: zero application/circuit errors, five completed processing graphs; four existing successful-commit slow-operation warnings, no other warnings |
| `unit-results/`, `integration-results/`, `browser-results/` | Retained TRX evidence |

The reproduction command for the final disposable instance is
`EVIDENCE_INSTANCE_SUFFIX=000000000005 node /tmp/opencode/1034-shell-evidence/browser.mjs after`.
It reads the disposable credential privately; it never prints it. The instance
must be running before reproduction. Baseline/prototype screenshot files remain
unchanged by this command.

## Measured Comparison

All values below are CSS pixels, not estimates from scaled contact sheets.

| Boundary | Before | Prototype | After |
| --- | --- | --- | --- |
| Desktop brand x/y/width/height | 40 / 12.80 / 208 / 38.39 | 16 / 16 / 304 / 32 | 16 / 16 / 304 / 32 |
| Desktop primary-nav start / link height | 272 / 64 | 320 / 64 | 320 / 64 |
| Desktop header height | 65 | 65 | 65 |
| Mobile header height / brand y | 61 / 10.80 | 64 / 15.5 | 64 / 15.5 |
| Desktop Current Sky outer root x/y | 40 / 97 | 0 / 65 | 0 / 65 |
| Mobile Current Sky outer root x/y | 16 / 81 | 0 / 64 | 0 / 64 |
| Desktop Operations rail x/y/width/height | 40 / 97 / 248 / 880.86 | 0 / 65 / 280 / 836 | 0 / 65 / 280 / 836 |
| Desktop Calendar subnav x/y/width/height | Absent | 37.44 / 65 / 1365.13 / 52.19 | 37.44 / 65 / 1365.13 / 52.19 |
| Shared font stack | Helvetica Neue | Inter, ui-sans-serif, system fallbacks | Same as prototype, no network font |
| Shared foreground | #f8fafc | #f7f9fc | #f7f9fc |

The desktop header brand, rail, subnav and mobile header boxes now match the
reference. Current Sky's own 32px desktop padding remains, versus 37.44px in the
prototype. Its mobile root padding remains 12.8px/12px versus 16px/10.4px. Those
are page-owned #1035 differences, not a remaining outer double gutter.

All six candidate destinations have no document overflow at 1440x900, 820x1180,
390x844 and 320x844. Baseline Events overflowed at 320px; the inherited scoped
heading/theme correction removes that observed overflow without page edits.
The reference Calendar itself overflows at 320px; the application does not copy
that defect.

## Behavior And Boundaries

- Four primary destinations: Current Sky, Archive, Events, Operations. Existing
  candidate/event routes remain `/transients`; no new detection capability.
- `ArchiveNavigation` is hosted once in `MainLayout`, only for the `/gallery`
  and `/archive` path segments. Capture details select Captures, observing days
  select Calendar, product details select Products. Lookalike paths do not match.
- `ResponsiveNavigation` supplies both mobile surfaces. Native `showModal()`
  gives background inertness; explicit Tab/Shift+Tab wrapping, Escape, focus
  return, route closing, desktop resize and removed-layout cleanup are verified.
  Browser testing caught and fixed a real detached-dialog JS call that terminated
  a circuit while leaving Operations. The retained acceptance test now covers
  Operations -> Events -> browser Back, not just initial rendering.
- Real account identity/settings/sign-out and antiforgery behavior remain.
  The prototype's invented "Main Fisheye online" and "Healthy" are deliberately
  not reproduced. Header subtitle remains neutral "Local CameraAgent"; wiring an
  actual deployment-friendly name or aggregate live health remains unfinished,
  rather than relabeling a sensor model as the camera's name.
- Every prototype Operations destination is present. Unavailable entries are
  disabled buttons, have no href, and carry accessible reasons plus visible
  owning issue numbers: Observatory #1010, Focus Assistant #1017, Transients
  #1026, Delivery #1027, Storage & retention #1029, Health & diagnostics #1030,
  System control #1031, Software & catalog #1032.
- Supported routes and labels (including Sky map & catalog, processing
  executions, named graphs, Data & storage, quarantine and System) remain. These
  additional entries make the rail longer than the prototype; they are not
  deleted or silently mapped to unavailable replacements. All six requested
  groups are present, and the scrollable rail exposes the lower groups.
- The neutral footer is retained for local clock, workspace and offline-asset
  information. Its old green clock/status pill treatment is removed. The
  prototype has no equivalent global footer, so this is an explicit preservation
  boundary, not a pixel-exact claim.
- No `Components/Pages` files, account authority, capture profile, recipe,
  persistence, stage policy, camera identity or backend capability changed.
  Archive pages retain their existing body-level links and page-specific spacing;
  their separate fidelity issues own those internals. Current Sky images prove
  this shared shell only, never #1035 overlay/inspector fidelity. Operations body
  fidelity remains #992; Events body fidelity remains #1023/#991.
- Existing page-specific radii, legacy theme vocabulary and body controls were
  not destructively restyled globally. Shared tokens and PageHeader changed, but
  this is not a claim that every application card/control is now prototype-exact.

## Validation

Tier B. Selector:
`./scripts/ci:classify pull_request b9da049ed4e1d06f9c73c5652d4ff73e392f067e HEAD`.
For the code candidate it reports `mode=full`, `complete=false`,
`cameraagent=true`; all other component and deployment flags are false.
This selects the CameraAgent lane, not a complete local solution test matrix.

| Gate | Result / source boundary |
| --- | --- |
| Focused OperatorRoute/OperationsLayout/SharedNavigation tests | 12/12, Release warnings-as-errors, rerun after lifecycle fix |
| CameraAgent Unit with invalid Docker endpoint | 1930 passed, 1 macOS-only skip, 1931 discovered, final behavior at 54336d7c |
| CameraAgent acceptance Unit | 27/27 |
| CameraAgent storage Integration | 219/219 |
| CameraAgent standalone acceptance Integration | 7/7 |
| CameraAgent host Integration | 21/21 |
| Retained `ObservatoryShellAndAccountPagesRemainLocalResponsiveAndKeyboardReachableAsync` | 1/1 at 8eef6b49; actual browser, including modal containment and Events/back |
| Disposable final-image browser | Pass at 8eef6b49; 24 viewport/route overflow checks, both menus, back navigation, direct detail/day routes, zero page/console errors |
| Category discovery audit | Pass: Unit=3742, Integration=672, Manual=116, Soak=1, External=0, Hardware=1 |
| `docs:audit-operations` | Pass; owning inventory 1928 -> 1931 and runbook Unit total 3739 -> 3742 |
| `ci:shell-syntax` | Pass, 150 scripts under Bash 5.2 |
| `test:ci-classification` | Pass |
| `test:coordination-guard` | Pass |
| `test:pr-review-tools` | Pass |
| `package:audit` | Pass, no vulnerabilities; six exactly reviewed deprecated occurrences |
| CameraAgent Debug and Release builds | Pass, warnings-as-errors |
| Solution Release build | Pass, warnings-as-errors; needed to provide all assemblies for category discovery |
| Solution `dotnet format --verify-no-changes` | Pass at 1f30d840; affected host/acceptance projects rechecked after later changes |
| Final Docker build/publish | Pass, CameraAgent plus self-contained replay runner; exact source/image above |
| `git diff --check` | Pass |

The initial Unit invocation and solution format invocation each exceeded a
120-second tool timeout; both were rerun successfully with larger limits. The
first category audit lacked unbuilt assemblies; a Release solution build and
repeat audit resolved it. The first screenshot image had a non-SHA revision label
and was rejected; the following in-place upgrade was also refused by the
installer's migration contract. Evidence uses normal fresh disposable installs,
not altered contract labels or a bypass.

Unaffected integration, script and package results are retained across the
later shell-only CSS/JS corrections; focused/browser checks cover those deltas.
The selected lane's protected coverage thresholds and protected CI have not run.
No independent review has occurred. These remain required before advancing the
local candidate through the main owner's PR lifecycle.

## Correction Evidence

Correction code and screenshot source:
`813f211c915a5a07fea06450066e3c755a412b00`. The correction range starts at the
independently reviewed `441cf57ade1ba19b244ffaa149f62dc23c524150`; this evidence
update is documentation-only. The full initial review range was
`b9da049ed4e1d06f9c73c5652d4ff73e392f067e..441cf57ade1ba19b244ffaa149f62dc23c524150`.

The main owner relayed the complete supplied review summary, REQUEST CHANGES
with four P2 findings, in
[issue comment 5813156146](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1034#issuecomment-5813156146).
The delegate was not supplied the independent execution identity, provider,
model or reasoning effort; those values remain explicitly unknown, not inferred
from main's forwarding identity. Main must attach that provenance before claiming
review convergence. The retained local summary is
`/tmp/opencode/1034-shell-evidence/initial-review-441cf57a.md`.

### Finding Dispositions

These are implementation dispositions, not independent verification of the fixes.
All four findings must be individually verified by the correction reviewer.

1. **F1, P2: delayed import/initialization can outlive disposal. Corrected.**
   Initialization owns its module and callback reference locally until both
   awaits complete, checks disposal after each await, and releases late resources
   without installing them on the disposed component. Disposal is idempotent;
   callbacks do not render after disposal. JS rejects null/detached panels and
   toggles. Two delayed-interop bUnit cases separately suspend import and
   initialization, dispose, then prove no invalid callback use and exactly one
   module release; the initialization case also proves JS cleanup. The real
   browser calls `initialize` with null and detached elements safely.
2. **F2, P2: route-close callback steals FocusOnNavigate heading focus. Corrected.**
   Route close suppresses explicit toggle restoration; Escape/cancel still
   restores it. Normal anchor activation closes native modality synchronously
   before Blazor focuses the destination heading. The real browser checks both
   menus and explicitly orders `close(false)`, heading focus, then the native
   queued close event, asserting that heading focus remains.
3. **F3, P2: first-dialog assertions select the shell rather than confirmation.
   Corrected.** Both assertions evaluate their existing `dialog.confirmation`
   locator and require native modality plus contained active focus. The new
   focused browser scenario invokes the actual existing capture pause/resume and
   seeded artifact-quarantine abandon helpers, including Escape/focus-return and
   confirmation actions. It does not substitute a fake confirmation dialog.
4. **F4, P2: action blue gives only 3.58:1 normal-text contrast. Corrected.**
   Text-bearing `--hvo-accent-strong` is restored to `#2563eb`; decorative
   `--hvo-accent` and the prototype soft-blue palette remain. Computed browser
   styles for the primary button and both selected segmented-state forms report
   `#f8fafc` on `#2563eb`, 13.12px/600, **4.939955:1**. Accessibility takes priority
   over the prototype's lighter action fill; no blanket styling waiver is used.

### Shared Geometry

The review additionally found that the initial measurements only proved the
Calendar desktop subnav, not Captures or the mobile inset. Captures uses the
prototype's padded `.site-main` while Calendar/Products use `.archive-main`.
`ArchiveNavigation` now applies that route-specific top padding/max-width and
the respective mobile media-query gutters. No page-body files were edited.

| Subnav boundary | Initial candidate | Prototype | Corrected |
| --- | --- | --- | --- |
| Captures desktop y | 65 | 102.44 | 102.44 |
| Captures mobile y | 64 | 80 | 80 |
| Calendar/Products mobile x | 12 | 10.39 | 10.39 |

The retained browser scenario asserts x/y with 0.1px tolerance on all three
archive routes at 1440x900, 390x844 and 320x844. The disposable-image probe also
compares x/y/width/height to the actual prototype measurements at those widths.
All 18 route/viewport no-overflow checks pass. Actual name/aggregate header health
remain unwired and incomplete acceptance, as the review explicitly allowed for
this correction. The actual account and neutral footer remain disclosed
preservation boundaries, not invented prototype state. Current Sky/other page
internals remain outside this issue's acceptance claim.

### Corrected Artifacts

Under `/tmp/opencode/1034-shell-evidence/`, without overwriting the initial PNGs:

- `correction-{sky,events,operations,captures,calendar,products}-{1440x900,390x844}.png`:
  twelve new exact-image direct-navigation screenshots.
- `correction-{site-menu,operations-menu,route-heading}-390x844.png`: three
  interaction screenshots including real destination-heading focus.
- `correction-measurements.json`: source/image identity, eighteen geometry and
  overflow observations, computed contrast, image dimensions, focus result,
  zero page/console errors, and screenshot SHA-256 hashes.
- `correction-browser.mjs`: reproducible probe. It privately reads only the
  disposable instance's credential and never prints it.
- `browser-results/correction-browser.trx`: focused real browser regression;
  `unit-results/correction-unit.trx` and `integration-results/correction-*.trx`:
  candidate test results.

Image `sha256:7dd7dc3385a61c2827a582a6054931283fd9645427af93d06fc7b7e9c1682250`
declares source `813f211c915a5a07fea06450066e3c755a412b00`. Instance UUID
`10340000-0924-4000-8000-000000000006` uses the same isolated product root,
loopback `:5234`, bundle, profile, observer and schedule inputs as the initial
evidence. Real retained catalog-backed frames/layers remain present. No primary
instance, pointer, configuration, data, credential or live deployment was changed.
The correction instance was preserve-by-default uninstalled after capture; no
disposable container remains running and private retained state is not published.

### Correction Gates

Selector `./scripts/ci:classify pull_request 441cf57ade1ba19b244ffaa149f62dc23c524150 HEAD`
reports `mode=full complete=false cameraagent=true`, all other component and
deployment flags false. The full PR range against freshly fetched
`origin/development/v1` selects the same lane. Target tip at the check was
`7040f216258c22834f54279b048ca5401c560a17`; merge base remains `b9da049e`.
No final target synchronization has been performed or claimed.

| Correction gate | Result |
| --- | --- |
| Focused shell Unit | 14/14, including delayed import and initialization disposal |
| CameraAgent Unit, invalid Docker endpoint | 1932 passed, one macOS-only skip, 1933 discovered |
| Acceptance Unit | 27/27 |
| CameraAgent storage Integration | 219/219 |
| Standalone acceptance Integration | 7/7 |
| Host Integration | 21/21 |
| Existing actual-browser shell test | Pass |
| New `SharedShellReviewRegressionsAsync` | Pass: focus ordering, null/detached initialization, both real confirmation flows, contrast, archive boxes/overflow |
| Exact-image disposable browser | Pass: 15 screenshots, 18 geometry/overflow observations, both route-heading focus transitions, 4.94:1 action contrast, no page/console errors |
| Category discovery | Pass: Unit=3744, Integration=672, Manual=117, Soak=1, External=0, Hardware=1 |
| Four CI-control guards | Pass |
| Affected host/Unit/acceptance format | Pass |
| Release build and exact-SHA Docker publish | Pass, warnings-as-errors in affected test builds |
| Documentation audit / diff check | Pass |

The initial correction-browser attempt used central integration disabled, which
hides artifact quarantine; using the existing isolated fixture's central-enabled
mode made the actual seeded confirmation workflow available. A geometry probe
initially sampled before interactive rendering; it now waits for the visible
subnav. Neither fix weakens the assertion. Category discovery exceeded the
120-second tool limit while gates competed for CPU, then passed with a larger
limit. Initial package/architecture evidence is retained because those boundaries
are unchanged. Independent correction review, protected coverage/CI, and final
base synchronization remain pending. The PR must remain draft.
