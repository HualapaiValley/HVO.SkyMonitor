# Space-weather source and policy decision checkpoint (#1398)

Date: 2026-10-19. Decision owner: Batch B,
`claude-code:anthropic:home-dev-01:d221666e`. Execution: Claude Code, Claude
Opus 5.5, Extra High (`xhigh`).

## Status and authority

The operator assigned this checkpoint in
[Prompt B](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1391#issuecomment-6274638327),
under the [bounded activation](../roadmap.md#bounded-satellite-and-sky-context-activation--2026-10-19)
and the [#158](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/158)
provider-planning family. #1398 itself says that this first source/policy
checkpoint is internal to the issue and must be reviewed before provider
implementation. This document, the
[fact contract](../contracts/space-weather-facts-v1.md) and the
[fixture pack](../../tests/fixtures/space-weather/swpc-v1/SOURCE.md) are that
checkpoint. Nothing here adds production code, configuration or network access.

Each decision has one of three statuses, which match Prompt B's
"proposed, approved and unresolved":

- **Approved.** Proposed during this PR's review. Merging the independently
  reviewed commit accepts it under the delegated assignment. Approval is a
  software design decision, not evidence that an implementation exists or
  passes.
- **Proposed.** A safe default that the implementation will use. A maintainer
  can change it before the provider implementation PR without reopening this
  checkpoint.
- **Unresolved.** Blocks only the behaviour it names. The first implementation
  tranche excludes that behaviour.

The activation bounds #1398's cache and local-notice software to its selected
products. Those are the four Approved core products below. OVATION stays outside
that bound until U3 is resolved.

#1398 stays open after this checkpoint merges. Provider acquisition, the
durable store, the operator surface and the acceptance tests are still to do.
Under Prompt B, Batch B pauses #1398 for the higher-priority #528 and #1392
work once Batch A's #1402 handoff lands, then returns to finish it.

## Decision ledger

| Decision | Status and disposition | Remaining boundary |
| --- | --- | --- |
| Provider | Approved: NOAA SWPC public data service, `https://services.swpc.noaa.gov/` only | No mirror, no NODD/S3 route, no third-party copy |
| Product set | Approved: four core products (alerts, NOAA scales, planetary Kp forecast, 3-day forecast). OVATION is a fifth product whose acquisition is not in the first tranche | OVATION acquisition depends on U3 |
| Topology | Approved (U13): one CameraAgent-local acquisition authority per deployment. Every other local consumer reads its store | A LogicHost distributor is optional future topology, never a second concurrent poller |
| Fact model | Approved: typed host-neutral facts that keep SWPC's own kinds, scales and probabilities. No universal severity score | Shared types are a Proposed default (see "Ownership") |
| Identity and deduplication | Approved: message identity is code, serial and issue minute; deduplication is by normalized content hash. HTTP validators are never used for deduplication | Validators may still be sent as conditional request headers |
| Lifecycle | Approved: per-family windows; extensions, continuations, corrections, cancels and watch supersession; a 96-hour active cap | Rules are in the contract; s03 to s11 and s21 exercise them, including the 96-hour cap (s21) |
| Request policy | Approved: a per-product cadence structure, 60 s minimum spacing, bounded transient backoff, `Retry-After`, and persisted halts that need an operator resume. A resume never bypasses spacing, a backoff or `Retry-After` | The values are Proposed: the base intervals (alerts 120 s under U1, scales 300 s under U15, Kp forecast 900 s, 3-day forecast 1,800 s), the decoded-byte maximums and the outdated thresholds. Halt reasons and counts are in the contract; s13, s19 and s22 exercise them |
| Freshness | Approved: freshness is based on retrieval time. The in-file content stamp is a separate fact that is shown next to it | — |
| Last-known-good and history | Approved: a rejected body never replaces last-known-good. Message history is kept locally. Absence from the rolling upstream window never means cancellation | The 35-day history period (U8), raw snapshot retention and the snapshot cap are Proposed |
| Local relevance | Approved: global facts stay global. Site relevance is a separately typed, derived HVO estimate based on geomagnetic latitude | The dipole model (U10) and the darkness qualifier are Proposed |
| Attention | Approved: local attention only, raised at most once per notice and level. Acknowledgement is separate from expiry. A restart never re-raises. Format drift and record drift raise provider attention | The 300 s new-notice grace is a Proposed value. No external delivery (#455) and no capture or safety action |
| Per-frame isolation | Approved: no network or space-weather lookup in capture, recipe or per-frame processing | #1395 measures contention when enabled |
| Credit and terms | Approved (U5): the credit text below, with no NOAA or NWS logo | — |
| Fixtures | Approved: `tests/fixtures/space-weather/swpc-v1/`. Real captures are byte-exact or have a recorded transform. Synthetic records are disclosed. Every scenario replays through a reference model | Live probes are external only, not Unit |

## Source and terms

SWPC is part of the National Weather Service. SWPC publishes no separate usage
policy, so the [NWS disclaimer](https://www.weather.gov/disclaimer) applies.
Its text was read verbatim on 2026-10-19. NWS web information is in the public
domain unless otherwise noted, and may be used for any lawful purpose provided
the user does not:

1. claim it as their own;
2. imply NOAA or NWS endorsement or affiliation; or
3. modify it and present it as official government material.

The material is a U.S. Government work and is not subject to copyright
protection in the United States (17 U.S.C. 105). Under 17 U.S.C. 403, a
copyright notice on a published work that consists predominantly of U.S.
Government works does not have the evidentiary effect of 17 U.S.C. 401(d) or
402(d) unless it identifies, affirmatively or negatively, the copyrighted
portions. The fixture pack's
[`SOURCE.md`](../../tests/fixtures/space-weather/swpc-v1/SOURCE.md) therefore
identifies which of its contents are NWS material that is not subject to
copyright, and which are HVO SkyMonitor's own work.

The same page's "appropriate use" guidance governs the request policy. It tells
users to:

- know the refresh frequency of the data they request;
- request only the data they need;
- limit retries for an unavailable service to one-minute intervals;
- check their logs for invalid links, because products are changed and
  decommissioned.

NWS may block IP addresses or query types. A blocked user should contact
`sdm@noaa.gov`.

**Approved credit (U5):** "Space weather data: NOAA Space Weather Prediction
Center (SWPC). Displayed by HVO SkyMonitor; not endorsed by NOAA/NWS. Local
relevance is derived by HVO SkyMonitor and is not an official forecast." The
last sentence keeps derived relevance from being presented as official
material, which is condition 3 above.

**Proposed User-Agent:** `HVO.SkyMonitor/<version>` followed by the project
URL. No requirement for one was found. The value lets SWPC identify the client
rather than block it. No personal contact address is committed.

## Product set

| Product | Status | Why |
| --- | --- | --- |
| `/products/alerts.json` | Approved, core | The only source of issued notices and their lifecycles: watches, warnings, alerts, summaries, extensions, continuations, corrections and cancels |
| `/products/noaa-scales.json` | Approved, core | The official current R/S/G levels, the 24-hour observed maximums, and three predicted days. Alerts start at R2, so R1 reaches the core set only through this file and the 3-day forecast's R1–R2 probabilities |
| `/products/noaa-planetary-k-index-forecast.json` | Approved, core | Observed, estimated and predicted 3-hour Kp with G labels, in one small file. It uses the post-SCN 26-21 keyed layout |
| `/text/3-day-forecast.txt` | Approved, core | The forecaster-issued forecast with a real `:Issued:` time, the Kp breakdown and S/R probabilities |
| `/json/ovation_aurora_latest.json` | Unresolved (U3): acquisition and wording | 918,460 bytes decoded and 150,748 bytes on the wire per fetch. The values are a model intensity proxy. Data-only fixture s18 records the site band |

Excluded, with the reason:

- GOES X-ray, proton and flare JSON. This is not a flare catalog. R and S events
  arrive through alerts and scales.
- `noaa-planetary-k-index.json`, which is redundant with the forecast file.
- `planetary_k_index_1m.json` (U2).
- Discussion, 27-day outlook, 3-day geomagnetic forecast and advisory outlook
  text (U4).
- Hemispheric power.
- The aurora view line, which SWPC removed in May 2026.
- RTSW solar wind, `products/summary/*`, event catalogs, the 45-day forecast,
  and all images and animations.

The issue scope also excludes image overlays, satellite matching input,
email/SMS/webhook delivery, and automatic capture or safety actions.

No service change notice for any product in the set was listed between
2026-09-01 and 2026-10-19. SCN 26-21 restructured the Kp JSON on 2026-03-31,
and the capture confirms the new keyed layout. The 2026-05-18 notice says SWPC
is "transitioning to a new internal alerting system", which is a format-drift
risk for `alerts.json`. The contract therefore keeps unknown codes visible as
unclassified and rejects unparseable records individually. It raises
format-drift attention after three consecutive rejected bodies, and
record-drift attention when an accepted alerts body contains rejected records,
because new messages may change format while older ones in the rolling window
still parse. Record drift raises once per episode. Only a clean body with a
newer in-file stamp ends the episode, so an edge serving alternating bodies
cannot raise it repeatedly (s20).

## Capture evidence

The committed bodies come from one GET per product on 2026-10-19 between
18:57:03Z and 18:57:05Z, with gzip negotiation and no credentials or cookies.
The full OVATION grid was downloaded once. The fixture manifest records the
fetch time, status, validators, transfer size, decoded size and SHA-256 for
each product.

The research session made 31 requests in all, between 18:56:38Z and 19:00:24Z.
They were:

- three directory indexes;
- the capture, including seven products that were evaluated and excluded and
  the `/products/summary/` index;
- six conditional GETs at 18:57:20Z;
- three HEAD requests for GOES products;
- one GOES flare GET and five conditional text GETs at 19:00:23Z.

`SOURCE.md` lists them. No other body is committed.

Observed facts that shape the policy:

- Every endpoint is served behind CloudFront with `cache-control: max-age=60`.
- Files are rewritten about every minute whether or not the content changes. A
  byte-identical body was returned 17 seconds later with a new `Last-Modified`
  and ETag. Five text products re-requested after three minutes were all
  identical but had new validators. Conditional requests save bytes only within
  the same one-minute generation, and validators cannot detect a content change.
- `alerts.json` held 83 records spanning about 28 days, newest first. The pair
  (code, serial) is not unique, because corrections reuse serials. The triple
  (code, serial, issue minute) was unique for all 83 records.
- Header lines use CRLF and body lines use LF. Normalization makes hashes
  stable.
- The Kp forecast labelled a future bin `estimated`. The label is provisional
  status, not past or future, so the past, current or future relation is
  computed from the bin start and end.
- JSON value types differ by product. The Kp forecast wrote every `kp` (81
  rows) as a JSON number with the source text `d.dd`. The scales values and
  probabilities were JSON strings or `null`, every alert record field was a
  string, and the OVATION cells were arrays of three integers. The contract
  states the type of every field it reads, and a value of another type is
  rejected, never coerced.
- `noaa-scales.json` and the 3-day forecast disagreed on R probabilities (25/5
  against 30/10). Both are shown with their own stamps and are never merged
  (U6).
- OVATION had 669 nonzero cells at latitudes -2 to 0. A site band in the site's
  own hemisphere excludes them.
- No 429, 5xx or `Retry-After` was observed. Failure handling is designed from
  the stated policy, not from evidence. The capture was taken during quiet
  conditions (G0/R0), with recent G1 to G3 activity in the alert history.

No CANCEL WARNING was published inside the capture window. The only public
example found is third-party text. It is not committed, and s09 uses a
disclosed synthetic record built from the documented rules and the captured
CANCEL WATCH.

## Ownership and seams

The existing environmental foundation is reused as a pattern, not as a type:

- `EnvironmentalObservationKind` carries one scalar per observation, and adding
  a kind forces a schema version and a central outbox change.
- The environmental store fails closed without migrations.
- Its deployment-location gate is wrong for a global feed.
- #1398 forbids forcing these facts into terrestrial fields.

Space weather therefore gets its own seams, all **Proposed** until the
implementation PR:

- **CameraAgent.Common.** `CameraAgent.Common/SpaceWeather/` owns the provider
  reader, the durable refresh job, its own STRICT SQLite database, the snapshot
  and message stores, the attempt journal, retention and health.
- **HTTP client.** A named client with redirects disabled, cookies disabled, a
  bounded read, a content-type check and sanitized reason codes. No generic
  retry library is used.
- **Processing.** Pure host-neutral fact records go in a
  `HVO.SkyMonitor.Processing` space-weather namespace, following the
  `EnvironmentalObservationContracts` precedent. AgentCore gains nothing.
- **Astronomy.** The geomagnetic-latitude helper belongs in
  `HVO.SkyMonitor.Astronomy`.
- **Refresh and Automations.** Scheduled and manual refresh share one durable
  task. Automations gets a space-weather task kind through the periodic-adapter
  seam, which #528 lands and #1398 reuses. Following the convention Batch A
  accepted for `OrbitalElementsRefresh` on #1391, the enum value and persisted
  spelling is `SpaceWeatherRefresh`. `space-weather-refresh` is only a prose
  and telemetry label.
- **Rollback.** A binary older than the implementation cannot read an
  automation store that holds the new kind, and fails that store closed. The
  database is preserved, and recovery is by upgrading forward. Deleting,
  archiving or restoring the database is not a routine rollback.
- **UI.** A `/operations/space-weather` section and a reusable notice panel.
  Batch A can later embed the panel with one line on its Observatory surface.
- **Sun and twilight.** Site context comes from the existing deployment
  location, solar-altitude and solar-event services.

**#1092 coordination.** #1092 owns terrestrial weather providers. This work does
not change `IEnvironmentalSource`, the environmental fact schema or its store.
The shared pieces are the HTTP client shape, the reason-code redaction helper
and the periodic Automations seam. The #1391 decision assigns that seam to
#528, which lands it once. #1398 and #1092 reuse it unchanged.

**#455 coordination.** #455 applies only if external multichannel delivery is
requested later. Local notices do not wait for it, and nothing here activates
email, SMS or webhooks.

## Network, cadence and metrics for #1395

The scheduled baseline uses the captured transfer sizes and excludes HTTP
header overhead. All requests come from one poller per deployment, never one
per camera.

| Product | Base interval | Requests per day | Wire bytes per fetch | Wire bytes per day |
| --- | --- | --- | --- | --- |
| alerts | 120 s | 720 | 5,174 | 3,725,280 |
| scales | 300 s | 288 | 216 | 62,208 |
| Kp forecast | 900 s | 96 | 509 | 48,864 |
| 3-day forecast | 1,800 s | 48 | 838 | 40,224 |
| **Core total** | | **1,152** | | **3,876,576** |
| OVATION, if U3 is ever approved: 600 s for at most 12 dark hours | 600 s | ≤ 72 | 150,748 | ≤ 10,853,856 |
| **Scheduled with OVATION** | | **≤ 1,224** | | **≤ 14,730,432** |

Uniform ±20% jitter leaves the mean rate unchanged. Even if every draw were
-20%, the scheduled core total would be 1,440 requests per day.

**Upper bound.** Manual refresh can request a product once every 60 seconds.
That is at most 1,440 requests per product per day, or 5,760 for the four core
products. At the captured sizes that is 9,701,280 wire bytes per day. At the
Proposed decoded-body caps below, at most 2,076,180,480 decoded bytes per day
are accepted for the core products, or 8,115,978,240 with OVATION.

**Proposed limits:**

- Connect timeout 10 s. Total deadline 30 s, or 60 s for OVATION.
- At most one outbound request at a time per provider.
- Uniform ±20% jitter on scheduled attempts. Jitter never schedules an attempt
  before the minimum spacing, a backoff or a `Retry-After` time.
- Decoded-body caps: 1 MiB for alerts, 64 KiB for scales and text, 256 KiB for
  Kp, 4 MiB for OVATION.
- At most 2,000 alert records per body and 16 KiB per message.

**Proposed retention:**

- Messages are kept until 35 days after their notice ends. That is longer than
  the observed 28-day upstream window.
- For scales, Kp and the 3-day forecast, each snapshot whose content hash
  changed is kept for 35 days. If every scheduled fetch changed content at the
  captured sizes this would reach 1,078,464 bytes per day, or 37,746,240 bytes
  over 35 days. Real change rates are far lower. At the upper bound above,
  with every manual fetch at its decoded cap and changed, it could reach
  566,231,040 bytes per day.
- Raw snapshots are therefore also capped at 256 MiB in total, which can end
  the 35-day period early. Over the cap, the oldest snapshots that are not
  last-known-good are pruned first.
- For alerts, only the last-known-good raw body is kept, plus each distinct
  message version with the snapshot hash and retrieval time that introduced it.

No CPU, allocation, working-set or latency figure is claimed. A
recorded-fixture benchmark in the implementation PR, and #1395's integrated
run, provide those.

## Decisions U1 to U15

| ID | Decision | Status |
| --- | --- | --- |
| U1 | Alerts poll every 120 s. NWS guidance allows "once or twice a minute" for short-fused warnings. Warning lead times are tens of minutes, and alerts are issued after the fact | Proposed |
| U2 | No 1-minute Kp in v1. Possibly revisit it only while a warning is active | Proposed |
| U3 | OVATION acquisition, display threshold, wording, and darkness gating. The fixture records the band, maximum and artifact count only | **Unresolved**. The first tranche reports Kp/G relevance without OVATION |
| U4 | The 3-day forecast is the only text forecast | Proposed |
| U5 | Credit text, above | Approved |
| U6 | Scales and 3-day forecast probabilities are shown separately, each with its own stamp | Proposed |
| U7 | Kp labels are shown verbatim, with a computed past/current/future relation | Proposed |
| U8 | Own 35-day message history, with no backfill from FTP or NCEI | Proposed |
| U9 | Unknown codes are shown raw as unclassified, with a coverage metric | Proposed |
| U10 | Centred-dipole geomagnetic latitude (pole 80.8 N, 72.6 W; error of a few degrees), documented as approximate. At HVO it is 42.2° | Proposed |
| U11 | CANCEL WARNING is implemented from the documented rules, with a disclosed synthetic fixture | Proposed |
| U12 | Watch day lines and the 3-day forecast's table columns carry no year. The year is inferred from the watch's issue time, or from the forecast's `:Issued:` time, with a six-month window, so 31 December rolls over (s14) | Proposed |
| U13 | Placement: one CameraAgent-local poller per deployment | Approved |
| U14 | K4/G1 items for a low-latitude site are listed under global activity with "not expected locally" | Proposed |
| U15 | The `noaa-scales.json` production cadence is unverified. It is polled every 300 s | Proposed |

## Changes from the research draft

The research draft became the policy below. The reference model and the
fixtures encode the final form.

- **Halts replace long backoff.** The draft backed 403, 404 and 410 off to six
  hours. The checkpoint halts instead, until an operator resumes, matching the
  #527 convention and the NWS invalid-link guidance:
  - 403 halts the whole provider;
  - three consecutive 429 responses halt the whole provider;
  - a redirect, 404, 410 or other 4xx halts that product.

  As in #527, a resume remains subject to the next permitted attempt. It never
  bypasses minimum spacing, a running backoff or a `Retry-After` time (s19).
- **Empty bodies are rejected.** The draft accepted an empty alert list once
  last-known-good was more than 35 days old. The checkpoint rejects `[]` as
  suspect: an empty rolling window has never been observed, and it cannot be
  told apart from a broken pipeline. If SWPC ever publishes a legitimately
  empty window, the operator sees format-drift attention and outdated freshness
  rather than a silent "no notices". This is a Proposed default.
- **Freshness uses retrieval time.** The draft mixed retrieval and in-file
  thresholds. The in-file stamp is now shown as its own fact.
- **The active cap is 96 hours.** The draft's 72 hours cut a valid three-day
  watch short when the watch was issued the afternoon before its first day
  (s08).
- **Windows are simpler.** K-alerts end at their synoptic period, X-ray alerts
  three hours after the threshold, and a cancel ends the whole watch at its
  issue time. SWPC downgrades a watch by issuing a new superseding watch, not
  by a partial cancel.
- **The 3-day forecast has no extra fetches.** The draft added fetches at 00:40
  and 12:40. The 1,800 s cadence picks up each issue within 30 minutes.
- **The fixtures use the deployment-profile site.** The draft used approximate
  site coordinates and got 41.9°. The fixtures use the deployment-profile
  location (35.5599378, -113.9119818), which gives 42.2°.

## Evidence

- Contract: [`docs/contracts/space-weather-facts-v1.md`](../contracts/space-weather-facts-v1.md).
- Fixtures: [`tests/fixtures/space-weather/swpc-v1/`](../../tests/fixtures/space-weather/swpc-v1/SOURCE.md).
  To verify, run:

  ```bash
  python3 -I tests/fixtures/space-weather/swpc-v1/fixtures.py check
  ```

  It verifies every manifest hash and the recorded capture facts, regenerates
  all 22 scenarios byte for byte, and replays each one through the reference
  model against its expectations.
- Research notes, raw headers and the request ledger are kept outside the
  repository as reviewer evidence. They are not needed to verify the pack.
