# Scoped archive navigation evidence (#1536)

Target profile: `development-v1`. Risk Tier B: LogicHost UI composition over
unchanged B1 archive and retrieval authority; no schema, contract, host boundary,
selection policy or CameraAgent changes. Base:
`954534e6149452ec35bf1345146b5d61d53c6df3`.

The protected route is
`/app/network/observatories/{observatoryId}/cameras/{cameraId}/archive`, with exact
central capture UUID detail at `.../archive/{captureId}`. Choose **Camera archive**
in the camera workspace. The URL holds the B1 capture-time interval, product,
recorded object/reconstruction state, exact identity, protected page cursor and
return focus. Applying filters restarts paging. Each page replaces at most 25
rows; no image requests occur on the archive list. There is no total or automatic
history drain. The exact viewer never asks Current Sky for a substitute.

Detail shows one bounded artifact metadata page and explicitly identifies further
retained artifacts. Trace is an explicit bounded read, with truncation shown.
Thirty-second, authentication, visibility, online, browser focus and circuit
recovery refreshes recheck scope. Refresh clears protected evidence while pending;
failed/denied reads clear images and metadata, while missing exact image bytes
retain authorized provenance and a same-image retry. Raw downloads use the
existing audited one-minute grant, reauthorize content and explain renewal after
expiry. The archive has no shared content cache or persistence writes.

## Reproduction

Use SDK 10.0.402, Release, Docker available for the SQL Server/Redis/Mailpit and
filesystem fixture, and the repository-installed Playwright Chromium. Retain
outputs under ignored `TestResults/issue-1536/`; these are evidence, not disposable
scratch. Source identity is the base plus the per-file SHA-256 source manifest
and its SHA-256. Record it in `HVO_ISSUE1536_REVISION` and
`HVO_ISSUE1236_REVISION`.

```bash
flock -o /tmp/hvo-heavy-gate.lock timeout 600 dotnet build \
  tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false

# For a clean checkout, bind new results to the commit. The recorded results
# below instead bind the original candidate worktree with its source manifest.
export HVO_ISSUE1536_REVISION="$(git rev-parse HEAD)"
export HVO_ISSUE1236_REVISION="$HVO_ISSUE1536_REVISION"
# Service measurements: one service operation; UI navigation also includes scope
# resolution, prerender and interactive reads.
flock -o /tmp/hvo-heavy-gate.lock timeout 900 env -u HVO_PRODUCTION_CATALOG_BUNDLE \
  HVO_ISSUE1236_EVIDENCE="$PWD/TestResults/issue-1536/service-costs.json" \
  HVO_ARCHIVE_PERFORMANCE_PAGE_SIZE=25 \
  dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --no-build --configuration Release -nodeReuse:false -p:UseSharedCompilation=false \
  --filter 'FullyQualifiedName~LogicalCameraArchiveIntegrationTests|FullyQualifiedName~LogicalCameraArchivePerformanceTests|FullyQualifiedName~ArtifactRetrievalTests'

# Fresh test hosts keep each Manual browser fixture independent of other seeds.
for browser_class in CameraArchiveBrowserAcceptanceTests CurrentSkyBrowserAcceptanceTests OperationsScopeBrowserAcceptanceTests; do
  flock -o /tmp/hvo-heavy-gate.lock timeout 600 env -u HVO_PRODUCTION_CATALOG_BUNDLE \
    HVO_ISSUE1536_EVIDENCE="$PWD/TestResults/issue-1536" \
    dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
    --no-build --configuration Release -nodeReuse:false -p:UseSharedCompilation=false \
    --filter "FullyQualifiedName~$browser_class"
done

flock -o /tmp/hvo-heavy-gate.lock timeout 600 env -u HVO_PRODUCTION_CATALOG_BUNDLE \
  DOCKER_HOST=unix:///tmp/hvo-no-docker.sock dotnet test \
  tests/HVO.SkyMonitor.LogicHost.Tests/HVO.SkyMonitor.LogicHost.Tests.csproj \
  --configuration Release --filter 'TestCategory=Unit' \
  -nodeReuse:false -p:UseSharedCompilation=false
```

