# NOAA SWPC space-weather fixture pack, v1 (issue #1398)

This directory is test data for the space-weather source decision in
`docs/planning/space-weather-source-decision-1398.md` and the fact contract in
`docs/contracts/space-weather-facts-v1.md`. Nothing here is production
configuration, and nothing here authorizes network access.

## Source and terms

All captured bodies come from the NOAA Space Weather Prediction Center (SWPC)
public data service at `https://services.swpc.noaa.gov/`. SWPC is part of the
National Weather Service (NWS). SWPC publishes no separate usage policy, so the
NWS disclaimer at https://www.weather.gov/disclaimer applies. It was read on
2026-10-19 and says, in part:

> The information on National Weather Service (NWS) Web pages are in the public
> domain, unless specifically noted otherwise, and may be used without charge
> for any lawful purpose so long as you do not: 1) claim it is your own (e.g.,
> by claiming copyright for NWS information ...), 2) use it in a manner that
> implies an endorsement or affiliation with NOAA/NWS, or 3) modify its content
> and then present it as official government material.

The captured bodies are U.S. Government works and are not subject to copyright
protection in the United States (17 U.S.C. 105). 17 U.S.C. 403 concerns
published works that consist predominantly of U.S. Government works. For such
a work, a copyright notice does not have the evidentiary effect given by 17
U.S.C. 401(d) and 402(d) unless the notice also identifies, either
affirmatively or negatively, the portions that are protected by copyright.
This statement makes that identification for this directory. The captured
bodies listed below, and the SWPC record text reused in the scenarios, are NWS
material and are not subject to copyright protection. Everything else in this
directory is HVO SkyMonitor's own work.

Credit: "Space weather data: NOAA Space Weather Prediction Center (SWPC)." HVO
SkyMonitor is not endorsed by or affiliated with NOAA or NWS. The scenario
expectations, derived windows, attention events and local relevance values in
this pack are HVO SkyMonitor's own policy outputs and are not official
forecasts. No NOAA or NWS logo or trademark is used.

## Capture

The committed bodies come from one GET per product made on 2026-10-19 between
18:57:03.332Z and 18:57:04.464Z, with gzip negotiation and no credentials or
cookies. The full OVATION grid was downloaded once. `manifest.json` records
each product's fetch start time, HTTP status, content type, transfer size,
`Last-Modified`, `ETag`, `Cache-Control`, decoded size and decoded-body
SHA-256. No other response headers are recorded. CDN request identifiers and
`Via` values were deliberately left out.

The same research session made 31 requests in all. Every one is listed here.
None of their other bodies is committed:

- 18:56:38Z to 18:56:39Z: the `/products/`, `/json/` and `/text/` directory
  indexes.
- 18:57:03Z to 18:57:05Z: the capture above. The same sweep also fetched seven
  products that were evaluated and excluded, and then the `/products/summary/`
  index at 18:57:04.928Z.
- 18:57:20Z: conditional GETs for six products. Five returned 304: alerts,
  scales, the Kp forecast, 1-minute Kp and the 3-day geomagnetic forecast.
  `noaa-planetary-k-index.json` returned 200 with a byte-identical body and new
  validators.
- 19:00:11Z to 19:00:13Z: HEAD requests for three GOES JSON products, which are
  excluded.
- 19:00:23Z to 19:00:24Z: one GET of `/json/goes/primary/xray-flares-latest.json`,
  which is excluded, and conditional GETs for five text products, including the
  3-day forecast. All five returned 200 with byte-identical bodies and new
  validators.

Scenario s17 step 2 uses the 3-day forecast validators from the 19:00:23Z
request: `ETag` `"7fe-65e361ddb4bcb-gzip"` and `Last-Modified`
`Mon, 19 Oct 2026 18:59:42 GMT`. That request's body matched the committed one.

| File | Product | Transform |
|---|---|---|
| `capture-20261019T1857Z/alerts.json` | `/products/alerts.json` | None. Byte-exact (54,064 bytes). |
| `capture-20261019T1857Z/noaa-scales.json` | `/products/noaa-scales.json` | None. Byte-exact (1,102 bytes). |
| `capture-20261019T1857Z/noaa-planetary-k-index-forecast.json` | `/products/noaa-planetary-k-index-forecast.json` | None. Byte-exact (6,905 bytes). |
| `capture-20261019T1857Z/3-day-forecast.original.json` | `/text/3-day-forecast.txt` | The exact decoded text (2,046 bytes) stored as a JSON string, so its trailing spaces survive `git diff --check`. The wrapper records the original size and SHA-256. |
| `capture-20261019T1857Z/3-day-forecast.txt` | same | The normalized form of the text above. The snapshot hash for text products is taken over this form. |
| `ovation-aurora-site-sector.json` | `/json/ovation_aurora_latest.json` | A sector subset: longitudes 236 to 256 and latitudes -5 to 90, one cell per line, with the source keys. The 918,460-byte original is not committed; its SHA-256 and size are recorded in the file and in `manifest.json`. |

