# Opt-in Frame Facts presentation v1

Issue #1552 integrates the [template](frame-facts-template-v1.md) and
[retained-context](frame-facts-retained-v1.md) contracts. Select the new
`FrameFactsPresentationLayerV1` graph alias explicitly. It is never auto-included;
the `EnvironmentPresentationLayer` alias, its producer version, default graph,
saved options and historical products remain unchanged. The four-corner editor
and explicit before/after draft conversion remain #1553.

## Versions and retained evidence

The new producer is `frame-facts-presentation-v1`. It emits
`presentation-metadata-facts-v3` through the existing metadata facts media type
and `presentation-layer-payload-v4` through the existing presentation layer
media type. Existing creators continue to emit facts v2 and payload v3. The new
fields are omitted entirely from old versions, preserving canonical bytes and
identities. Mixed manifests use compositor `typed-presentation-compositor-v7-rich-text`
and grouped SVG `cameraagent-grouped-svg-v6-rich-text`; old version selection is
unchanged.

The new facts record contains `FrameFactsSnapshotV1`: the complete ordered
retained token catalogue, scalar availability, typed values and per-scalar
provenance. Its canonical identity covers all of that evidence. Reading validates
the catalogue, value kinds, bounds and identity before constructing the existing
immutable token context. Both hosts can evaluate it without fetching facts,
consulting current settings, resolving a zone or reading a clock.

The CameraAgent builder projects the exact descriptor, selected stack's source
artifact IDs/integration/output identity, retained submission cadence, available
raw-frame source timestamp, projected scene and selected structured environmental
observations. Fresh, stale, missing and contradictory associations map to the
existing context availability contract. It never parses `DisplayLine`.
Optional historical location coordinates, a frozen local offset and selected
execution-completion evidence are unavailable when not supplied by the retained
source; this slice does not add their acquisition/persistence. A source timestamp
is unavailable if raw frame metadata was not loaded; metadata production does
not load pixel buffers merely to obtain it.

The rich layer retains each template, missing policy, selected font/size/colour,
evaluated styled runs, snapshot identity, grammar/catalogue/producer versions,
layout version and the three font checksums. Its source identity names the new
facts product, which binds immediate scene/stack sources. These identities and
the payload dimensions bind a retained result; changing a current graph cannot
change it. An optional rendering failure publishes neither partial facts nor a
partial layer and leaves source/raw/science products intact.

## Graph options

The existing descriptor and typed codec describe the exact new alias/version.
For each prefix `topLeft`, `topRight`, `bottomLeft`, `bottomRight`:

| Suffix | Value |
| --- | --- |
| `Template` | v1 source, default empty (hidden corner) |
| `Font` | `MonoRegular`, `MonoBold`, `PlexRegular`; default MonoRegular |
| `SizeMilliPixels` | 4,000–64,000; default 16,000 |
| `Red`, `Green`, `Blue` | 0–255; default white |
| `MissingPolicy` | `Placeholder` or `DropLine`; default Placeholder |

`inset` is 0–64 px (default 12); `lineSpacing` is 0–16 px (default 2).
`widthPixels` and `heightPixels` select the presentation dimensions, bounded by
the compositor's 32 Mi-pixel admission. Source/output limits remain 4,096 UTF-16
source units, eight lines and 64 Unicode scalars per line per corner, including
explicit blank lines. No wrapping, truncation or automatic shrinking occurs.
Only explicitly selected `environmentalKinds` are retained; default is empty.

The new default output variants are `presentation-metadata-facts-v3` and
`environment-layer-v2`. An explicitly authored graph must select those variants
in its manifest/materializer's `environmentFactsVariant` and `environmentVariant`
and declare the new node as a dependency. Both consumer nodes must explicitly set
`useRichFrameFacts: true` to accept the new schemas. False is omitted from effective
options, preserving the exact legacy graph contracts and identities, as well as
their default variants. The
required scene/combined-preview dependencies and normal durable publication
policy still apply. No upgrade is registered or executed implicitly.

## Shared rendering and bounds

`frame-facts-corner-layout-v1` lays out font outlines once per requested layout;
SVG and raster use the same path/rectangle builder. It uses only the checksum
verified DejaVu Mono regular/bold and Plex regular assets already packaged in
Imaging. Bold Mono uses the packaged bold face. Plex with bold emphasis is
rejected, including a bold source line dropped for missing data, because no
accepted bold Plex face exists. There is no synthetic bold or font fallback.

