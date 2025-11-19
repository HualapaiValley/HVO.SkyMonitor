# AllSky Camera Cloud Service – Design & POC Summary

## 0. Purpose of this document

You asked for a complete, self-contained summary of:

- What we discussed about the **AllSky camera service** idea.
- The **proof-of-concept (POC)** direction.
- The **standalone camera agent** design.
- How your existing **StarFieldEngine / simulator / rig config** should influence the new structure.
- The key **decisions** and **next steps**.

This file is written so you can drop it directly into a repo as something like:

`docs/AllSky-Agent-POC-Summary.md`

---

## 1. Overall product idea (future vision)

### 1.1 High-level vision

You want a service similar to a hosted security camera system, but specifically for **AllSky cameras**:

- End users run one or more **local camera agents** (e.g., Raspberry Pi with a Docker container).
- Cameras can be:
  - Your own hardware (ZWO, etc.).
  - Any camera the user wants (USB, RTSP, etc.), with custom agents where needed.
- The agent sends **images or video** to a central service (eventually cloud, initially local).
- The service provides:
  - **Daily timelapses**.
  - **Overlays / annotations**:
    - Constellation lines.
    - Star labels.
    - Planet markers.
    - Other objects.
  - **Meteor detection** and other transient events.
  - Browsing of per-night / per-event data.

Longer term: multi-user, multi-camera, potentially global network of AllSky stations, with shared scientific or public-output features.

---

## 2. POC strategy – start small, all local

### 2.1 Constraints and approach

Key constraints / design goals:

- **Start fully local**:
  - Everything runs on a single machine or LAN.
  - No cloud dependencies initially.
- **Single user, single camera** to begin with:
  - But design as if it will support multi-camera, multi-user, multi-site later.
- **Agents are per-camera**:
  - Each agent instance is responsible for **one camera**.
  - A user with multiple cameras runs multiple agents.
- **Camera agent is mostly self-contained**:
  - It can function even if “no cloud exists yet”.
  - It keeps a **local 7-day rolling buffer** of images and basic derived products.
- **Config-file driven** at first:
  - All configuration via JSON/YAML.
  - UI is for **viewing** and simple manual operations, not full configuration editing (for POC-1).

### 2.2 POC phases (high level)

We converged on a phased approach:

1. **Phase 1.0 – Standalone agent with dummy camera module**
   - Implement the **agent plumbing**:
     - Capture loop.
     - Storage + retention.
     - Metrics.
     - Simple web UI.
   - Use a very simple `ICameraModule` that generates **random / synthetic images**.
   - Focus on:
     - Data formats.
     - Separation of concerns.
     - Measurable CPU / memory / disk usage.
   - The image content does **not** matter at this stage.

2. **Phase 1.1 – Integrate realistic simulator (StarFieldEngine)**
   - Refactor the existing sky rendering logic into a clean, reusable **`ISkyRenderer`**.
   - Implement a `SimulatedSkyCameraModule` that uses `ISkyRenderer`.
   - Keep the agent logic unchanged — just swap camera modules via config.
   - Add or refine **annotation overlays** using the same projector used by the simulator.

3. **Later phases – Real hardware and “logic server”**
   - Create camera modules for actual hardware (ZWO, UVC, RTSP).
   - Introduce a **logic server** (still local or on LAN) that behaves like the eventual cloud backend.
   - Eventually move that logic server into the cloud and enable multi-user, multi-site.

The important decision: **Phase 1.0 uses a dumb/random camera module** so you can get the agent and metrics right before worrying about the real sky rendering.

---

## 3. Standalone Camera Agent – responsibilities and layering

The standalone agent is responsible for:

1. **Talking to a camera module** (real or simulated).
2. **Persisting frames** to disk in a consistent layout.
3. Enforcing **retention** (e.g., keep the last 7 days).
4. Running **basic processing** (stacking, simple overlay hooks).
5. Exposing a **simple UI** to:
   - View the latest image.
   - Browse captured images.
   - Preview basic processing outputs.
6. Emitting **metrics** (CPU, memory, disk usage, frames per minute, etc.).

### 3.1 Layered structure

Conceptually:

```text
+---------------------------+
|           UI              |
| - Latest image            |
| - Gallery                 |
| - Basic processing view   |
+-------------+-------------+
              |
              v
+---------------------------+
|        Agent Core         |
| - Capture loop            |
| - Retention policy        |
| - Processing pipeline     |
| - Metrics/logging         |
+-------------+-------------+
              |
              v
+---------------------------+
|       Camera Module       |
| (Random, SimulatedSky,    |
|  Real hardware, etc.)     |
+-------------+-------------+
              |
              v
+---------------------------+
|        Rig Config         |
|   (sensor, optics, site)  |
+---------------------------+
```

