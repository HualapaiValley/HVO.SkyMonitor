# Space-weather facts v1 (#1398)

Status: design checkpoint under the
[#1398 decision](../planning/space-weather-source-decision-1398.md), dated
2026-10-19. The fixture pack in `tests/fixtures/space-weather/swpc-v1/`
contains a reference model, `fixtures.py`, that encodes this policy so the
scenarios can be checked. That model is a specification aid, not production
code. This document does not show that any implementation exists.

When this document and the reference model disagree, that is a defect in the
checkpoint. Fix both together and regenerate the pack.

The decision's ledger and its U1 to U15 table give each rule's status:
Approved, Proposed or Unresolved. The numbers in this document follow those
statuses. For example, the per-product structure of the request policy is
Approved, while the base interval values and the 35-day retention period are
Proposed values (U1, U8, U15). The 64-level JSON nesting limit and the
1900–9998 source-year range are also **Proposed**, under the decision's
"Proposed limits" section.

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
| `SwpcMessage` | issued notice version | Identity (message code, serial, issue minute); `product_id` and `issue_datetime` as attributes; headline kind and modifier; corrected flag; link (extension, continuation or cancel target serial); scale token and raw descriptor; impact latitude when stated; parsed time fields; watch day lines; the normalized text and its SHA-256; the original record's verbatim source JSON text |
| `ScaleReading` | observation or forecast | Window (`observed24hMax`, `current`, `predictedDay1..3`), stamp, and the R, S and G scale values and probabilities as SWPC wrote them (JSON strings), including `null` |
| `KpBin` | observation or forecast | Bin start (UTC); bin end (start plus 3 hours); Kp as the source text of the JSON number (`4.67` is kept as `"4.67"`, never rounded to `4+`); the source label verbatim (`observed`, `estimated` or `predicted`); NOAA scale or `null`. The relation to now (`past`, `current` or `future`) is computed when the bin is read and is not stored |
| `IssuedForecast` | forecast | `:Issued:` time (UTC); 24 Kp breakdown bins with G marks; the solar radiation (S) and radio blackout (R) probabilities, keyed by the row label as printed and the UTC date; normalized-text SHA-256 |
| `AuroraNowcast` | forecast model output | Observation time; forecast time; lead in minutes; per site, the band bounds, cell count, expected cell count and maximum value, or `null` for a site without a location; the number of equatorial cells ignored; the original grid SHA-256. Data only: acquisition and wording are unresolved (U3) |
| `Notice` | derived lifecycle | Group identity; family; level; state; window bounds; end reason; latest version; version count; corrected flag; orphan flag; acknowledgement; per-site relevance; watch days; site-local projection of the window |
| `LocalRelevance` | derived HVO estimate | Site; category; basis (impact latitude stated in the message, or the typical latitude for the level); the computed geomagnetic latitude; the model version |

Observation, forecast, watch, warning and alert are told apart as follows:

- **Observation.** A scale reading of `observed24hMax` or `current`, or a Kp bin
  whose relation is `past`.
- **In progress.** A Kp bin whose relation is `current`. It is neither a
  completed observation nor a forecast. Its label, usually `estimated`, says
  how provisional the value is.
- **Forecast.** A predicted scale day, a Kp bin whose relation is `future`
  (whatever its label), or the 3-day forecast.
- **Watch, warning or alert.** Taken from the message headline. Summaries
  describe closed events and are never active.

A Kp bin's label stays verbatim. Its relation to the time `t` at which it is
read is computed from the bin's start and end (U7):

- `past` when end ≤ `t`;
- `current` when start ≤ `t` < end;
- `future` otherwise.

The label never decides the relation, because SWPC labels future bins
`estimated` (s16).

## Normalization and hashes

**Text normalization:**

- CRLF becomes LF.
- Trailing spaces, tabs and carriage returns are removed from every line.
- Trailing blank lines are removed.
- A nonempty result ends in exactly one LF. If no lines remain, the result is
  the empty string.

**Field whitespace.** For parsing, message field values, headlines, media types
and times in the `YYYY Mon DD HHMM UTC` layout are trimmed at both ends using
Python whitespace semantics: Unicode whitespace plus U+001C to U+001F. The
headline is the first nonblank line after the three header lines; blank-line
checks use the same whitespace set. This trimming is separate from text
normalization and does not alter the normalized text or its hash.

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

**Digits, times and JSON limits:**

- Every letter and digit that a rule matches is ASCII. A "digit" is `0` to `9`
  only, so a field written with other Unicode digits does not match (s12).
  Case-insensitive matching folds ASCII letters only.
- Under the **Proposed** source-year limit, a source time with a year parses
  only when the year is 1900 to 9998 inclusive (s25). A
  source time is a message time (`Issue Time`, `Valid From`, `Valid To`,
  `Now Valid Until`, `Threshold Reached` or `Begin Time`), the 3-day
  forecast's `:Issued:` time, or a JSON product stamp. Every time derived from
  one, such as a window bound, the 96-hour cap or the 35-day purge time, is then
  representable. An `Issue Time` that does not parse rejects the record as
  `message-header` (s12). Any other source time that does not parse counts as
  unparseable where it is used.
- `issue_datetime` is never read as a time. It is checked by its layout, and
  its minute and its place in version order are compared as text.
  A `Retry-After` IMF-fixdate uses years 0001 to 9999 instead, because its
  delay is clamped (see "Fetch policy").
- The JSON product stamps are matched whole: scales `DateStamp` and
  `TimeStamp` joined by one space as `YYYY-MM-DD HH:MM:SS`, Kp `time_tag` as
  `YYYY-MM-DDTHH:MM:SS`, and the OVATION times as `YYYY-MM-DDTHH:MM:SSZ`.
- Under the **Proposed** nesting limit, a JSON body nested more than 64
  levels deep is `malformed-json`. The
  outermost array or object is level 1, and brackets inside strings do not
  count (s12).
- For every JSON product, `NaN`, `Infinity` and `-Infinity` are not JSON
  numbers (RFC 8259 §6). Their presence anywhere in a body is `malformed-json`,
  before product schema checks; last-known-good and its validators are kept (s24).
- A JSON number is valid whatever its length. A body is never rejected because
  a number has many digits. A long number is still a number, not a string, so
  a 4,301-digit `product_id` is `record-schema` (s12; see "JSON value types").

**JSON value types:** every field a rule reads has the JSON type below, as
observed in the captured bodies. A value of any other JSON type fails that
field. It is never coerced, and its text is never matched against the field's
layout: a JSON number is never a string, and a string is never a number.
"Number" means every JSON number form, of any length: an integer (no fraction
or exponent), a fraction or an exponent. `true` and `false` are not numbers.
Fields not listed are not read, and their types are not checked.

| Product | Field | JSON type | Another type is |
| --- | --- | --- | --- |
| alerts | the body | array | `schema` |
| alerts | each record | object | `record-schema` for that record |
| alerts | `product_id`, `issue_datetime`, `message` | string. A number of any form, such as `1E5`, `1.5` or a 4,301-digit integer, is not a string even when its text would match the layout (s12) | `record-schema` for that record |
| scales | the body; each day (`-1` to `3`); its `R`, `S` and `G` | object | `schema` |
| scales | `DateStamp`, `TimeStamp` | string | `schema` |
| scales | `R.Scale`, `R.MinorProb`, `R.MajorProb`, `S.Scale`, `S.Prob`, `G.Scale` | string or `null`, kept as written. The number `1` is not a string (s15) | `schema` |
| Kp forecast | the body | array | `schema` |
| Kp forecast | each row | object | `schema` |
| Kp forecast | `time_tag`, `observed` | string. An `observed` of `null` is not a string (s16) | `schema` |
| Kp forecast | `kp` | number whose source text is exactly `d.dd` in ASCII digits, such as `4.67`, kept as that text (`"4.67"`). The string `"1.67"` is not a number (s16), and number text such as `5`, `5.0`, `4.670` or `4.67E0` is not `d.dd` | `schema` |
| Kp forecast | `noaa_scale` | string or `null`. The number `1` is neither (s16) | `schema` |
| OVATION | the body | object | `schema` |
| OVATION | `Observation Time`, `Forecast Time` | string | `schema` |
| OVATION | `coordinates` | array of cells, each an array of exactly three integers (longitude, latitude, value). One cell outside every site band with the value `0.5` still rejects the body (s18) | `schema` |

## Alert records

**Original record.** Every accepted message version stores its verbatim source
JSON text: the exact UTF-8 byte span of that record in the decoded source body,
from its opening `{` through its matching `}`, inclusive. This preserves all
fields (including unread fields), JSON value types and number spellings,
property order, escape spellings, and internal whitespace, without re-encoding.
The outer array delimiters and whitespace outside the record are excluded.
Message normalization and hashing do not change this original record. Restart
and refetch preserve it unchanged (s26, s27); an unread `"x": 1.5` remains a JSON
number, byte for byte, rather than becoming the JSON string `"1.5"`.

**Per-record checks.** An `alerts.json` body is a JSON array. Each record is
validated on its own. A failure rejects only that record. The checks run in
the order below, and the first failure gives the reason (s12):

| Reason | Condition |
| --- | --- |
| `record-schema` | Not an object; `product_id`, `issue_datetime` or `message` missing, not a JSON string (a JSON number of any form is not a string; see "JSON value types"), or a string with no UTF-8 form (an unpaired surrogate escape such as `\ud800`); `product_id` not 2 to 8 ASCII uppercase letters or digits (s12) |
| `issue-datetime` | `issue_datetime` is not `YYYY-MM-DD HH:MM:SS.fff` in ASCII digits |
| `record-oversize` | The message, before normalization, is larger than 16,384 UTF-8 bytes |
| `message-header` | After normalization, the message has fewer than four lines (a header-only message is rejected), or its first three lines are not exactly `Space Weather Message Code: <code>` with a code of 4 to 8 ASCII uppercase letters or digits, `Serial Number: <serial>` with 1 to 6 ASCII digits, and `Issue Time: YYYY Mon DD HHMM UTC` in ASCII digits naming a valid UTC time in the year range under "Digits, times and JSON limits". Each line is matched whole, with single spaces and nothing before or after (s12) |

**Identity.** Message identity is `CODE/serial/issue-minute`, all parsed from
the message text. Link values (`Extension to Serial Number`, `Continuation of
Serial Number`, `Cancel Serial Number`) are used only when they are 1 to 6
ASCII digits. When several link fields are valid, the last valid one in
extension, continuation, cancel order supplies the single link, regardless of
their order in the message.

**Version handling:**

- A record whose identity and content hash are both already stored is a
  duplicate and has no effect (s01).
- The same identity with a different hash is a new version of that identity,
  reported as `source-revision` (s02). In a `stale-snapshot` body (see "Fetch
  policy"), a different hash for a stored identity is not stored and is
  reported as `stale-revision-ignored`. A record with a new identity in that
  body is still stored (s11).
- A repeat of the same record within one body is reported once, as
  `duplicate-record-in-body`.
- If the minute of `issue_datetime` disagrees with the text's issue minute, the
  record is kept under the text identity and `issue-time-mismatch` is reported
  (s12).
- **Equal stamps.** The alerts in-file stamp is the newest issue time among
  the body's accepted records (see "Freshness"). An in-place revision or a
  rejected record does not move it. So when an edge serves an older body with
  no newer record in between, the body's stamp equals last-known-good's, and
  the body is `updated`, not `stale-snapshot`:
  - A text of a stored identity that was never stored is a `source-revision`.
    It cannot be told apart from a genuine revision (s02). It is the latest
    version only if it sorts last in the version order below (issue minute,
    then `issue_datetime`, then ingest order).
  - A text that was stored before is a duplicate. It adds no version and no
    diagnostic, and the latest version does not change.

  When an edge alternates between two equal-stamp bodies, last-known-good
  follows the latest body and no stored version is removed (s20).

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

A group's family comes from its first non-cancel version. For a group containing
only cancels, `WATA*` is `watch` regardless of headline kind; any other group
takes its first version's family.

**Levels:**

- `WARK0n` and `ALTK0n` map to K4 for n = 4, otherwise to G(n−4).
- A watch takes its level from `Category G<n>` in its headline.
- Any other message takes its level from its `NOAA Scale:` token, matched
  without regard to case.
- Level rank is 0 for K4 or an absent level, otherwise the scale number. An
  absent source level stays `null` (s12).

**Dates without a year.** Watch day lines carry no year, and neither do the 3-day
forecast's table columns. The year is inferred from a reference time: the
message issue time for a watch, and the `:Issued:` time for the 3-day forecast.
A month more than six months earlier than the reference month belongs to the
next year, and one more than six months later belongs to the previous year. A
year printed in a 3-day table title is not used (s14).
A watch day line with an unrecognized month token is ignored.

## Notice lifecycle

A group's window is `[from, until)`, half-open (s05). The window is computed
from the group's latest non-cancel version:

| Family | `from` | `until` |
| --- | --- | --- |
| `watch` | Root issue time | 24:00 UTC at the end of the last listed day whose level is not `None`, or `from` if every listed day is `None` |
| `warning` | The root's `Valid From` only. A later version's `Valid From` is never used | The latest `Now Valid Until`, otherwise `Valid To` |
| `k-alert` | `Threshold Reached` | For a `Synoptic Period` matching `HHMM-HHMM` in ASCII digits, UTC midnight on the threshold date plus 60 × the end's two-digit hour + its two-digit minute, in minutes, without hour or minute range checks (`2400` is the next midnight). An end that is not after `Threshold Reached` counts as missing |
| `xray-alert` | `Threshold Reached` | 3 hours later |
| `radio-sweep` | `Begin Time` | 1 hour later |
| `flux-alert` | The root's `Threshold Reached`, otherwise `Begin Time` | 30 hours after the latest non-cancel issue time |
| `unclassified` | Root issue time | 6 hours later |
| `summary` | Root issue time | Never active |

**Fallback.** If a required time is missing or unparseable, the window becomes
the root issue time to 6 hours later. Both bounds are replaced. A watch's day
lines are unparseable when a listed day with a recognized month is not a real
date, such as `Oct 32`. The watch then takes the fallback window and its watch
days are `null` (s12).

**Adjustments.** A summary with a non-cancel version bypasses all adjustments:
both window bounds are its root issue time and its end reason is `null`.
For other groups, the following are applied in this order. Each applies only
if it is earlier than the current `until`, and each sets the end reason:

1. **Watch supersession** (`superseded`). Every watch states that it supersedes
   all prior watches. A later watch of any level ends an earlier watch at the
   later watch's root issue time (s07, s08).
2. **Cancel** (`cancelled`). Except for summaries, a group with a non-cancel
   version ends at its earliest cancel's issue time (s07, s09). A group
   containing only cancels bypasses all adjustments: both window bounds are its
   latest version's issue time and its end reason is `cancelled`. It is recent
   from then and never raises attention. A single cancel of an absent target is
   this case (s09).
3. **Cap** (`capped`). No group stays active later than 96 hours after its
   latest non-cancel issue time (s21).

For groups subject to adjustments, the end reason is `expired` if none applies.

**States** at time `t`. A summary with a non-cancel version is `recent` whenever
`t < root issue time + 24 h`, including before its issue time, and `history`
otherwise. For every other group, the first matching row applies:

| State | Condition |
| --- | --- |
| `recent` | `until ≤ t < until + 24 h` |
| `history` | `t ≥ until + 24 h`, until purged |
| `pending` | `t < from`, for example a prospective `Valid From` (s21) |
| `active` | `from ≤ t < until` |

**Retention.** A group and its versions, raised levels and acknowledgements are
purged once `t ≥ until + 35 days`. A summary with a non-cancel version has
`until` at its root issue time and `endReason` of `null`, so it is purged
35 days after its root was issued (s10). A cancel-only group, including one
whose family is `summary`, keeps both window bounds at its latest version's
issue time and its end reason is `cancelled`; it is purged 35 days after that
time. Upstream `alerts.json` is a rolling window of about 28 days. A record that
leaves that window was not cancelled. It stays until its own retention ends
(s10).

**Projection.** Windows are stored in UTC. The operator surface also shows them
in the site's IANA time zone (s14).

## Local attention

Attention is a local operator-surface event. It never sends anything outside
the host and never triggers a capture or safety action.

Notice attention is evaluated after an `alerts` body is merged. It covers only
the groups to which that body added a version. The groups are evaluated in
order of root issue time, then group identity, so a group can depend on one
evaluated before it in the same merge (s08).

- **Eligible.** A group is eligible at a merge when it is `pending` or
  `active`, or when it is `recent` and the merge happens less than 300 seconds
  (`newGraceSeconds`) after its latest non-cancel issue time. The grace keeps a
  short-window alert that ended before the next poll from being silenced (s11,
  s21). This state and grace gate applies to first raises and higher watch
  replacements.
- **New.** A group that has never raised attention raises `new` when it is
  eligible. Otherwise it raises nothing and no rank is recorded, which keeps a
  first run on a 28-day backlog quiet (s04, s11, s15, s21). A group that raised
  nothing when first seen can still raise `new` at a later merge that adds a
  version while it is eligible. For a watch with a raised predecessor, "Watch
  replacement" applies instead of this rule.
- **Escalated.** A group that has raised and later reaches a higher level rank
  raises `escalated` and records the higher rank without rechecking eligibility.
- **Watch replacement.** A watch that has never raised is compared with the
  earlier watches that were active one second before its root issue time and
  had raised, including any that raised earlier in the same merge. A rank
  recorded by a silent replacement counts as raised. If none of them had
  raised, "New" applies. Otherwise the highest raised rank among them decides,
  whatever the watch's own state:
  - At the same or a lower rank, the watch raises nothing, and its own rank is
    recorded as raised for it.
  - At a higher rank, the watch raises `escalated` if it is eligible, and its
    rank is recorded. If it is not eligible, it raises nothing and no rank is
    recorded.

  So a watch first seen after an outage stays silent, even while active, when a
  raised watch it replaced was at the same or a higher rank. A higher watch
  that ended outside the grace during the outage leaves no rank, so a later
  watch is compared only with the ranks that were raised (s08).
- **No attention.** Summaries never raise attention, and neither does a group
  that contains only a cancel (s09).
- **Durability.** Raised ranks are durable. A restart, a refetch, a duplicate
  or a later version at the same or a lower rank never raises the same group
  again (s01, s10).
- **Acknowledgement.** It records the operator and the time on the group, and
  it is durable. It does not change lifecycle state and does not suppress a
  later escalation. Acknowledgement and expiry are separate facts (s10).
- **Format drift.** The third consecutive rejected body for a product raises
  `provider/<product>/format-drift` once (s12; for the Kp forecast, s16). A
  success, including a `stale-snapshot`, resets the count. A resume does not.
- **Record drift.** An `updated` alerts body with at least one rejected record
  raises `provider/alerts/record-drift` when no record-drift episode is open,
  and opens one. Records that are repeats within the body are not rejections.
  - The open episode keeps the newest in-file stamp of the drifted `updated`
    bodies it has seen.
  - Only an `updated` body with no rejected records and a stamp strictly newer
    than the kept one closes the episode. A clean body at the same stamp
    leaves it open, so an edge alternating between equal-stamp bodies raises
    once (s20).
  - Each later raise therefore follows a clean body with a newer stamp, and
    successive raises have strictly increasing stamps.
  - `unchanged` and `stale-snapshot` bodies neither raise, close nor advance
    the episode, even when they contain rejected records (s20).
  - The episode and its stamp are durable across a restart (s12, s20).

  This catches a format change in new messages while the older records in the
  rolling window still parse.
- **Order.** A fetch reports notice events in ordinal order of group identity,
  then the provider event, whatever order the groups were evaluated in (s10,
  s12).

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

The per-product structure of this table is Approved. Its values are Proposed:
the base intervals (U1 for alerts, U15 for scales, and the Kp and 3-day values
with them), the decoded-byte maximums and the outdated thresholds.

**Per-product state.** Each product keeps its own last attempt, next permitted
attempt, backoff, consecutive transient, 429 and rejected counts, and
`Retry-After` time. A 429 or a backoff for one product does not delay another.
Only a provider halt stops every product.

**Attempt gating:**

- **Scheduled attempts** run at or after the product's next permitted attempt
  time. A scheduler that fires earlier is a defect. Production applies uniform
  jitter (Proposed: ±20%) to the base interval only. Jitter never moves an
  attempt earlier than a backoff, `Retry-After` or minimum-spacing time. The
  scenarios set jitter to zero.
- **Manual refresh** shares the same task. The first matching refusal applies:
  - `halted` while a provider or product halt is in effect;
  - `backoff` while a transient or rate-limit backoff for that product has not
    run out;
  - `min-interval` within 60 seconds of that product's last attempt.

  A refused request sends nothing. Manual refresh never clears a halt.
- **Backoff.** Only a `transient-failure` or a 429 starts a backoff. A halting
  429 starts one as well; it takes effect at the resume, when its
  `retryNotBeforeUtc` sets the next permitted attempt (s13). A backoff ends
  at the product's next attempt, whatever that attempt's outcome. So after a
  backoff, a 404 halt and a resume, a manual request within 60 seconds of the
  404 is refused as `min-interval`, not `backoff` (s22).

**Response handling.** The outcome is decided before any alert record is
merged. The first matching row applies:

| Response | Outcome and effect |
| --- | --- |
| Timeout, network error, 5xx, any other status below 300 except 200, or 304 when no validators were sent | `transient-failure`. With n consecutive transient failures, including this one, the next attempt is after min(1,800 s, max(60 s, base × 2ⁿ)). Last-known-good is kept. The 429 and rejected counts are unchanged (s13, s22) |
| 304, validators sent | `not-modified`. A success. The stored validators are kept |
| 429 | `rate-limited`. The 429 count rises; the transient and rejected counts are unchanged. The next attempt is after `Retry-After`, otherwise after 1,800 s. `Retry-After` is read as delta-seconds (1 to 9 ASCII digits) or IMF-fixdate only (RFC 9110 §5.6.7); any other value is ignored and gives the 1,800 s fallback. The delay is clamped to between 60 s and 21,600 s. The third consecutive 429 for a product halts the provider (`rate-limited`) instead. That 429's clamped time is kept as `retryNotBeforeUtc` and applied when the halt is resumed (s13, s19) |
| 403 | Provider halt (`forbidden`). The halt leaves the transient, 429 and rejected counts unchanged |
| 3xx other than 304 | Product halt (`endpoint-moved`). Redirects are never followed. The halt leaves the transient, 429 and rejected counts unchanged |
| 404, 410 or any other 4xx | Product halt (`not-found`, `gone` or `client-error`). The halt leaves the transient, 429 and rejected counts unchanged (s13, s22) |
| 200, body rejected | `rejected`, with a reason. Last-known-good and the validators are kept. The rejected count rises; the transient and 429 counts reset and any backoff ends. The next attempt is at the base interval. The third consecutive rejection raises format-drift attention |
| 200, accepted, same snapshot hash | `unchanged`. A success. Alert records are not merged again. The validators are replaced |
| 200, accepted, in-file stamp older than last-known-good's | `stale-snapshot`. Last-known-good, the validators and the last success time are not changed, so freshness does not improve. For the counters and the next attempt it acts as a success. Alert records with new identities are merged; new text for a stored identity is not (s11) |
| 200, accepted, otherwise | `updated`. A success. Replaces last-known-good and the validators |

An IMF-fixdate has exactly the layout `Tue, 03 Feb 2026 01:02:03 GMT`:
case-sensitive English weekday and month names, two-digit day, four-digit year,
and two-digit hour, minute and second, with exactly the shown spaces and `GMT`.
The date and time must exist in the calendar, in years 0001 to 9999, and the
weekday must agree with the date. Year 0000, impossible dates, lower-case names,
one-digit fields, extra whitespace, obsolete rfc850 and asctime forms, and
mismatched weekdays are ignored (s23). Valid dates keep the delay clamp above.

A success (`not-modified`, `unchanged` or `updated`) records the retrieval time
as the last success. It resets the transient, 429 and rejected counts, ends any
backoff and schedules the next attempt at the base interval. A
`stale-snapshot` does the same except that it does not record a last success.
When either in-file stamp is missing, a body is never `stale-snapshot`.

**Body rejection reasons.** Checks run in this order, and the first failure
gives the reason:

1. `oversize`: the decoded body exceeds the product's maximum.
2. `content-type`: the media type, without parameters and ignoring case, is
   not the product's. Trim it using the field-whitespace rule; case folding
   still matches ASCII letters only.
3. `encoding`: the body is not UTF-8.
4. For the 3-day forecast, `schema` for any of the following (s14, s17):
   - the normalized text does not start with `:Product: 3-Day Forecast`
     immediately followed by LF;
   - `:Issued:` is missing or not a valid time;
   - a Kp breakdown, solar radiation or radio blackout table is missing (for
     example after a rename);
   - a solar radiation or radio blackout table has a column header but no
     rows;
   - the column header does not name three days;
   - a row does not parse;
   - the Kp rows are not exactly 00 to 21 UT in 3-hour steps.
5. For JSON products, `malformed-json`, including `NaN`, `Infinity` and
   `-Infinity` anywhere in the body (s24).
6. For alerts:
   - `schema` if the body is not an array;
   - `suspect-empty` for `[]`;
   - `too-many-records` for more than 2,000 records;
   - `no-valid-records` when every record fails the per-record checks (s12).
7. For scales, `schema` when the body is not an object, a day key (`-1` to
   `3`) or field is missing, a field has another type than under "JSON value
   types", or a stamp does not parse in the layout under "Digits, times and
   JSON limits".
8. For the Kp forecast:
   - `schema` if the body is not an array;
   - `suspect-empty` for `[]`;
   - `schema` when a row is incomplete or a field has another type than under
     "JSON value types", a `time_tag` does not parse in the layout under
     "Digits, times and JSON limits", or any `kp` is not a JSON number whose
     source text is exactly `d.dd` in ASCII digits (a JSON string such as
     `"1.67"` is `schema`, s16). One bad row rejects the whole Kp body.
9. For OVATION, `schema` when a key is missing or malformed, including a field
   of another type than under "JSON value types". A site without a location
   gets a `null` band. That is not a rejection (s18).

**Halts:**

- Every halt records its scope, reason and start time.
- Halts survive a restart.
- A provider-wide halt applies to every product, so changing which products are
  enabled cannot get around it.
- Only an explicit operator **resume** clears a halt:
  - Resuming the provider clears only the provider halt. Product halts remain.
  - Resuming a product clears only that product's halt.
- Resuming lifts the halt and nothing else. Counters stand, a running backoff
  continues, and `Retry-After` and minimum spacing still apply. Each resumed
  product that has no remaining halt gets a next permitted attempt at the
  latest of:
  - the resume time;
  - its existing next permitted attempt;
  - its `retryNotBeforeUtc`;
  - its last attempt plus 60 seconds.

  Its `retryNotBeforeUtc` is then cleared. This matches the #527 rule that a
  resume remains subject to the next permitted attempt (s13, s19, s22).
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
| `alerts` | The newest issue time among the body's accepted records |
| `scales` | The `current` stamp |
| `kp-forecast` | The latest start of a bin labelled `observed` |
| `3-day-forecast` | `:Issued:` |
| `ovation` | Observation time |

A notice still ends by its own window when its source is stale. While
`alerts` is not `fresh`, every `pending` or `active` notice carries
`mayBeOutOfDate: true` and is not removed (s05, s21).

**Diagnostics** are reason codes, identities and counts only. A fetch reports
them sorted in ordinal (code-point) order:

- `record-rejected:<reason>:<index>`, where the index is the record's
  zero-based position in the body
- `issue-time-mismatch:<identity>`
- `duplicate-record-in-body:<identity>`
- `source-revision:<identity>`
- `stale-revision-ignored:<identity>`
- `orphan-link:<identity>`
- `orphan-resolved:<group>`
- `body-rejected:<reason>`
- `stale-snapshot`

`record-rejected`, `issue-time-mismatch` and `duplicate-record-in-body` are
reported only for `updated` and `stale-snapshot` bodies, never for `unchanged`
bodies. s20 steps 3 and 5 pin the silent unchanged case; step 10 pins a
`record-rejected` diagnostic on a stale snapshot.

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
  `poleward of NN degrees Geomagnetic Latitude` when nonzero, or otherwise the
  typical latitude for the level: K4 65, G1 60, G2 55, G3 50, G4 45, G5 40,
  from the NOAA scale descriptions. A stated zero counts as absent; without a
  typical latitude, the category is `Unknown`.
- **Site geomagnetic latitude.** A centred dipole with its pole at 80.8 N,
  72.6 W, rounded to 0.1° (U10).
- **The difference** d = required − |site geomagnetic latitude|, rounded to
  0.1° before comparison.

Both one-decimal operations reproduce Python `round(value, 1)` on the actual
binary64 double: nearest decimal tenth, with ties to even. Rounding an exact
decimal interpretation of its shortest `repr` text, or using .NET's default
`Math.Round(value, 1)`, can disagree near half-way boundaries. For example,
`2.25` rounds to `2.2`, `2.45` to `2.5` and `2.55` to `2.5`. The pack's
`rounding-vectors.json` pins the inputs, exact unrounded doubles as `repr`
text, expected rounded outputs and signed zero. It checks both latitude and
relevance-difference operations. Directly parsing `unroundedDouble` allows the
C# implementation to verify rounding separately from trigonometric-library
last-bit differences. Synthetic intermediate relevance latitudes exercise
half-way boundaries that normal one-decimal latitude outputs do not reach.

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
- Longitudes within 10° of the rounded site longitude (0 to 359), which gives
  210 expected cells.
- The record keeps, per site, the band bounds, the number of band cells
  present in the grid, the expected count and the band's maximum value. It also
  keeps the number of nonzero cells at latitudes -2 to 0 anywhere in the grid,
  which measures the equatorial artifact. The HVO band (36° to 45° N) excludes
  those cells.
- A site without a location has a `null` band (s18).

## Fixture scenarios

The scenario schema is `hvo-space-weather-swpc-scenario-v1`. Each scenario
has:

- `scenarioId`, `title` and `covers`;
- `containsSyntheticRecords`, with `notes` that disclose synthetic records;
- `sites`;
- `configuration` (zero jitter);
- ordered `steps`.

**Step kinds:**

- **`fetch`.** Has `atUtc`, `product` and `trigger` (`scheduled` or `manual`),
  and a `response`. The response has one of:
  - `status`, `contentType`, `etag`, `lastModified` and `retryAfter`, with the
    body given as one of `body`, `bodyBase64`, `bodyFile` (confined to the pack)
    or `bodySynthesis`;
  - `outcome` (`timeout` or `network-error`).

  A step whose request should be refused has no response. A step that the
  model does not refuse but that has no response is a scenario error, and so
  is a scheduled step before the product's next permitted attempt.
  `bodySynthesis` builds a body that is too large to commit:
  - `padded-empty-array` with `totalBytes` is `[`, spaces and `]`;
  - `repeated-record` with `record` and `count` is `count` copies of `record`
    in the `alerts.json` layout.
- **`acknowledge`.** Has `atUtc`, `group` and `operator`.
- **`resume`.** Has `atUtc` and `scope`. A product resume also has
  `product`.
- **`restart`.** A round trip of the durable state through JSON that discards
  everything else.
- **`evaluate`.** Has `atUtc` and observes state without acting.

**Durable state** is the provider halt; the stored message versions and their
verbatim original-record text and ingest order; the raised ranks; the acknowledgements; the orphan set; and, per
product:

- last-known-good;
- validators;
- last success, last attempt and next permitted attempt;
- `retryNotBeforeUtc`;
- the backoff flag;
- the three counters;
- the halt;
- the last outcome;
- the record-drift stamp, which is `null` when no episode is open.

**Observed output.** Every step reports `originalRecords`: identity to the
ordered verbatim source texts of its stored versions (s26). Every step that has `atUtc` first purges by retention.
It then reports `notices` and, for every product:

- `lastOutcome`, `freshness`, `lkgSha256` and `lastSuccessUtc`;
- `nextAttemptNotBeforeUtc` and `retryNotBeforeUtc`;
- the three consecutive counters;
- `recordDriftOpen`, `recordDriftStampUtc` and `halt`;
- `validators` and `facts`;
- for `kp-forecast`, `kpBinRelations` at the step time.

A fetch also reports:

- `refused`;
- `request` (the validators sent);
- `outcome`;
- `attention`;
- `diagnostics`;
- `product`, the view of the fetched product.

**Expectations.** Each step's `expect` is compared against the model's output:

- Objects are compared as subsets.
- The notice group set must match exactly.
- `attention` and `diagnostics` lists must match exactly, in the order given
  under "Local attention" and "Fetch policy" → "Diagnostics".

| Scenario | Covers |
| --- | --- |
| s01 | Duplicate refetch under a churned ETag; repeated record in one body |
| s02 | Same identity with revised content (synthetic edit) |
| s03 | Corrected reissue with the same serial; expiry |
| s04 | Continuation chain with self-reference; first-run backlog |
| s05 | Extension chain; half-open expiry; "may be out of date" while alerts are not fresh |
| s06 | Extension before its root; orphan link resolved |
| s07 | Same-level watch supersession; CANCEL WATCH |
| s08 | Silent watch replacement; escalation (synthetic G2 watch); two watches first seen in one merge, evaluated in root issue-time order (synthetic G3 and G2 watches); watches first seen after an outage: silent lower replacements with recorded ranks, and a higher watch that ended outside the grace raises and records nothing (synthetic watches) |
| s09 | CANCEL WARNING (synthetic); a cancel of an absent target never alerts |
| s10 | Restart, then an extension, without re-alert; acknowledgement kept across both; rolling window; retention purge, including a summary |
| s11 | Older CDN body after a newer one; stale snapshot with an ignored stale revision; new-notice grace |
| s12 | Malformed bodies; per-record faults (schema, issue time, mismatch, oversize, header-only, long serial, a doubled space in `Issue Time`, an unpaired surrogate, non-ASCII digits, a year beyond range, a 4,301-digit number, and JSON numbers `1E5` and `1.5` where strings are required); an impossible watch day takes the fallback window; unknown code; record drift; format drift; too many and no valid records; a body nested 64 levels deep passes the depth limit and one nested 65 levels is `malformed-json` |
| s13 | Timeout and 5xx backoff; 304; 429 with both `Retry-After` forms; halting 429 keeps `retryNotBeforeUtc` across a restart and applies it at the resume; halts; manual refusals; restart while halted; resume |
| s14 | Year rollover on watch day lines and on 3-day forecast columns (synthetic forecast); UTC days shown in the site zone |
| s15 | Missing scale and impact lines; null predicted scales; site without a location; a scales value written as the JSON number `1` rejects the body as `schema` and keeps the last-known-good snapshot (synthetic body) |
| s16 | Future Kp bin labelled `estimated`; past, current and future relations as time moves; a `kp` written as the JSON string `"1.67"`, a `noaa_scale` written as the JSON number `1`, or an `observed` of `null` rejects the body as `schema` and keeps the last-known-good snapshot; the third consecutive rejection raises format drift (synthetic bodies) |
| s17 | Text normalization; `:Issued:` versus `Last-Modified`; S and R probabilities; a renamed table, or a probability table with no rows, rejects the body |
| s18 | OVATION site band and equatorial artifact; `null` band for a site without a location; a cell whose value is the JSON number `0.5` rejects the body as `schema` and keeps the last-known-good snapshot (data only; synthetic body) |
| s19 | An early resume keeps minimum spacing, backoff, `Retry-After` and the 429 count; per-product 429 state |
| s20 | Record drift raised once per episode, kept across a restart; equal-stamp bodies alternating neither close nor re-raise it; a stale-snapshot drifted body neither raises, closes nor advances it; closed by a clean body with a newer stamp and raised again |
| s21 | Pending before `Valid From`; short-window alert inside and outside the new-notice grace; 96-hour cap |
| s22 | 410 and 400 product halts; network error backoff; 304 without validators; product resume; an attempt ends the backoff before it |
| s23 | Exact Retry-After grammar; invalid forms give 1,800 s; matching weekday; valid dates in years 0001 and 9999 keep the clamp |
| s24 | NaN, Infinity and -Infinity are malformed-json for alerts, scales, Kp and OVATION; last-known-good is kept |
| s25 | Inclusive source-year bounds: 1899 and 9999 reject; 1900 and 9998 update, for alerts and scales |
| s26 | Verbatim original alert JSON (including numeric x: 1.5, escapes and whitespace) survives two restarts and an unchanged refetch |
| s27 | Outer JSON whitespace; literal UTF-8 and Unicode escape spellings; two message versions retain their exact original-record bytes in ingest order through two restarts and an unchanged refetch |

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

The v1 errata in Refs #1412 (2026-10-20), before the first implementation,
make Retry-After grammar, JSON constants and bounds, diagnostic scope and
verbatim original records explicit, bound model scanning work, and add binary64
rounding vectors. The model, new scenarios and document change together;
s01–s22 and captured bytes retain their original expectations and contents.

The v1 errata in Refs #1398 (2026-10-20) clarify empty/all-blank normalization,
zero stated latitudes, summaries before issue, summary adjustments and
retention, multiple cancel-only versions, cancel-only families, competing link
fields, unrecognized watch months, synoptic end components,
field/headline/media whitespace, missing-level rank,
the 3-day product marker and escalation after an earlier raise, to state the
reference model's existing behaviour. The model, scenarios, captured bytes and
every expectation are unchanged.
