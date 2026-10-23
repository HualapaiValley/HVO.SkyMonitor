# Weather and cloud overlay options

From CameraAgent **Current Sky → Diagnostics → Edit weather and cloud overlay**,
open the active graph's draft at `weather-cloud-overlay`. You can also open
**Operations → Pipeline → Graphs** and create a draft from a retained revision.
If the graph has no overlay node, the editor explains the missing dependencies;
opening the link does not add or enable one.

The shared Processing catalog describes registered alias `WeatherCloudOverlay`
at exact graph step version `weather-cloud-overlay-v1`. Both hosts resolve that
same contract. The existing shared recipe is `weather-cloud-overlay`, semantic
version `1.0.0`, implementation `weather-cloud-overlay-v1`; these controls do not
introduce a new producer or style version. LogicHost does not acquire the
CameraAgent step through a descriptor.

| Control | Current default | Existing limits and meaning |
| --- | --- | --- |
| Enabled | `true` | Edits the v2 node's enabled field, not `options.enabled`. |
| Artifact recipe label (`recipeVersion`) | `weather-cloud-overlay-v1` | Required non-whitespace text; existing custom labels remain valid. This labels the retained artifact and does not select an implementation. |
| Output variant | `weather-cloud-overlay-v1` | A graph identifier of 1–128 characters, trimmed and without control characters. Update variant-specific dependent inputs separately. |
| Cloud boundary thickness | `1` px | Integer, 1–8 px; existing cloud tile boundaries only. |
| Show weather and cloud labels | `true` | Existing coverage, quality and precipitation-status text. |
| Maximum label length | `32` characters | Integer, 0–128 per label; zero hides text. |

The enabled overlay requires both a preview (Preview or AnnotatedPreview) and
metadata produced by `CloudAssessment`. Image-quality metadata cannot substitute
for that assessment. Disabling an upstream producer while leaving its dependent
enabled still fails the existing compound/dependency validation.

**Preview plan → Save new revision → Validate → Activate** is the explicit flow.
Saving does not activate. To restore an eligible retained revision, use
**Rollback** with the current registry version. Unchanged saves and restoring
absent defaults do not materialize option members or create revisions. Unknown
and out-of-scope JSON members and their source formatting survive edits. Old
graph and product identities remain reconstructable from their frozen inputs.

These are display controls over retained preview and assessment products. They
bound the recipe's status text before rendering, so short or zero character
budgets work without a layout failure, including with labels hidden. They
do not modify raw/science bytes, assessment grid dimensions, cloud measurements
or their unavailable/unknown status. Hiding labels does not make missing weather
known or qualify a physical cloud profile. No physical profile (#1004), weather
provider (#1092), deployment or automatic activation is included. This is partial
delivery of #1386 through #1564; #1386 remains open.