Key point: the **agent core** doesn’t care which camera module is used; it just consumes frames of a known shape and writes them out.

---

## 4. Camera Module abstraction & Phase 1.0 dummy module

### 4.1 `CameraFrame` and `ICameraModule`

We defined a conceptual data contract between the agent and any camera module:

```csharp
public sealed record CameraFrame(
    DateTimeOffset TimestampUtc,
    int Width,
    int Height,
    PixelFormat Format,            // e.g. Mono16, Mono8, Rgb24
    ReadOnlyMemory<byte> PixelData,
    FrameMetadata Metadata);

public sealed record FrameMetadata(
    TimeSpan Exposure,
    double Gain,
    double TemperatureC,
    string? SourceId,
    IReadOnlyDictionary<string, string>? Extra);

public sealed record CaptureRequest(
  DateTimeOffset RequestedStartUtc,
  TimeSpan TargetInterval,
  CaptureMode Mode);

public sealed record CaptureResult(
  CameraFrame? Frame,
  CaptureSetpoint NextSetpoint,
  TimeSpan ProcessingLatency,
  CaptureMode Mode,
  bool RequiresImmediateUpload);

public sealed record CaptureSetpoint(
  TimeSpan Exposure,
  double Gain,
  TimeSpan? NextIntervalOverride,
  double? TargetFps);

public enum CaptureMode
{
  Still,
  Video
}

[Flags]
public enum CameraModuleCapabilities
{
  None = 0,
  StillFrames = 1,
  Video = 2,
  AdaptiveGain = 4,
  AdaptiveExposure = 8
}

public interface ICameraModule : IAsyncDisposable
{
    string Id { get; }             // e.g., "Random1" or "SimAllSky"
    string DisplayName { get; }
  string ModuleType { get; }
  CameraModuleCapabilities Capabilities { get; }

    Task InitializeAsync(CameraModuleConfig config, CancellationToken ct);
  Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken ct);
}
```

The **agent core** only depends on `ICameraModule` and `CameraFrame`. It does not know whether the data came from:

- A real ZWO camera.
- A simulated sky rendered from catalogs.
- A random/noise generator.

### 4.2 `CameraModuleConfig` & config file structure

The camera module receives configuration that includes:

- Agent-level behavior (capture cadence, retention, storage root).
- Rig description (sensor, optics, site, etc.).
- Module descriptor (`type` + opaque `options`).
- Optional processing pipeline steps configured per camera.

Example JSON (conceptual):

```jsonc
{
  "agent": {
    "storageRoot": "/var/hvo/agent1",
    "retentionDays": 7,
    "captureCadenceSeconds": 10
  },
  "camera": {
    "module": {
      "type": "HVO.SkyMonitor.CameraAgent.Common.Modules.RandomImage.RandomImageCameraModule, HVO.SkyMonitor.CameraAgent.Common",
      "options": {
        "pattern": "Noise",
        "seed": 42
      }
    },
    "common": {
      "width": 1920,
      "height": 1080,
      "pixelFormat": "Mono8"
    },
    "rig": {
      "sensor": { /* SensorProfile-like data */ },
      "optics": { /* OpticsProfile-like data */ },
      "orientation": { /* RigOrientation */ },
      "observatory": { /* ObservatoryLocation */ },
      "pipeline": { /* PipelineExposureProfile */ }
    },
    "processingSteps": [
      {
        "id": "LocalStorage",
        "type": "HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.NoOpFileStorageProcessingStep, HVO.SkyMonitor.CameraAgent.Common",
        "order": 100,
        "options": {
          "storageRoot": "/var/hvo/agent1",
          "retentionDays": 7
        }
      },
      {
        "id": "Telemetry",
        "type": "HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.TelemetryCaptureProcessingStep, HVO.SkyMonitor.CameraAgent.Common",
        "order": 1000
      }
    ]
  }
}
```

In C#, this looks like:

