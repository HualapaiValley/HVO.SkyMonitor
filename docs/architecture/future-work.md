# Future Work & Roadmap

This document captures follow-on efforts required to finish the multi-tenant camera-agent platform. Use it alongside `docs/projects/future-infra-todos.md` and the legacy repos (`HVOv9`, `HVOv9-SkyMonitorv6`) to guide implementation sequencing.

## 1. Phase 01 (In Progress)
| Workstream | Description | Reference |
| --- | --- | --- |
| Architecture docs | Current effort – capture platform, auth, contracts, diagrams. | This folder. |
| RAW-first capture | Extend `FileSystemFrameStorageService` + processing steps to persist RAW frames and metadata. | `camera-agent-platform.md`, `contracts.md`. |
| Azure Entra wiring | Option B.1 adoption + local fallback. | `auth-strategy.md`. |

## 2. Phase 02 – Multi-Tenant Ingest Foundation
| Item | Why | Legacy inspiration | Notes |
| --- | --- | --- | --- |
| `FrameEnvelope` + upload step | Agents must push RAW + metadata to LogicHost queue/API. | `HVOv9` Redis queue payloads (`FrameRecord`). | Add `FrameEnvelope` to `HVO.SkyMonitor.Common`; update `contracts.md`. |
| LogicHost `/api/v1.0/frames` | Central ingest endpoint; validates tokens, stores blobs, enqueues derivative work. | `HVOv9-SkyMonitorv6` `FramesController`. | Align with Azure Entra scopes; document in `auth-strategy.md` once built. |
| MinIO integration | Agents directly upload to tenant buckets; LogicHost references object keys only. | `CaptureFrameCacheService` stored latest frames per tenant. | Evaluate whether to reuse existing MinIO in docker-compose or adopt Azure Blob/AWS S3 for production. |
| Tenant-aware buffering | Replace `ILatestFrameAccessor` with distributed cache so LogicHost UI can show newest frame without polling each agent. | Legacy framebuffer (Redis). | Document caching contract in `contracts.md`. |

## 3. Phase 03 – Observability & Derivatives
| Item | Description | Legacy reference |
| --- | --- | --- |
| Derivative planner step | Implement stacking/overlays as dedicated processing steps (mirrors `HVOv9` DerivativePlanner). | `HVOv9` `FrameDerivativeGenerator`. |
| Telemetry APIs | Expose capture telemetry via versioned endpoints on both agent and LogicHost. | `HVOv9` Prometheus exporter. |
| Azure Monitor dashboards | Wire existing meters (`HVO.SkyMonitor.CameraAgent.Capture`) into dashboards and alert rules. | N/A (new). |

## 4. Phase 04 – Tenant Provisioning Automation
| Item | Description |
| --- | --- |
| Self-service onboarding | Provide LogicHost UI + automation that registers tenants in Azure Entra, issues config bundles, and provisions storage namespaces. |
| Device enrollment | Optionally support device code flow or enrollment tokens for agents deployed by tenants. |
| Runbook updates | Expand `docs/identity/operations-runbook.md` and new onboarding runbooks to cover automation scripts. |

## 5. Backlog / Investigations
- **Queue technology choice** – evaluate Redis Streams vs. Azure Queue vs. RabbitMQ for ingest scaling; legacy used Redis but we may need guaranteed delivery.
- **Edge caching** – determine whether agents should locally serve historical data or rely entirely on LogicHost + MinIO.
- **Config as code** – consider merging `cameraagent.sample.json` into per-tenant config packages to remove manual edits.
- **Security hardening** – adopt managed identities for cloud-hosted agents and rotate API keys automatically for on-prem nodes.

## 6. How to Use This Document
- Treat each row as a placeholder for ADRs or GitHub issues.
- Whenever a backlog item graduates into an active project, update the relevant architecture doc (`camera-agent-platform.md`, `auth-strategy.md`, or `contracts.md`) and strike it from this list.
- Keep diagrams (`docs/architecture/diagrams/*.mmd`) synchronized with the latest Phase deliverables.
