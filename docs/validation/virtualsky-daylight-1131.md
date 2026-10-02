# VirtualSky day/night qualification (#1131)

## Accepted visual checkpoint

The operator [accepted the revised POC and starting-sunrise date label on
2026-10-02](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1131#issuecomment-5959368176).
The retained LAN review is <http://192.168.2.45:8095/>; the earlier rejected
orientation remains at port 8094. Accepted output is pinned to rendering commit
`33712845b6b31c9382b24bb8b0eb8ac63723b486`, with per-run harness source,
assembly hashes, source times, settings and raw/display checksums. Later test
and evidence changes do not regenerate these accepted pixels.

The evidence includes 66 matched day/sunset/twilight/night/dawn captures,
fixed-transfer linear and gamma display images, raw mean/percentiles/clipping,
controlled-background comparisons, and actual sensor-path Sun/Moon captures.
The POC rig uses North up, East left, West right (`horizontalFlip: true`).
Sun/Moon locator circles are labels, not angular-size representations. The
1.5-degree detail captures show the true-scale disk and lunar phase; additional
lunar dates are explicitly separate from the October 12 daily period.

The modest sky approximation, defaults, provenance and limitations are described
in [the VirtualSky contract](../virtual-camera.md). Legacy scalar illumination
and explicit electron-rate fixtures retain their defaults; SolarDriven and
resolved disks are opt-in. ControlledNight changes the background, not the
celestial clock. No atmospheric photometry certification is claimed.

## Reproducible workload and performance

The isolated #1134 harness uses verified catalog `hyg-v4.2-p3-s2-r1`, 119,625
rows, database SHA-256
`b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2`.
Its `measure` mode captures 640-square Bayer frames with five warm-ups and
thirty measured captures per setting, one capture at a time, on the same host.
The retained `renderer-comparison.json` records exact clocks/settings, CPU,
allocation, peak working set, output checksums, byte counts and work boundaries.

| Scenario | Median ms | p95 ms | CPU s / 30 | Allocated bytes / 30 | Peak working set bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Day, legacy scalar | 35.16 | 37.95 | 1.35 | 321,549,184 | 171,716,608 |
| Day, SolarDriven | 130.47 | 136.64 | 4.39 | 321,506,648 | 180,617,216 |
| Day, SolarDriven + disks | 220.93 | 254.90 | 6.82 | 4,581,075,912 | 183,287,808 |
| Night, legacy scalar | 136.83 | 148.90 | 4.21 | 5,526,362,888 | 183,287,808 |
| Night, SolarDriven | 137.73 | 146.04 | 4.23 | 5,526,390,488 | 189,771,776 |
| Night, SolarDriven + disks | 137.25 | 146.03 | 4.24 | 5,526,641,728 | 191,078,400 |

The spatial daytime approximation adds per-pixel projection/trigonometry.
A bright disk raises the conservative stellar admission query ceiling, so the
daytime disk case evaluates more catalog candidates and allocates more. These
costs are explicit; the renderer does not hide them by truncating the catalog.
The uniform-night fast path avoids that projection cost. All three measured
night cases have identical raw checksums for this scenario. These are bounded
single-worker measurements, not physical-camera throughput qualification.

The 1,381-source full-day run used 423.90 s elapsed, 429.39 s CPU, and peak
working set 629,460,992 bytes, with sources processed sequentially. The full
planned grid has 1,441 slots, including 15 leading unscheduled slots, a deliberate
30-minute outage, and 15 trailing missing captures. The trail uses only 557
eligible dark-night sources (11,140 seconds of actual integration). Production
scheduling, encoder integration and historical calendar migration remain outside
#1131 and #1134.

## Validation ledger

Local selection uses `scripts/ci:classify` against target
`cb5d7e3f63c69e85f0f079ef63c687fbb7e5f818`: `mode=full`, `complete=false`,
`shared=true`, `cameraagent=true`, `logichost=true`, `combined=true`,
`delivery=true`; deployment flags are false. Tier C additionally requires the
complete local candidate commands in AGENTS.md. Exact final heads, gate results,
independent review and protected CI are recorded in the issue/PR ledger; visual
acceptance alone is not a green gate or a production-completion claim.

Evidence retained under `/tmp/hvo-1131-2a2d0ca8` includes candidate, affected,
source-map recollection and publish-matrix logs. POC inputs and accepted public
artifacts are retained under `/home/roys/.cache/hvo/1134-2a2d0ca8`, with runtime
secrets outside the allowlisted public tree. Source-map reports from different
renderer revisions were not merged for final coverage: affected reports were
recollected against one source revision before enforcing unchanged floors.