Normalization for text: CRLF becomes LF, trailing spaces, tabs and carriage
returns are removed from every line, trailing blank lines are removed, and
every line ends in LF. The same rule is used for alert message content hashes.

## Scenarios

`scenarios/*.json` (schema `hvo-space-weather-swpc-scenario-v1`) are
deterministic step sequences of fetches, acknowledgements, restarts, operator
resumes and evaluations, each with expected outcomes. `fixtures.py` generates
them from the captured bodies. A scenario marks
`containsSyntheticRecords: true` and explains in `notes` when it contains
records that SWPC did not publish. These include:

- synthetic CANCEL WARNING records (s09, serials 2271 and 2272), because no
  CANCEL WARNING was published in the capture window. They are built from the
  documented SWPC rules and the shape of the captured CANCEL WATCH;
- synthetic watches (s08: a G2 watch, WATA30 900, and a G3 watch, WATA50 100,
  with a G2 watch, WATA30 901, first seen in the same body; and watches first
  seen after outages, WATA50 101 and 104 at G3, WATA30 902 and 905 at G2 and
  WATA20 903 at G1), a year-rollover watch (s14, WATA20 1200), a watch whose
  day lines name the impossible date `Oct 32` (s12, WATA20 950) and an unknown
  message code (s12, ALTZZZ 1);
- a synthetic warning extension (s10, WARK04 5437) and a synthetic warning
  whose validity exceeds the active cap (s21, WARPX1 200);
- a synthetic 3-day forecast issued 2026 Dec 31 (s14), whose table columns
  cross the year;
- altered or damaged copies of real records and bodies, used to model:
  - revisions (s02, s11);
  - malformed, mismatched, oversized, header-only and long-serial records, a
    record whose `Issue Time` line has a doubled space, a record whose
    message ends in an unpaired surrogate escape, a record whose `Issue Time`
    year is written in Arabic-Indic digits (a JSON `\u` escape, so the pack
    stays ASCII), a record whose `Issue Time` is in the year 9999, a
    record whose `product_id` is a 4,301-digit number, a record whose
    `product_id` is the JSON number `1E5`, and records whose
    `issue_datetime` or `message` is the JSON number `1.5` (s12);
  - records with a renamed header line, and the drifted body served again
    after a newer one (s20);
  - malformed bodies, including bodies nested 64 and 65 levels deep (s12);
  - the captured scales with day 1's G scale rewritten from the JSON string
    `"1"` to the JSON number `1` (s15);
  - the captured Kp forecast with its first row's `kp` rewritten from the
    JSON number `1.67` to the JSON string `"1.67"`, then, separately, with
    that row's `noaa_scale` rewritten from `null` to the JSON number `1`, and
    with its `observed` label rewritten from the JSON string `"observed"` to
    `null` (s16);
  - the committed OVATION sector subset with the cell `[236, 60, 0]`
    rewritten as `[236, 60, 0.5]` (s18);
  - a 3-day forecast with a renamed table, and one whose solar radiation
    table has no rows (s17).

The errata scenarios use entirely synthetic records and bodies:

- s23 supplies synthetic Retry-After headers and a fresh synthetic reset alert;
- s24 supplies synthetic baselines and nonfinite JSON constants for every JSON product;
- s25 supplies synthetic alerts and scales at the source-year boundaries;
- s26 supplies a synthetic alert with an unread fraction-valued field, nested
  values and deliberate JSON escape spellings and whitespace;
- s27 supplies two synthetic message versions with outer JSON whitespace, an
  unread fraction-valued field, literal UTF-8 and a Unicode escape spelling,
  retaining both original records through restart and unchanged refetch.

`rounding-vectors.json` is a separate manifest-listed golden vectors file,
checked by both `check` and `write`. These are pure numerical probes, so a
vectors file keeps them independent of fetch timing and notice lifecycle.
It records synthetic inputs, the exact unrounded binary64 double as Python
`repr` text, and the expected rounded output for both one-decimal operations.
Some inputs are exact binary ties; others lie immediately beside decimal
half-way boundaries. Relevance probes also use deliberately higher-precision
intermediate latitudes. Expectations are fixed, never regenerated from the
rounding implementation. See the contract's local-relevance rounding rule.

No third-party copy of any SWPC message is included.

## Verify and regenerate

Run from the repository root with an isolated interpreter:

```bash
python3 -I tests/fixtures/space-weather/swpc-v1/fixtures.py check
```

`check` performs the following checks:

- It verifies every file against `manifest.json` and rejects unlisted files.
- It confirms that the captured bodies still match the recorded fetch sizes
  and hashes.
- It regenerates every scenario and compares the bytes.
- It generates an unterminated string of one quote and 524,287 escaped-quote
  pairs in memory, and requires `malformed-json` within 5 seconds (including
  subprocess startup). The adversarial body is never committed.
- It checks all golden rounding vectors, including the sign of zero.
- It replays every scenario through the reference model in `fixtures.py` and
  compares the results with the expectations.

`write` regenerates the scenarios and the manifest. It rebuilds the OVATION
subset only when `--ovation-original` names a file with the recorded size and
SHA-256.
