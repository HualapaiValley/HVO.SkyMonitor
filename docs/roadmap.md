# HVO SkyMonitor Product Roadmap

Status date: 2026-08-24

This document is the repository-visible portfolio roadmap. It owns stable
roadmap initiative IDs, planning horizons, and the mapping from initiatives to
top-level GitHub epics or issues. It describes intended outcomes, not detailed
implementation contracts.

[`project-plan.md`](project-plan.md) remains authoritative for system
architecture, virtual-first scope, phase order, requirements, and completion
criteria. Owning specifications define subsystem behavior. Linked GitHub issues
own live execution state, dependencies, acceptance evidence, and delivery
decisions. If this summary disagrees with one of those sources, use that source
and correct this index.

## Roadmap Model

A roadmap item is a durable product or operational outcome with a stable
`RM-###` ID. The ID does not encode priority, phase, or date and is never reused
or renumbered.

A top-level epic normally represents one roadmap item. Child epics and
implementation issues inherit the parent's roadmap ID; they do not receive a
new ID unless they become independently managed portfolio outcomes. Research
items record a decision-producing investigation and do not imply a production
commitment.

The planning horizons are:

| Horizon | Meaning |
| --- | --- |
| Current | Actively delivering or the next approved work on the current execution path. |
| Next | Accepted and dependency-ordered, but not yet active. |
| Future | Intended direction without a committed start date. |
| Research | Investigation or architecture work required before implementation is approved. |
| Deferred | Retained work with an explicit reason not to schedule it now. |
| Delivered | Completed outcome retained for context and dependency history. |

## Current

No initiative is active. Promote an accepted item from `Next` only after its
owning epic records priority, readiness, and the coordination owner for the new
milestone.

## Next

