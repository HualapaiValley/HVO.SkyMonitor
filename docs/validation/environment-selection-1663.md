# Retained environment observation selection (#1663)

This Tier B change exposes the existing `EnvironmentalKinds` input on the
`EnvironmentPresentationLayer` graph panel at its unchanged
`metadata-corner-presentation-v6-original-colours` implementation. It builds on
#1621's retained-layer panels, #1623's nullable intensity, #1626's enum collection
codec/editor and #1660's separate scene styling. There is no producer version,
acquisition, storage, calibration or deployment change. Parent #1386 remains open.

The target profile is `development-v1`: Preflight and Build and Unit, after
independent review convergence. This is integration evidence, not production
qualification. The classifier on the implementation range from `409a5b02` reports `mode=full`,
`complete=false`, `shared/cameraagent/logichost/combined/delivery=true`, and
`deployment/secret_scan_contract/release_scripts=false`. Its affected boundaries
select shared contract and CameraAgent form/service/graph/producer checks for
the Tier B local gate; protected v1 CI supplies the complete Release/Unit gate.
No separate full-qualification workflow is authorized or dispatched.

## Compatibility and operator behavior

An omitted member displays the existing six code defaults: AirTemperature,
RelativeHumidity, AtmosphericPressure, WindSpeed, RainState and CloudCover.
An explicit empty list retains no environmental readings and displays
`ENVIRONMENT / NOT RETAINED`. All twelve existing kinds are available. The
producer continues to sort and deduplicate effective kinds; the editor retains
source order, duplicate occurrences, numeric/string enum spellings, unknown
members and unchanged bytes. The consumer has no occurrence-count limit, so
repeated source values are not rejected at twelve occurrences. Existing graph
JSON size limits still bound the input, and there are at most twelve effective
kinds. Other enum collection consumers keep their existing occurrence bounds.

The shared tests cover every kind, undefined numbers and names, invalid shapes,
absent/empty/subset, source token preservation and host-neutral compilation.
The native form tests cover accessible labels, deep links, unchanged/restored
edits, explicit empty, invalid preview invalidation and opening invalid retained
source without silently repairing it. Existing explicit corrections remain
possible through the same codec. Service tests reject unauthorized operations
and invalid kinds before persistence.

The lifecycle fixture uses the real SQLite graph coordinator, real retained
environment store and association service, and actual Environment producer.
It creates and validates immutable subset and empty drafts, rejects early and
stale activation, explicitly activates each, and rolls back to the original
selection. It checks selected observation and association input hashes,
unselected-reading exclusion, retained revision bytes and restored output
identity. Shared reconstruction from the retained metadata facts reproduces
the original layer bytes without a LogicHost reference. Existing missing,
stale, conflict, rich-fact and cross-host rendering tests run in the affected
selection as regression evidence.

All selections reuse one immutable retained capture association policy. The
selected observation/association hashes keep their original identity; the set
of canonical inputs shrinks with a subset. The layer then binds the newly
selected metadata-facts artifact, whose immediate identity changes while the
upstream scene/stack source IDs remain unchanged. Creating competing policies
for the same capture would correctly fail as ambiguous and is not needed here.

The fixtures use fixed UTC time and observation IDs with measured temperature
and stale humidity; unavailable SkyQuality stays missing. No hardware or live
provider runs. Provenance remains tied to the retained observation content.
Selecting a kind does not acquire or invent data. All-kind facts retain twelve
entries while the corner keeps its existing eight-line/64-character bounds;
the heading consumes one line. Image-fit rules can reduce or omit text.

## Reproduction

Create an evidence directory with `hvo scratch HVO.SkyMonitor 1663 evidence`.
After a Release build, the focused fixtures can be regenerated there:

```bash
EVIDENCE_ROOT="$(hvo scratch HVO.SkyMonitor 1663 evidence)"
flock -w 900 -o /tmp/hvo-heavy-gate.lock timeout 600 env \
  -u HVO_PRODUCTION_CATALOG_BUNDLE DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  DOTNET_PROCESSOR_COUNT=2 dotnet test \
  tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --no-build --configuration Release -nodeReuse:false -p:UseSharedCompilation=false \
  --filter 'FullyQualifiedName~EnvironmentSelection' \
  --logger trx --results-directory "$EVIDENCE_ROOT"
```

The TRX attachments include actual rendered component HTML, canonical layer and
metadata-facts JSON, canonical input identities, full-size diagnostic PNGs and
bounded resource receipts. Source pixels are a uniform value-12 Mono8 canvas
at 1936×1216. Tests verify source bytes remain unchanged, unrelated corner
blocks remain equal, and rollback reproduces the prior layer bytes. Graph
coordinator and producer seams are exercised together; this is not a deployed
host or new whole-capture durability campaign.

## Browser and image evidence

