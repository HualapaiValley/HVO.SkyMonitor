# CameraAgent smoke regression audit (#1389)

Audit date: 2026-11-08 UTC. Source: `development/v1`
`16545df686f3abe6df357545278bbc61dc29ea4b`, also the initial topic merge base.
Recent feature PR #1708 confirms this target branch. This is one operator-requested
batch; no merge, deployment or installed-instance operation is included.

## Current issue inventory

The tracker has no native GitHub sub-issues. Its body and complete comment thread
were reconciled with current issue states using `gh`, including the referenced
UI parents' implementation slices. Its October 22 status index is historical:
#1387 (Frame Facts), #1388 (independent deep-sky catalog/layer), #1475
(mixed-gain stacking), and #1490 (changelog placement) are now closed. The
historical direct installer, overlay, startup, paging, redaction and build
source defects listed as delivered remain closed; they are not reopened here.

Still-open references with remaining acceptance relevant to the tracker:

| Issue | Current remaining scope and disposition |
| --- | --- |
| #1386 | Typed filter/overlay configuration parent. Referenced slices A–T are closed. Fix the delivered rolling/scene panel collision below; remaining families and complete parent acceptance stay open. |
| #1016 | Observing Day fidelity and current real-date desktop/mobile interaction/visual acceptance. Fix stale day navigation below; full visual acceptance stays open. |
| #1393 | Satellite planning/layers. Planner and exact-exposure publication are delivered; wider association, cadence and declared-hardware accuracy remain with their owning work. No further reproducible planner defect found in this audit. |
| #529 | Broader capture-time satellite integration and scientific acceptance; credit delivered #1641/#1649 rather than rebuilding it. |
| #527 | Remaining orbital population/source policy, physical uncertainty and architecture/resource qualification. The approved bounded source/propagation checkpoint is delivered. |
| #523 | Measured astrometric solutions and pixel/sky consumer capability with its own numerical acceptance. |
| #526 | Wider measured-association qualification, retained scientific findings and existing owner. Its protected product foundation is delivered. |
| #1169 | Separate astrometry-registered stacking feature, dependent on accepted measured solutions. |
| #1004 | Evidence-backed cloud profile/reference integration; Roy assigned a concurrent owner on `kepler/w1-1004-cloud-layer`. Preserve that claim and its virtual acceptance. |
| #158 | Environmental provider/device planning, distinct from the existing simulated source. |
| #1089 | Local station adapter selection and qualification. Existing deterministic virtual environment remains explicitly simulated. |
| #1092 | Weather-provider capability and accepted source decisions; implementation has a recorded hold. No existing weather adapter defect is established by the absent feature. |
| #1208 | Actual deployed runtime/crypto servicing receipts; package migration is complete and ownership is retained elsewhere. |
| #1511 | Backlog audit/index, rather than an implementation defect. |

The next selected prerelease identity, retained-history Pi cold-start
remeasurement, installed setup/refresh/upgrade/resume checks, and runtime-servicing
receipts remain tracker acceptance. #1292 is now closed; its old open checkbox
is not a new repair task. Synthetic timing cannot establish physical Pi I/O/RSS
or the identity and loaded assemblies of an installed release. Fakes can test
software behavior, but cannot replace those measurements. No hardware is needed
for the two defects in this batch. Wider research/features and other owners'
claims are not silently converted into smoke fixes.

## Regressions and the missing coverage

### Graph overlay deep links

`RollingCombination` used the default `constellations` section ID. In a normal
graph containing rolling combination and a scene producer, both panels claimed
the same fragment. The existing Current Sky constellation link could land on
rolling controls.

`RollingPanelsKeepSceneDeepLinksDistinctWithRepeatedNodes` composes rolling,
scene and repeated rolling nodes with both legacy and styled scene aliases. It
checks every DOM ID for uniqueness and verifies that constellation/scene links
select scene controls while rolling panels have their own distinct targets.
Existing rolling tests used an isolated node. Existing mixed-panel anchor tests
covered materializers and scene producers, leaving this composition uncovered.

### Observing Day route changes

When a new date began loading, the heading changed but the previous view stayed
in memory. Capture/event actions and the boundary description therefore still
referred to the previous date, offering the wrong evidence under the new heading.

`ObservingDay_NavigationDoesNotOfferThePreviousDaysEvidenceWhileLoading` loads a
populated first day, delays the second read, and checks that no old actions or
boundary text remain under the new date. It then checks the second day's exact
capture/event ranges on success, or the unavailable state without stale actions
on failure. Existing component tests rendered one synchronously loaded day;
they did not exercise a pending route transition.

## Validation contract

Tier B: localized component/navigation fixes. No processing algorithm, durable
contract, profile or acquisition behavior changes. Use
`scripts/ci:classify pull_request <merge-base> <head>` as the local scope selector
and `profile=development-v1` for protected CI: Preflight and Build and Unit.
This provides v1 integration evidence, not release or physical qualification.

The PR ledger records the executed red/green evidence, final classifier output,
Release build, format/whitespace checks, full relevant suites, four CI-control
guards, independent immutable-range review and required CI result. No full
qualification workflow is dispatched for this ordinary v1 PR.