| ID | Initiative | Outcome | Owning epic and children | Dependencies and boundary |
| --- | --- | --- | --- | --- |
| `RM-004` | Canonical layered capture products | Replace chained flattened annotations with durable projected scenes, analytical metadata, independently selectable layers, and explicit final materialization. Complete CameraAgent first, then add optional LogicHost ingestion and presentation. | [Epic #436](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/436); [#431](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/431) -> [#435](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/435) -> [#433](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/433) -> [#434](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/434) -> [#437](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/437) -> [#432](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/432) | CameraAgent remains fully operational without LogicHost. SVG is a cacheable presentation; canonical analysis remains structured metadata. General replay and distributed execution remain `RM-005`. |
| `RM-014` | CameraAgent presentation experience | Make the local CameraAgent an image-led, presentation-friendly observatory experience with a clear current view, large-image viewing, capture-stage comparison, and approachable archive while retaining technical operations as a separate workspace. | [Epic #438](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/438); [#440](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/440) -> [#443](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/443) -> [#439](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/439), [#442](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/442) -> [#441](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/441) | Begins after `RM-003`. Presentation foundations may use existing durable artifacts, while canonical layers and materialization coordinate with `RM-004/#434`. The baseline remains authenticated and CameraAgent-local; anonymous or kiosk publication requires a separate security decision. |

The blocked CameraAgent storage/upload naming cleanup
[#142](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/142) also inherits
`RM-004` after #433 establishes the final layered product vocabulary.

## Future

| ID | Initiative | Outcome | Owning epic or issue | Dependencies and boundary |
| --- | --- | --- | --- | --- |
| `RM-005` | Local-first processing graphs and distributed runners | Generalize immutable graphs, durable CameraAgent jobs and replay, central graph execution, self-hosted runners, fairness, and optional elastic providers. | [Epic #421](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/421); [#422](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/422)-[#430](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/430) | Consumes `RM-004` product semantics. Live CameraAgent processing remains immediate and in-process; only explicit archived replay or central work may use external runners. |
| `RM-006` | Certified processing extension platform | Add signed, versioned extension lifecycle and a bounded external-processing bridge without exposing host infrastructure or weakening reproducibility. | [#140](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/140) | Consumes layered producer/compositor/encoder contracts from `RM-004` and complements, but does not duplicate, `RM-005` runners. |
| `RM-007` | Native camera artifacts and pluggable formats | Preserve camera-native and proprietary source bytes, decode them into canonical processing frames, and produce versioned scientific, display, and export formats. | [#141](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/141) | Builds on certified extensions and layered composition. It does not identify demosaiced, corrected, or lossy data as raw evidence. |

## Research

| ID | Initiative | Decision outcome | Owning issue | Boundary |
| --- | --- | --- | --- | --- |
| `RM-008` | Stream-native fireball detection V2 | Produce a reviewed design for buffered streams, temporal evidence, event promotion, clips/stills, and optional accelerated or external assessment. | [#143](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/143) | Research and issue decomposition only; no production implementation or physical sensitivity claim is committed. |
| `RM-009` | Physical environmental source acquisition | Define CameraAgent-local adapters, schedules, provenance, health, and evidence for weather stations, sky-temperature sensors, SQM devices, and online providers. | [#158](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/158) | No hardware vendor or online provider is selected. LogicHost does not poll local devices or become part of acquisition correctness. |

## Deferred

| ID | Initiative | Retained outcome | Owning issue | Reason deferred |
| --- | --- | --- | --- | --- |
| `RM-010` | Production profiling and observability operations | Add bounded always-on signals and a secure escalation path to short .NET/Linux profiling with measured telemetry overhead. | [#244](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/244) | Existing evidence is sufficient for current delivery; broad profiling remains low priority unless an active defect requires a focused prerequisite. |
| `RM-012` | Physical camera soak and USB qualification | Produce sustained matched ARM64/x64 acquisition and multi-camera USB isolation evidence or a deterministic failing-layer diagnosis. | [#288](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/288) | Requires suitable physical hardware, stable power/cooling, and controlled USB topology. |
| `RM-013` | Native ARM64 CI evidence | Add advisory native Linux ARM64 build, publish, container, catalog, and VirtualSky smoke evidence before deciding whether it becomes required. | [#381](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/381) | Depends on available native runner capacity and remains advisory until measured evidence supports a required gate. |

Deferred validation follow-ups [#252](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/252)
and [#262](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/262)
inherit `RM-001`; they validate delivered boundaries and are not separate
portfolio initiatives.

## Delivered

| ID | Initiative | Delivered outcome | Owning epic or issues |
| --- | --- | --- | --- |
| `RM-001` | Virtual-first SkyMonitor platform | Standalone CameraAgent plus optional reconstructable LogicHost processing, weather/cloud/transient workflows, UI, recovery, and production-readiness foundations. | [Epic #89](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/89). Nested epics #59, #60, #62, #64, #65, #109, #205, and #243 inherit this ID. |
| `RM-002` | Deferred Phase 14 evidence campaign | Imported, executed, aggregated, and dispositioned exhaustive evidence without reopening virtual-first completion. | [#305](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/305); children #318-#322 inherit this ID. |
| `RM-003` | Installable and lifecycle-managed deployment | Delivered multi-instance-safe persistent layout, a self-contained installer, transactional upgrade/rollback/uninstall, and signed release/catalog distribution without adding physical-camera discovery or vendor SDK installation. | [#414](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/414), [#415](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/415), [#416](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/416), and [#417](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/417) |
| `RM-011` | Direct ZWO camera enablement | Added the optional direct ZWO CameraAgent adapter and retained Linux x64 ASI178MC and ARM64 ASI676MC functional deployment evidence. | [#265](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/265), [#266](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/266), and [#267](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/267) |

Delivered does not imply broader physical calibration or field-performance
acceptance. The retained future hardware requirements remain in the
[requirements crosswalk](planning/requirements-crosswalk.md#5-deferred-hardware-evidence).

## Dependency View

The primary forward dependency is:

```text
RM-003 delivered installable deployment
  +-> RM-014 CameraAgent presentation experience

RM-004 layered capture products
  +-> RM-005 processing graphs and runners
  +-> RM-006 extension platform
  +-> RM-007 native artifacts and formats
  +-> RM-008 stream transient research
  +-> RM-014 layered CameraAgent presentation integration

RM-009 environmental source research is independently plannable.
RM-010, RM-012, and RM-013 remain trigger- or capacity-dependent.
```

Dependencies in this summary show portfolio direction only. Formal issue
blocking relationships in GitHub determine whether implementation can start.

## GitHub Mapping Convention

- Keep the generic `roadmap` label on approved roadmap work.
- Use `roadmap:RM-###` as the roadmap-ID label on the owning epic and its owned
  child epics/issues when those labels are established.
- Assign one primary roadmap ID to an issue. Represent cross-initiative effects
  with GitHub dependencies or relationship text rather than multiple primary IDs.
- A top-level epic normally owns one roadmap ID. A nested delivery epic inherits
  its parent ID unless this document explicitly promotes it.
- A future idea receives an ID only after its outcome and boundary are accepted.
  IDs for rejected or retired initiatives remain reserved and are never reused.
- Closing, splitting, or replacing a GitHub issue does not change the roadmap ID;
  update the mapping here to the successor issue.

When an initiative changes horizon, scope boundary, or owning epic, update this
document in the same branch or PR as the corresponding GitHub planning change.
Do not copy detailed acceptance criteria or live execution status here.
