# Orbital data and prediction decision checkpoint (#527)

Date: 2026-10-19. Decision owner: Batch A,
`codex:codex:hvo-dev-03:1d916c0f`. Execution: primary/account1 Codex,
`gpt-6.1-sol`, Extra High (`xhigh`).

## Status and authority

The operator issued [Prompt A](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1391#issuecomment-6274638327)
and authorized the bounded [roadmap activation](../roadmap.md#bounded-satellite-and-sky-context-activation--2026-10-19).
The choices marked **selected** below are proposed during this PR's review;
merging its independently reviewed commit accepts this software checkpoint
under that delegated assignment. A selected design is not a claim that its
implementation or physical qualification has passed. Issue/PR ledgers record
the actual reviewer, immutable range, acceptance and merge SHA.

#527 remains research-only and open while the explicitly retained investigations
below remain. #1402 implements the [shared contract](../contracts/orbital-elements-v1.md).
After its reviewed delivery, Batch B can implement #528 while Batch A implements
#529 against frozen fixtures. #528 remains a live-integration requirement.
Neither optional fleet enrollment nor completion of all stream-native #143 is
a prerequisite for this bounded slice. #530 still needs its actual measured
geometry and transient products; #1395 supplies integrated cadence evidence.

## Decision ledger

| Decision | Checkpoint disposition | Qualification / remaining boundary |
| --- | --- | --- |
| Initial source | Selected: CelesTrak public GP JSON, OMM field definitions, explicitly configured `GROUP=active` | Partial population; not a complete streak-screening catalog |
| Supplemental/authenticated providers | Deferred: SupGP, authenticated Space-Track, operator ephemerides | Separate terms, credentials, validity and precedence decision before enablement |
| Source topology | Selected: one CameraAgent-local acquisition authority per deployment; other local consumers read its immutable cache | Central distribution/installation-wide proxy is optional future topology, not a second concurrent poller |
| Format | Selected: HVO normalized Earth/TEME/UTC/SGP4 records with modern IDs, original bytes retained | No five-digit TLE round-trip; other formats require explicit provider profiles |
| Source data use | Selected: local calculation/cache, retained assessment evidence and attributed small research facts | Whole-feed release redistribution and supplemental products are not selected |
| Propagator direction | Selected: qualify the two-file aholinch C# core, pinned below, maintained by HVO | #529 must pass the independent full reference suite before supported propagation |
| Numerical model | Selected: WGS72, AFSPC `a`, geometric/unrefracted output; WGS84 observer | Existing POC uses `i`; retain that identity rather than silently relabel it |
| Time / EOP | Selected: explicit UTC/UT1, EOP and elevation facts; opt-in declared approximations only | Missing EOP cannot silently become precise measured association; leap-crossing intervals initially unsupported |
| Freshness | Selected: signed age at requested interval endpoints; 48-hour warning and 168-hour rejection envelope, at most 24 hours before epoch | Operational bounds, not measured angular-error/confidence guarantees |
| Scientific uncertainty | Selected: unknown physical orbital error unless separately supported | Empirical age/population/maneuver calibration remains #527 research and later adoption evidence |
| Refresh | Selected: disabled until configured; provider-specific two-hour minimum, persisted halt/recovery and last-known-good | Conditional/304, automatic redirects and generic HTTP retries are not enabled for CelesTrak |
| Identity / history | Selected: separate object, record, normalized payload, acquisition manifest and product hashes | Original-availability replay and retrospective reassessment are distinct append-only operations |
| Sampling / performance | Selected: explicit query bounds and numerical acceptance below | #529 and #1395 measure component and full-cadence behavior; no performance pass is inferred here |

## Source, terms and coverage evidence

| Candidate | Access / model | Terms and operational evidence | Initial use |
| --- | --- | --- | --- |
| [CelesTrak GP](https://celestrak.org/NORAD/documentation/gp-data-formats.php) | Public HTTPS, JSON/CSV using OMM field definitions; underlying USSF/Space-Track GP | [Usage policy](https://celestrak.org/usage-policy.php), source attribution retained; no feed-redistribution license is inferred from accessibility | Selected local GP input |
| [CelesTrak SupGP](https://celestrak.org/NORAD/documentation/sup-gp-queries.php) | Supplemental operator/mission ephemerides fitted into mean elements; several records per object and forward intervals possible | Source-specific terms, intervals and selection differ from ordinary GP | Deferred until separately qualified |
| [Space-Track](https://www.space-track.org/documentation) | Authenticated current/historical GP and catalog metadata | Account/API/redistribution rules apply; protect credentials; ordinary CelesTrak GP and Space-Track are not independent observational fallbacks | Optional historical/comprehensive coverage route |
| [NASA ISS trajectory](https://spotthestation.nasa.gov/trajectory_data.cfm) | Mission-specific trajectory/ephemeris products | Different schema, validity and source terms; not a general satellite population | Comparison/research candidate |

CelesTrak's documented GP query supports one-to-nine-digit catalog identifiers.
Its JSON may omit Earth, TEME, UTC and SGP4 constants. Supply those only under
this source profile; reject conflicting metadata. Preserve source numerics,
units and the normalizer identity. `CLASSIFICATION_TYPE=U` is a security
classification, not object kind or operational status. Names and international
designators are aliases, not primary identity. Analyst-number reuse needs
additional source/disambiguation facts before historical analyst support.

The initial active **provider group** is not an authoritative payload-only
taxonomy: the measured sample includes objects whose names contain attached
rocket bodies. Do not infer physical type from names. Inactive payloads,
unattached rocket bodies, debris, unpublicized objects and missing/analyst
records are coverage gaps until appropriately sourced. A no-match means
unidentified within this coverage, never proof of a meteor.

The provider's [usage policy](https://celestrak.org/usage-policy.php) requires
GP/SupGP clients to respect two-hour updates and stop/report non-200 responses.
The selected implementation uses canonical HTTPS only, no automatic redirects,
no conditional requests and no automatic HTTP retry. A 301, 304, 403, 404, 429
or 5xx response halts that provider's acquisition until recorded operator
investigation/resume. Manual refresh shares the same task and cannot bypass
the minimum interval or clear the halt. Resume remains subject to the next
permitted attempt. EOP is a separate daily source, not another GP query.

Only explicitly configured acquisition runs. Schedule with stable per-deployment
jitter, but enforce at least two hours since the last outbound attempt even
after restart or manual requests. Persist UTC scheduling facts and reason codes;
use monotonic durations for work timing. A transport timeout without an HTTP
response retains last-known-good and waits for the next permitted scheduled
attempt, with no immediate retry. A provider-wide halt applies to its configured
GP queries, so changing the group cannot evade it. Coordinate shared NAT/cache
deployments; do not let every frame or consumer poll independently.

### Retained scale sample (one acquisition, not a time series)

The retained active JSON was fetched once on 2026-10-19 at
17:28:18.864260 UTC, HTTP 200. SHA256:
`dfee7c9e5d9d58e1889200a4610f278d3e9809a8a3fa0a5e392040d52c96b62a`.
It contains 16,754 records, 11,151 Starlink-named objects, 826 six-digit IDs
and 7,057,239 bytes. Its epochs include two future records, up to about
22.52 hours ahead, and 25 older than seven days. The initial normalization
must preserve those facts rather than resetting age on retrieval.

Local deterministic gzip of those bytes is 1,015,570 bytes. At twelve full
acquisitions/day that is 84,686,868 raw bytes or a **local compression estimate**
of 12,186,840 bytes/day, excluding HTTP overhead. It does not prove the server
offers that encoding. Ten direct instances multiply raw traffic to 846,868,680
bytes/day; a single approved cache/distributor can avoid that duplication.
Multi-update changed-record ratios, transport compression, comprehensive-feed
volume and long-term bandwidth remain unmeasured. No redundant GP fetch was
performed for this checkpoint.

## Propagator and exact software notices

| Candidate / exact pin | License / behavior | Decision |
| --- | --- | --- |
| [SGP.NET 1.6.0](https://www.nuget.org/packages/SGP.NET/1.6.0), tag `dd7e08f03f846b7a885a55fe01abba3fff046221` | [MIT](https://github.com/parzivail/SGP.NET/blob/dd7e08f03f846b7a885a55fe01abba3fff046221/LICENSE); supports net10.0 and OMM, but hardcoded WGS84 propagation without public gravity/mode controls | Not selected without a qualified source change; do not confuse WGS84 observer geometry with WGS72 propagation |
| [aholinch/sgp4](https://github.com/aholinch/sgp4/tree/552cb1489a52c3023ae70cb6c7e239e84c5950fe), `552cb1489a52c3023ae70cb6c7e239e84c5950fe` | [Unlicense](https://github.com/aholinch/sgp4/blob/552cb1489a52c3023ae70cb6c7e239e84c5950fe/LICENSE); C# core has explicit WGS72 and mode; C# last changed in 2021 and maintainer declines contributions | Selected qualification direction, with HVO maintenance ownership and upgrade review |
| [Vallado/CelesTrak](https://github.com/CelesTrak/fundamentals-of-astrodynamics/tree/49df0479c950917c0a2c5ac31f2db6477bba57f6), `49df0479c950917c0a2c5ac31f2db6477bba57f6` | [NOTICE](https://github.com/CelesTrak/fundamentals-of-astrodynamics/blob/49df0479c950917c0a2c5ac31f2db6477bba57f6/NOTICE) preserves unrestricted terms only for `software/cpp/SGP4/`; current C# and surrounding code are AGPL | Independent C++ oracle, using only the unrestricted directory and an HVO-written harness |

#529 may vendor only `SGP4.cs` and `ElsetRec.cs` with their upstream bytes,
license, SHA and a documented minimal namespace/nullability adaptation. Do not
import the old executable, fixed-width TLE wrapper, or unqualified helpers.
Mutable element records are private per calculation; verify concurrent and
out-of-order calls. The baseline is 2,251 lines / 74,661 bytes. Upgrades require
license/algorithm diffs, all reference vectors and measured regression evidence.
No production source or package is imported in #527 or #1402.

Content pins:

```text
SGP4.cs      fd66e164f17ee23a424e517e131a59dad6f3217dc178ce06165d3b597ab4d419
ElsetRec.cs  ed45d790765fdd131a41e952efa12b38f83bb66cfec69d4b3719e16912c8b1de
LICENSE      88d9b4eb60579c191ec391ca04c16130572d7eedc4a86daa58bf28c6e14c9bcd
AIAA archive 3642043b706c76be87cf012db3f22e04da6b80498d00f515e51879e0ffadc115
SGP4-VER.TLE d246d1d9d768ace445a38a965713fa9ba52d80fd8a41a0502ff83d7acffe2881
```

The official [verification FAQ](https://celestrak.org/publications/AIAA/2006-6753/faq.php)
recommends AFSPC mode and WGS72. The retained Python comparison found mode `i`
can differ from the published `a` cases by about 0.964 km (case 23599); that
is a model variant, not grounds to loosen tolerances. Preserve the POC's `i`
identity. Published-vector qualification uses `a`; any future `i` engine needs
its own independently generated oracle. Numerical agreement does not establish
physical orbit accuracy.

## Time, selection and uncertainty policy

- Propagate the source epoch in UTC with the selected SGP4 model; use UT1 for
  Earth rotation and record the TEME/GMST82/polar-motion model. TEME is not
  J2000/ICRS. Stellar midpoint conversion is not a satellite sample transform.
- The observer uses WGS84 geodetic latitude, east-positive longitude and
  ellipsoidal height. Existing rig/location fields remain authoritative;
  label an assumed height datum rather than presenting it as surveyed.
- Bind EOP source/hash, sample coverage and final/predicted/approximated status,
  DUT1 and polar motion. A missing EOP input may use an explicitly requested
  zero-DUT1/zero-polar approximation for configured-rig diagnostics only,
  with a degraded reason. Do not silently extrapolate or provide a measured
  precision bound. Shadow/solar conversion similarly binds its model/input
  identity and may return unknown until qualified.
- Freeze clock facts and actual/reported/estimated/unknown exposure/readout
  timing. Command or status timestamps are not automatically sensor timing.
  DateTimeOffset cannot represent leap seconds: explicitly withhold a
  leap-crossing interval until a supported continuous-time representation and
  leap table are approved. No refraction is the initial satellite convention.
- Preserve the CelesTrak numeric `MEAN_MOTION_DOT` / `DDOT` values and their
  GP/TLE-field convention. The SGP4 adapter performs unit conversion only
  (rev/day to rad/min and the corresponding powers for these fields), matching
  [python-sgp4's OMM initializer](https://github.com/brandon-rhodes/python-sgp4/blob/092d206d6e6556a3cbbbe86f6ba4293c1f4ec646/sgp4/omm.py).
  Do not apply an extra factor of two or six, relabel the stored coefficients
  as independently measured physical acceleration, or infer track motion from
  them. Motion comes from propagated states.
- For initial GP-only selection, require the approved source/model, requested
  interval within any source validity, and signed epoch offsets at **both**
  endpoints between -24 and +168 hours. Offsets whose absolute value exceeds
  48 hours are stale; >168 hours is expired. Missing source validity stays
  unknown, separately from policy eligibility. This is an operational envelope,
  not a calibrated covariance or a promise that a fresh maneuvering object is
  accurate. Stale data is permitted only for explicitly qualified diagnostics;
  no high-confidence association is inferred from age alone.
- Select one eligible record for the whole continuous interval by approved
  source rank, closest epoch to interval midpoint, newest epoch on equal
  distance, then ordinal record hash. Unknown source precedence is ineligible.
  Record the policy/hash, offsets, tie-break and exclusions. Do not switch
  records mid-track or apply this GP rule to unqualified SupGP records.
- Sampling/chord tolerance, physical orbit uncertainty, clock error, calibration
  covariance, sensor clipping and detection error stay separate. Unknown
  components are nullable/reason-coded, never zero. A single integrated streak
  does not necessarily reveal direction or angular speed. Multiple candidates,
  partial populations and no matches remain explicit.

## Durable acquisition state and retention

```text
disabled -> configured/due -> acquiring -> validating -> publishing -> active
                            |            |              |
                            + non-200 -> halted          + crash -> reconcile
                            + transport -> next permitted scheduled attempt
                                         + invalid -> quarantine
```

The active pointer changes only after validated immutable bytes and metadata
are durable. Crash/restart reconciles staged candidates without editing
accepted bytes. Readers materialize a validated immutable snapshot once and
pin it for a calculation; activation and retention cannot mix/delete its
inputs. Keep retrieval success, successful activation and per-record age
distinct. Acquisition halt can coexist with a usable last-known-good snapshot.

Manual and scheduled refresh share one bounded durable task and run history.
Extend the existing task registry for the periodic task as needed; it currently
accepts source-window adapters and is not an unchanged plug-in seam. This
focused extension does not activate the held #1133 wizard or a new scheduler.
Keep separate source-specific payload, decompression, object and work limits;
the current projected-scene 4 MiB/10,000-object limits cannot hold this sample.
Initial ceiling: 32 MiB wire, 128 MiB expanded, 100,000 records, 32 sources,
30-second request timeout and single-flight execution. Exceeding any bound
rejects/quarantines the candidate, retains last-known-good and reports why.

Retain original bytes, normalized bytes/manifest, selected record, parser/model
and supporting inputs for every referenced assessment/product. Garbage
collection needs complete reference and active-reader checks; ambiguous
references prevent deletion. Original-availability replay uses retained local
activation history. Later historical data or improved astrometry creates a new
assessment with its processing time and provenance; a current snapshot cannot
substitute for unavailable historical GP. No secret-bearing URI, credential or
authorization header enters hashes, artifacts, logs or handoff files.

## Acceptance, portable evidence and retained research

The [reference fixture manifest](../validation/orbital-reference-v1/manifest.json)
pins small source facts and numerical inputs. #1402 adds strict schema and
selection conformance fixtures; #529 adds propagation/projector/pass evidence.
The old POC commit `d97607b09f5433e3f0f1b8b5582286a37ea5ae43` remains local until
explicitly transferred. Its external 640-square/20-second images are research
evidence, not five-second or physical qualification. Do not tell Batch B that
a local artifact path is a remotely usable dependency.

#529's numerical gate is maximum position-vector residual 1e-6 km and velocity
residual 1e-8 km/s against the pinned AFSPC/WGS72 oracle. Test all published
near/deep/resonance/error cases, including negative/fractional/reordered times;
assert expected errors instead of requiring every invalid case to propagate.
Test coordinate conversion separately against a frozen EOP-aware independent
reference, declaring tolerances before implementation. Adaptive projected
chord error is at most 0.25 pixels, maximum sample interval one second, with
hard query/cancellation bounds; inability to meet them produces partial/budget
evidence, never a silently complete coarse track. Future-pass event accuracy
and near-tangent search behavior need separate declared qualification.

Full live acceptance in #1395 includes queue/admission/preparation/detection,
prediction/association/render/local publication before the next completed image
at the configured five-second arrival cadence. Measure actual exposure/readout
and arrivals separately, with next capture and background refresh running.
Use declared hardware/full configured resolution and separate held-out tuning
and evaluation sets. Report worst/tail latency, misses, CPU, allocations,
working set, I/O and durable backlog. Centered N+2 refinement is later evidence,
not a causal deadline pass. Retain raw evidence and explicit incomplete status
under overload.

Remaining #527 research (not a prerequisite invented for the bounded contract
delivery): multi-update change rates/volume, complete inactive/debris/analyst
coverage and terms, supplemental precedence/redistribution, physically measured
age/maneuver error and match thresholds, validated solar/shadow assumptions,
target architecture/AOT/resource qualification and calibrated real-image cases.
The selected engineering direction enables software preparation; these
limitations remain visible on the owning issue and gate only claims that need
their evidence. No flight tracking, individual-meteor prediction, hard event
rejection, physical certification or deployment is added here.
