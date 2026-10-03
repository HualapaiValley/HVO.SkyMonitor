# CameraAgent still products

Issue #993 supplies immutable keograms and star trails through the durable local
automation runner. The Processing recipes and Imaging algorithms remain host
neutral; CameraAgent owns source admission, publication, catalog and automation
adoption. Video encoding, custom scripts, the general wizard and archive page
adoption retain their separate issues.

## Source configuration and preset identity

The configured `Preview` graph alias can produce an absolute fixed transfer:

```json
{
  "type": "Preview",
  "id": "fixed-preview",
  "dependsOn": ["$raw"],
  "options": {
    "fixedTransfer": { "blackLevel": 64, "whiteLevel": 4095, "gamma": 2.2 },
    "recipeVersion": "fixed-native-gamma-v1"
  }
}
```

This is a step fragment for an explicit v2 graph; retain the deployment's actual
configuration structure and source node ID. Mono16 and RGGB16 sources use fixed
native levels, with linear Bayer reconstruction for CFA. Supported Mono8/RGB24
sources use the same fixed transfer in their native 8-bit sample scale; configure
black/white levels 0/255 and gamma 1 for an identity transfer. The defaults above
are for 12-bit native values. No frame histogram or percentile changes the
transfer. The durable output records `fixed-display-transfer` version
`fixed-native-levels-gamma-v1` and the exact recipe identity.

After verifying a published fixed preview, pin its source recipe identity and
rig profile in host configuration:

```json
{
  "CameraAgent": {
    "NightlyProducts": {
      "Enabled": true,
      "SourceNodeId": "fixed-preview",
      "SourceRecipeIdentitySha256": "<64 hexadecimal characters from the published preview>",
      "RigProfileSha256": "<64 hexadecimal characters from the captured rig profile>",
      "KeogramColumnSeconds": 60,
      "MaximumSegmentSources": 32,
      "MaximumSegmentsPerRun": 256
    }
  }
}
```

The adapter publishes `keogram:<preset SHA-256>` and `star-trail:<preset SHA-256>`.
The preset binds source node, source recipe, rig, bin duration, composition bounds
and JPEG quality. An old retained target fails if those settings are no longer
resolvable. Changing settings requires a new target and definition revision;
retry never substitutes current settings into an old occurrence.

## Scheduled windows and actual source selection

Create an operator definition from the registered `StillImageGeneration` target,
using `SourceWindowClosed`, interval 1 and the versioned source-window policy:

| Product | Window | Source selection |
| --- | --- | --- |
| Keogram | `CompletedCivilHour` or `SunriseDay` | `AllActualSources` |
| Star trail | `CompletedCivilHour` or `SunriseDay` | `DarkNightActualSources` |

The [automation contract](cameraagent-local-automation.md) owns settlement,
missed periods, explicit backfill, retry, DST, owner authority and durable run
history. Each final consumes its own occurrence; daily generation does not
depend on hourly products. The producer uses the retained full UTC period,
definition version/revision, site, time-zone rules and solar-event identities.
The starting-sunrise date labels the day: October 12 covers sunrise October 12
through sunrise October 13. Unavailable sunrise remains unavailable.

Admission uses available, published outputs from the configured node, selecting
one latest output per capture by actual exposure start in `[start, end)`. Capture
contracts retain millisecond exposure facts; fractional window endpoints round
up for the indexed half-open source query. Processing and commit clocks never
select the window. Sources must match the pinned fixed transfer and rig and the
retained site. Missing or unresolved locations are excluded. Star trails
additionally require geometric Sun altitude at or below −18° at each actual
exposure; day and twilight sources remain eligible for keograms.

The planned keogram axis uses fixed UTC bins over the complete period. The
earliest actual source in a bin supplies its column; additional sources in that
bin are explicitly decimated for display while retained in ordered lineage.
Every empty bin, including leading and trailing coverage, receives the declared
32/96 grey pattern. The last bin may be shorter than the bin duration. There is
no interpolation of missing sky pixels and no shortening to available captures.
Rows follow the shared north–zenith–south meridian projection. Star trails use
lighten composition of actual eligible frames, preserving any missing interval.

## Bounds, persistence and read API