The service harness uses W3M 1,000/10,000 captures, two retained installations,
page size 25, 5 warmups and 30 measured operations per scenario. It records SQL commands,
actual-plan rows read, logical reads, indexed bounds, serialized response bytes,
latency, CPU, process allocations and working set, with the existing unfiltered
operations service as nearest baseline. Browser costs use separate 1,000/10,000
histories, a 25-row page, two trials each with 1 warmup and 5 measured navigation/detail/decode cycles,
and ten consecutive Next-page operations. Chromium CDP reports post-GC heap and
DOM plus per-process native RSS/CPU; resource timings report encoded and transferred image bytes. The
1936 × 1216 PNG has deterministic dark noise (seed 1536) and 1,000 synthetic stars.
Its dimensions, encoded length and SHA-256 are recorded. This establishes decode
and transfer cost for that explicit fixture, not a representative compression
ratio for all real cameras. The nearest unchanged camera page includes installation
evidence and a 50-row page; browser comparison is context, not an equivalent
latency comparison or a speedup claim. Report min/median/max for five browser
samples (and their ten-sample pool), not p95. No worker/backlog or capture pipeline is introduced.

## Results

Final UI/browser/Unit source fingerprint:
`3e50ec6529617162a15d18538f2ff23d079bdc04d37f4669b12b4609acb4257f`.
The manifest records the base and SHA-256 of every changed source/test file.
Service cost evidence is retained from fingerprint
`5f23a1bbe3cbbcfe0502b7e8bb83cebd99c373674dfaa22577d5b66306d56ab3`;
its two archive test/harness files and all production service files are unchanged
on the final candidate. UI disposal corrections do not invalidate that SQL path.

Environment: Linux x64 VM, 12 vCPUs (i9-14900K reported), 15 GiB RAM, ext4,
SDK 10.0.402/.NET 10.0.13, Docker 29.8.1; pinned SQL Server 2022 CU26,
Redis 7.4.11 and Mailpit 1.31.0 fixtures; Chromium 153.0.8010.12. Heavy work
was serialized with `/tmp/hvo-heavy-gate.lock`.

- Both affected Release builds passed with warnings as errors; touched C# files
  were formatted. Classifier selection: `mode=full`, `complete=false`,
  `logichost=true`; every other component/deployment flag is false.
- LogicHost Unit: **820 passed, 1 existing provider-specific skip**, 821 total.
  This includes 13 new archive component cases and the raw-grant expiry case,
  plus existing exact-viewer/layer, Current Sky and scope authorization coverage.
- Archive browser: **2 passed**. Current Sky browser: **1 passed**. Existing
  scope browser: **1 passed**, each class in a fresh test host.
- Archive service/endpoint and artifact retrieval selections: **20 passed**.
  SQL cost harness: **1 passed**. Manual discovery: **46**, matching its inventory.
- Four CI controls passed: `ci:shell-syntax` (202 files),
  `test:ci-classification`, `test:coordination-guard`, `test:pr-review-tools`.
  Both affected JavaScript modules passed Node module syntax checks.

The browser follows a retired installation among 70 same-time captures, submits
UTC filters, opens an exact central UUID, and returns with cursor and focused
item retained. It uses keyboard and touch at 1440×900, 390×844, 844×390 and
320×700; checks ARIA headings/status/navigation and document overflow; loads a
bounded trace; clears failed exact images and recovers only the same image;
then revokes membership and verifies metadata/image clearing. Current Sky's
existing real websocket reconnect-denial test also passes. Screen-reader coverage
is browser accessibility-tree evidence, not an OS assistive-technology session.
Unit/service tests cover the remaining unavailable/reconstruction states,
metadata-only content, cross-scope identities, credential restrictions and
expired raw grants. No content/publication mutation or fallback selection occurs.
Browser testing also reproduced a null DOM reference during disposal; cleanup now
uses a removal observer and tolerates a missing reference without ending the circuit.
`ExactLayeredPresentation_TakesPrecedenceOverAnotherPlainPreviewVariant` failed
before correction and passes afterwards: archive composition preserves the exact
layered service's base preference, with plain Preview fallback only when needed;
Current Sky still requires its selected artifact to match.

