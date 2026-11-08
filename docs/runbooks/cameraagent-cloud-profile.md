# CameraAgent cloud profile

The shipped VirtualSky sample and installer-generated full-frame ASI174 profile
include `CloudAssessment` and `CloudPresentationLayer` as optional nodes. They use
the delivered image comparison algorithm; no percentage comes from star counts,
VirtualSky scenario truth, environmental cloud-cover observations or dashboard
prototype data. This changes newly generated configuration only. Existing
installations retain their immutable active graph until an owner explicitly
validates and activates a new revision.

## Clear-reference provisioning

Cloud assessment compares each raw linear capture against an explicitly selected
clear image. The supplied path is
`references/cloud/clear-reference.manifest.json`, relative to
`CameraAgent:RawIngressRoot`. The file must contain the actual v2 artifact manifest;
its `relativeArtifactPath` names the retained payload under the same storage root.
The loader verifies the manifest, layout and pixel checksum and holds that
reference against retention. Do not copy an unrelated image into this path or
call the first frame clear automatically.

The sample declares the virtual ASI174 native 12-bit codes in a 16-bit container,
black level 64 ADU and white level 4095 ADU. The installer already declares the
same linear regime at full sensor dimensions. These are virtual input settings,
not a physical calibration certificate.

Capture and independently qualify a clear image with the same rig, readout,
calibration, geometry, gain, exposure and processing graph as the assessed
captures. Keep both its original manifest and immutable pixel payload. Provision
the designated manifest only after that qualification. The raw input and raw
reference intentionally share one calibration domain; a calibrated derivative
from a different graph or processing regime is not interchangeable. See the
[cloud evidence contract](../contracts/cloud-assessment-v1.md) and
[assessment options](cloud-assessment-options.md) for compatibility and thresholds.
Day/night setpoint changes can legitimately make a reference incompatible.

No reference is bundled or downloaded. Missing references produce retained
`cloud.missing-clear-reference` evidence with null coverage; incompatible
references produce `cloud.incompatible-clear-reference`. A corrupt manifest or
payload fails the optional cloud node. Raw capture, combination and preview
remain independent of the cloud branch.

## Retained presentation

Both profiles require quantified assessment before publishing measured cloud
mask/label layers (`requireQuantifiedAssessment: true`). Unavailable or contaminated
assessments retain their reason-coded metadata but produce no selectable measured
cloud layer. Current Sky and archived capture controls therefore stay disabled
until compatible image evidence has produced a retained layer. Quantified layers
use the existing grouped mask and label products, overlay manifest and materializer.

The option is omitted by default to preserve older retained graph identities and
their explicit unavailable-status presentation. New cloud nodes have
`required: false` and `publication.persistence: durable-local`. Manifest and
materializer declare the optional cloud producer; raw/preview/storage policies do
not depend directly on it. The existing graph worker controls retries and terminal
optional failures.

## Virtual qualification boundary

Regression tests exercise first-frame/no-reference, clear, partial and overcast
VirtualSky pixels, independently retained clear-reference manifests, incompatible
setpoints and corrupt references. The full-frame cases use ASI174 geometry
1936×1216; reduced cases keep the ordinary 484×304 sample geometry. The renderer
receives deterministic cloud scenarios, while the analyzer receives only image
artifacts and normal capture/environment evidence.

These tests demonstrate software behavior for their explicit virtual inputs.
They do not establish physical ASI174 sensor/optics accuracy or qualify a clear
reference for an installed physical rig. The default 85% tile transmission
threshold can classify some broadly attenuated partial-cloud fixtures as fully
cloudy; tile coverage is a sample-weighted algorithm result, not a universal
meteorological sky fraction. Physical reference and accuracy qualification remain
tracked by #1004; no live activation or deployment is part of this change.