The [desktop default](environment-selection-1663/browser-default-desktop.png),
[mobile default](environment-selection-1663/browser-default-mobile.png),
[mobile subset](environment-selection-1663/browser-subset-mobile.png) and
[mobile empty](environment-selection-1663/browser-empty-mobile.png) show actual
bUnit-rendered component markup with shipped Bootstrap, HVO theme and scoped
CSS. The [fixture receipt](environment-selection-1663/browser-fixture-receipt.json)
pins those HTML and stylesheet bytes. These are static browser fixtures;
component event tests and the real coordinator/producer test supply save and
lifecycle evidence, rather than a deployed host session.

The [browser audit](environment-selection-1663/browser-audit.json) records no
horizontal overflow at 1280×1100 or 390×844, no duplicate IDs, and collection
button heights of at least 44 px. Tab skips disabled Up and reaches Down from
the first default dropdown. Home chooses AirTemperature in the subset dropdown,
matching the producer screenshot; the component save test also verifies the
WindDirection choice. Normal vertical scrolling reaches the complete list and
help text.

Full-size producer rasters: [default](environment-selection-1663/environment-default.png),
[subset](environment-selection-1663/environment-subset.png),
[empty](environment-selection-1663/environment-empty.png),
[unavailable](environment-selection-1663/environment-unavailable.png) and
[all kinds](environment-selection-1663/environment-all-kinds.png).
The diagnostic capture is 2×2 Mono16 retained evidence rendered on the explicitly
configured 1936×1216 canvas; its original capture dimensions remain visible.
[Pixel checks](environment-selection-1663/image-checks.json) show zero differences
for explicit defaults and rollback. Every changed pixel in the other states is
inside the bottom-left environment corner (x 12–451, y 1028–1203); the rest of
the canvas and other corner blocks remain unchanged. Canonical layer,
metadata-facts and selected-input JSON are retained beside each PNG.

## Focused resource result

The producer, facts builder and compositor implementations are unchanged from
`409a5b02`. The absent/default and explicitly selected-default payloads are
byte-identical. Their output RGB SHA-256 is
`ED70E0A11B2FB87E89E4D3E5FCCD432F59A198DC143E85BBBB00CD52D895933D`.

The per-state `environment-*-resources.json` receipts record three warm-ups and
15 measured composites on pinned inputs. Their process CPU interval also
includes checksum assertions; working set reflects a shared test process with
earlier allocations, so it is not an isolated per-state memory delta. Initial
process CPU variation between identical defaults prompted a separate rotating,
interleaved comparison with checksums outside the timed interval.

The [standalone comparison](environment-selection-1663/interleaved.json) uses
SDK 10.0.402, Release, two logical processors, workstation GC, ten warm-ups per
state and three trials of 30 operations per state, concurrency one. Median of
trial medians is 11.411 ms for the omitted default and 11.450 ms for the explicit
default; p95 medians are 12.154 and 12.245 ms. CPU per 30 operations is 356.473
and 362.173 ms. Allocations are 7,177,316 and 7,177,299 bytes/operation; final
process working set is 124,829,696 bytes. GC counts are retained per trial.
The paired comparison resolves the earlier process/GC timing variation and
shows no material default regression. All-kind and subset rendering remains
bounded by the existing layout and full-frame buffer; there is no queue,
provider request, storage I/O or backlog at this measured rendering boundary.

After the Release build, reproduce the pinned comparison and pixel checks with:

```bash
python3 docs/validation/environment-selection-1663/reproduce.py "$EVIDENCE_ROOT"
```

The helper serializes its heavy work with the host lock, creates a fresh
`environment-measure` child, copies central package versions, and uses the built
Release assemblies plus the centrally pinned managed and native Skia packages.
It retains `build.log`, `interleaved.json` and `image-checks.json`. The
[measurement source](environment-selection-1663/Measure.cs.txt) is retained.

## Local gate

The [gate and artifact receipt](environment-selection-1663/receipt.json) records
source commits, selections, checksums and the effective results. Local evidence
is 1,167 shared Processing Unit passes and 572 effective affected CameraAgent
passes, including the corrected producer fixture. The final 192 editor passes
replace the affected earlier editor evidence rather than increasing that count.
CameraAgent Unit discovery is 7,507, matching its inventory; the shared inventory
is 1,167, and the repository declared Unit total is 14,601.

The affected Release project graph builds with zero warnings/errors. Affected
format verification, all four CI-control guards, operations documentation audit
and diff whitespace checks pass. Unit selections unset
`HVO_PRODUCTION_CATALOG_BUNDLE` and use an invalid Docker endpoint. Heavy steps
use `flock -o /tmp/hvo-heavy-gate.lock timeout ...`, disabled node reuse and
shared compilation. Original failed fixture/build attempts remain in the
recorded issue scratch directory; corrected assertions distinguish explicit
source correction, immutable association policy and immediate facts lineage.

No worker, queue, health check, metric label or log schema is introduced.
Existing graph lifecycle audit records and retained canonical input identities
remain the runtime evidence. Disposable browser serving is stopped after capture;
shared services and runtime state are untouched.