```csharp
public sealed record CameraModuleConfig(
    AgentOptions Agent,
    CameraConfig Camera,
    CapturePipelineConfig? Pipeline = null);

public sealed record AgentOptions(
    string StorageRoot,
    int RetentionDays,
    int CaptureCadenceSeconds);

public sealed record CameraConfig(
    CameraModuleDescriptor Module,
    CameraCommonOptions Common,
    AgentRigConfig Rig,
    IReadOnlyList<CaptureProcessingStepConfig>? ProcessingSteps = null);

public sealed record CameraModuleDescriptor(
    string Type,
    JsonElement? Options = null);

public sealed record CaptureProcessingStepConfig(
    string Type,
    string? Id = null,
    int? Order = null,
    JsonElement? Options = null);
```

Each module parses `Module.Options` into its own strongly-typed options, while processing steps deserialize their own `options` payloads.

### 4.3 `RandomImageCameraModule` (Phase 1.0)

For the first POC step:

- `RandomImageCameraModule` will:
  - Read `width`, `height`, and `pixelFormat` from `CameraCommonOptions`.
  - Read optional `pattern`, `seed`, etc. from `Specific`.
- On each call to `CaptureAsync`:
  - Allocate a pixel buffer.
  - Fill it with:
    - Random noise, or
    - Simple gradient / test pattern.
  - Create a `CameraFrame` with:
    - `TimestampUtc = DateTimeOffset.UtcNow`
    - Dummy `FrameMetadata` (e.g., fixed exposure/gain).
- The agent:
  - Saves the frame as JPEG/PNG.
  - Updates metrics.
  - Exposes it via the UI.

**We explicitly decided**: this is sufficient for Phase 1.0 because we care about the **plumbing, metrics, and data flow**, not about visual correctness yet.

---

## 5. Agent storage, retention, and simple UI

### 5.1 Storage layout

We converged on a simple, time-based directory structure:

```text
<storageRoot>/
  frames/
    YYYY/
      MM/
        DD/
          2025-11-17_03-00-00.000Z.jpg
          2025-11-17_03-00-10.000Z.jpg
          ...
  derived/
    timelapse/
      2025-11-17.mp4
    stacks/
      2025-11-17_03-00_to_03-10_stack.png
  index/
    frames_2025-11-17.jsonl
```

- **Frames**: raw per-capture images.
- **Derived**:
  - Timelapses, stacked images, etc.
- **Index**:
  - Optional JSONL file containing per-frame metadata (timestamp, path, exposure, etc.) for quick lookup.

### 5.2 Retention job

A small background job:

- Runs periodically.
- Looks for `frames/YYYY/MM/DD` directories older than `retentionDays`.
- Deletes:
  - Matching frame directories.
  - Any associated derived products (timelapse/stack outputs).
  - Corresponding index files.

Retention is purely time-based in POC-1.

### 5.3 Simple web UI

For POC-1, the UI is minimal but functional:

**Endpoints (conceptual):**

- `GET /`  
  - Dashboard with:
    - Latest image.
    - A few recent thumbnails.
    - Basic metric summary.

- `GET /api/latest`  
  - Returns metadata + reference to the most recent frame file.

- `GET /api/frames?date=YYYY-MM-DD&page=N&pageSize=M`  
  - Lists frames for a given date.

- `GET /frames/{filename}`  
  - Serves a frame image file directly.

- `GET /api/stacks` / `GET /stacks/{filename}`  
  - For viewing derived stack images.

In the UI:

- **Latest image view**:
  - Auto-refresh every few seconds.
  - Shows timestamp and key metadata.
- **Gallery view**:
  - Date chooser.
  - Scrollable grid of thumbnails.
- **Processing view**:
  - List of available stacks/timelapses.

Configuration is still file-based; the UI is primarily a viewer and status surface for POC-1.

---

## 6. Metrics to collect in POC-1

You want **real numbers** for feasibility:

1. **Data volume / disk usage**
   - Average frame size (bytes).
   - Frames per minute / night.
   - Timelapse/stack output sizes.

2. **CPU usage**
   - Average CPU% consumed by:
     - Capture only.
     - Capture + encoding.
     - Capture + encoding + processing (e.g., stacking).

3. **Memory usage**
   - Peak working set of the agent process.
   - How many frames you actually keep in memory at once.

4. **Latency**
   - Time from capture to:
     - Frame saved to disk.
     - Frame visible in UI.
     - Stack / timelapse generated.

5. **Retention impact**
   - Disk usage pattern over 24h, 7 days at given:
     - Resolution.
     - Cadence.
     - Compression quality.

Metrics can initially be logged to file and optionally exposed via a simple JSON endpoint for later dashboards.

---

## 7. Existing HVO codebase influences: Rig / Imaging Domain

You provided an archive and pointed out:

