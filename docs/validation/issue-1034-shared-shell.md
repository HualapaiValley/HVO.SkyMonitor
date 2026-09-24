# Shared Shell Candidate Evidence (#1034)

Local candidate, not visual acceptance of the page bodies and not authorization
to push, open a PR, merge, or deploy. Independent exact-range review is pending.

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
