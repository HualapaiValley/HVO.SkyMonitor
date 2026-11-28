# HVO.SkyMonitor.CameraAgent.Common

Utility helpers, shared services, and plumbing that belong to the camera-agent family but do not live directly in the host. This project depends on `HVO.SkyMonitor.AgentCore` so it can reuse the shared contracts when composing capture loops, retention policies, or HTTP models.

Included building blocks:

- File-based configuration loader with an accessor that unblocks dependent hosted services.
- Silo admission, publication, and catalog services for raw captures and processing products.
- Silo retention workers that apply age and space policies while protecting held artifact versions.
- Capture loop background service that honors module-provided cadence setpoints, logs telemetry per frame, and persists outputs.
- In-memory telemetry sink (`ICaptureTelemetryProvider`) that records the last ~120 capture cycles for dashboards or UI widgets.
- Camera module factory/registration helpers plus the default `RandomImageCameraModule` for pipeline validation.
