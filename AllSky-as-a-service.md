# AllSky-as-a-Service

## 1. Vision & Motivation
- Deliver an "AllSky-as-a-Service" platform: end users run their own cameras/agents (e.g., Raspberry Pi with Docker) while a central logic service provides storage, time-lapse generation, overlays, meteor detection, and UI access.
- Focus on astronomy-first value: organized archives, quick scrubbing, annotated timelapses, meteor/event search, and eventually cross-site insights.
- Long-term targets include multi-camera per user, multi-user per logic server, optional community features (triangulation, global dashboards), and SaaS tiers based on retention/resolution/analytics.

## 2. Current Scope (POC Track)
- Stay local-first: all components can run on a single machine until cloud hosting is justified.
- Start with **single user / single camera** but design interfaces for multi-camera and multi-user expansion.
- Each **Camera Agent** owns exactly one camera; users can run multiple agents if needed.
- Agents currently operate without an upload destination (7-day rolling storage, basic processing). A logic server will be introduced later to mimic the cloud backend.
- Configuration is file-based for simplicity; UI is only for visibility (latest frame, browsing, basic processing outputs).

## 3. High-Level Architecture (Future View)
- **Camera Agents**: Lightweight processes/containers that talk to hardware (or simulators), normalize frames, enforce retention, collect metrics, and eventually upload to the logic service.
- **Logic Service**: Receives frames/metadata, stores in object storage + DB, orchestrates processing pipelines (timelapse, overlay, detections), exposes APIs/UI.
- **Processing Pipelines**: Timelapse generation, annotation overlays, meteor/transient detection; can run on agent or server depending on cost vs flexibility.
- **UI / Client Experience**: Web app with calendar view, timeline scrubber, event lists, overlay toggles, and future community dashboards.
- **Shared Core Components**: Rig configuration, projector engine, sky renderer, annotation utilities, Result/Option patterns from the shared HVO.Core package.

## 4. Camera Module & Rig Model Decisions
- Introduce a generic `ICameraModule` abstraction returning `CameraFrame` objects (timestamp, pixel data, metadata). Modules can represent real hardware, simulators, or trivial generators.
- Module configuration structure:
  - `AgentOptions`: storage root, retention days, capture cadence, metrics cadence.
  - `CameraConfig`: `type`, `common` (width, height, pixel format), `specific` (module-specific JSON blob).
- **Rig Configuration** (mirrors existing Imaging rig concepts):
  - `SensorProfile`: pixel geometry, dimensions, color mode, capabilities.
  - `OpticsProfile`: projection model, focal length, field of view, roll.
  - `RigOrientation`: boresight altitude/azimuth, roll adjustments.
  - `ObservatoryLocation`: latitude, longitude, elevation, time zone.
  - `PipelineExposureProfile`: capture interval, day/night exposure & gain bands.
- Rig config is the shared contract consumed by camera modules, projector factory, and annotation pipelines.

## 5. Projector & Sky Rendering
- The **image projector** (sky <-> pixel transformation) is central. It must be:
  - Configured from the rig and optics.
  - Shared by the simulator, annotation overlays, and detection logic so rendered images and overlays align pixel-perfectly.
- Define `IImageProjector` for forward/back projection (Alt/Az ↔ pixel, optionally RA/Dec ↔ pixel).
- The **sky renderer** (`ISkyRenderer`) wraps `StarFieldEngine` and uses the projector plus star/planet catalogs to synthesize frames for simulators and annotation previews.
- Because the projector is shared, the platform can reproduce or annotate any camera’s frame as long as the rig configuration is accurate.

## 6. POC Plan (Standalone Agent Track)

### Phase 1.0 – Plumbing with Dummy Camera (Status)
1. **Define Contracts** – ✅ Complete. `CameraFrame`, `FrameMetadata`, `ICameraModule`, `AgentRigConfig`, and related DTOs now live in `HVO.SkyMonitor.AgentCore` and are consumed by the new shared infrastructure.
2. **Implement RandomImageCameraModule** – ✅ Complete. Module options (pattern/seed) round-trip via `cameraagent.sample.json`, providing deterministic gradient/noise output.
3. **Agent Core** – ✅ Complete for capture + retention + processing telemetry:
   - Capture loop, processing pipeline registration, storage layout, and retention worker operate through `HVO.SkyMonitor.CameraAgent.Common`.
   - File-system storage currently writes RAW payloads + JSON metadata; derived products folder is stubbed for future processors.
