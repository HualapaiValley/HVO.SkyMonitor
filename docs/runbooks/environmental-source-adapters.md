# CameraAgent environmental source adapters

The RM-009 software slice (#1089–#1092) is explicitly activated by the operator's
2026-11-08 assignment. No device or provider is enabled by default. Configure
`CameraAgent:EnvironmentalAcquisition` in the existing operator settings file;
keep `Sources` empty when acquisition is disabled. LogicHost only receives facts
through the existing durable outbox and never polls these sources.

## Hardware transport and simulation

`LocalWeatherStation`, `InfraredSky` and `SkyQualityMeter` implement
`IEnvironmentalSource` in CameraAgent.Common. Their `Options` are
`EnvironmentalDeviceOptions`: a non-sensitive `deviceId`, optional `simulation`,
and the infrared model settings. With `simulation` absent, the adapter uses the
injected `IEnvironmentalDevice`. Without that transport it reports
`device-unconfigured`. Stable settings and simulated/measured nature select an immutable source-version
digest; switching to a physical transport can retain the source alias without
relabeling old readings. Physical drivers are intentionally behind that seam:
**physical qualification held** for #1089, #1090 and #1091. No hardware issue is
closed by the software evidence.

The chosen station protocol is a local bridge JSON v1. An existing Davis gateway
must remain the sole console poller; the seam consumes its sample rather than
adding a second console connection. Direct vendor LAN/serial/USB/MQTT transports
were rejected for this slice because no hardware is available to qualify them.
A bridge returns device sample time independently of CameraAgent receipt time,
UTC (`SampledAtUtc` and actual `ReceivedAtUtc` on the returned sample), and at most 16 KiB. The physical implementation must honor cancellation and
report missing, disconnected, faulted and out-of-range states explicitly.

The stateless simulator returns reproducible configured bytes, sample age and
fault states. `mode` is `Normal`, `Missing`, `Disconnected`, `Faulted`, `Stale`,
`ClockSkewed`, `OutOfRange` or `Timeout`. `sampleAgeSeconds` is configurable.
Configure clear/cloudy, rain/drying, saturation, slow integration and malformed
scenarios through `payload`. Recreation after restart reproduces the same
scenario. A simulated device always publishes `Simulated`, including when its
adapter is later used with a measured transport. Computed infrared cloud cover
is `Simulated` when its inputs are simulated, and `Derived` when measured. Both
retain the two input facts and model lineage; simulated cloud cover never gains
the default priority of measured or derived evidence.

Example source entry (repeat with a different `id` and `kind` for other fields):

```json
{
  "id": "station-temperature",
  "type": "LocalWeatherStation",
  "kind": "AirTemperature",
  "required": false,
  "triggers": ["Periodic", "OnDemand"],
  "periodSeconds": 60,
  "validForSeconds": 900,
  "staleAfterSeconds": 300,
  "options": {
    "deviceId": "station",
    "simulation": {
      "mode": "Normal",
      "sampleAgeSeconds": 2,
      "payload": "{\"temperature\":{\"value\":68,\"unit\":\"F\",\"uncertainty\":0.9}}"
    }
  }
}
```

| Station field | Kind | Bridge units → canonical units |
| --- | --- | --- |
| `temperature` | AirTemperature | C or F → °C |
| `humidity` | RelativeHumidity | % → % |
| `pressure` | AtmosphericPressure | Pa or hPa → Pa (station pressure only) |
| `windSpeed`, `windGust` | WindSpeed, WindGust | m/s or mph → m/s |
| `windDirection` | WindDirection | deg → degrees true, [0,360) |
| `rainRate` | PrecipitationRate | mm/h or in/h → mm/h |
| `rain` | RainState | bool → boolean |

Each field is `{ "value": ..., "unit": ..., "uncertainty": ... }`; uncertainty
is optional and scales by the unit conversion factor. `fault: true` suppresses
the field; `suspect: true` preserves Suspect quality. Missing fields never become
zero. Calm or missing wind speed makes direction unavailable. `counterReset:
true` suppresses rain rate after gauge resets/rollovers: the bridge must obtain
a fresh valid interval before clearing it. Other values are instantaneous
samples; rain rate requires the bridge's native measurement interval through
`EnvironmentalDeviceSample.ObservedFromUtc`. Simulation supplies a configurable
`intervalSeconds` (default 60); a physical sample without that interval is unavailable.
Dew point is not a new kind.

`InfraredSky` consumes JSON such as
`{"skyCelsius":-20,"ambientCelsius":10,"rain":false,"drying":false,"brightness":20.5}`.
It supports SkyTemperature, AirTemperature, CloudCover, RainState and
SkyBrightness (mag/arcsec²). Sky-facing thermopile/rain bridge v1 was selected;
proprietary cloud-watcher commands and direct I2C require physical qualification.
The `sky-ambient-linear-v1` model maps sky minus ambient between
`clearDeltaCelsius` (default -25) and `cloudyDeltaCelsius` (default -5), clamps to
[0,1], and retains both thresholds and version in provenance. Thresholds require
site qualification. Temperature input facts are durably published before the
derived cloud fact; retries are idempotent. Synthetic temperature inputs carry
`readingRole: "lineage-input"` in provenance. They remain in history and delivery
but do not compete with configured station readings in current, capture or central
correlation selection. The outbox waits for both inputs to be acknowledged before
claiming the cloud fact; missing-lineage conflicts are retryable, and an unresolved
waiting projection does not block independent records. Sensor and image cloud-cover facts
keep separate source identities. The bridge owns heater control and must assert
`drying` throughout heater-driven drying/warm-up until a valid dry reading is
qualified; the adapter never commands a heater or assumes it has finished. Active
`rain: true` takes precedence over `drying: true` and reports RainState true.
A drying payload without a rain field retains the unavailable drying outcome.
When rain is false and drying is true, the adapter reports unavailable
`wet-sensor-drying`; it never reports dry during drying.

Only IR facts include `modelVersion`, `clearDeltaCelsius` and
`cloudyDeltaCelsius` in their source parameters. New station and SQM facts retain
`protocol`, `deviceGroup`, `acquisitionSourceId` and `readingRole`. Removing the
irrelevant fields changes their parameter digest and source-version digest;
`environmental-adapter-v1` and the `environmental-adapter` / `adapter-v1` method
label remain valid. LogicHost validates the parameter digest and bounded method
identity without an adapter-version allowlist. IR parameters and source-version
digests stay byte-identical. Retained station/SQM facts and queued deliveries
keep their original parameters, identities and payloads; they are not rewritten.

An input that is terminal, quarantined or discarded cannot leave its dependent
v3 cloud delivery pending indefinitely. The derivative becomes terminal with
`lineage-input-undeliverable` and appears in environmental dead letters, including
when it has never been attempted. This propagates through dependent derivatives
without affecting unrelated delivery. Restart and idle-claim reconciliation repair
stranded work; an active lease keeps its fencing until it expires.

Recover the inputs first, then explicitly replay the derivative. Replaying an
input does not silently revive terminal dependents, and derivative replay never
bypasses input acknowledgement or a still-unrecoverable input. An input frozen to
a different site, device or rig also requires fresh compatible evidence.
Abandoning v3 delivery discards that projection durably: restart, waiting projection
and duplicate publication cannot recreate it. Local history and capture lineage
remain immutable and expire only through their normal retention policy. The
legacy v1/v2 independent-reprojection behavior is preserved.

IR input publication failures report `journal-unavailable`, rather than blaming
the sensor as malformed. Busy-only capture requests retain the last completed
source availability while remaining visible in the attempt audit; actual read
timeouts and failures still make the source unavailable.

`SkyQualityMeter` issues Unihedron-compatible `rx` through the transport seam and
parses the six-field `r,<mag>m,<frequency>Hz,<counts>c,<integration>s,<temperature>C`
response. LAN and USB transport are both physically held. The real integration
interval is retained, with sensor temperature in per-reading metadata. A zero
or negative magnitude is saturated; invalid ranges, malformed values and
unbounded integrations are unavailable. A bright frequency-mode response with a
zero period reports `sqm-frequency-mode`, rather than inventing an integration
interval or classifying a valid frequency-mode packet as malformed. Configure `BeforeCapture` (optional,
`required: false`) and/or `Periodic`. A timeout or missing reading does not block
the exposure; its association is Missing. No image-derived SQM measure is added.
The retained integration interval describes the measurement. Optional pre-exposure
association currently uses validity/freshness, so a sample ending just before
capture can describe that capture without claiming temporal overlap. A versioned
interval/gap policy and physical timing validation remain tracked in [#1735](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1735).
`BeforeCaptureWaitMilliseconds` bounds device acquisition. Durable attempt
settlement can add storage latency; using an expired acquisition token for that
settlement would allow old failed-source evidence to reappear. A strict total
capture deadline with a durable capture fence is also tracked in #1735.

## Online weather

`OnlineWeather` uses NWS or the confirmed HVO station profile. Both require an
explicit station identity. NWS uses its pinned `/stations/{station}/observations/latest`
endpoint. HVO requires an operator-configured anonymous HTTPS `hvoEndpoint` and
`hvoStationConfirmed: true` after confirming the endpoint is the single intended
station. Its bridge profile expects WeeWX/Davis JSON with Unix `dateTime`,
`outTemp` (°F), `outHumidity` (%), `windSpeed` (mph), `windDir` (degrees true), and
`barometer` (inHg). Endpoint/schema confirmation is an operator integration
checkpoint; no live provider request was used as test evidence.

| Provider | Terms and attribution | Authentication and limits | Coverage and age |
| --- | --- | --- | --- |
| NOAA/NWS | Public-domain US government data; retain NOAA/NWS attribution. [API documentation](https://www.weather.gov/documentation/services-web-api) | No key; descriptive User-Agent; explicit 300-second minimum interval and server Retry-After. No fixed public numerical rate limit is assumed. | Explicit US station; stale after 90 minutes, valid 2 hours |
| HVO | Observatory feed; retain Hualapai Valley Observatory attribution and confirm endpoint use with the operator | Anonymous profile, 60-second minimum interval; honor server backoff | Single confirmed station; stale after 5 minutes, valid 15 minutes |
| Weather Underground | Accepted design defers use until written rights permit normalization, retention and redistribution; [terms](https://www.wunderground.com/company/legal) | Inactive | Deferred |
| Open-Meteo | [Terms](https://open-meteo.com/en/terms) and attribution differ by usage | Not selected | Forecasts deferred; never masquerade as observations |

Example NWS options: `{ "provider": "Nws", "stationId": "KIGM" }`, with only
`Periodic` triggers, `periodSeconds: 300`, `staleAfterSeconds: 5400` and
`validForSeconds: 7200`. HVO uses `provider: "Hvo"`, the confirmed endpoint and
station ID, and 60/300/900-second timing. The shared bounded SWPC transport
handles deadlines, gzip, redirects, validators and redaction. Decoded bodies are
limited to 64 KiB and JSON depth to 12. A 304 preserves the provider sample time;
a stale cached sample remains stale. 429 uses bounded Retry-After, failures clear
current availability, and no last value is relabelled current. The body cache restarts
cold; provider Retry-After and minimum polling times survive restart in the
existing durable source runtime store under reserved `__weather-provider-<digest>`
rows, independently from each reading's cadence, alongside observation history; no provider secret store is needed by these two key-free adapters.

HVO corrected barometer values are `SeaLevelPressure`, never
`AtmosphericPressure`; 1 inHg = 3386.37526 Pa. Gust, rain rate and cloud cover are
missing when absent. NWS values require their explicit WMO unit codes. All
provider samples are `Imported`; forecasts are not supported. Observations more
than 120 seconds in the future are rejected. URLs, host addresses and transport
exception messages never enter facts or outcome strings.

When online weather is configured, `weatherSourcePriority` must list configured
source IDs explicitly in the desired order, per kind. A typical ordering is the
local measured temperature source, HVO temperature, then NWS temperature, followed
by the equivalent humidity entries. The first fresh Good or Unknown Measured or
Imported source wins. Suspect and simulated sources are excluded from real
weather fallback; values are never averaged. Failed sources are unavailable for
new associations and history remains intact. The Environment history retains
separate Imported and Measured facts with source and sample age.

Optional `stationDistanceKilometers` and `stationElevationMeters` describe the
explicitly pinned station; missing metadata is shown as unknown. The Environment
page shows the source, age, distance and elevation on each imported reading,
with a mixed-source label when groups differ. Local devices keep their source
identity without unknown distance/elevation placeholders. Imported stations
without that metadata still show it as unknown. Frame Facts retain source and
age and label mixed groups. No nearest-station selection or averaging is performed.
Overlay lines reserve space for source class, `SRC` identity and `AGE`; compact
units keep them within 64 characters. Long source identifiers are shortened only
for display; retained identities stay complete. `?` marks non-Good quality.
Mixed-source markers distinguish local measured/derived evidence, simulations,
and imported provider/station origins. Per-kind virtual sources describe one
simulated environment; co-located local devices describe one local environment.
Full per-reading identities remain visible and retained.
The `ENVIRONMENT MIXED SOURCES` header marks differing origins without using a
reading row, so seven readings still fit the eight-line block and retain their age.
History retains all sources; the latest grid shows
the selected current reading for each kind. Failed slots say `Unavailable`.

## Contract and evidence

`environmental-observation-v3` adds SkyTemperature and SeaLevelPressure and
optional typed sample metadata for device receipt time and SQM sensor temperature.
V1/V2 keep their serialization and hash domains; null metadata is omitted. New
kinds and metadata are rejected in older envelopes. LogicHost accepts v3 through
the normal authenticated environmental delivery endpoint. POST/read-back/hash
validation tests cover the metadata, including an explicitly empty metadata object.
LogicHost stores bounded sample-metadata JSON and a normalized lineage-input role.
The canonical initial migration, designer and model snapshot are updated together
under the repository's unreleased-schema contract. An already-applied older initial
migration does not add these columns: pre-fix unreleased databases need a current
schema provisioned through that contract before adoption. This PR does not alter
running databases or deploy anything.

Unit evidence starts with failing-first missing-adapter builds, then behavioral
parsing regressions. Integration tests use the real factory, coordinator,
SQLite history/attempt store, capture association and fake HTTP handlers. No
hardware, live API credentials or live provider availability is needed. See PR
validation for exact ranges, test counts and qualification holds.

Provider transport state is keyed only by provider/request identity; optional
station display metadata cannot create another request or backoff budget.
Completed same-kind capture associations retain their original selection policy
when source priority changes; the new ordering applies to new captures. Synthetic
IR inputs identify their acquiring source explicitly in provenance; source aliases
sharing a prefix remain independent. Before-capture acquisition has its own time
budget, distinct from host cancellation, and records timeout before association.
Physical device freshness uses actual receipt time, while deterministic simulated
readings retain the scheduled clock.

Each before-capture request reserves its own durable leased acquisition operations
under the internal `__capture-before:` namespace in the existing command store.
The optional `cycleEvidence.acquisitionAttemptId` correlates that request with the
raw capture. It is omitted for legacy captures, preserving their serialized form.
No attempt is attributed to a capture by its timestamp or by the latest source row.

A busy request follows the exact in-flight read: success leaves that capture
available, while failure/missing/timeout fences its older readings. With any
before-capture budget, queued work may finish after exposure starts; a positive
budget bounds the caller's wait and the eventual device read, not queue admission.
Pending work defers the association lane without consuming failure attempts or
repeating after-capture reads. Queue refusal records an explicit unavailable result.
Known capture results settle before unrelated coalesced work drains, including
during bounded shutdown grace. Transient completion writes retry up to three times
within 15 seconds. Unsettled work remains pending only until its receipt lease
expires, then becomes unavailable; startup also marks interrupted work unavailable.
Independent on-demand operations retain their own leases and admission allowance.

Capture receipts and all acquisition-attempt diagnostics expire according to
`RetentionDays`, independently of retained raw-capture rows. Capture leases never
exceed that retention period. Expiry uses receipt/attempt timestamps without
parsing raw manifests. An unassociated capture whose receipt has expired or is
missing reports unavailable evidence, even if other associations still pin old
facts. Completed associations remain immutable. Association binds retained tokens
to raw capture IDs; explicit legacy capture IDs remain readable and anonymous
legacy history is never guessed into a capture. The SQM interval/gap
policy and a strict total deadline under SQLite contention remain in Refs #1735.

Coalesced triggers accepted during a bounded before-capture acquisition move to a
bounded deferred queue drained by the same hosted acquisition service. Queue
capacity refusal is recorded explicitly; a regime-change request does not need
a later capture or periodic trigger to make progress. Host shutdown owns worker
cancellation.

Coalesced receipts remain in attempt history but preserve the last completed
acquisition's effective availability, reason, timestamps and failure count. Only
a completed recovery restores a failed source. Online startup schedule reads retry
inside the hosted recovery loop. Native-period polling also respects completion
time, avoiding skipped refreshes when scheduling latency shrinks while retaining
the provider's strict minimum wire spacing. Freshness remains half-open: an
exposure ending exactly at `StaleAfterUtc` is fresh; a point at that instant is stale.

Current-reading queries filter lineage-only and unavailable sources before selecting
the newest primary per kind. A newer IR housing or sky input cannot hide a configured
primary reading; all input evidence remains in history. LogicHost observatory detail
uses the same lineage-only exclusion. Each waiting-projection pass attempts a row at
most once and stops at the first outbox-capacity refusal, so an outage cannot multiply
one refusal into a full batch of overflow counts.
