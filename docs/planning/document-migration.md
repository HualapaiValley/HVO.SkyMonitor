# Document Migration and Retirement

This crosswalk records how the virtual-first plan replaces older planning
narratives without deleting normative requirements or reproducibility evidence.

## 1. Governing Rules

- `docs/project-plan.md` is the only authoritative roadmap and aggregate phase
  status; linked GitHub issues own live issue/PR execution state and evidence.
- Subsystem specifications define behavior but do not maintain competing phase
  status.
- Validation and calibration files are evidence, not roadmap authority.
- Runbooks describe current executable operations, not aspirational platforms.
- No file is deleted until every normative statement has a destination,
  requirement ID, explicit exclusion, or retained evidence path.
- Machine-readable evidence, checksums, catalog provenance, and calibration
  manifests are never deleted as planning cleanup.

## 2. Migration Crosswalk

| Source | Retained content | Destination | Disposition |
| --- | --- | --- | --- |
| `docs/project-plan.md` before the virtual-first rewrite | Host split, project boundaries, acquisition invariants, virtual-camera behavior, pipeline goals, quality gates, UI goals, durable ingest, and completion criteria | Rewritten `docs/project-plan.md` plus `docs/planning/requirements-crosswalk.md` | Replaced in place |
| Retired `docs/projects/virtual-planetarium-implementation.md` | Fixed fixture, ownership boundaries, astronomy model, provenance, catalog policy, visible-scene behavior, rendering invariants, annotation, Stellarium hierarchy, RGB/Bayer distinction, test matrix, ASI178 residual calibration | Requirement groups `ARCH-*`, `ASTRO-*`, `PROV-*`, `VSKY-*`, `SENSOR-*`, `OPTIC-*`, `MATRIX-*`, `FIXTURE-*`, `FIXTURE-HVO-*`, `SCENE-*`, `RENDER-*`, `ANNO-*`, `CAT-*`, `QA-*`, and `VAL-STEL-*` in `docs/planning/requirements-crosswalk.md`; retained owning specifications | Deleted after migration in issue #90 |
| Retired `docs/calibration/asi178mc-pi-smoke-test.md` | Distinct 2026-07-12 SDK deployment, gain/exposure capture matrix, paths, USB/control settings, medians, RAW16 findings, repeat command, and calibration caveat | `docs/calibration/asi178mc-characterization.md` section 2026-07-12 Open-Sky Matrix; SDK identity remains in `asi178mc-sdk-profile-v1.json`; the separate 2026-07-13 binning session remains in `asi178mc-session-20260713.json` | Deleted only after unique dated evidence migrated in issue #90 |
| `docs/virtual-camera.md` | Normative VirtualSky, sensor, optics, simulation, fixture, and validation behavior | Same file, linked by phase 1/#92 and phases 2, 11, 12, and 14 | Retain without live roadmap status |
| `docs/projects/fireball-transient-detection.md` | Lane invariants, detector design, event model, edge/central modes, scenarios, and product decisions | Same file plus issues #61-#65 and master phase 12 | Retain as subsystem specification; move live status to issues |
| `docs/validation/cameraagent-arm64.md` | Dated ARM64 functional and performance evidence | Same file, referenced as evidence only | Retain; keep future hardware gates outside virtual milestone |
| `docs/validation/stellarium.md` | Pinned external-oracle procedure and evidence limitations | Same file and separate workflow | Retain |
| `docs/calibration/asi178mc-characterization.md` | Active ASI178 verification state and calibration backlog | Same file | Retain |
| `docs/calibration/asi174mm-characterization.md` | Future sensor onboarding procedure | Same file | Retain as procedure, not current hardware status |
| `docs/catalog/*.md` | Catalog and constellation provenance | Same files | Retain immutable provenance |
| `docs/reference-code.md` | Commit-pinned legacy source map and porting policy | Same file | Retain reference only |
| `docs/identity/overview.md` | Registration/bootstrap architecture and local identity boundary | Same file; open production rollout/security work moves to plan/issues | Retain and remove live phase status under #90 |
| `docs/identity/operations-runbook.md` | Current source-validated identity and security operations | Same file; SQL Server/Compose/current routes replaced the historical procedures under #120 | Retain as active runbook; unsupported controls are explicit implementation gaps |
| `docs/security/secrets.md` | Consumed secret catalog, supported providers, scope, rotation capability, and leakage controls | Same file | Retain as active guidance; optional providers must not be presented as implemented |
| `docs/runbooks/ci-pipeline.md` | Current CI reproduction | Same file plus `QA-*` requirements | Retain and update under #109 |
| `docs/runbooks/local-dev.md` | Daily local workflow | Same file | Retain and update after categories/catalog/storage change |
| `docs/runbooks/infra-operations.md` | Current shared-service and application-container operations | Same file plus `OPS-*` requirements | Retain and correct fixture/full catalog and persistent storage under #109 |
| `docs/runbooks/cameraagent-retention.md` | Current outbox hold, retention, pressure, and recovery behavior | Same file plus phases 3-7 | Retain and extend after SQLite ingress/lanes |
| Unreferenced generated SVGs under `docs/images` | No active normative content; capture topology becomes obsolete with durable lanes | Master plan text and future source-controlled diagrams | Delete after repository link check |

## 3. Requirements Preserved From Retired Plans

The following content from the retired virtual-planetarium plan remains owned:

- Fixed observer, camera, lens, and time fixtures: `docs/virtual-camera.md`.
- Projection and astronomy ownership: `ARCH-*`, Astronomy, and
  `docs/virtual-camera.md`.
- HYG selection and provenance: `docs/catalog/hyg-v42.md`.
- Annotation and constellation behavior:
  `docs/catalog/d3-celestial-constellations.md` and shared Processing phase 2.
- Stellarium comparison: `docs/validation/stellarium.md`.
- ASI178 profile uncertainty and calibration:
  `docs/calibration/asi178mc-characterization.md`.
- Cross-host conformance and full-resolution validation: phases 2, 9, and 14.
- Exact requirement ownership and implementation destinations:
  `docs/planning/requirements-crosswalk.md`.
- Hardware acceptance: explicitly excluded from the virtual-first milestone and
  retained in validation/calibration evidence.

The following content from the retired ASI178 smoke-test narrative remains
owned:

- Camera identity and SDK properties: `asi178mc-sdk-profile-v1.json`.
- The 2026-07-12 matrix, source paths, controls, medians, RAW16 findings,
  repeat command, interpretation, and caveat:
  `asi178mc-characterization.md`.
- The distinct 2026-07-13 hashes, host state, bins, parity, and failures:
  `asi178mc-session-20260713.json`.
- Current utility procedure: `tools/asi-capture/README.md`.

## 4. Retirement Checklist

Before deleting a document:

1. Search repository links and plain-text path references.
2. Move every retained requirement to its destination.
3. Preserve machine-readable evidence and hashes.
4. Update `docs/README.md`, `AGENTS.md`, prompts, and issues.
5. Validate Markdown links.
6. Run repository build/tests when source-visible configuration or prompts
   change.
7. Verify every requirement group in `requirements-crosswalk.md` has an owning
   retained source and phase/issue destination.
8. Record the deletion and destinations in the consolidation PR.

## 5. Planned Cleanup Issues

- [#90](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/90) owns roadmap,
  documentation authority, retirement, identity/reference status cleanup, and
  link validation.
- [#109](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/109) owns CI,
  catalog packaging, persistent CameraAgent storage, and infrastructure runbook
  corrections.
- [Epic #89](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/89) records
  current phase and continuation handoff.
