# HVO.SkyMonitor.CameraAgent.Common

Utility helpers, shared services, and plumbing that belong to the camera-agent family but do not live directly in the host. This project depends on `HVO.SkyMonitor.AgentCore` so it can reuse the shared contracts when composing capture loops, retention policies, or HTTP models.

Included building blocks:

- File-based configuration loader with an accessor that unblocks dependent hosted services.
- File-system frame storage service that preserves raw payloads, JSON metadata, and JSONL indexes.
- Retention background worker that prunes expired data based on the agent config.
- Capture loop background service that honors module-provided cadence setpoints, logs telemetry per frame, and persists outputs.
- In-memory telemetry sink (`ICaptureTelemetryProvider`) that records the last ~120 capture cycles for dashboards or UI widgets.
- Camera module factory/registration helpers plus the default `RandomImageCameraModule` for pipeline validation.
