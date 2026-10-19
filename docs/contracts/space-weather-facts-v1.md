# Space-weather facts v1 (#1398)

Status: design checkpoint under the
[#1398 decision](../planning/space-weather-source-decision-1398.md), dated
2026-10-19. The fixture pack in `tests/fixtures/space-weather/swpc-v1/`
contains a reference model, `fixtures.py`, that encodes this policy so the
scenarios can be checked. That model is a specification aid, not production
code. This document does not show that any implementation exists.

When this document and the reference model disagree, that is a defect in the
checkpoint. Fix both together and regenerate the pack.

## Scope and ownership

This contract covers NOAA SWPC products that are acquired by one
CameraAgent-local authority per deployment. It defines the following:

- the facts that are kept;
- how notices are identified, grouped and aged;
- when local attention is raised;
- how fetch outcomes, halts and freshness behave;
- how site relevance is derived.

It is not a general data-feed framework. It is not a notification framework,
and it is not an image layer.

Proposed ownership, to be confirmed in the implementation PR:

- **`HVO.SkyMonitor.Processing`** (space-weather namespace) holds the pure fact
  records and enums below. They contain no storage, HTTP, ASP.NET or DI types.
- **`HVO.SkyMonitor.Astronomy`** holds the geomagnetic-latitude helper.
- **`CameraAgent.Common/SpaceWeather/`** holds the provider reader, the durable
  refresh task, the STRICT SQLite store, retention, health, the attempt journal
  and diagnostics.
- **The CameraAgent host** holds the operator surface, the refresh, resume and
  acknowledge endpoints, and DI.

LogicHost does not consume these facts in v1. If a central consumer is approved
later, it reads the same fact records. It is never a second poller.

## Fact taxonomy

Every fact is global. Location applicability is a separate derived fact
(`LocalRelevance`). The G, R and S families are never collapsed into one
severity, and probabilities are never converted into scales. SWPC's own words
(watch, warning, alert, summary) are kept. SWPC publishes no confidence value
other than its probabilities, and none is invented.

| Fact | Kind | Content |
| --- | --- | --- |
| `SourceSnapshot` | provenance | Product key; canonical URL path; fetch start (UTC); HTTP status; media type; decoded byte count; snapshot SHA-256; validators; in-file stamp; outcome |
| `SwpcMessage` | issued notice version | Identity (message code, serial, issue minute); `product_id` and `issue_datetime` as attributes; headline kind and modifier; corrected flag; link (extension, continuation or cancel target serial); scale token and raw descriptor; impact latitude when stated; parsed time fields; watch day lines; the normalized text and its SHA-256; the original record |
| `ScaleReading` | observation or forecast | Window (`observed24hMax`, `current`, `predictedDay1..3`), stamp, and the R, S and G scale values and probabilities as SWPC wrote them, including `null` |
| `KpBin` | observation or forecast | Bin start (UTC); bin end (start plus 3 hours); Kp as the source decimal text (for example `"4.67"`, never rounded to `4+`); the source label verbatim (`observed`, `estimated` or `predicted`); NOAA scale or `null` |
| `IssuedForecast` | forecast | `:Issued:` time (UTC); Kp breakdown bins with G marks; S and R probabilities; normalized-text SHA-256 |
| `AuroraNowcast` | forecast model output | Observation time; forecast time; lead in minutes; per site, the band bounds, cell count and maximum value; the number of equatorial cells ignored; the original grid SHA-256. Data only: acquisition and wording are unresolved (U3) |
| `Notice` | derived lifecycle | Group identity; family; level; state; window bounds; end reason; latest version; version count; corrected flag; orphan flag; acknowledgement; per-site relevance; watch days; site-local projection of the window |
| `LocalRelevance` | derived HVO estimate | Site; category; basis (impact latitude stated in the message, or the typical latitude for the level); the computed geomagnetic latitude; the model version |

Observation, forecast, watch, warning and alert are told apart as follows:

- **Observation.** A scale reading of `observed24hMax` or `current`, or a Kp bin
  whose start is in the past.
- **Forecast.** A predicted scale day, a Kp bin whose start is in the future
  (whatever its label), or the 3-day forecast.
- **Watch, warning or alert.** Taken from the message headline. Summaries
  describe closed events and are never active.

A Kp bin's label stays verbatim. Its relation to now (past, current or future)
is computed from the bin start (U7), because SWPC labels future bins
`estimated` (s16).

## Normalization and hashes

**Text normalization:**

- CRLF becomes LF.
- Trailing spaces, tabs and carriage returns are removed from every line.
- Trailing blank lines are removed.
- The result ends in exactly one LF.

**Hashes:**

- **Alert message.** The content hash is the SHA-256 of the normalized message
  text.
- **Text product.** The snapshot hash is the SHA-256 of the normalized body. The
  3-day forecast (s17) is the example.
- **JSON product.** The snapshot hash is the SHA-256 of the decoded body bytes.

All hashes are 64-character uppercase hexadecimal.

**HTTP validators** (`ETag` and `Last-Modified`) change every minute even when
the content does not change (s01, s17). They may be sent as `If-None-Match` and
`If-Modified-Since`, but they never decide whether content changed.

## Alert records

**Per-record checks.** An `alerts.json` body is a JSON array. Each record is
validated on its own. A failure rejects only that record, with one of these
reasons:

| Reason | Condition |
| --- | --- |
| `record-schema` | Not an object; `product_id`, `issue_datetime` or `message` missing or not a string; `product_id` not 2 to 8 uppercase letters or digits |
| `issue-datetime` | `issue_datetime` is not `YYYY-MM-DD HH:MM:SS.fff` |
| `record-oversize` | The message is larger than 16,384 bytes |
| `message-header` | The first three lines are not `Space Weather Message Code:`, `Serial Number:` and a parseable `Issue Time:`, or the message has fewer than four lines |

**Identity.** Message identity is `CODE/serial/issue-minute`, all parsed from
the message text.

**Version handling:**

- A record whose identity and content hash are both already stored is a
  duplicate and has no effect (s01).
- The same identity with a different hash is a new version of that identity,
  reported as `source-revision` (s02).
- A repeat of the same record within one body is reported once, as
  `duplicate-record-in-body`.
- If `issue_datetime` disagrees with the text's issue minute, the record is kept
  under the text identity and `issue-time-mismatch` is reported.

**Grouping.** Records that `Extension to`, `Continuation of` or
`Cancel Serial Number` link together within the same message code form one
notice group. The group identity is `CODE/<lowest serial>`.

- A **correction** reuses the serial with a later issue minute and a
  `CORRECTED` line. It is a new version in the same group (s03).
- A **self-referencing continuation** names its own serial. It adds no link
  and is not an orphan; it stays in the group its serial already belongs to
  (s04, `ALTEF3/3751`).
- **Version order** is issue minute, then `issue_datetime`, then ingest order.
- If a link's target is not stored, the group is an **orphan**, reported as
  `orphan-link`. It is shown using its own times and nothing is inferred about
  the target. When the target arrives, `orphan-resolved` is reported and the
  groups join (s06). Orphan status is evaluated after the whole body is
  ingested, because SWPC lists records newest first.

**Families.** Each message is assigned a family from its code and headline
kind:

| Family | Codes and kinds |
| --- | --- |
| `watch` | `WATA*` with headline `WATCH` (including `CANCEL WATCH`) |
| `warning` | `WAR*` with headline `WARNING` (including `EXTENDED` and `CANCEL`) |
| `k-alert` | `ALTK04` to `ALTK09` with headline `ALERT` |
| `xray-alert` | `ALTX*` with headline `ALERT` |
| `radio-sweep` | `ALTTP2` and `ALTTP4` with headline `ALERT` |
| `flux-alert` | `ALTEF3`, `ALTPX*` and `ALTPC0` with headline `ALERT` (including `CONTINUED`) |
| `summary` | `SUM*` with headline `SUMMARY` |
| `unclassified` | Anything else, shown raw (U9) |

**Levels:**

- `WARK0n` and `ALTK0n` map to K4 for n = 4, otherwise to G(n−4).
- A watch takes its level from `Category G<n>` in its headline.
- Any other message takes its level from its `NOAA Scale:` token, matched
  without regard to case.
- Level rank is 0 for K4, otherwise the scale number.

**Watch day lines.** They carry no year. The year is inferred from the issue
time: a month more than six months earlier than the issue month belongs to the
next year, and one more than six months later belongs to the previous year
(s14).

## Notice lifecycle

A group's window is `[from, until)`, half-open (s05). The window is computed
from the group's latest non-cancel version:

| Family | `from` | `until` |
| --- | --- | --- |
| `watch` | Root issue time | 24:00 UTC at the end of the last listed day whose level is not `None`, or `from` if every listed day is `None` |
| `warning` | The root's `Valid From` | The latest `Now Valid Until`, otherwise `Valid To` |
| `k-alert` | `Threshold Reached` | End of the `Synoptic Period` on that UTC date |
| `xray-alert` | `Threshold Reached` | 3 hours later |
| `radio-sweep` | `Begin Time` | 1 hour later |
| `flux-alert` | The root's `Threshold Reached`, otherwise `Begin Time` | 30 hours after the latest non-cancel issue time |
| `unclassified` | Root issue time | 6 hours later |
| `summary` | Root issue time | Never active |

**Fallback.** If a required time is missing or unparseable, the window falls
back to the root issue time plus 6 hours.

**Adjustments.** The following are applied in this order. Each applies only if
it is earlier than the current `until`, and each sets the end reason:

1. **Watch supersession** (`superseded`). Every watch states that it supersedes
   all prior watches. A later watch of any level ends an earlier watch at the
   later watch's root issue time (s07, s08).
2. **Cancel** (`cancelled`). The group ends at its earliest cancel's issue time
   (s07, s09). If a cancel's target is absent, the group contains only the
   cancel, which is recent from its issue time.
3. **Cap** (`capped`). No group stays active later than 96 hours after its
   latest non-cancel issue time.

Otherwise the end reason is `expired`.

**States** at time `t`:

| State | Condition |
| --- | --- |
| `pending` | `t < from`, for example a prospective `Valid From` |
| `active` | `from ≤ t < until` |
| `recent` | `until ≤ t < until + 24 h`. A summary is recent for 24 hours from its root issue time |
| `history` | Later than recent, until purged |

**Retention.** A group and its versions, raised levels and acknowledgements are
purged once `t ≥ until + 35 days`. Upstream `alerts.json` is a rolling window of
about 28 days. A record that leaves that window was not cancelled. It stays
until its own retention ends (s10).

**Projection.** Windows are stored in UTC. The operator surface also shows them
in the site's IANA time zone (s14).

## Local attention

Attention is a local operator-surface event. It never sends anything outside
the host and never triggers a capture or safety action.

- **New.** A group raises `new` once, when it is first ingested in the
  `pending` or `active` state. A group that is already recent or in history
  when first seen raises nothing. This keeps a first run on a 28-day backlog
  quiet (s04, s11, s15).
- **Escalated.** A group that later reaches a higher level rank raises
  `escalated`.
- **Watch replacement.** A watch that replaces an active watch that had already
  raised at the same or a higher rank raises nothing. At a higher rank it
  raises `escalated` (s08).
- **Summaries** never raise attention.
- **Durability.** Raised ranks are durable. A restart, a refetch or a
  duplicate never raises the same group and level again (s01, s10).
- **Acknowledgement.** It records the operator and the time on the group, and
  it is durable. It does not change lifecycle state and does not suppress a
  later escalation. Acknowledgement and expiry are separate facts.
- **Format drift.** The third consecutive rejected body for a product raises
  `provider/<product>/format-drift` (s12).

## Fetch policy

Scheduled and manual refresh are two triggers of one durable task per provider,
with at most one outbound request in flight.

**Base intervals:**

| Product | Base interval (s) | Maximum decoded bytes | Media type | Outdated after (s) |
| --- | --- | --- | --- | --- |
| `alerts` | 120 | 1,048,576 | `application/json` | 3,600 |
| `scales` | 300 | 65,536 | `application/json` | 10,800 |
| `kp-forecast` | 900 | 262,144 | `application/json` | 21,600 |
| `3-day-forecast` | 1,800 | 65,536 | `text/plain` | 93,600 |
| `ovation` (U3) | 600 | 4,194,304 | `application/json` | 3,600 |

**Attempt gating:**

- **Scheduled attempts** run at or after the product's next permitted attempt
  time. A scheduler that fires earlier is a defect. Production applies uniform
  jitter (Proposed: ±20%) to the base interval only. Jitter never moves an
  attempt earlier than a backoff, `Retry-After` or minimum-spacing time. The
  scenarios set jitter to zero.
- **Manual refresh** shares the same task. It is refused with:
  - `halted` while a provider or product halt is in effect;
  - `backoff` during a transient or rate-limit backoff;
  - `min-interval` within 60 seconds of the last attempt.

  It never clears a halt.

**Response handling.** The first matching row applies:

| Response | Outcome and effect |
| --- | --- |
| Timeout, network error, 5xx, any other status below 300 except 200, or 304 when no validators were sent | `transient-failure`. With n consecutive transient failures, the next attempt is after min(1,800 s, max(60 s, base × 2ⁿ)). Last-known-good is kept |
| 304, validators sent | `not-modified`. Counts as a success |
| 429 | `rate-limited`. The next attempt is after `Retry-After` (delta-seconds or HTTP-date), otherwise 1,800 s, clamped to between 60 s and 21,600 s. The third consecutive 429 halts the provider (`rate-limited`) |
| 403 | Provider halt (`forbidden`) |
| 3xx other than 304 | Product halt (`endpoint-moved`). Redirects are never followed |
| 404, 410 or any other 4xx | Product halt (`not-found`, `gone` or `client-error`) |
| 200, body rejected | `rejected`, with a reason. Last-known-good is kept and the next attempt is at the base interval. The third consecutive rejection raises format-drift attention |
| 200, accepted, same snapshot hash | `unchanged`. Counts as a success |
| 200, accepted, in-file stamp older than last-known-good | `stale-snapshot`. Last-known-good, validators and freshness are not changed. Alert records in the body are still merged (s11) |
| 200, accepted, otherwise | `updated`. Replaces last-known-good. Counts as a success |

A success resets every consecutive counter and schedules the next attempt at
the base interval.

**Body rejection reasons** are `oversize`, `content-type`, `encoding`,
`malformed-json`, `schema`, `suspect-empty` (`[]`), `too-many-records` (more
than 2,000) and `no-valid-records`.

**Halts:**

- Every halt records its scope, reason and start time.
- Halts survive a restart.
- A provider-wide halt applies to every product, so changing which products are
  enabled cannot get around it.
- Only an explicit operator **resume** clears a halt:
  - Resuming the provider clears only the provider halt. Product halts remain.
  - Resuming a product clears only that product's halt.
  - Each resumed product that has no remaining product halt may be attempted
    from the resume time, and its counters reset.
- Last-known-good, history and notices survive every halt (s13).

**Freshness** is based on retrieval time, measured from the last success:

| Freshness | Condition |
| --- | --- |
| `none` | No success yet |
| `fresh` | Age less than 3 × the base interval |
| `stale` | Age less than the product's outdated threshold |
| `outdated` | Otherwise |

The in-file content stamp is a separate fact. The operator surface shows it
next to freshness:

| Product | In-file content stamp |
| --- | --- |
| `alerts` | Newest accepted issue time |
| `scales` | The `current` stamp |
| `kp-forecast` | The latest observed bin start |
| `3-day-forecast` | `:Issued:` |
| `ovation` | Observation time |

A notice still ends by its own window when its source is stale. While the
source is not fresh, an active notice carries a "may be out of date" qualifier
and is not removed.

**Diagnostics** are reason codes, identities and counts only:

- `record-rejected:<reason>:<index>`
- `issue-time-mismatch:<identity>`
- `duplicate-record-in-body:<identity>`
- `source-revision:<identity>`
- `orphan-link:<identity>`
- `orphan-resolved:<group>`
- `body-rejected:<reason>`
- `stale-snapshot`

They never contain a credential, an authorization or cookie header, a CDN
request identifier, a secret-bearing URI, or message text.

## Local relevance

**Which families have local relevance:**

| Notice | Relevance |
| --- | --- |
| `watch`, `k-alert`, and `WARK04` to `WARK09` warnings | Geomagnetic: a category is computed below |
| Other warnings, X-ray alerts, radio sweeps, flux alerts and summaries | `NoLocalObservingImpact` for optical observing. They are still listed under global activity (U14) |
| `unclassified` | `Unknown` |

**Inputs:**

- **Required latitude.** The message's stated
  `poleward of NN degrees Geomagnetic Latitude`, or otherwise the typical
  latitude for the level: K4 65, G1 60, G2 55, G3 50, G4 45, G5 40, from the
  NOAA scale descriptions.
- **Site geomagnetic latitude.** A centred dipole with its pole at 80.8 N,
  72.6 W, rounded to 0.1° (U10).
- **The difference** d = required − |site geomagnetic latitude|.

| d | Category |
| --- | --- |
| ≤ 0 | `InImpactArea` |
| ≤ 5° | `PossibleNearHorizon` |
| ≤ 10° | `Unlikely` |
| > 10° | `NotExpected` |
| Site without a location, or no stated or typical latitude | `Unknown` |

At HVO (35.5599378, -113.9119818, geomagnetic latitude 42.2°):

| Level | Category |
| --- | --- |
| K4, G1, G2 | `NotExpected` |
| G3 | `Unlikely` |
| G4 | `PossibleNearHorizon` |
| G5 | `InImpactArea` |

**Proposed darkness qualifier.** The implementation adds a darkness and twilight
qualifier from the existing site Sun context. The qualifier can only add a
restriction, such as "site in daylight until 18:12 local". It never raises a
category.

Relevance never says "visible" or "likely", and it never triggers capture.
Clouds, the Moon and the horizon are not modelled. Every relevance value is
presented as HVO SkyMonitor's estimate, not as a NOAA statement.

**OVATION band** (data only, U3):

- Latitudes from round(|site latitude|) to that value plus 9°, in the site's
  own hemisphere.
- Longitudes within 10° of the rounded site longitude, which gives 210 cells.
- The record keeps the band's maximum value and the number of nonzero
  equatorial cells (latitudes -2 to 0) that the band excludes (s18).

## Fixture scenarios

The scenario schema is `hvo-space-weather-swpc-scenario-v1`. Each scenario
has:

- `scenarioId`, `title` and `covers`;
- `containsSyntheticRecords`, with `notes` that disclose synthetic records;
- `sites`;
- `configuration` (zero jitter);
- ordered `steps`.

**Step kinds:**

- **`fetch`.** Has `product` and `trigger` (`scheduled` or `manual`), and a
  `response`. The response has one of:
  - `status`, `contentType`, `etag`, `lastModified` and `retryAfter`, with the
    body given as one of `body`, `bodyBase64`, `bodyFile` (confined to the pack)
    or `bodySynthesis`;
  - `outcome` (`timeout` or `network-error`).

  A step whose request should be refused has no response.
- **`acknowledge`.** Has `group` and `operator`.
- **`resume`.** Has `scope` (`provider` or `product`).
- **`restart`.** A round trip of the durable state through JSON that discards
  everything else.
- **`evaluate`.** Observes state without acting.

**Expectations.** Each step's `expect` is compared against the model's output:

- Objects are compared as subsets.
- The notice group set must match exactly.
- `attention` and `diagnostics` lists must match exactly.

| Scenario | Covers |
| --- | --- |
| s01 | Duplicate refetch under a churned ETag; repeated record in one body |
| s02 | Same identity with revised content (synthetic edit) |
| s03 | Corrected reissue with the same serial; expiry |
| s04 | Continuation chain with self-reference; first-run backlog |
| s05 | Extension chain; half-open expiry |
| s06 | Extension before its root; orphan link resolved |
| s07 | Same-level watch supersession; CANCEL WATCH |
| s08 | Silent watch replacement; escalation (synthetic G2 watch) |
| s09 | CANCEL WARNING (synthetic) |
| s10 | Restart without re-alert; durable acknowledgement; rolling window; retention purge |
| s11 | Older CDN body after a newer one; stale snapshot |
| s12 | Malformed bodies and records; unknown code; format drift |
| s13 | Timeout and 5xx backoff; 304; 429 with both `Retry-After` forms; halts; manual refusals; restart while halted; resume |
| s14 | Year rollover on watch day lines; UTC days shown in the site zone |
| s15 | Missing scale and impact lines; null predicted scales; site without a location |
| s16 | Future Kp bin labelled `estimated` |
| s17 | Text normalization; `:Issued:` versus `Last-Modified` |
| s18 | OVATION site band and equatorial artifact (data only) |

**Implementation conformance.** An implementation passes when it reproduces each
scenario's expectations from the same committed bodies. Unit tests use those
bodies through a stub HTTP handler and a fixed clock. A live probe is an
External test only.

## Versioning

A change that alters any of the following produces v2 of this contract and
regenerates the fixture pack:

- an identity or hash rule;
- a lifecycle window;
- an attention or halt rule;
- a fetch outcome;
- a relevance category or threshold;
- a scenario's expectations.

Proposed defaults may be tuned before the first implementation without a
version change, provided the reference model, the scenarios and this document
change together.
