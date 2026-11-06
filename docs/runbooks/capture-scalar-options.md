# Image quality, telemetry and simulated cloud options

In CameraAgent, open **Operations → Pipeline → Graphs**, then create a draft
from the desired retained revision. The node panel now describes these existing
registered steps:

| Registered alias | Exact shared graph version | Controls and defaults |
| --- | --- | --- |
| `ImageQuality` | `integer-image-statistics-v1` | Enabled `true`; output variant `image-quality-v1` |
| `Telemetry` | `cameraagent-step-v2:Telemetry` | Enabled `true` |
| `VirtualSkyCloudObservation` | `cameraagent-step-v2:VirtualSkyCloudObservation` | Enabled `true` |

For v2 graphs, the panel's enable switch edits the existing **node enabled**
field, also shown in the node row. It never writes `options.enabled`, which v2
rejects. The runtime option defaults remain `true`; legacy options are not moved
or migrated. An enabled dependent cannot consume a disabled producer: disable
the dependent or change its inputs explicitly before validation.

The quality output variant is a graph identifier: 1–128 characters, trimmed,
with no control characters. It routes the existing statistics metadata; changing
it does not change the analysis. Update any variant-specific input selectors
separately. There are no other numeric ranges in this slice.

Telemetry reports existing capture samples, metrics and completion logs.
Simulated cloud observations require a VirtualSky frame with valid cloud-scenario
provenance, and remain labelled simulated. A physical or cloudless frame does
not gain cloud evidence from this toggle. Neither step publishes automatically
for archived replay. A descriptor does not register a step or qualify a physical
capability; LogicHost resolves the same host-neutral catalog without acquiring
CameraAgent execution support. Central `image-quality` at
`central-image-quality-v1` is a different contract and remains undescribed here.

Preview and save create a draft only. **Validate**, then explicitly **Activate**
with the current registry version. To restore previous behavior, use the existing
**Rollback** action on a retained eligible revision. Retained captures keep their
original graph and product identities. Opening, previewing, saving unchanged
values, or restoring absent defaults does not create a revision or materialize
defaults. Unknown JSON members and their source formatting are carried forward.

This is partial delivery of #1386 through #1563. It adds no analysis, telemetry
protocol, cloud algorithm, producer version, profile activation or profile default.