| Service scenario | History | Queries | Max result rows | Actual rows read (all plans) | Response B | Median / p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Archive first page | 1k | 3 | 26 | 115 | 7,463 | 1.350 / 1.814 |
| Archive first page | 10k | 3 | 26 | 115 | 7,463 | 1.244 / 1.677 |
| Older keyset page | 1k | 3 | 26 | 140 | 7,540 | 1.377 / 1.877 |
| Older keyset page | 10k | 3 | 26 | 140 | 7,565 | 1.119 / 1.558 |
| Product/state filter | 10k | 3 | 2 | 24,445 | 573 | 20.451 / 29.459 |
| Missing exact identity | 10k | 3 | 1 | 20,009 | 30 | 17.469 / 20.684 |
| Nearest old unfiltered service | 10k | 2 | 26 | 20,160 | 16,092 | 20.863 / 31.071 |

These query counts describe one B1 service call, not an entire UI navigation.
The existing indexed seek remains bounded for ordinary/older pages. Selective
no-match filters can examine retained index rows; the materialized bound is not
a claim of constant physical reads. CPU, allocation, RSS and logical reads for
all scenarios remain in `service-costs.json`.

| Browser metric (two trials, ten measured cycles per history) | 1k | 10k |
| --- | ---: | ---: |
| Archive interactive render ms, min / median / max | 63.879 / 117.446 / 131.956 | 68.545 / 127.816 / 140.121 |
| Exact image navigation/decode/frame ms, min / median / max | 76.933 / 94.034 / 116.826 | 74.502 / 94.527 / 134.514 |
| Archive post-GC JS heap MiB, min / median / max | 3.537 / 3.599 / 3.603 | 3.593 / 3.603 / 3.604 |
| Archive sum of browser-process RSS MiB, min / median / max | 586.871 / 666.230 / 691.012 | 556.418 / 617.881 / 646.508 |
| Post-image sum of process RSS MiB, min / median / max | 600.434 / 665.873 / 688.559 | 577.773 / 636.000 / 667.070 |
| Archive document body B | 37,872–37,907 | 38,043–38,083 |
| Archive image requests | 0 | 0 |

The PNG is 5,924,453 encoded bytes (SHA-256
`088b18e9d27a863c8f7e6daa302194352c7e815de083e9ada967c3709bb568bb`).
Every detail cycle transferred one exact image: Resource Timing reports 5,924,753 B,
with request durations 10.0–18.3 ms (1k) / 9.7–14.5 ms (10k). Its HTTP
`decodedBodySize` describes content encoding, not decoded pixels. A reference RGBA
pixel footprint is 9,416,704 B; native allocation is observed through process RSS.
RSS sums include shared pages and are stage snapshots, not unique physical memory
or continuous peaks. They vary with Chromium warming/cache reclamation; there is
no RSS improvement claim. The nearest old camera page has different content,
a 50-row page and earlier readiness; its render median over both trials is
47.502 / 67.811 ms. This is context, not equivalent latency.

A second trial checked the first trial's higher 10k median. Archive render medians
are 66.227 / 129.051 ms in trial 1 and 128.935 / 126.580 ms in trial 2.
The slower timing mode appears at both history sizes; these samples do not show
a repeatable history-size increase. They do not isolate the cause of timing
variation or support a speedup claim. All samples are retained; no browser p95
is inferred from ten operations.

Each trial's ten consecutive Next operations retain exactly 25 items. DOM remains
1,162–1,163 nodes (1k) / 1,164–1,165 (10k), three documents and **77 listeners**.
Post-GC JS heap settles near 4.8 MiB over the final pages; native RSS declines
across the paging windows. This is finite observed paging evidence, not an
unlimited-duration soak. No full-history client collection is introduced.

Artifacts are retained outside the disposable worktree at
`/home/roys/development/HVO.SkyMonitor/TestResults/issue-1536/`:
`source-manifest.json`, `service-source-manifest.json`, `browser-acceptance.json`,
`browser-costs.json`, `service-costs.json`, dedicated Unit/service/browser TRXs,
four viewport PNGs, both browser cost trials, failing regression evidence,
browser traces/diagnostics, gate logs and `artifacts.sha256`.
Earlier failing attempts are retained for audit; the dedicated final TRXs above
are the passing evidence. Independent review and protected v1 CI belong to the PR ledger; this document
records local acceptance. No full `ci.yml`, release or production qualification
is claimed.
