# Existing cloud and environment layer options

In CameraAgent **Current Sky**, choose **Edit existing cloud layer** or **Edit
existing environment layer**. The links open the named `cloud-layer` and
`environment-layer` sections of a draft copied from the active graph. You can
also select a retained revision under **Operations → Pipeline → Named graphs**
and draft from it. Missing or unsupported nodes are explained without creating
or upgrading them. Select a graph with the existing producer and configured
dimensions and dependencies.

| Panel / exact implementation | Controls | Code defaults / bounds |
| --- | --- | --- |
| Cloud layer / `cloud-presentation-v5-original-colours` | `drawLabels`, `lineThickness` | `true`; `1` px, range 1–8 |
| Environment layer / `metadata-corner-presentation-v6-original-colours` | `scale`, `inset`, `lineSpacing` | `1`, range 1–4; `4` px, range 0–64; `2` px, range 0–16 |
| Environment layer / same implementation | `environmentalKinds` | Absent: AirTemperature, RelativeHumidity, AtmosphericPressure, WindSpeed, RainState, CloudCover. Explicit empty: no environmental readings, `NOT RETAINED`. |

In **Environmental observations**, use each native dropdown to select an existing
kind. **Add**, **Remove**, **Up** and **Down** work with keyboard Tab and Enter or
Space as well as touch. All twelve existing kinds are available, including wind
direction/gust, precipitation rate, sky brightness/quality and camera sensor
temperature. Remove every item to request no environmental readings. Reordering
or repeating source entries is preserved; the producer sorts and deduplicates
the effective kinds, so the editor order does not set the displayed order.

Selecting a kind does not enable a sensor or provider, query live hardware, or
manufacture a measurement. Existing Measured, Simulated, Imported, Manual and
Derived provenance and missing, stale, conflicting or unavailable status remain
bound to retained observations and association identities. When CameraAgent
environmental acquisition is enabled, the selected kinds must belong to its
configured association policy. **Preview plan** and **Save immutable draft**
reject other kinds and name them; choose a supported subset or remove all
items. Selecting an unconfigured kind cannot enable a provider. A configured
kind with no retained reading keeps its existing missing status.
The existing eight
lines per corner, 64 characters per line and image-fit rules still apply; the
`ENVIRONMENT` heading consumes one line, so selecting every kind does not promise
every reading will fit. The complete selected facts remain retained even when
the display truncates them.

These are the existing producer inputs. Environment text retains its image-size
scaling and fit rules; scale can be reduced to fit. Inset retains the producer's
image-size-dependent minimum of 8–32 px, and line spacing retains its four-pixel
minimum. Values below those minima can produce the same output. Cloud labels
report the retained assessment's quality and precipitation status; hiding labels
does not change the assessment or mask. No live provider is queried by these
editors; missing and stale environmental facts retain their current status.

Edit, **Preview plan**, then **Save immutable draft**. Validate the saved revision
and explicitly activate it for subsequent captures. Roll back to the earlier
validated revision to restore its styling and observation selection. Saving never activates a revision or
enables a disabled node. Retained products and reconstruction with the old
revision retain their original bytes and identity.

The shared exact-version descriptors and codec preserve source whitespace,
member case, unknown fields, explicit nulls and absent defaults. Unchanged or
restored edits create no new revision. Described scalars reject nulls, wrong
types, duplicate members and values outside the bounds. Undefined numeric or
named observation kinds are rejected without repairing retained source on read.
Equivalent supported enum spellings and duplicate/order representations survive
unchanged saves. The existing nullable intensity control restores the original
palette when cleared. Pipeline JSON remains authoritative for dimensions,
output/input variants and all source/stack bindings. There are no dimension
defaults, reference inputs, providers, new rendering identities or automatic
conversion to Frame Facts. The shared descriptor validation applies on both
hosts; LogicHost gains no edge collection or configuration surface. #1386
remains open for the remaining editor families and final acceptance.