| Boundary | Limit and failure behavior |
| --- | --- |
| Window candidates | 4,096; query returns one extra to reject excess |
| Recipe input count | At most 512; configured part count defaults to 32 |
| Resident source bytes per recipe | 256 MiB; parts and rollups also obey this bound |
| Recipe executions per occurrence | Configured 1–256; rejects excess before restoring or publishing |
| Star-trail reduction | Multiple frames require fan-in ≥2 within the source count/byte bound; impossible reductions reject before restoration |
| Planned axis | Configured maximum columns, default 16,384; at most 65,536; reject before source restoration |
| Planned output bytes | 256 MiB; final and part output sizes are checked before source restoration |
| Keogram assembly inputs | Predicted total part bytes at most 256 MiB; reject before publishing parts |
| Provenance document | 8 MiB |
| Products or windows in one date listing | 1,024; larger listings fail explicitly |

The still store has schema version 2. Earlier, newer or corrupt stores fail
closed; installed-state migration is outside this change. Packed payload,
JPEG rendition and canonical provenance are durably written before the SQLite
publication transaction. Immutable product IDs bind recipe and occurrence.
Ordered lineage identifies actual preview outputs and intermediate products.
Keogram assembly recipe 3.0.0 retains each segment’s original source IDs and
orders actual sampled columns by exposure time then original source identity,
including equal-time sources split across segments. The window fingerprint
version is 3; earlier candidate evaluations are re-evaluated, with immutable
outputs reused only when their current recipe identity still matches.
Current pointers belong to the occurrence, including definition revision, and
are updated transactionally. Unchanged retries verify checksums of all three
files before reusing publication; missing or corrupt files fail the run.
Selected sources that become unavailable fail restoration rather than produce
a silently thinner result. Payload and sidecar reads are bounded by their
committed journal lengths before allocation; excess, truncation or growth fails
restoration. Candidate layout and length must still match the selected facts. Rejected and empty windows retain their reason and
exclusion counts in the catalog.

Authenticated `OperationsReadV1` endpoints under
`/api/v1/operations/still-products` are:

| Suffix | Result |
| --- | --- |
| `/dates/{yyyy-MM-dd}` | Bounded product and evaluated-window listing |
| `/{productId}` | Detail, retained occurrence and ordered lineage |
| `/{productId}/preview` | Checksum-verified JPEG |
| `/{productId}/provenance` | Checksum-verified canonical JSON |

Unknown IDs return 404; unavailable or invalid storage returns a sanitized 503.
Content responses are private and are not cached. Broader archive page and
historical archive compatibility adoption remain separate work.

## Reproducible qualification

`FullDayStillProductQualificationTests` uses actual 640×640 RGGB VirtualSky
output and the verified 119,625-row production catalog. Its fixed site is
35.347° N, 113.878° W, America/Phoenix, starting October 12, 2026. It resolves
both sunrise boundaries through shared Astronomy and retains every actual raw
capture and published fixed preview. The bounded exposure recipe is documented
in its evidence; it is qualification input generation, not production AE.

Use fresh private paths and the exact commit that built the selected binaries:

```bash
HVO_ISSUE993_FULL_DAY_ROOT=/absolute/fresh/evidence-root \
HVO_ISSUE993_CATALOG_ROOT=/absolute/verified/catalog-root \
HVO_ISSUE993_SOURCE_HEAD=<full-source-commit> \
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~ActualSunriseDay_ProducesFullAxisDarkTrailDurableLineageAndReproducibleRetry"
```

The default cadence is one source per minute, with leading/trailing ten-minute
omissions and a deliberate 30-minute internal omission. Setting
`HVO_ISSUE993_SOURCE_STRIDE_MINUTES=60` runs an explicitly sparse smoke recipe;
it does not qualify minute-cadence coverage. The harness executes the real
durable automation runner, verifies dark-only leaf lineage and unchanged
retry, and retains actual times/settings, raw statistics, code/binary/catalog
identities, checksums and resource measurements. Public review files must be
copied to an exact allowlist; runtime state and private keys remain private.

`NightlyProductGenerationPerformanceTests` separately measures W1 durable
source I/O and synthetic whole-day W1 monochrome/W2 RGB24 composition. Synthetic inputs are declared
and must not be represented as actual sky images. Follow the canonical
[performance evidence rules](../planning/performance-validation.md), including
baseline/after attribution and the actual measured build configuration.
