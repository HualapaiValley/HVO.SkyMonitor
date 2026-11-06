# Cloud assessment options

From CameraAgent **Operations → Pipeline → Graphs**, create a draft from a retained
revision and open its **Cloud assessment** panel. The direct link
`/operations/pipeline/graphs/new?section=cloud-assessment#cloud-assessment` starts
from the active graph. Opening the link never adds or enables a node. If no
assessment node exists, the editor explains its image dependency.

Processing's shared catalog describes registered alias `CloudAssessment` at exact
step version `comparative-tiled-transmission-v1`. Both hosts resolve that contract.
The existing shared recipe is `cloud-assessment`, semantic version `1.0.0`,
implementation `comparative-tiled-transmission-v1`. There is no new recipe,
producer version, profile activation or settings store.

| Control | Current default | Existing bounds and meaning |
| --- | --- | --- |
| Enabled | `true` | Edits the v2 node's enabled field. Enabled dependents still require an enabled producer. |
| Output variant | `cloud-assessment-v1` | Graph identifier, 1–128 characters, trimmed and without control characters. Adjust variant-specific dependent inputs separately. |
| Grid columns | `16` | Integer, 1–64. |
| Grid rows | `12` | Integer, 1–64; at most 4096 tiles. The runtime also requires the grid to fit the image dimensions. |
| Transmission threshold | `850000` millionths | Integer, 1–999999; 850000 means 0.85. A valid tile below the threshold is cloudy. |
| Minimum reference signal | `64` ADU | Integer, 0–65535, above black level. |
| Minimum samples per tile | `16` | Integer, 1–2147483647. Too little accepted support or no reference variance yields unavailable coverage. |
| Maximum saturated fraction | `100000` millionths | Integer, 0–1000000; 100000 means 10 percent of considered samples. Exceeding it marks the assessment contaminated. |
| Include cloud tile mask | `true` | Includes the existing row-major tile mask; region evidence remains when disabled. |

**Preview plan → Save immutable draft → Validate → Activate** remains explicit.
Saving does not change live capture. Use **Rollback** with the current registry
version to restore an eligible retained revision. Unchanged saves and edits
restored to absent defaults create no revision and materialize no option members.
Unknown and out-of-scope JSON, source formatting, retained graph identities and
old product reconstruction stay intact.

`ClearReferenceManifestPath` remains opaque retained provisioning. This panel
does not display or edit its path, discover references or replace their identity.
CameraAgent continues loading the already configured storage-relative manifest;
LogicHost retains its own explicit designation and frozen job inputs. Both use
the existing [reference compatibility and evidence contract](../contracts/cloud-assessment-v1.md).
Matching linear Mono16/RGGB16 layouts, levels, integration/setpoints and capture
compatibility identities are still required. Missing, unavailable or incompatible
references produce explicit insufficient evidence and null coverage, never a
fabricated zero-cloud percentage. Saturation, daylight, weather and insufficient
support retain their current reason codes and quality rules.

These controls change assessment metadata, not immutable raw/reference pixels.
Geometry exclusions and other out-of-scope members remain untouched. No shipped
or physical profile, camera qualification (#1004), migration, deployment or
automatic activation is included. This is a bounded partial delivery through
#1565, Refs #1386; #1386 remains open.