- `CameraAgent` (generic agent).
- `StarFieldEngine` and rendering logic.
- Catalog and configuration objects: camera, optics, rig, observatory, pipeline.

We agreed to **reuse the domain concepts** but in a more decoupled way.

### 7.1 Key domain concepts identified

From your existing code (names paraphrased):

- **Sensor / Camera**:
  - `SensorWidthPixels`, `SensorHeightPixels`.
  - `PixelSizeMicrons`.
  - `SensorColorMode` (mono/color).
  - Capabilities: gain, exposure, temperature, binning, etc.
  - Identity (manufacturer, model, driver name).

- **Optics**:
  - `ProjectionModel` (rectilinear, equidistant fisheye, etc.).
  - `FocalLengthMillimeters`.
  - `FieldOfViewXDegrees`, `FieldOfViewYDegrees`.
  - `RollDegrees`.

- **Rig**:
  - Links sensor + optics.
  - `BoresightAltitudeDegrees`, `BoresightAzimuthDegrees`.

- **Observatory / Site**:
  - `LatitudeDegrees`, `LongitudeDegrees`.
  - `TimeZoneId`.
  - Display name, slug, etc.

- **Pipeline / Exposure profile**:
  - `CaptureIntervalMilliseconds`.
  - `DayExposureMilliseconds`, `NightExposureMilliseconds`.
  - `DayGain`, `NightGain`.
  - Min/max bounds for exposure and gain.
  - “Enabled” flags.

- **ImagingRigConfiguration**:
  - A combined, runtime model that unifies:
    - Sensor profile.
    - Optics profile.
    - Rig orientation.
    - Observatory location.
    - Pipeline exposure profile.
  - This is already close to what the new agent’s **rig config** should be.

### 7.2 New POC-level RigConfig

For the POC, we want a **simple DTO** that captures the same essence as `ImagingRigConfiguration`, but is:

- Easy to serialize as JSON.
- Independent of EF/database/catalog machinery.

Conceptual example:

```csharp
public sealed record AgentRigConfig(
    SensorProfile Sensor,
    OpticsProfile Optics,
    RigOrientation Orientation,
    ObservatoryLocation Observatory,
    PipelineExposureProfile Pipeline);
```

This rig config is:

- Provided to the **camera module** (simulated or real).
- Used by **ISkyRenderer** and **IImageProjector**.
- Later can be sourced from:
  - Local config files (POC).
  - Logic server / DB / catalogs (future cloud version).

---

## 8. Projector as central shared component

You explicitly highlighted that:

> The same projector used to **generate a simulated image** is also used to **locate stars/planets/objects in that image** for annotation. It should be used in both camera agents and the logic server.

This is a **key architectural point**.

### 8.1 Role of the projector

The projector is the **geometric engine** that maps between:

- **Sky coordinates** (alt/az, RA/Dec) and
- **Pixel coordinates** (x, y).

It depends on:

- Sensor geometry (width, height, pixel size).
- Optics (projection model, focal length/FOV).
- Rig orientation (boresight alt/az, roll).
- Observatory location and time (for RA/Dec ↔ alt/az).

It must be:

- Shared by:
  - Simulated camera rendering.
  - Annotation/overlay logic.
  - Logic server operations (e.g., object search, detection mapping).
- Deterministic and consistent across all uses.

### 8.2 `IImageProjector` and `ProjectorFactory`

We defined a conceptual interface:

```csharp
public sealed record PixelPoint(double X, double Y);
public sealed record AltAzPoint(double AltitudeDeg, double AzimuthDeg);

public interface IImageProjector
{
    // Sky -> pixel
    PixelPoint ProjectAltAz(double altitudeDeg, double azimuthDeg);

    // Possibly RA/Dec -> pixel (via site/time & precomputed transforms)
    PixelPoint ProjectEquatorial(double raHours, double decDeg, DateTimeOffset whenUtc);

    // Pixel -> sky
    AltAzPoint UnprojectPixel(double x, double y);
}
```

A `ProjectorFactory` would:

- Take `AgentRigConfig` + `ProjectionModel`.
- Return a properly configured `IImageProjector`.

### 8.3 Shared use of the projector

- **SimulatedSkyCameraModule**:
  - Uses `IImageProjector` inside `ISkyRenderer` to render the sky:
    - For each pixel → sky direction → sample star/planet models → draw.

- **Annotation / overlay engine**:
  - Uses the same `IImageProjector` for **object placement**:
    - For each star/planet/object → alt/az → `ProjectAltAz` → pixel location → draw label/marker.
  - Also uses `UnprojectPixel` to:
    - Map pixel coordinates (e.g., meteor trails) back to sky positions.

