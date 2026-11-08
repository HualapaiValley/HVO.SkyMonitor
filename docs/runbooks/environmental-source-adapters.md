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
adapter is later used with a measured transport. Derived infrared cloud cover
remains `Derived`, with lineage to the two simulated or measured input facts.

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
derived cloud fact; retries are idempotent. Sensor and image cloud-cover facts
keep separate source identities. The bridge owns heater control and must assert
`drying` throughout heater-driven drying/warm-up until a valid dry reading is
qualified; the adapter never commands a heater or assumes it has finished. A drying wet sensor reports
`wet-sensor-drying`; it never reports dry.

`SkyQualityMeter` issues Unihedron-compatible `rx` through the transport seam and
parses the six-field `r,<mag>m,<frequency>Hz,<counts>c,<integration>s,<temperature>C`
response. LAN and USB transport are both physically held. The real integration
interval is retained, with sensor temperature in per-reading metadata. A zero
or negative magnitude is saturated; invalid ranges, malformed values and
unbounded integrations are unavailable. Configure `BeforeCapture` (optional,
`required: false`) and/or `Periodic`. A timeout or missing reading does not block
the exposure; its association is Missing. No image-derived SQM measure is added.

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
with a mixed-source label when groups differ. Frame Facts retain source and age
and label mixed groups. No nearest-station selection or averaging is performed.

## Contract and evidence

`environmental-observation-v3` adds SkyTemperature and SeaLevelPressure and
optional typed sample metadata for device receipt time and SQM sensor temperature.
V1/V2 keep their serialization and hash domains; null metadata is omitted. New
kinds and metadata are rejected in older envelopes. LogicHost accepts v3 through
the normal authenticated environmental delivery endpoint, covered by compatibility
integration tests. There is no database schema migration or deployment change.

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