4. **Simple UI** – ⚠️ Partial:
   - Blazor dashboard shows latest frame preview, capture telemetry, and refresh controls.
   - Gallery-by-date view, derived output listing, and richer config display remain **TODO** for Phase 1.0 completion.
5. **Metrics & Telemetry** – ⚠️ Partial:
   - Capture loop emits cadence/exposure/gain telemetry and exposes Prometheus metrics.
   - Disk consumption, CPU/RAM snapshots, and timelapse/derived-product latency metrics are **deferred**.

**Phase 1.0 remaining work**
- Add gallery & derived-output UI panels using existing storage metadata.
- Surface disk usage + retention stats (can leverage storage index JSONL files).
- Capture system resource metrics (CPU/RAM/disk) and persist simple trend lines for export.
- Document operational playbook (config paths, telemetry endpoints) to close out the “simple UI/metrics” goals.

### Phase 1.1 – Integrate Real Simulator (Not Started)
1. **Refactor existing StarFieldEngine** into standalone `ISkyRenderer` that only depends on rig configuration and catalogs.
2. **Add SimulatedSkyCameraModule**:
   - Uses `ISkyRenderer` + pipeline profile to render frames based on real time or simulated time.
   - Shares projector with annotation pipeline to maintain alignment.
3. **Enhance Processing**:
   - Introduce overlay processor that reuses projector to label stars/planets/objects.
   - Optionally add simple meteor-candidate detector (frame differencing) to measure CPU impact.
4. **Swap Modules via Config**:
   - Agent should only require `camera.type = "SimulatedSkyCamera"` to switch from dummy to full simulator; rest of pipeline remains unchanged.

### Later Phases (Future Notes)
- Phase 2: Introduce local logic server pretending to be the cloud, define upload APIs, and start multi-camera data modeling.
- Phase 3: Move logic server + storage to actual cloud environment, add authentication, multi-tenant controls, pricing model experiments.

## 7. Metrics & Experiments to Run
- **Storage**: Track bytes per frame, per night, and per week at multiple cadences/resolutions/compression settings.
- **CPU & Memory**: Measure capture-only vs capture+encode vs capture+processing; record peaks and averages.
- **Disk IO**: Evaluate throughput when generating timelapses or stacks from a night’s data set.
- **Latency**: Time from capture to disk write, to derived product availability, to event detection (even with simple stub processors).
- **Simulator knobs**: Use the sky renderer to vary star density, noise models, and exposure patterns to stress-test pipeline behavior.

## 8. Open Questions / Follow-Ups
1. **Bandwidth Modeling**: Need empirical data on upload volumes for different cadences once simulator frames are representative.
2. **Processing Placement**: Determine what stays on the agent (pre-filtering, event detection) versus the logic server (heavy analytics) after initial metrics.
3. **Config Distribution**: Decide how agents receive updated rig/pipeline configs when the logic server exists (push vs pull, versioning).
4. **Security**: Outline authentication/authorization strategy for agents → logic server before moving to multi-user/cloud.
5. **UI/UX Depth**: Define how rich the agent’s local UI needs to be vs what will live exclusively in the cloud portal.
6. **Data Retention Policies**: Confirm default retention (currently 7 days) and tiered retention strategy for future SaaS plans.
7. **Simulator Refactor**: Plan the extraction of existing simulator/star-field code into reusable libraries with clean dependencies.

## 9. Immediate Next Steps
1. Scaffold core projects/classes for the standalone agent (contracts, storage, UI shell).
2. Implement `RandomImageCameraModule` and wire the capture loop + retention + UI to start gathering metrics.
3. Begin refactoring the current simulator/star-field code into an `ISkyRenderer` + `IImageProjector` package for Phase 1.1 integration.
4. Document metrics gathered from Phase 1.0 runs to inform bandwidth and compute assumptions for later phases.

This document captures the current decisions and roadmap so we can resume the AllSky-as-a-Service effort without re-deriving the original context.