- **Logic server (future)**:
  - Uses the same projector when:
    - Rendering annotated versions of frames/timelapses.
    - Doing coordinate searches on captured images.

This ensures:

- Consistent alignment between:
  - How the simulator draws the sky.
  - How overlays show objects.
  - How detections are mapped back to sky coordinates.

---

## 9. `ISkyRenderer` and future SimulatedSkyCameraModule (Phase 1.1)

To integrate your existing `StarFieldEngine` cleanly, we treat it as an implementation of a high-level `ISkyRenderer` interface:

```csharp
public sealed record SkyRenderRequest(
    DateTimeOffset TimestampUtc,
    AgentRigConfig Rig,
    SensorColorMode ColorMode,
    bool IncludePlanets,
    bool DimFaintStars);

public interface ISkyRenderer
{
    SKBitmap Render(SkyRenderRequest request);
}
```

Then:

- `SimulatedSkyCameraModule`:
  - Accepts a `CameraModuleConfig` with `RigConfig`.
  - Constructs an `IImageProjector` via `ProjectorFactory`.
  - Uses `ISkyRenderer` (which in turn uses the projector and catalogs).
  - Converts the resulting `SKBitmap` into `CameraFrame` for the agent.

The agent core remains unchanged — it still sees only `CameraFrame` from `ICameraModule`.

---

## 10. Decisions captured

Here are the key decisions we’ve implicitly/explicitly made:

1. **Start with a local, single-agent POC** before cloud and multi-user.
2. **Use a trivial `RandomImageCameraModule` for Phase 1.0**:
   - Focus first on agent plumbing, storage, retention, metrics, and UI.
3. **Standardize on a `CameraFrame` record**:
   - All camera modules, simulated or real, emit this shape.
4. **Separate configuration cleanly**:
   - `AgentOptions` for agent behavior (storage, retention, cadence).
   - `CameraConfig` containing:
  - `Module` descriptor (type + options blob).
  - Common options (width/height/pixelFormat).
  - Rig description + optional processing steps (per-camera pipeline overrides).
     - `AgentRigConfig` (sensor/optics/orientation/site/pipeline).
     - Module-specific config as JSON blob.
5. **Use a time-based storage layout with 7-day retention**:
   - Directory structure: `<root>/frames/YYYY/MM/DD`.
   - Derived outputs in `<root>/derived/...`.
6. **Implement a simple web UI in the agent**:
   - For latest image, gallery, basic derived outputs.
7. **Metrics are first-class**:
   - Track CPU, memory, disk usage, frame size, cadence effects.
8. **Rig/Imaging domain is built around existing concepts in your codebase**:
   - Reuse the shape of `ImagingRigConfiguration` (sensor + optics + orientation + site + pipeline).
   - Use simple JSON DTOs for the POC, independent of DB/catalog code.
9. **Projector is a central, shared component (`IImageProjector`)**:
   - Used by both simulation (`ISkyRenderer`) and annotation/logic.
   - Derived from rig configuration and projection model.
10. **`ISkyRenderer` wraps StarFieldEngine in a decoupled way**:
    - The simulator camera module calls `ISkyRenderer`.
    - The logic server and other tools can also call it if needed for previews.

---

## 11. Next steps (practical)

If you want to continue from here in code, the logical next steps based on this summary are:

1. **Define core types in a shared “Core” or “Imaging.Domain” project**:
   - `AgentRigConfig`, `SensorProfile`, `OpticsProfile`, `RigOrientation`, `ObservatoryLocation`, `PipelineExposureProfile`.
   - `CameraFrame`, `FrameMetadata`.
   - `ICameraModule` and `CameraModuleConfig`.

2. **Implement Phase 1.0 agent skeleton**:
   - Config loading.
   - `RandomImageCameraModule`.
   - Capture loop.
   - Storage layout and retention job.
   - Very basic ASP.NET Core UI for:
     - Latest frame.
     - Frame list per day.

3. **Introduce `IImageProjector` and `ProjectorFactory`**:
   - Even if not fully used by `RandomImageCameraModule`, ensure the rig config and projector wiring are in place.

4. **Later, integrate `ISkyRenderer` and `SimulatedSkyCameraModule`**:
   - Wrap the existing StarFieldEngine logic behind `ISkyRenderer`.
   - Ensure annotation/overlays use the same `IImageProjector`.

This summary should give you enough context to re-create the design in VS Code or docs without needing the original chat.
