# Issue #1644 browser and retained-read evidence

Production revision: `d579532bf553129cd994fb4f22838f8daede5f0a`, targeting
`development/v1`. This delegated change contains tests, evidence and documentation
only. The parent owns the complete Tier C candidate, PR/review and the
**Development v1 / Preflight** and **Development v1 / Build and Unit** protected
profile. This record does not claim production or physical qualification.

## Reproduction and fixture boundary

Follow the [retained astrometry runbook](../runbooks/retained-astrometry.md#browser-and-read-resource-acceptance)
for pinned Playwright installation, fixture generation and the explicit Manual
selection. All heavy builds/tests used `flock -o /tmp/hvo-heavy-gate.lock`, a
bounded `timeout`, SDK **10.0.402**, Release, `-nodeReuse:false`,
`-p:UseSharedCompilation=false`, an unset `HVO_PRODUCTION_CATALOG_BUNDLE` and
`DOCKER_HOST=unix:///tmp/hvo-no-docker.sock`. Fixture validation finished outside
the heavy lock before browser launch. Actual commands/logs remain in the scratch
roots below; no producer benchmark was repeated.

The canonical generator uses the real seeded 512 x 512 Mono16 workload: 560
catalog rows and a one-second exposure. It normalizes times to milliseconds and
uses native raw-ingress paths **before** generating scene, assessment and solution
identities. It also runs the real raw reconciler, requiring one inspected capture
and zero quarantined/missing entries. Existing durability/resource callers keep
the helper's original default mode.

A reusable browser helper copies the completed canonical tree without rewriting
its journal, envelope, descriptor, pixels or numerical products. Acquisition,
distribution, retention and autonomous derived processing are paused only in the
test host. Raw startup recovery, authenticated Identity, gallery, artifact reads,
retained reconstruction, composition, Razor and JavaScript remain real.
Original and prepared-host SHA-256 baselines cover every non-database file;
SQLite runtime state is excluded from byte comparisons. Canonical raw, fixture
raw aliases, base image and all existing retained products remain byte-identical
after read/render/export. Prepared baselines also name preserved host roots.

## Executed gates

[Machine-readable runs and exact case names](evidence/issue-1644/gate-results.json)
record passes and failures independently:

| Selection | Passed / executed |
| --- | ---: |
| Canonical generator plus existing durability, stale/source and authorization guards | 33 / 33 |
| Canonical viewer tests without evidence environment, including source invalidation | 5 / 5 |
| Diagnostic sanitization, default discard, opt-in success retention and lifecycle | 3 / 3 |
| First complete functional browser acceptance | 5 / 5 |
| Corrected whole-route CPU browser acceptance | 5 / 5 |

The first functional pass remains valid. Its archive whole-route `TaskDuration`
delta crossed a navigation reset; the corrected run uses monotonic renderer
`ProcessTime`. Within-document toggle/calculate `TaskDuration` remains valid.
The [selector output](evidence/issue-1644/classifier.txt) comes from
`scripts/ci:classify pull_request d579532bf553129cd994fb4f22838f8daede5f0a HEAD`:
`mode=full`, `complete=false`, `cameraagent=true`; all other component,
deployment, delivery, secret-scan and release-script flags are false. The local
affected selection is CameraAgent; this does not authorize full qualification
or change the v1 protected profile. The parent owns the full issue gate.

No browser case was skipped or Inconclusive. The five new Manual cases change
CameraAgent acceptance inventory from 88 to 93 and global Manual totals from
288 to 293; Unit and Integration inventories are unchanged.

## Screenshots and behavior

The inspected [Current Sky](evidence/issue-1644/current-residual-calculator.png)
and [archive](evidence/issue-1644/archive-residual-calculator.png) screenshots show
the accepted retained mapping, 512-square processed image, residual layer and
calculator result. [Image detail](evidence/issue-1644/residual-image.png) shows
fitting/held-out residual rings in green/amber. Their radius keeps subpixel
residuals locatable; predicted-to-measured vectors are not magnified. The
[card detail](evidence/issue-1644/calculator-card.png) labels the geometry
`MeasuredFit`, reports 82 inliers / 24 held-out sources and explicitly withholds
point uncertainty. The score is engineering quality, not probability.

The browser independently enables associations with residuals off, then residuals
with associations off, and checks actual SVG group display through the real
JavaScript handlers. The [off image](evidence/issue-1644/current-off.png) provides
a visual baseline. Geometry, expected catalog context and predicted diagnostics
retain separate labels; no accepted mapping is inferred from configured geometry.

For `X=280, Y=240`, the visible result is RA **21.89893666 h**, Dec
**−10.55546664°**, altitude **78.41909658°**, azimuth **204.14570741°** at the retained
capture epoch. Both equatorial and human-visible horizontal inverse inputs return
280/240 within `1e-7` pixels in Current Sky and archive. The saved residual-only
[frozen export descriptor](evidence/issue-1644/frozen-export.json) retains
512 x 512 dimensions, exact selected source IDs and the durable payload checksum;
the authenticated download matches the stored bytes. Base/raw hashes are unchanged.

[Rejected](evidence/issue-1644/rejected.png) and
[no-data](evidence/issue-1644/no-data.png) examples show disabled calculators and
measured controls; no-data explicitly says no assessment was retained. Unavailable
and budget cases also pass. All five cases check anonymous 401 and nonowner 403.
The [390px view](evidence/issue-1644/narrow.png) stacks controls without horizontal
page overflow, and the accepted calculator passes labeled X-to-Y keyboard tab
order. This is focused accessibility evidence, not a complete accessibility audit.

## Read and render resources

[Raw samples and structural limits](evidence/issue-1644/read-resources.json) use
Ubuntu 24.04.5 x64, .NET 10.0.13, 12 logical processors (i9-14900K KVM host),
Playwright 1.63.0 / Chromium 153.0.8010.12, concurrency one. Each operation has
one first instrumented call and five warm samples. Login already warmed the host;
these are not cold OS-cache samples. Base-only composition omits optional
astrometry on the same immutable image; this measures feature cost at one
revision, not a historical speedup.

| Warm operation (n=5) | Max wall ms | Max host CPU ms | Max allocation MiB | Max positive RSS growth MiB | Max logical read MiB | Artifact opens / declared bytes | Response/SVG bytes |
| --- | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| base-composition | 4.01 | 6.81 | 0.27 | 0.09 | 0.47 | 2 / 262,819 | 221 |
| retained-mapping-diagnostics | 145.92 | 233.04 | 28.97 | 17.40 | 2.38 | 5 / 949,966 | 0 |
| measured-composition | 122.04 | 163.67 | 31.51 | 0.41 | 3.34 | 10 / 1,253,655 | 27,982 |
| authenticated-astrometry | 110.12 | 180.54 | 29.04 | 0.39 | 2.42 | not instrumented at HTTP boundary | 18,814 |
| authenticated-presentation | 152.63 | 226.81 | 31.87 | 0.80 | 3.43 | not instrumented at HTTP boundary | 1,480 |

Each warm sample must satisfy **<500ms wall/host CPU**, **<100MiB allocation**,
**<128MiB positive RSS growth**, **<64MiB logical/physical process reads**,
**≤16 artifact opens / ≤16MiB declared artifact bytes**, and **<1MiB response**.
Retained reconstruction additionally requires ≤5 authenticated opens and ≤4MiB
for this fixture (production total payload ceiling 44MiB). The table reports
sample maxima, not population percentiles or peaks between observations.

Host CPU/allocation/RSS and Linux `/proc/self/io` include the test caller and host
infrastructure; they exclude Chromium/driver processes. Artifact counts instrument
the real service boundary, excluding HTTP-operation internals and persistence's
direct restore reads; process I/O includes those. Zero physical reads means OS
cache hits, not zero storage work. Full logical/physical writes and syscall counts
are retained in the JSON.

| Route (one sequence each) | To interactive image ms | Screenshot off / residual ms | Renderer process CPU ms | Toggle/calculate task CPU ms | Sampled ending JS heap MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Current Sky | 648.55 | 301.32 / 311.66 | 2970 | 332.66 | 7.94 |
| Archive | 461.17 | 398.41 / 291.84 | 3250 | 430.99 | 12.80 |

Navigation must reach the real 512-square image/interactive shell in <10s;
full-page screenshots must finish in <5s. Renderer **process-wide** CPU includes
all its threads and automation/screenshot activity within the sequence, excludes
the browser/GPU/driver processes, and must remain monotonic and <5s per route.
Within-document toggle/calculate task CPU is separately <5s. Sampled JS heap
must be <64MiB. Process-wide host UI CPU/allocation/RSS before/after each sequence
are recorded separately in the [browser evidence](evidence/issue-1644/accepted-browser.json).
These multi-request UI totals are not the per-read 100MiB allocation ceiling.

The retained reader permits one reconstruction with no queued raw buffers,
32MiB raw / 44MiB total / 128KiB solution bounds, and never solves/refits.
Composition limits remain 64 cache entries / 16MiB cache, 16MiB source payload,
2MiB SVG and 20,000 SVG elements. These structural limits complement the measured
small workload; no full-resolution or physical accuracy claim follows.

## Diagnostics, failures and retained roots

The committed [sanitized diagnostics](evidence/issue-1644/browser-diagnostics.json)
and [representative trace](evidence/issue-1644/accepted-owner.trace.zip) contain
metadata-only event timelines. They contain no cookies, request bodies, storage,
raw console text or authenticated replay snapshots. The security tests cover
secret sentinels in both failure and opt-in success capture. Raw traces are
discarded in the capture scope. Real product screenshots are captured separately.
All browser page-error counts are zero.

The partial fixture omits the Combined-lineage thumbnail representation:
owner logs include HTTP 415 image responses and its tile says Preview unavailable.
This does not affect the verified processed image, calculators, selected layers
or export. Calibrated/raw display stages are also explicitly unavailable in this
minimal graph. Host shutdown logs include existing `MainLayoutFooter` clock
`ObjectDisposedException` entries after service-provider disposal; no in-page
exception or failed product assertion resulted. These diagnostics are disclosed,
not presented as a clean application log; no unrelated production fix was made.

| Registered root | Retained content |
| --- | --- |
| `/tmp/hvo-skymonitor-1644-viewer.aRxEkM` | Untouched original incompatible solver/read fixture trees |
| `/tmp/hvo-skymonitor-1644-browser-evidence.PuHwuP` | First compile failure, bounded prerequisite waits, first five failed browser cases and diagnostics |
| `/tmp/hvo-skymonitor-1644-browser-fixed.WVnv6Q` | Path-only import attempt: all five failed due synthetic sub-ms timing; source integrity record |
| `/tmp/hvo-skymonitor-1644-canonical-viewer.hLz32w` | Five canonical real solution trees, generator/guard33-case TRX and command/log |
| `/tmp/hvo-skymonitor-1644-canonical-browser.vgOybz` | Standalone5-case pass; SVG-byte compile failure; canonical browser4-pass/1-selector-failure; preserved copied hosts |
| `/tmp/hvo-skymonitor-1644-browser-final.QoEN5t` | Functional5/5 and diagnostic3/3 passes, screenshots, hashes, resources, frozen export, safe traces and preserved hosts |
| `/tmp/hvo-skymonitor-1644-browser-final2.EWOLfw` | Corrected process-CPU run, final screenshots/resources, focused format/discovery and preserved hosts |

The first two executed attempts used the old fixture's automatic host-copy
cleanup; their registered result roots, inputs, logs and TRX remain. Later host
copies explicitly opt into preservation. The first 40-minute prerequisite timeout
executed zero tests and held no heavy lock; the authorized second bound completed.
Fixture path/timing incompatibility and the ambiguous test selector were corrected
without relaxing product assertions. The original producer roots were not repaired
or deleted. Failed runs are never counted as passing acceptance.
