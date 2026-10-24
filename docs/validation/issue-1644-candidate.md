# Issue #1644 local candidate evidence

The protected target profile is **Development v1 / Preflight** and
**Development v1 / Build and Unit**. This record describes local Tier C evidence
and its known qualification limitation; it is not production qualification.
[Viewer/browser/resource evidence](issue-1644-retained-viewer.md) supplies the
explicit software acceptance, screenshots and reproducible virtual captures.

The complete candidate ran on `d579532bf553129cd994fb4f22838f8daede5f0a`.
Browser/evidence and test-only corrections were integrated as `fadb8b39` and
`b4d21638`. Target `d82abb9cf30c0bc45cbc35029ef322429d5919c7` was merged as
`e84cf6c57b541aebb5f22124e92d89b4fc8cf3eb`; that synchronization changed only
catalog qualification scripts/documentation. The production tree remains
`d59c76b28e4ab6169d493960b13670bec530f55e`. The final source/test trees are
byte-identical to the isolated corrected candidate; package, SDK and solution
inputs are unchanged. No reviewed history was rewritten.

## Selector and execution

`scripts/ci:classify pull_request <target> <candidate>` selected `mode=full`,
`complete=false`, shared/CameraAgent/LogicHost/combined/delivery true and all
deployment, release-script and secret-scan-contract flags false. The final
synchronization delta separately selects `complete=true` and deployment because
of the upstream catalog qualification controls. That does not authorize a full
workflow dispatch for this v1 PR. The complete Tier C gate was executed locally;
no full `ci.yml` workflow was dispatched.

SDK 10.0.402, Bash 5.2 and Release test execution were used. Heavy steps ran under
`flock -o /tmp/hvo-heavy-gate.lock timeout ...`, with MSBuild node reuse and shared
compilation disabled. Unit runs unset `HVO_PRODUCTION_CATALOG_BUNDLE` and use an
invalid Docker endpoint. Integration uses actual Docker where required.

Completed evidence:

- Pinned tools/package restore, warning-clean whole-solution Debug and Release
  builds, formatting verification and package audit passed.
- All four CI-control guards passed on the candidate and again after the final
  synchronization. The operations documentation audit and the upstream focused
  catalog-control regression also pass.
- Whole-solution category discovery passed on the original candidate. The
  added Manual browser selection was separately discovered as 93 CameraAgent
  acceptance cases, and inventories/documentation agree on Unit=14518,
  Integration=998 and Manual=293.
- Complete composed Unit evidence: **14394 passed, zero failed, 124 skipped**,
  across all 21 selected projects. The two CameraAgent/LogicHost platform/provider
  skips and 122 absent-production-catalog prerequisites remain explicit skips.
- Complete composed Integration evidence: **998 passed, zero failed, no skips**,
  including all seven Architecture & Publish cases and the five retained viewer,
  source integrity and frozen export scenarios.
- Host boundary/publish evidence, x64/ARM64 installer and runner publishes,
  checksums, ELF/capability verification, both canonical migration counts and
  both pending-model checks passed. All 29 test-exclusive containers were reaped.

## Corrections and coverage provenance

The original complete selection retained three failed component UI cases whose
helper reran a cold numerical solve and received BudgetExceeded. Accepted UI
input now loads a checksum-pinned, parser-validated actual retained solution and
its exact calibration. Numerical producer and real browser coverage remain
separate; no production solver budget or acceptance threshold changed.

The original storage Integration selection retained one failed wrapper-reference
assertion after the cache began reattaching fresh evidence. Its replacement
checks SVG memory/content/checksum/manifest/presentation reuse and a fresh
NotRetained-to-Unavailable evidence snapshot, preserving selected-stack lineage,
audit, publication and replay assertions.

The complete affected covered replacements pass: CameraAgent Unit **7459 passed,
one expected macOS-only skip**, and storage Integration **324 passed**. These two
reports replace only their two canonical slots. All original failures and all raw
replacement reports remain preserved; the other green suites were not repeated.
Only the fresh reports' single source-root path was normalized for the parent
checkout. Production source bytes and coverage measurements were preserved.

Exactly 23 canonical reports were selected with per-slot hashes. The reconciled
CameraAgent floor passes at **90.1829% line / 76.8335% branch**. The unchanged
shared, LogicHost and combined component floors pass. Reconciled aggregate is
**88.8902% line / 75.2023% branch**.

**Delivery and aggregate enforcement remain failed** solely at the unchanged
catalog-absent ReleaseTool `Program.cs`: **63/155 lines, 31/88 branches** (line
floor 50%). This reproduces the prerequisite #1643 limitation. No coverage floor
was weakened and no passing full-qualification claim is made. The ReleaseTool,
its tests and all coverage baselines are unchanged by this issue.

## Retained evidence

Registered scratch roots preserve exact commands, UTC timing, original and
replacement TRX, raw reports, hashes, output manifests and failed attempts:

- `/tmp/hvo-skymonitor-1644-candidate.KCh4NA`: complete candidate `summary.json`,
  `handoff.txt`, 28 TRX reports, 23 canonical coverage slots, publish/migration
  evidence, original failures and container cleanup.
- `/tmp/hvo-skymonitor-1644-candidate-corrections.RpaGaA`: deterministic fixture
  provenance, corrected full affected reports and `replacement-evidence.json`.
- `/tmp/hvo-skymonitor-1644-reconciled-evidence.q32HCJ`: exact 23-slot
  `canonical-selection.json`, two replacements, merge/enforcement logs and
  `results.json`; original files are untouched.
- `/tmp/hvo-skymonitor-1644-owner.akg3on`: final selectors, synchronization and
  local guard evidence. The browser report links its own preserved roots.

These are validation/evidence artifacts, retained separately from disposable
runtime state. Independent exact-range review and fresh post-ready v1 CI are
subsequent merge gates owned by the PR owner.
