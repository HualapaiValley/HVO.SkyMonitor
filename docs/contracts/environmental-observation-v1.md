# Environmental Observation v1 and v2

`EnvironmentalObservationV1` is the storage-neutral weather and sky evidence contract owned by
`HVO.SkyMonitor.Processing`. LogicHost persists the contract in normalized SQL records; the
contract has no EF Core, ASP.NET, worker, or host dependencies.

## Identity and provenance

- `ObservationId` is producer-assigned and is unique within one source identity.
- A source identity is the canonical hash of site, optional agent/rig scope, provider, source ID,
  and source version.
- The source content hash additionally covers evidence kind and stable canonical provenance. Reusing a
  source identity with different content is a conflict.
- The observation content hash covers every source-authored field but excludes receiver-owned
  `ReceivedAtUtc`. Exact retries therefore converge even when received at different times.
- `Measured`, `Simulated`, `Imported`, `Manual`, and `Derived` are distinct evidence classes.
  Correlation requires an explicit ordered source-kind policy and never silently treats one class
  as another. Derived evidence must identify at least one source observation using its source
  identity SHA-256 plus source-scoped observation ID. Lineage belongs to each observation, not the
  stable source descriptor. Producer order is semantically significant, persisted by ordinal, and
   protected through restrictive normalized relationships.

V1 and v2 are concurrent schemas. V2 source-identity and source-content hash
domains include the observation kind so one configured source cannot change its
kind without changing identity. V1 domains, payloads, and hashes remain
byte-for-byte compatible. Existing v1 rows are never rewritten as v2.

CameraAgent targetless facts use the canonical
`hvo-targetless-environmental-source-identity-v1/v2`,
`hvo-targetless-environmental-source-content-v1/v2`, and
`hvo-targetless-environmental-observation-content-v1/v2` domains. A targetless fact omits
receiver-owned site, agent, and receipt time. Its optional `RigId` is
producer-authored and, when centrally projected, overrides only the resolved
delivery target rig. Receiver time and the later central target do not change
producer content identity.

Canonical JSON uses camel-case property names, string enums, ordinal object-key ordering,
array-order preservation, uppercase SHA-256 text, invariant numbers, and UTC timestamps. Numeric
enums, missing required members, malformed JSON, unsupported schema versions, and payloads larger
than 64 KiB are rejected. Provider/source/method and optional rig identities are bounded to 128
characters; the site identity is a non-empty GUID, and source/method versions plus submitted-unit
assertions are bounded to 64 characters.
Provenance parameters must be an object without duplicate keys, and lineage is bounded to 256
qualified references. Provenance JSON numbers retain their lexical representation, so `1`, `1.0`,
and `1e0` are intentionally different parameter documents and hashes.

## Time semantics

- `ObservedAtUtc` is the authoritative source observation instant.
- Optional `ObservedFromUtc` and `ObservedThroughUtc` describe the sampled interval and must either
  both be present or both be absent.
- Validity is half-open: `[ValidFromUtc, ValidThroughUtc)`.
- `StaleAfterUtc` is explicit and may equal the exclusive validity end. Freshness is half-open at
  the stale boundary: an instant equal to `StaleAfterUtc` is stale. Staleness is evaluated for the
   requested capture instant or exposure interval; it is not stored as mutable truth.
- An instant equal to `StaleAfterUtc` is stale. A half-open exposure
  `[from, through)` remains fresh when `StaleAfterUtc == through`, because the
  stale instant is not part of that exposure.
- `ReceivedAtUtc` is assigned by LogicHost. Delayed, out-of-order, and apparently clock-ahead
  observations remain visible rather than being reordered by receipt time.
- Missing observations produce `Missing`; stale evidence produces `Stale`. No standard pressure,
  dry, clear, zero, or current value is synthesized.

Capture correlation uses the historical frame site, device, rig, and exposure interval. A current
registration or current rig profile is never substituted. Candidates must match explicit source
and quality policies. Selection is deterministic by fresh coverage first, then source priority, target
specificity, authoritative observation time, source identity hash, and observation ID. Receipt
order is not a tie-breaker.

