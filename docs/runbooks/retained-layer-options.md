# Existing cloud and environment layer styling

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

These are the existing producer inputs. Environment text retains its image-size
scaling and fit rules; scale can be reduced to fit. Inset retains the producer's
image-size-dependent minimum of 8–32 px, and line spacing retains its four-pixel
minimum. Values below those minima can produce the same output. Cloud labels
report the retained assessment's quality and precipitation status; hiding labels
does not change the assessment or mask. No live provider is queried by these
editors; missing and stale environmental facts retain their current status.

Edit, **Preview plan**, then **Save immutable draft**. Validate the saved revision
and explicitly activate it for subsequent captures. Roll back to the earlier
validated revision to restore its styling. Saving never activates a revision or
enables a disabled node. Retained products and reconstruction with the old
revision retain their original bytes and identity.

The shared exact-version descriptors and codec preserve source whitespace,
member case, unknown fields, explicit nulls and absent defaults. Unchanged or
restored edits create no new revision. Described scalars reject nulls, wrong
types, duplicate members and values outside the bounds. Pipeline JSON remains
authoritative for dimensions, output/input variants, nullable grayscale `Value`,
`EnvironmentalKinds`, and all source/stack bindings. There are no dimension
defaults, reference inputs, providers, new rendering identities or automatic
conversion to Frame Facts. The shared descriptor validation applies on both
hosts; LogicHost gains no edge collection or configuration surface. #1386
remains open for the remaining editor families and final acceptance.
