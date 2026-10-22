# CameraAgent Space-Weather Refresh

CameraAgent keeps NOAA SWPC facts and local notice history in its own SQLite store. One
reserved automation refreshes the configured core products. LogicHost never polls them.
There is no outbox, notification delivery, email, SMS, webhook, capture action or
`IEnvironmentalSource` integration. External delivery remains under #455; terrestrial
weather acquisition remains under #1092. Operators work through the
[Operator UI](#operator-ui) below.

## Credentials And Attribution

SWPC needs no credential. The fixed User-Agent is `HVO.SkyMonitor/<version>` followed
by the project URL, as defined by `SwpcSource`. No NOAA or NWS logo is used.

Space weather data: NOAA Space Weather Prediction Center (SWPC). Displayed by HVO
SkyMonitor; not endorsed by NOAA/NWS. Local relevance is derived by HVO SkyMonitor
and is not an official forecast.

## Enable And Configure

1. Set `CameraAgent:SpaceWeather:Enabled` to `true` and restart CameraAgent. The
   catalogue key is `spaceWeather.enabled`; the CLI commands are:

   ```bash
   hvo-sky config get spaceWeather.enabled
   hvo-sky config set spaceWeather.enabled=true
   hvo-sky restart
   ```

   The switch applies only after a restart; the running value and a saved value
   pending restart are shown separately. Environment variables and `config/secrets`
   take precedence, and an overridden switch is read-only and names its source.
   There is no web switch; the Space weather page shows the current state and these
   commands.
2. Create the `space-weather-refresh` local automation definition, task kind
   `SpaceWeatherRefresh`, target `swpc`, trigger `Periodic`, with an interval of
   60–120 seconds (default 120), and enable that definition. **Automations** opens
   this definition pre-filled at `/operations/automations?setup=space-weather`; the
   identifier, target and trigger are fixed, and an existing definition is kept and
   never replaced. Creation does not contact the provider.

No definition is seeded. Saving a definition while the feature is disabled is allowed;
execution skips with `space-weather.disabled`. `Products` defaults to `Alerts`,
`Scales`, `KpForecast` and `ThreeDayForecast`; the list must be nonempty, distinct and
contain only those enum values. `Ovation` fails binding or startup validation.
These are the only options. Cadence, jitter, retention, timeouts and provider URLs are
fixed. The store initializes even while disabled, keeping last-known-good readable.

The automation interval is a **runner tick**, not the provider cadence. A tick runs
configured products in Alerts, Scales, KpForecast, ThreeDayForecast order. Policy
refusals are normal results. No new product starts after 30 seconds of injected-clock
run time; remaining tokens are `budget`. A request already started may take another
30 seconds. The existing bounded automation worker runs the tick outside the sweep.

## Provider Policy And Freshness

| Product | Base cadence | Decoded cap | Media type | Outdated after |
| --- | --- | --- | --- | --- |
| alerts | 120 s | 1 MiB | application/json | 3,600 s |
| scales | 300 s | 64 KiB | application/json | 10,800 s |
| kp-forecast | 900 s | 256 KiB | application/json | 21,600 s |
| 3-day-forecast | 1,800 s | 64 KiB | text/plain | 93,600 s |

Freshness is `none` before any success, `fresh` below three base intervals, `stale`
below the outdated threshold, and `outdated` otherwise. The in-file stamp is separate.
An unchanged retrieval refreshes last success; a stale snapshot does not.

There is one outbound request at a time, a 10-second connect timeout and a 30-second
total client deadline. Redirects, cookies, credentials, retry/resilience handlers and
HTTP loggers are disabled. Scheduled attempts have uniform ±20% base-interval jitter.
Every product keeps its own 60-second minimum spacing, backoff and Retry-After floor.

403 halts the provider. Three consecutive 429 responses for one product halt the
provider. Redirects, 404, 410 and other 4xx responses halt that product. Transient
failures back off to at most 1,800 seconds. A 429 uses Retry-After, clamped to
60–21,600 seconds, or a 1,800-second fallback. Manual refresh never clears a halt or
bypasses spacing, backoff or Retry-After. Resume clears only the requested halt.
Counters and all existing floors remain authoritative.

## Status, Notices, Refresh, Resume And Acknowledge

All routes require the owner operations policy. Reads allow an owner session or
read API key; mutations require an owner session or read-write API key. Session
mutations also require antiforgery. Every mutation needs exactly one printable,
trimmed `Idempotency-Key` of at most 128 characters.

| Method | Path under `/api/v1/operations/space-weather` | Result |
| --- | --- | --- |
| GET | `/` | Redacted per-product status, provider halt, availability, definition existence/enabled state and credit |
| GET | `/notices` | `state` absent or pending/active/recent/history; exclusive ordinal `cursor`; `limit` 1–50, default 25 |
| POST | `/refresh` | No body needed; queues the reserved definition; 202 queued, 200 replay |
| POST | `/resume` | `{scope, product?, expectedHaltSinceUtc}`; 200 resumed/replayed/not-halted |
| POST | `/acknowledge` | `{group}`; 200 acknowledged/replayed |

A provider resume has `scope: "provider"` and no product. A product resume has
`scope: "product"` and a configured product token from the table above. Echo the
status halt's `sinceUtc` exactly: it is UTC round-trip (`O`) text. Missing or non-UTC
time is 400 `space-weather.resume-invalid-halt-time`; a different halt is 409
`space-weather.resume-halt-changed`. The authenticated actor (bounded to 128
characters) and server time populate the acknowledgement's operator and time.
No body can supply a different actor, provider URL, schedule or storage path.

Acknowledgement records attention disposition. It does not change pending/active/
recent/history lifecycle, expiry or eligibility for later escalation. Notice paging
is stable in ordinal identity order within the current retained view; it is not a
snapshot across concurrent refresh, expiry or purge.

A reused key is reported as 409 `space-weather.idempotency-key-reused` and is not applied again while its receipt is retained (35 days, and at most the 10,000 most recent resume and acknowledge commands). A resume names the halt it clears, so a retried resume never clears a later halt. Read the status to confirm the earlier effect, and use a new key for every new command.

Resume and acknowledge derive a UUIDv8 from the first 16 bytes of SHA-256 over
`hvo.skymonitor/space-weather/` + command kind + LF + key, setting the RFC 9562
version and variant bits. Refresh retains the automation store's string key and its
seven-day replay window, bound to the reserved definition version. A retry after a
successful resume normally returns `not-halted`; later acknowledgement retries may
return key-reused instead of the first response.

Commands serialize with the complete job run. Operations wait five seconds on the
injected clock, then return 503 `space-weather.busy` with a fresh ungated status read.
If that read fails, the reason is `space-weather.store-unavailable`. Request
cancellation propagates. Timed reads can purge but cannot change halts, checkpoints
or acknowledgements on retained groups. A purge can bump the revision between a
command read and write: 409 `space-weather.revision-conflict` writes no receipt, so
the client may retry with the **same key**. There is no internal retry loop.

Other fixed reasons include `space-weather.invalid-idempotency-key`,
`space-weather.resume-invalid-scope`, `space-weather.invalid-command`,
`space-weather.unknown-notice` (404), `space-weather.invalid-paging` (400),
`space-weather.antiforgery-invalid` (400), `space-weather.disabled`,
`space-weather.definition-missing`, `space-weather.definition-disabled`,
`space-weather.halted`, and the automation run-now conflict reasons. Rejections use
fixed problem titles and `reasonCode`; authenticated command problems carry
`currentStatus` when available. Authentication/antiforgery and framework binding
failures disclose no status. Storage failure can only report an unavailable view,
not claim current durable facts.

### Run Outcomes

| Outcome | Rule |
| --- | --- |
| Skipped | disabled; store unavailable before admission; all configured products refused halted; all refused not-due |
| Failed | explicit retention failure; any transient-failure, rejected, rate-limited or newly halted product; storage/runner failure or noncommitted attempted result |
| Succeeded | remaining cases, including ordinary manual spacing/backoff refusals and partial budget completion |

Detail is a reason code plus ordinal-sorted `product=outcome` tokens, bounded to
512 characters. Cancellation is never converted into Failed. Explicit retention
runs once before any product. A retention exception stops the run with
`space-weather.retention-failed` and attempts no product.

## Operator UI

**Operations → Space weather** (`/operations/space-weather`, in the Processing
group) is the operator page. Reading it requires the owner operations read policy.
Refresh, resume and acknowledge require an owner session that may mutate; otherwise
they are disabled with a visible reason. The page calls the same in-process
operations as the API above and adds no poller, timer or provider request of its own.

Reads go through one shared, cached projection. A result at most 10 seconds old is
reused, concurrent readers share one read, and that read has its own 10-second
timeout. A failed read is cached for the same 10 seconds. Every command drops the
cache. The page reads when it opens, on **Reload** and after each command. If status
and notices change between the two halves of one read, it retries once and then says
the data changed while reading.

The page shows the feature state, the alerts freshness chip and the schedule link
(**Set up schedule** opens `?setup=space-weather` when the definition is missing). It
lists one sources row per product, showing:

- whether the product is configured;
- the last outcome, freshness, last success and in-file stamp;
- the next attempt and the retry not-before time;
- the consecutive counters and any halt.

It then shows format drift (three or more consecutive rejected bodies), record drift
on configured products, and halts. A retained halt on a product that is not configured
is labelled as such and offers no Resume. The notice lists follow: active and pending,
recent, and paged history.
"Cloud: not assessed" appears once, and the SWPC credit is shown verbatim.

Each of these states has its own banner:

- loading;
- feature off, where retained notices may still be listed;
- no successful fetch yet;
- "No SWPC notices retained.", never "no space weather";
- definition missing or disabled;
- store unavailable;
- data changed while reading;
- read failed.

When the store cannot be read, nothing is claimed about current notices.

### Bounds

| List | Bound |
| --- | --- |
| Active and pending | At most 50 combined, then "N more" with the exact count |
| Recent (24 h after the end) | At most 50, then "N more" |
| History | Pages of 25, eight pages (200 rows), then "N older retained notices are not shown" |
| Overview summary | The first three active or pending rows, then "N more" |

Counts are exact and taken before bounding. Membership can change between reads, and
paging makes no stability promise. Active rows come before pending ones, each newest
issue first. Recent and history rows are ordered by end time, newest first. Ties are
ordered by group identity.

### Notice Rows, Relevance And Darkness

Each row shows:

- the family label, level, state and end reason;
- the acknowledgement;
- Issued, Last update, From and Until, in UTC and in site-local time with offset and
  zone, falling back to labelled UTC when the zone is missing or invalid.

Pending and active rows also say "may be out of date: alerts are not fresh" while
alerts are not fresh.

Two lines are kept apart:

- **SWPC:** the source's global activity, such as "G2 watch", or the family with
  "level not stated".
- **At this site:** an HVO SkyMonitor estimate, ending "HVO SkyMonitor estimate, not a
  NOAA statement."

For a geomagnetic notice, the site line gives one of these categories:

- "within the stated impact latitude";
- "possibly near the poleward horizon";
- "unlikely at this latitude";
- "not expected at this latitude";
- "unknown", with its reason.

It also gives:

- the required latitude, either stated or typical for the level;
- the site's geomagnetic latitude;
- the model: centred-dipole approximation (pole 80.8° N, 72.6° W); model version
  unspecified.

Other notices say "no local optical observing impact". Without an active site
location, relevance is unknown ("site location not configured").

Darkness covers pending and active rows only. It only ever adds a restriction and
never raises a category. It lists site daylight or twilight intervals within the
notice window in local time. Otherwise it says one of:

- "darkness not assessed for windows longer than 96 hours";
- "site daylight/twilight could not be determined for this window";
- "not assessed: no site location".

The page never calls a notice dark, visible or observable. Cloud is not assessed.

### Escalation Since Acknowledgement

An acknowledgement never hides, ends or reorders a notice. For an acknowledged group,
the page derives the following on read; it writes nothing and raises no attention
event:

1. Every group version must join to its retrieval time. If any does not, the result
   is **Unknown**.
2. The baseline is the highest rank among non-cancel versions retrieved before the
   acknowledgement. A version's rank is its G level; K4 or no level counts as 0.
3. The result is also **Unknown** when a non-cancel version was retrieved at exactly
   the acknowledgement time, or when no non-cancel version precedes it.
4. Otherwise the row reads "escalated since acknowledged" if the current level's rank
   exceeds the baseline. If it does not, the group is not escalated.

An acknowledged row reads "Acknowledged" with the acknowledgement time, followed by
"escalated since acknowledged" or, for Unknown, "escalation since then cannot be
determined"; a row that is not escalated adds nothing. An Unknown row counts toward
the reach-or-approach attention item when its category qualifies, and it is offered
for acknowledgement again. Acknowledging it afresh records a new acknowledgement time.

### Commands

**Refresh now (all enabled products)** queues the reserved definition. It is disabled,
with the reason shown, when:

- the feature is off;
- the definition is missing or disabled;
- a provider halt is active;
- the store is unavailable;
- the user cannot mutate.

Provider policy still decides what is fetched.

**Resume** confirms one provider or product halt. It sends the halt start exactly as
read, without the minute rounding used for display. If the halt has changed, the
dialog closes and the page rereads.

**Acknowledge** is offered on pending and active rows that are unacknowledged,
escalated since acknowledgement, or of undeterminable escalation.

Each deliberate command uses a new idempotency key, and repeated clicks while a
command runs are ignored. The key is kept for a retry in two cases:

- an unavailable outcome, where the durable result is unknown, so the retry repeats
  the same request and key;
- a revision conflict.

After `space-weather.idempotency-key-reused`, the next attempt uses a new key. Known
`space-weather.*` reasons have fixed operator text, and the same text appears in
Automations for the reserved definition's identifier and interval rejections.

### Overview, Attention And Health

The **Overview** shows a compact summary from the Operations layout's read:

- the feature state and the alerts freshness;
- the active and pending count with the first three rows;
- the page link and the credit.

When off, it says "Space weather is off" and lists nothing. When the data cannot be
read, it says "Space-weather data is unavailable".

The sidebar badge and the Overview's **Needs attention** list carry these items:

| Item | When |
| --- | --- |
| "N notices reach or approach this site's geomagnetic latitude" | Warning. Pending or active rows within the impact latitude or possibly near the horizon, that are unacknowledged, escalated or of unknown escalation |
| "N notices to review" | Other pending or active rows that are unacknowledged or escalated |
| "Format drift on P" | One per affected product |
| "Record drift" | An open record-drift episode |
| "SWPC fetching halted (provider)" / "(P)" | A provider halt, or a halt on a configured product |
| "Space-weather store unavailable" | The store cannot be read and the feature is on |

There are no items while the feature is off, and a failed read keeps no stale items.
A failed read while the switch is on shows only the store-unavailable item; a denied
read shows none.
The layout rereads on navigation, and on other Operations activity once its last read
is at least 30 seconds old.

**System** lists the `space-weather` health check as **Space weather** (Local), linked
to the page. With the feature off and the store readable, the row shows Disabled.
Otherwise the check is at most Degraded.

## Health And Metrics

`/health` registers `space-weather`, tagged `dependency`. It is at most **Degraded**.
A readable disabled store is Healthy with description `disabled`. Reasons are evaluated
in this order:

| Reason | Rule |
| --- | --- |
| `space-weather.store-unavailable` | Initialization, integrity or storage read fails, including while disabled |
| `space-weather.provider-halted` | Provider halt exists |
| `space-weather.product-halted` | A configured product halt exists |
| `space-weather.alerts-outdated` | Configured alerts have outdated retrieval freshness |
| `space-weather.format-drift` | A configured product has at least three consecutive rejected bodies |
| `space-weather.record-drift` | Configured alerts have an open record-drift episode |
| `space-weather.first-success-overdue` | A configured product has no success, and its earliest retained attempt is at least that product's outdated threshold old |
| `space-weather.store-oversized` | Retained raw-body bytes exceed twice the 256 MiB raw-history cap |

First-success evidence is retained-journal evidence: without a retained attempt,
including after 35-day purge or the journal cap, no first-attempt age is claimed.
Health pages that product's bounded journal instead of using another product's start.
Healthy enabled state reports `space-weather.ready`. Health data contains only
`Reason` and `Availability`; it never contains provider text, headers or paths.

Meter: `HVO.SkyMonitor.CameraAgent.SpaceWeather`. Prefix:
`skymonitor.cameraagent.space_weather.`

| Instrument | Tags | Meaning |
| --- | --- | --- |
| attempts | product, outcome | Durably applied completed provider outcomes |
| fetch_duration | product | Client exchange milliseconds, excluding policy/store work |
| decoded_bytes | product | Returned decoded body bytes |
| attention_raised | kind | new/escalated/format-drift/record-drift local attention |
| runs | outcome | Succeeded/Skipped/Failed runs |
| retention_deleted | kind | groups/versions/snapshots/raw-bytes from explicit retention |
| product_age_seconds | product | Last-success age from cached state |
| active_notices | family | Active group count by source family |
| halted | scope | Provider halt, or any product halt, as 0/1 |
| store_bytes | none | Retained raw-body bytes; excludes database/index/journal overhead |

All labels come from closed enum or reason vocabularies. Gauges never open the
store. Initialization, settled runs and applied commands publish cached snapshots.
Explicit retention counters do not count automatic purge inside other timed calls;
the frozen public seam returns deletion counts only for explicit retention.
Performance evidence reports physical database/sidecar bytes separately.

## Logs

Only generated LoggerMessage events are used. No exception object, stack trace,
exception message, body text, URL query, header or validator is logged.

| EventId | Level | Meaning |
| --- | --- | --- |
| 7810 | Information | SQLite schema initialized (store) |
| 7811 | reserved | Store range, not emitted |
| 7812 | Warning | Completion projection failed, exception type only (store) |
| 7813–7824 | reserved | Store range, not emitted |
| 7825 | Information | Run admitted, trigger and product count |
| 7826 | Information | Run settled, outcome/reason/bounded detail |
| 7827 | Warning | Start budget exhausted, product tokens |
| 7828 | Warning | Storage unavailable/failure, exception type only |
| 7829 | Information | Retained incomplete attempts observed before first run/resume recovery |
| 7830 | Information | Refresh queued or replayed |
| 7831 | Information | Resume accepted, scope/product token |
| 7832 | Warning | Resume/acknowledge rejection, reason only |
| 7833 | Information | Acknowledged group identity |
| 7834 | Warning | Antiforgery rejected |
| 7835 | Warning | Operator UI read failed, exception type only |
| 7836 | Warning | Operator UI command failed, command token and exception type only |
| 7837 | Information | Operator UI access denied, read or command token |
| 7838 | Warning | Operator UI projection still inconsistent after one retry |
| 7839 | reserved | Operator UI range, not emitted |

The 7829 observation precedes the first runner call. It identifies attempts to
settle, not proof that a failing recovery succeeded. The runner itself owns settlement.

## Storage, Retention And Recovery

Everything lives under `<RawIngressRoot>/.space-weather/`, database `space-weather.db`,
STRICT schema `PRAGMA user_version=1`, WAL and FULL synchronization. The exclusive
root claim permits one writer. The store initializes regardless of enablement.

Groups, versions, ranks and acknowledgements expire 35 days after the notice ends.
Completed attempts live 35 days from start, capped at 50,000; incomplete attempts are
never pruned. Resume/acknowledgement receipts live 35 days, capped at 10,000. Non-LKG
snapshots live at most 35 days and share a 256 MiB raw-history cap; alerts keep only
LKG raw bytes plus distinct original message versions. LKG remains pinned.

Before its first run or resume, the runner settles incomplete attempts as interrupted
without changing checkpoint, halt, counters, backoff, validators or LKG. No request is
automatically replayed. Storage failures disclose a fixed reason and type-only event.
Repair the root or storage fault; initialization retries without stopping the host.
Never delete state to clear a halt or rewind a permitted-attempt floor.

The image and release contract declare `spaceWeather: {writes: 1, reads: {min: 1,
max: 1}}`. Like orbital state, it is deliberately excluded from #1340 migrating-upgrade
snapshots. Restoring an older copy would rewind per-product Retry-After floors,
backoff and provider/product halts. Ordinary whole-instance backup restore can rewind
it; preserve the latest attempt/halt evidence and keep refresh disabled until those
provider constraints are reconciled. Future incompatible schemas must revisit the
snapshot exclusion.

## Downgrade

Remove the `space-weather-refresh` definition before considering an older binary.
**Removal alone is insufficient**: retained definition revisions and immutable run
occurrences still contain `SpaceWeatherRefresh`, which an older binary cannot parse.
It fails the automation store closed. The supported recovery is upgrading forward.
This follows the existing retained-history behavior; this slice adds no history
rewrite, durable cache, schema downgrade or automatic archive/disposition workflow.
Leave `.space-weather/` intact so its floors and halts survive roll-forward.

## Differences From Orbital Acquisition

SWPC has an append-only attempt journal bounded by retention; interrupted startup
settlement preserves policy state, rather than creating an unknown-outcome halt.
It uses the project User-Agent, a short runner tick with independent product cadences,
per-attempt jitter instead of a persisted phase offset, outcome-specific backoff and
product/provider halts instead of halt-on-any-non-200, and separate connect/total
timeouts instead of one configurable request timeout. Retention precedes the whole
run and is mandatory, rather than best-effort after publication.

## Offline Evidence And Live Probe

`SpaceWeatherRefreshPerformanceTests` is Manual, uses committed capture/scenario bytes
and disclosed synthetic record-bound bodies, and emits JSON under
`HVO_ISSUE1398_EVIDENCE_ROOT` (default `TestResults/issue-1398/`). It measures tick,
retention, restart, a compressed day, sweep overhead and capture interference. The
[performance record](../planning/performance-validation.md) gives results and bounds.

`SwpcLiveProbeTests` is Manual and Inconclusive unless
`HVO_ISSUE1398_LIVE_PROBE=1`. It makes exactly one GET per core product through the
production client composition, checks status/media type/parsing, and persists and
logs no body. It is external evidence, never a Unit/Integration gate. It is not run
as part of this slice's offline validation.