Local capture association persists `Fresh`, `Stale`, `Missing`, or
`Contradictory`, plus the policy identity, selected evidence identity, ordered
conflicting identities, exposure interval, and association hash. Candidates are
first collapsed to the newest applicable observation per source identity, so
overlapping readings from one source cannot contradict themselves. Contradiction
is evaluated only across independent equally ranked sources and has no selected
record. Cloud-assessment and weather-overlay inputs preserve that explicit status
instead of manufacturing a dry or safe value.

## Values and units

Each observation carries one scalar value, one quality, and optional nonnegative uncertainty.
Wind speed/direction and related multi-axis data are represented as separate observations sharing
source and time semantics.

| Kind | Canonical unit | Contract range |
| --- | --- | --- |
| `AirTemperature` | `DegreesCelsius` | finite, at least -273.15 |
| `RelativeHumidity` | `Percent` | 0 through 100 |
| `AtmosphericPressure` | `Pascals` | positive finite |
| `WindSpeed`, `WindGust` | `MetersPerSecond` | nonnegative finite |
| `WindDirection` | `DegreesTrue` | 0 inclusive through 360 exclusive |
| `PrecipitationRate` | `MillimetersPerHour` | nonnegative finite |
| `RainState` | `Boolean` | explicit true/false |
| `SkyBrightness`, `SkyQuality` | `MagnitudesPerSquareArcsecond` | finite |
| `CloudCover` | `Fraction` | 0 through 1 |

`environmental-observation-v2` is an additive concurrent schema. It preserves
all v1 fields and meanings and adds `CameraSensorTemperature` in
`DegreesCelsius`, finite and at least -273.15. This value means an effective
image-sensor reading or an explicit image-sensor simulation and must be
rig-scoped. It never means a temperature setpoint, camera-body or cold-finger
reading, or calibration applicability temperature. V1 remains supported and
continues to reject the v2-only kind; existing v1 canonical hashes do not
change.

Sky temperature is not defined. Radiative brightness temperature, an IR cloud
sensor differential, and an inferred equivalent temperature are different
quantities and must not be mapped to `AirTemperature` or
`CameraSensorTemperature`.

When normalization converts a submitted numeric value, `SubmittedNumericValue` and
`SubmittedUnit` are retained together as bounded, opaque producer assertions. LogicHost does not
re-run provider-specific conversions; the versioned source method and parameters identify that
normalization. The canonical value/unit and same-unit uncertainty are the validated query fields.
Image-derived cloud scores, masks, and confidence products are not this
`CloudCover` observation and remain owned by the later cloud-assessment contract.

## Persistence and retention

CameraAgent publication succeeds when the canonical targetless fact commits to
its local SQLite journal. Optional central target resolution, projection,
delivery, or acknowledgement is an independent consumer and cannot reject or
delete that local history. Missing, timeout, invalid, and failed source calls are
durable attempt outcomes and are never scalar observations.

LogicHost stores immutable source versions separately from observations. Unique source identity
and `(source, observation ID)` indexes enforce idempotency. Temporal indexes begin with source and
kind. Observation rows also snapshot immutable site/agent/rig, source-kind, and source-identity
query keys so correlation can select one row by exact scope before loading its source and lineage
graph. Retention uses `(received time, validity end, ID)`. Retention removes only observations
that are both older than the receipt policy and no longer valid, in configurable bounded batches;
source definitions remain available for provenance. Qualified derived lineage is normalized and
prevents deletion of a referenced source observation while the derived observation remains
durable. Future processing references must likewise use restrictive foreign keys or explicit pins.
Rig identity is compared with ordinal, case-sensitive SQL semantics so differently cased producer
identities cannot be correlated or historically bound to each other.

Runtime signals use bounded observation kind, source kind, quality, disposition, diagnostic, and
outcome labels. Provider/source/site/rig IDs, values, provenance JSON, paths, connection strings,
and secrets are prohibited metric labels and public health details.
