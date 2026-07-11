# Legacy Reference Code

## Purpose

This document records the external SkyMonitor implementations used as design
references for the current repository. Links are pinned to immutable commits so
future implementation work can reproduce the same source even when repository
branches move.

The external repositories are reference material, not dependencies. Do not add
project references to them, copy their host topology, or vendor their complete
source trees into this repository. Port only understood algorithms behind the
current architecture, preserve applicable attribution and license terms, and
write current tests for adopted behavior.

## Reference Repositories

### SkyMonitor V5 implementation

- Repository: [`RoySalisbury/HVOv9`](https://github.com/RoySalisbury/HVOv9)
- Reference commit: [`8fd662aca22e6b595f4a1882bab5c6fa319acb7b`](https://github.com/RoySalisbury/HVOv9/tree/8fd662aca22e6b595f4a1882bab5c6fa319acb7b)
- Source root: [`src/HVO.SkyMonitorV5`](https://github.com/RoySalisbury/HVOv9/tree/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5)
- Role: most complete source for rolling combination, local processing,
  planetarium rendering, annotations, FITS encoding, export, and ZWO behavior.

This is the V5 source referred to by V6 notes as
`HVOv9-readonly/src/HVO.SkyMonitorV5`. The separate archived repository named
`RoySalisbury/HVOv5` is an older web/IoT solution and is not the SkyMonitor V5
baseline for this project.

### SkyMonitor V6 redesign

- Repository: [`RoySalisbury/HVOv9-SkyMonitorv6`](https://github.com/RoySalisbury/HVOv9-SkyMonitorv6)
- Reference commit: [`ad41be92e1a478e19ba7e6a62189e601af6ecc42`](https://github.com/RoySalisbury/HVOv9-SkyMonitorv6/tree/ad41be92e1a478e19ba7e6a62189e601af6ecc42)
- Source root: [`src`](https://github.com/RoySalisbury/HVOv9-SkyMonitorv6/tree/ad41be92e1a478e19ba7e6a62189e601af6ecc42/src)
- Role: intermediate redesign containing reusable imaging boundaries,
  projection implementations, catalog configuration, simulated frame
  generation, and single-frame FITS upload behavior.

V6 does not contain a working frame stacker. Its stacking and several overlay
options are configuration surfaces without a connected implementation. Treat
source and tests as evidence; do not infer behavior from option names alone.

## V5 Source Map

All links in this section use commit
`8fd662aca22e6b595f4a1882bab5c6fa319acb7b`.

### Rolling combination and buffering

- [`RollingFrameStacker.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Pipeline/RollingFrameStacker.cs)
  is the owning rolling-combination implementation. It emits after every
  capture, averages the available compatible frames during warm-up, tracks
  total integration time, and resets on projection-affecting context changes.
- [`RollingFrameStackerTests.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi.Tests/Pipeline/RollingFrameStackerTests.cs)
  covers partial windows, integration totals, linear averaging, sRGB behavior,
  monochrome/high-bit inputs, and ownership.
- [`ComposedFrameQueue.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Pipeline/Composition/ComposedFrameQueue.cs)
  is a separate fixed-size processed-history ring buffer. It is not the stack
  accumulator.
- [`BackgroundFrameStackerService.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/HostedServices/BackgroundFrameStackerService.cs)
  demonstrates queue and telemetry behavior, but its nested queue topology
  should not be copied.

### Planetarium rendering and projection

- [`StarFieldEngine.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Cameras/Rendering/StarFieldEngine.cs)
  owns sidereal-time, RA/Dec to Alt/Az, optional refraction, camera-ray
  conversion, optical projection, and star/planet rendering flow.
- [`MockCameraAdapter.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Cameras/MockCameraAdapter.cs)
  shows how catalog selection, rendering, exposure delay, noise, and twinkle
  were assembled into a simulated camera.
- [`OpticsPresets.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Cameras/Optics/OpticsPresets.cs)
  records ASI174MM/ASI174MC physical and synthetic profiles plus fisheye,
  rectilinear, and telescope lens presets.
- [`RigSpec.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Cameras/Projection/RigSpec.cs)
  includes mono and color ASI174-family fisheye rig presets.
- [`FisheyeProjector.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Cameras/Projection/FisheyeProjector.cs)
  implements equidistant, equisolid-angle, orthographic, and stereographic
  radial mappings.
- [`RectilinearProjector.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Cameras/Projection/RectilinearProjector.cs)
  adapts the physical rectilinear lens model to the common projector boundary.
- [`RectilinearLens.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Cameras/Lenses/RectilinearLens.cs)
  derives perspective/gnomonic intrinsics from focal length and pixel pitch or
  from horizontal field of view.
- [`RigFactory.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Cameras/Projection/RigFactory.cs)
  selects fisheye versus rectilinear/telescope projection from lens kind.
- [`MockColorCameraAdapter.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Cameras/MockColorCameraAdapter.cs)
  applies RGB-channel response and noise for the synthetic color profile. It
  does not produce a true Bayer mosaic.
- [`SkyMonitorRepository.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Data/SkyMonitorRepository.cs)
  is the live HYG-backed catalog implementation and selection path.

The legacy simulated noise values are heuristic. Reuse camera geometry only
after checking it against hardware documentation, and label all unverified
response values as simulation parameters.

### Labels and annotations

- [`CelestialAnnotationsFilter.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Pipeline/Filters/CelestialAnnotationsFilter.cs)
  projects stars, planets, and deep-sky objects with the same engine used by
  rendering and draws bounded labels.
- [`CelestialAnnotationsFilterTests.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi.Tests/Pipeline/Filters/CelestialAnnotationsFilterTests.cs)
  provides initial overlay behavior fixtures.
- [`ConstellationFigureFilter.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Pipeline/Filters/ConstellationFigureFilter.cs)
  resolves and projects constellation segment topology.
- [`ConstellationFigureFilterTests.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi.Tests/Pipeline/Filters/ConstellationFigureFilterTests.cs)
  verifies rendered output near projected figures.

### Artifacts, FITS, and export

- [`FrameExportPublisher.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Exports/FrameExportPublisher.cs)
  separates raw publication from processed delivery and history payloads.
- [`FitsFrameEncoder.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Services/FitsFrameEncoder.cs)
  is the reference for image and provenance headers such as `DATE-OBS`,
  `MJD-OBS`, exposure, gain, pixel size, focal length, `NCOMBINE`, and
  integration time.
- [`S3FrameExportSink.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Exports/Sinks/S3FrameExportSink.cs)
  demonstrates MinIO bucket creation, date-partitioned keys, metadata, and
  manifests. Do not copy its archive/delivery payload duplication.

### Physical ZWO behavior

- [`ZwoCameraAdapter.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Cameras/Zwo/ZwoCameraAdapter.cs)
  contains ASICamera2 initialization, controls, ROI, capture, and retry behavior.
- [`ZwoPixelConverter.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi/Cameras/Zwo/ZwoPixelConverter.cs)
  documents prior Y8, RAW16, and RGB24 conversion decisions.
- [`ZwoPixelConverterTests.cs`](https://github.com/RoySalisbury/HVOv9/blob/8fd662aca22e6b595f4a1882bab5c6fa319acb7b/src/HVO.SkyMonitorV5/HVO.SkyMonitorV5.RPi.Tests/Cameras/Zwo/ZwoPixelConverterTests.cs)
  captures the tested conversion behavior.

## V6 Source Map

All links in this section use commit
`ad41be92e1a478e19ba7e6a62189e601af6ecc42`.

### Projection and rendering

- [`StarFieldEngine.cs`](https://github.com/RoySalisbury/HVOv9-SkyMonitorv6/blob/ad41be92e1a478e19ba7e6a62189e601af6ecc42/src/HVO.SkyMonitorV6.Imaging/Rendering/StarFieldEngine.cs)
  is the intermediate shared starfield engine.
- [`FisheyeProjector.cs`](https://github.com/RoySalisbury/HVOv9-SkyMonitorv6/blob/ad41be92e1a478e19ba7e6a62189e601af6ecc42/src/HVO.SkyMonitorV6.Imaging/Rendering/Projectors/FisheyeProjector.cs)
  and [`ProjectorFactory.cs`](https://github.com/RoySalisbury/HVOv9-SkyMonitorv6/blob/ad41be92e1a478e19ba7e6a62189e601af6ecc42/src/HVO.SkyMonitorV6.Imaging/Rendering/Projectors/ProjectorFactory.cs)
  show the redesigned optical-projection boundary.
- [`CatalogImagingConfigurationFactory.cs`](https://github.com/RoySalisbury/HVOv9-SkyMonitorv6/blob/ad41be92e1a478e19ba7e6a62189e601af6ecc42/src/HVO.SkyMonitorV6.Imaging/Configuration/CatalogImagingConfigurationFactory.cs)
  maps catalog and rig information into imaging configuration.
- [`ImagingRigConfiguration.cs`](https://github.com/RoySalisbury/HVOv9-SkyMonitorv6/blob/ad41be92e1a478e19ba7e6a62189e601af6ecc42/src/HVO.SkyMonitorV6.Imaging/Configuration/ImagingRigConfiguration.cs)
  contains useful configuration vocabulary, but its stacking and overlay flags
  are not proof of implemented behavior.
- [`Rendering/Planets`](https://github.com/RoySalisbury/HVOv9-SkyMonitorv6/tree/ad41be92e1a478e19ba7e6a62189e601af6ecc42/src/HVO.SkyMonitorV6.Imaging/Rendering/Planets)
  contains the V6 planet rendering and ephemeris surface.

### Simulator and upload

- [`SimulatedStarfieldGenerator.cs`](https://github.com/RoySalisbury/HVOv9-SkyMonitorv6/blob/ad41be92e1a478e19ba7e6a62189e601af6ecc42/src/HVO.SkyMonitorV6.CameraAgent.Sim/Imaging/SimulatedStarfieldGenerator.cs)
  connects location, time, rig configuration, catalog data, and the renderer.
- [`UploadSimulator.cs`](https://github.com/RoySalisbury/HVOv9-SkyMonitorv6/blob/ad41be92e1a478e19ba7e6a62189e601af6ecc42/src/HVO.SkyMonitorV6.CameraAgent.Sim/Services/UploadSimulator.cs)
  demonstrates one-frame FITS upload only. It does not implement stacking.
- [`phase5-starfield-notes.md`](https://github.com/RoySalisbury/HVOv9-SkyMonitorv6/blob/ad41be92e1a478e19ba7e6a62189e601af6ecc42/docs/projects/sky-monitor-v6/phase5-starfield-notes.md)
  records the V5 source relationship and V6 starfield migration context.

## Reproducing the Reference Checkouts

Checkouts are optional and should live outside the current repository. The
paths below match the inspection convention but are not required by builds.

```bash
git clone https://github.com/RoySalisbury/HVOv9.git /tmp/HVOv9-legacy-inspect
git -C /tmp/HVOv9-legacy-inspect checkout 8fd662aca22e6b595f4a1882bab5c6fa319acb7b

git clone https://github.com/RoySalisbury/HVOv9-SkyMonitorv6.git /tmp/HVOv6-legacy-inspect
git -C /tmp/HVOv6-legacy-inspect checkout ad41be92e1a478e19ba7e6a62189e601af6ecc42
```

Repository access may require the owner's GitHub credentials. Never make the
current solution restore or build depend on these checkouts.

## Porting Checklist

Before adopting legacy code or behavior:

1. Record the repository, commit, and source path in the issue or pull request.
2. Verify the source repository's license and preserve required attribution.
3. Describe the behavior being retained and the topology being rejected.
4. Reimplement it behind the current Astronomy, Imaging, AgentCore, or pipeline
   boundary rather than preserving legacy host dependencies.
5. Add focused tests in current terminology, including known legacy gaps.
6. Validate deterministic behavior, buffer ownership, cancellation, logging,
   configuration validation, and nullable contracts against current standards.
7. Update this document if a better reference commit or owning file is found.

The reference commits should change only through an intentional documentation
update explaining why the new snapshot is a better baseline.