# Opt-in measured stellar associations

`MeasuredStellarAssociations` measures retained **linear raw** Mono16/CFA
samples against that capture's own projected scene and emits compact typed
Metadata. It does not fit a calibration. The explicit downstream
[measured astrometry recipe](measured-astrometry.md) consumes the retained product
for bounded post-admission fitting. Shipped profiles do not enable either recipe.
The [#1541 protocol](../validation/issue-1541-producer-protocol.md) defines the
bounded software acceptance, configuration and exclusions.

An explicitly selected graph can add the node after `ProjectedScene`:

```json
{
  "steps": [
    { "type": "ProjectedScene", "id": "scene", "order": 15, "dependsOn": ["$raw"] },
    { "type": "MeasuredStellarAssociations", "id": "measured", "order": 16, "dependsOn": ["scene"] }
  ]
}
```

This is a fragment, not a complete profile. Label consumers must explicitly
depend on scene and measured product. Missing, mismatched or ineligible facts
suppress stellar labels. Expected-position diagnostics are separate opt-in;
their complete marker is retained or the diagnostic label is suppressed.
Catalog predictions never assert detections. Solar-system and curated catalog
presentation retain their own semantics.

Products bind raw capture/artifact/descriptor/recipe/layout, scene identity,
catalog version/checksum, calibration/projection/astronomy algorithms,
measurement settings/reconstruction and association/label-policy settings.
CameraAgent publication and replay preserve these facts. Scene payload checksum
and scene content identity are distinct. Replay rejects a mismatched checksum or
identity.

## Saved scene-overlay drafts

The saved `ScenePresentationLayer` v12 descriptor preserves its declared version
and option bytes for inspection/editing. It does **not install a historical
producer**. The current implementation is
`projected-scene-presentation-v15-plain-circle-bounded-point-source-labels`;
its label policy suppresses unmeasured star and point-source planet (Mercury–Neptune)
labels while leaving resolved Sun/Moon discs untouched. Retained artifact recipes
and algorithm stamps identify the actual implementation.

A portable v12 draft compiles to its declared v12 plan identity; that is not
execution compatibility. CameraAgent delivery reconstructs the installed plan
and rejects a definition it cannot represent exactly. Explicitly rebuild its
options as a **new** local draft, validate and activate the v13 revision to
execute them with the current producer. The old draft/identity stays unchanged;
no automatic descriptor upgrade ships. Superseded frozen named revisions also
need operator selection of a compatible revision; configured-basic recompiles
at startup.

`SavedV12Draft_RejectsVersionSubstitutionAndExecutesExplicitlyRecompiledV13Revision`
pins actual delivery rejection followed by durable execution of the rebuilt
draft: option effect, new identity, actual v13 output stamps and fail-closed
labels. `BothSceneVersionsRoundTripWithoutChangingSavedDrafts` separately pins
byte preservation. This migration treatment addresses
[PR1549 F1](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1541#issuecomment-6352713879).

## Acceptance source map

#1540 merged the implementation. #1541 verified acceptance on the revisions
recorded in its results.

| Boundary | Current owners and executable acceptance |
| --- | --- |
| Compact schema/identity/recipe, linear producer | Processing contracts/recipe; `MeasuredStellarAssociationRecipeTests`, identity tests |
| Policy, labels and expected markers | Processing associator/policy and Imaging formatter; `StellarLabelPolicyTests`, `AnnotationMeasuredAssociationBindingTests`, `ExpectedPositionLabelOutputTests`, `PresentationResolvedFootprintTests` |
| Actual final pixels and durable edge publication/replay | CameraAgent.Common step/adapter/store; `MeasuredAssociationDurabilityTests`, `MeasuredStellarAssociationAcceptanceTests` |
| v1/v2 scene, measured star outside resolved disk | CameraAgent adapter and pixel output, `ResolvedSceneMeasuredLabelTests` |

> Historical acceptance for the retired LogicHost ingest, runner services, and
> SQL graph is recorded in the [pre-retirement source map](https://github.com/HualapaiValley/HVO.SkyMonitor/blob/66e6aa237b40c70342815af6e2511122daa56bb6/docs/runbooks/measured-stellar-associations.md#acceptance-source-map).

The [results](../validation/issue-1541-producer-results.md) retain exact revisions,
commands, receipts, coverage and separate PR1190/PR1549 finding histories.
This candidate does not qualify physical/native targets or wider #526 accuracy.