Italic is a fixed horizontal shear of `-0.21255656` (12 degrees). Underline starts
0.12 em below baseline and is `max(1 px, size/16)` thick. Baselines are 1.5 em
apart plus configured spacing. Font advances preserve spaces across run
boundaries. Each nonempty corner gets a six-pixel padded dark panel (`#03080E`,
80% opacity). Each panel must fit its own image quadrant including inset;
oversized corners fail the optional layer rather than overlap or clip. Empty
templates/drop-all-lines hide that panel, while explicit blank lines remain.

The schema allows exactly four unique corner records, at most eight lines and
64 bounded runs per line. Compositor admission includes their primitive, text
and geometry work before allocating output. Raster keeps the existing single
owned full-frame output and reusable native tile of at most 4 MiB. Native paths
are disposed on success and failure; no image copies enter the frozen context.

## Hardware-free validation and cost

The Unit tests in `FrameFactsPresentationTests` and
`PresentationMetadataFactsBuilderTests` cover fonts/emphasis, all corners,
missing policies, strict source/output bounds, immutable snapshots, identities,
legacy serialization, opt-in registration, failure isolation and the actual
CameraAgent/shared LogicHost rendering seams. The manual measurement is:

```bash
dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj \
  --no-build --configuration Release \
  --filter FullyQualifiedName~FrameFactsPresentationPerformanceTests \
  --logger 'console;verbosity=detailed'
```

Run under the repository's host heavy-gate lock/timeout with SDK 10.0.402.
The harness compares unchanged legacy producer/composition against context
snapshot/evaluation/rich producer/composition at W1 1936×1216 and W6 3552×3552
Mono8, promoted to RGB24 by both paths. It uses identical frozen scalar content,
four one-line corners, concurrency one, five warmups and 30 measured operations.
Legacy uses its unchanged automatic typography; rich uses explicit 16 px and
bold/italic/underline. This compares feature costs, not pixel-equivalent styles.
It records producer/total median and p95, process CPU, managed allocations,
sampled working set, canonical payload size, output checksums and ownership.
Checksumming and payload-size reporting follow each timed operation; process CPU
includes those checks. There is no I/O, queue or backlog in this pure fixture.
Working-set observations share a process and include GC/runtime/native caches;
they are not isolated per-path native allocation measurements. No Pi or physical
accuracy qualification is claimed.

Local comparative observation on macOS 26.5.2 arm64, Apple M4 Max, 36 GiB RAM,
SDK 10.0.402, Release/native (2026-10-23; baseline method unchanged from
`523d43d9a36fbf7f3d7bfff47d29a74909061fd7`):

| Workload/path | Total median/p95 ms | Producer median/p95 ms | Allocated bytes/op | Sampled max working set | Payload bytes |
| --- | --- | --- | --- | --- | --- |
| W1 legacy | 3.864 / 4.377 | 0.112 / 0.159 | 7,118,486 | 183,435,264 | 3,801 |
| W1 rich | 6.589 / 7.528 | 1.845 / 2.393 | 8,122,562 | 252,362,752 | 2,027 |
| W6 legacy | 18.264 / 24.893 | 0.074 / 0.183 | 37,906,537 | 737,165,312 | 3,805 |
| W6 rich | 20.054 / 22.232 | 0.574 / 1.556 | 38,890,173 | 739,885,056 | 2,027 |

The added context/provenance canonicalization, strict validation, evaluation and
rich layout account for roughly 1 MB more managed allocation per operation and
0.5–1.8 ms producer median in this new opt-in path. The total median difference
is +2.72 ms W1 and +1.79 ms W6; these absolute feature costs remain bounded,
with no added full-frame owners. Shared-process warmup/tiering/GC makes the
producer medians and working-set comparison noisy; the lower W6 p95 is not an
improvement claim. Process CPU over each 30-operation window was 211/285 ms
(W1 legacy/rich) and 1370/1271 ms (W6). Throughput was 258/155 and 50.7/50.1
frames/s respectively, with zero I/O/backlog. This is a local baseline for the
new capability, not a portable threshold or a regression in the old path.

The stable legacy output checksums were
`556D0FC2AEE5CB0470B9C974FF55899CB60A92D3EE0BC78229B6988C1D1C6A35` (W1) and
`21CDDADB3B5C9D6D52EF1FF442E5801AAB54C080EAD0C260B326D9625D47BD4F` (W6).
The stable rich checksums were
`55C892EC4D25C726282C6CCC2C9A0618CF7299D053CD9D362E4DD1AE622A2DA6` (W1) and
`15A54E8A0819E400DF4AF4AF515A276D35116C8330ECF822FB0D0D8403B4BCB0` (W6).
