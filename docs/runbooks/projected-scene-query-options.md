# Projected scene query options

Open **Operations → Pipeline → Named graphs**, select a revision and choose
**Draft a new revision from this one**. The `ProjectedScene` node has a
**Projected scene query** panel. To draft from the active revision directly, open
`/operations/pipeline/graphs/new?section=projected-scene#projected-scene`.
If the graph has no supported node, the page explains how to add one with an
explicit raw dependency.

| Control / JSON member | Code default | Existing bound and effect |
| --- | --- | --- |
| Maximum star magnitude / `maximumMagnitude` | `6.5` | Finite number, −30–30; larger values include fainter catalog stars. |
| Maximum star results / `maximumResults` | `2000` | Whole number, 1–10000 (`ProjectedSceneJson.MaximumObjectCount`); limits magnitude-selected catalog stars after projection. |
| Include constellation endpoint stars / `includeConstellationEndpointStars` | `false` | Boolean; adds available endpoints of the existing constellation selection beyond the magnitude/result query limits. |

These controls govern **physical predicted-scene staging**. CameraAgent selects
one immutable graph revision before the physical scene query and uses that same
revision for staging and durable live execution, including a concurrent
activation. VirtualSky keeps its separate module settings and authoritative
rendered scene. Editing this panel does not reselect VirtualSky's stars.

After editing, select **Preview plan**, then **Save immutable draft**. Open that
saved revision, **Validate**, and explicitly **Activate** it in Named graphs.
Drafting, previewing and validation leave the active graph unchanged. Activation
affects subsequent captures; an in-flight capture keeps its selected revision.
Use **Roll back** on the previous eligible revision to restore its query for
future captures. Retained scenes, archived replay, original revision identities,
reconstruction and expected-position captions remain tied to their frozen inputs.
LogicHost consumes retained scene metadata through the shared recipes; this panel
adds no central catalog query behavior.

The shared descriptor binds only registered alias `ProjectedScene` (case
insensitive) at exact version `canonical-projected-scene-v1`. Pipeline JSON stays
authoritative. Only edited scalar tokens change; original spelling, whitespace,
unknown members and absent/default/null semantics of other fields are preserved.
Opening an unchanged draft or restoring controls to their source/default values
keeps saving disabled and creates no revision. Invalid input clears the preview
and displays the affected option; correct it and preview again before saving.

`OutputVariant`, `AstronomyAlgorithmVersion`, `ConstellationIds`,
`SolarSystemBodies` and nullable `CuratedAnnotations` remain preserved JSON
without controls. There is no silent upgrade, algorithm/identity replacement,
catalog selection, general NGC/M/IC query, astrometry change or data acquisition.
See [scene overlay options](constellation-overlay-options.md) for display-only
label magnitude and [curated annotation configuration](curated-catalog-annotations.md)
for the separate curated selection. This is slice I of #1386; #1386 remains open.
