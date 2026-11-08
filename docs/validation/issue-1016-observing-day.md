# Observing Day acceptance reconciliation (#1016)

Status: **partial implementation evidence; visual acceptance remains open**.
Target profile: `development-v1`. Tier B UI work; the operator additionally
requires the complete local Unit/Integration, warning-clean Debug/Release and
format gate. Starting target: `7d5ec63b09a67ab4b675ba6a29641013df1247d8`.

The exact design authority is `docs/prototypes/pipeline-operations/day.html`
at `b9da049ed4e1d06f9c73c5652d4ff73e392f067e`, with its styles and scripts.
Those four reference files are unchanged in the starting target. The issue's
[2026-10-20 handoff](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1016#issuecomment-6298546478)
retains the delivered #1054 page and requires current real-state acceptance.
The former #1044 tar-upgrade blocker is superseded; it is not a current blocker.

Pinned illustrated reference (`?date=2026-08-31`):
[desktop viewport](evidence/issue-1016/reference-1440x900.png),
[desktop full page](evidence/issue-1016/reference-1440-full.png),
[phone viewport](evidence/issue-1016/reference-390x844.png), and
[phone full page](evidence/issue-1016/reference-390-full.png).
[Reference geometry](evidence/issue-1016/reference-metrics.json) records viewport,
device scale, panel coordinates and provenance. Native browser images were
normalized to CSS-pixel dimensions; overlapping viewport captures were stitched
without changing document geometry. These are prototype images only, with
illustrative IDs/facts and **no production-ID mapping or production comparison**.
At 1440px the reference image/facts widths are 929.40/433.72px (ratio 2.143),
the three cards are 448.11px wide with 10.40px gaps, and the timeline begins at
y=864.18px. At 390px the image/facts stack before the timeline; the three cards
are 369.20px wide and appear in one column before events and automations.

## Measured gaps corrected

- A one-minute bin on a 24-hour period is 0.069444% of the axis. The previous
  0.15% minimum represented 2.16 minutes, and the final bin could extend past
  100%. Bands now keep their calculated widths, reject out-of-period starts,
  and clip to the half-open period. Overlapping bins merge; retained gaps stay
  empty. No display minimum is interpreted as observed time.
- Clear/usable and cloudier intervals now retain unavailable rows in the
  timeline. Absence of retained captures is explicitly distinct from cloudy
  or unusable sky. Candidate truncation has a visible range link.
- Candidate marker links have 44 by 44 CSS-pixel controls and focus outlines.
  The period's ticks use actual elapsed time, including 23/25-hour DST periods,
  with offsets when the period crosses a clock change.
- A failed representative preview retains its exact capture link and a visible
  unavailable statement. Another capture is never substituted. Its factual
  selection caption overlays the hero, and product cards stack at 650px.

The timeline has its own Razor/code-behind/scoped CSS. No product selection,
generation, state, or historical attribution was changed. PR #1728 owns the
time-lapse slot and PR #1732 owns the daily summary. Their existing product
markup and helpers are retained. The shared page wrapper, stylesheet, changelog
and count inventories will need normal integration when those PRs land.

## Criterion audit

All issue acceptance checkboxes remain unchecked pending reviewed evidence.
“Covered” below describes software regression coverage, not owner acceptance.

| Original criterion | Current software evidence | Remaining acceptance |
| --- | --- | --- |
| Archive navigation, heading, previous/next, selected range and calendar | Existing `ArchivePagesTests` cover exact return/range links, invalid dates and calendar versions; new HTTP Integration cases cover empty and invalid days. Sunrise ownership is retained, with explicit legacy noon mode. | Review desktop/mobile navigation on the pinned real snapshot. |
| Representative hero and factual health panel | Exact-day archive policy retained; new Unit preview-failure case and real VirtualSky HTTP Integration verify the representative's capture and image destination. Unknown cloud remains unassessed. | Review real hero/facts ratio, selection, quality/integration/count facts and partial states. |
| Proportional timeline, gaps, evidence times and unavailable lanes | New timeline Unit cases cover half-open boundaries, precise sparse widths, gaps, absent lanes, truncation, exact links, DST and UTC fallback. | Candidate summary is creation-time filtered and lacks observation times. It explicitly says record time, not observation time; reconcile with #991 evidence before claiming this criterion. Review real timeline geometry and historical schedule caveats. |
| Three daily product slots with durable state and artifact navigation | Delivered still-product behavior and existing product/viewer tests retained; no generator changes. | Integrate #1709/PR #1728 and #1713/PR #1732, then review actual retained IDs, source lineage/span/creation and available/unavailable states. Current delivered product UI can show more than three tiles. |
| Event preview and scheduled automations | Delivered local-candidate links and actual bounded automation-run history retained. No confirmed meteor, quality, confidence, duration, media or authoritative-review facts are invented. | Existing candidate summary is not the complete evidence-backed event preview. Reconcile independent media/classification/review availability under #991, and pin real definition/run evidence. |
| Empty/loading/error/retry, focus, phone stacking and 44px controls | Existing page tests retained; new empty-lane and preview-failure tests, explicit focus styles and phone stacking. | Browser-measure every active control, mobile order and loading/retry behavior; closely spaced timeline markers can overlap hit regions and retain the full-size candidate-list alternative. |
| Real before/reference/after at 1440x900 and 390x844 | No production evidence asserted. VirtualSky tests are automated software evidence only. | Approved real-state source, immutable snapshot, screenshots and owner review are absent. No fixture substitution is acceptable. |
| State matrix, side-by-side and pixel/overlay comparisons, interactions | Numerical timeline regression assertions retained. | Real visual comparison and interaction recording remain open; rough/source-level fidelity is not acceptance. |
| Reviewed evidence and ownership boundaries | #988 remains delivered; #993 generation and #991 events retain their ownership. | #1016 remains open; this PR references it without a closure directive. |

## Required real-state evidence packet

Before any owner acceptance, retain a manifest with the observing date, calendar
version and resolved UTC boundaries, site/timezone and fallback state,
representative capture and artifact IDs/checksums, schedule revision and creation
time, selected retained product IDs and source-period identities, candidate/event
IDs and evidence/review snapshot, and automation definitions/runs/history bounds.
Record the prototype's `date` query/state and any illustrative-ID mapping.

Capture reference, production-before and production-after at **1440x900** and
**390x844** with hero, full timeline, products and both lower panels. Keep the
same real snapshot for before/after. Cover in-progress/partial, sparse/empty,
unavailable and actually available evidence. Keep viewport and full-page images,
side-by-side, alpha overlays and pixel differences, and measure hero/facts ratio,
heading positions, timeline bounds, card gaps and mobile order. Record keyboard
and pointer previous/next, exact capture/event/product/automation destinations,
range preservation, retry and return-focus behavior. Explain data differences
separately from layout differences. Never populate production with test fixtures.

No deployed CameraAgent container or existing retained #1016 evidence packet was
found on this work host. The operator prohibits deployment, production restart,
networking and secret changes; this work does not request or perform those
actions. An approved existing read-only source or later owner-run supported
rollout is needed for the real before/after packet. No acceptance or production
qualification is inferred from local tests or development-v1 checks.

## Reproduction and retained local evidence

Use SDK 10.0.402 and `HVO.SkyMonitor.v9.slnx`. Run the classifier on the committed
review range; retain its output with the gate ledger. The full local gate is the
block in `AGENTS.md`, with canonical `tests/coverage.runsettings` and XPlat
coverage for both selections. Unit must pass with
`DOCKER_HOST=unix:///tmp/hvo-no-docker.sock`; Integration uses Docker.

Focused tests:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --configuration Release --filter 'FullyQualifiedName~ObservingDayEvidenceTests|FullyQualifiedName~ArchivePagesTests'
dotnet test tests/HVO.SkyMonitor.CameraAgent.IntegrationTests/HVO.SkyMonitor.CameraAgent.IntegrationTests.csproj --configuration Release --filter 'FullyQualifiedName~ObservingDayEvidenceIntegrationTests'
```

The Integration cases use the real in-process host and SQLite archive. The
available-preview case waits for normal VirtualSky acquisition, checks the
selected period, follows the thumbnail's real retained-artifact redirect, opens
the exact capture page, and verifies the artifact belongs to that capture.
This requires no physical camera, production state or live provider.

The PR's append-only ledger records the exact head, classifier, full gate
results/counts and independent review. Local logs, TRX and coverage are retained
under `TestResults/issue-1016/` in this worktree; they are not production visual
acceptance evidence.
