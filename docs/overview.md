# AllSky Program Overview

This document replaces `AllSky_Agent_POC_Summary.md` and
`architecture/phase-plan.md`. It captures the product vision,
current-phase status, and the near-term roadmap for the AllSky camera
platform.

## 1. Product Vision

- Deliver a hosted experience similar to security-camera services but
  tailored for **AllSky cameras**.
- Operators run one or more **CameraAgent** instances (usually on a
  Raspberry Pi or Linux host). Each agent manages exactly one camera but
  the platform scales to multi-camera, multi-observatory setups.
- Agents capture frames locally, enforce retention, and can function
  offline. When connected to LogicHost they upload frames, telemetry, and
  derived products for centralized viewing, alerting, and sharing.
- Long term the service provides daily timelapses, annotated overlays
  (constellations, planets, transients), meteor detection, and global
  station collaboration.

## 2. Phase Snapshot

| Phase | Focus | Status |
| --- | --- | --- |
| **1.0 – Agent plumbing** | Standalone agent with dummy camera module, capture loop, retention policy, metrics, and simple UI. | ✅ Complete |
| **1.1 – Simulator integration** | Use StarFieldEngine via `ISkyRenderer`, add overlay hooks, and keep swap-friendly camera modules. | ✅ Complete |
| **2 – Multi-tenant ingest foundation** | Standardized frame envelopes, LogicHost upload APIs, MinIO-backed storage, cached "latest frame" views. | 🕒 Planned |
| **3 – Observability & derivatives** | Planner for stacking/overlays, telemetry APIs, dashboards. | 🕒 Planned |
| **4 – Tenant provisioning automation** | Self-service onboarding, automated storage/queue provisioning, device enrollment tokens. | 🕒 Planned |

Detailed infra/test modernization milestones live in
`docs/projects/infra-modernization.md`.

## 3. Agent Responsibilities

1. **Camera module abstraction** – `ICameraModule` implementations (random
   generator, simulator, hardware-specific drivers) feed frames into the
   agent core without changing the pipeline.
2. **Capture + retention** – frames are written under
   `<storageRoot>/frames/YYYY/MM/DD/HH-mm-ssZ.jpg`, with derived outputs
   (`timelapse/`, `stacks/`) and JSONL indices. A background retention job
   prunes directories older than the configured window (default 7 days).
3. **Processing hooks** – pipeline supports stacking, overlays, and
   future detection modules via pluggable steps.
4. **UI + metrics** – the built-in web UI exposes latest image, gallery,
   and processing previews while emitting metrics (frame cadence, CPU,
   memory, retention stats) that feed into LogicHost dashboards.
5. **Device registration** – agents generate a local bootstrap identity
   and pair with LogicHost via single-use envelopes. See
   `docs/identity/overview.md` for the workflow.

## 4. Architecture Layers

```
UI (latest image, gallery, processing)
        ↓
Agent Core (capture loop, retention, processing pipeline, metrics)
        ↓
Camera Module (random, simulator, ZWO/UVC/RTSP, etc.)
        ↓
Rig Configuration (sensor, optics, observatory metadata)
```

- The agent core never depends on a specific camera. Swapping modules is
  a configuration change.
- Rig metadata (sensor profile, optics, orientation, observatory
  coordinates) lives in JSON and is shared with simulator modules to keep
  overlays accurate.

## 5. Metrics Checklist

Track these values during Phase 1.x to validate hardware sizing and
storage budgets:
- Average frame size, cadence, and disk growth for 24h / 7-day windows.
- CPU/memory usage for capture-only vs capture+processing scenarios.
- Latency from capture → disk → UI availability.
- Retention impact when toggling cadence or resolution.
- Processing throughput (stack generation time, overlay cost).

## 6. Related Documents

| Doc | Purpose |
| --- | --- |
| `docs/security/secrets.md` | Configuration + secrets reference used by LogicHost and agents. |
| `docs/identity/overview.md` | Camera-agent registration, bootstrap envelopes, and identity program status. |
| `docs/projects/infra-modernization.md` | Docker/Testcontainers, PostgreSQL, TestSupport, and integration-test roadmap. |
| `docs/runbooks/local-dev.md` | End-to-end development workflow and tooling setup. |
| `docs/runbooks/infra-operations.md` | Operating Docker/Testcontainers infrastructure locally or remotely. |

Keep this doc updated as phases ship; use concise tick-box summaries
rather than duplicating full plan prompts.
