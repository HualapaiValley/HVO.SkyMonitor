# HVO SkyMonitor Product Roadmap

Status date: 2026-09-06

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

Implementation capacity follows horizon and table order. Fill available slots
from the highest-priority Current initiative before selecting Next, Future,
Research, or Deferred work. A lower-horizon issue may move ahead only when the
active epic records it as a real blocking dependency; component independence or
an unrelated failure in a broad validation plan is not enough.

## Current

| ID | Initiative | Outcome | Owning epic or issue | Dependencies and boundary |
| --- | --- | --- | --- | --- |
| `RM-017` | Standalone CameraAgent product completion | Close the remaining edge durability, lifecycle, release-integrity, test-reliability, multi-architecture, and final standalone evidence gaps so CameraAgent is independently installable, operable, recoverable, observable, and releasable. | [Epic #513](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513); [Standalone CameraAgent Product Completion milestone](https://github.com/RoySalisbury/HVO.SkyMonitor/milestone/2) | This is the only active implementation initiative. The delivered product foundation includes #507, #514-#517, #532-#534, #536-#537, #563-#564, #597-#599, #602-#603, #608, and #614. Epic #513 owns the two CameraAgent-only lane orders and native subissues; #535 is formally blocked by every accepted open milestone prerequisite. LogicHost product and LogicHost-only test work are excluded. Existing optional cross-host behavior remains compatibility evidence, never a CameraAgent correctness dependency. |

Both default implementation slots remain inside `RM-017`: one follows release
integrity and distribution, and one follows runtime stability and focused test
reliability. If one lane empties, its slot takes the next ready CameraAgent item
from the other lane or helps prepare #535; it does not advance to LogicHost.
Live order, claims, delivery state, and blockers stay in epic #513 rather than
being copied into this portfolio summary.

## Next

| ID | Initiative | Outcome | Owning epic or issue | Dependencies and boundary |
| --- | --- | --- | --- | --- |
| `RM-005` | Local-first processing graphs and distributed runners | Close the delivered initiative after its remaining LogicHost-only test and heterogeneous-fleet follow-ups are dispositioned. | [Epic #421](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/421); remaining [#632](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/632) and [#613](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/613) | Native delivery children #422-#430 are complete. #632 and #613 are paused and formally blocked by RM-017 final issue #535. Neither may change CameraAgent or be pulled into its active queue merely because a broad CI plan selects LogicHost. Close #421 after both are complete or explicitly dispositioned. |
| `RM-016` | Provider-neutral LogicHost object storage | Replace the archived MinIO local baseline with an HVO-owned durable filesystem provider behind `IObjectStore`, while retaining optional S3 portability and qualifying one exact same-host Linux topology. | [Epic #499](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/499); required children [#584](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/584), [#592](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/592), [#585](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/585), [#586](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/586), and [#506](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/506) | Starts only after `RM-017` and `RM-005` close. #584 is formally blocked by #535 and #421; the active order then remains #584 -> #592 -> #585 -> #586 -> #506. Production scope is LogicHost plus narrow host-neutral filesystem primitives. No legacy MinIO migration, CameraAgent production/state change, remote filesystem certification, bundled replacement S3 server, or cloud dependency is included. |

`RM-005` is the first post-CameraAgent queue. `RM-016` follows its closure; it
does not run beside `RM-017`. Shared CI, architecture-test, or combined-fixture
changes still require affected regression evidence, but that evidence does not
transfer product ownership between hosts. The detailed provider decision and
support envelope are recorded in
[`planning/object-storage-provider-decision.md`](planning/object-storage-provider-decision.md).

The deferred CameraAgent storage/upload naming cleanup
[#142](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/142) inherits
delivered `RM-004`. Its #433 vocabulary prerequisite is complete, but the
naming-only residual is not approved for automatic start.

## Future

| ID | Initiative | Outcome | Owning epic or issue | Dependencies and boundary |
| --- | --- | --- | --- | --- |
| `RM-006` | Certified processing extension platform | Add signed, versioned extension lifecycle and a bounded external-processing bridge without exposing host infrastructure or weakening reproducibility. | [#140](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/140) | Consumes layered producer/compositor/encoder contracts from `RM-004` and complements, but does not duplicate, `RM-005` runners. |
| `RM-007` | Native camera artifacts and pluggable formats | Preserve camera-native and proprietary source bytes, decode them into canonical processing frames, and produce versioned scientific, display, and export formats. | [#141](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/141) | Builds on certified extensions and layered composition. It does not identify demosaiced, corrected, or lossy data as raw evidence. |
| `RM-015` | Production multichannel notifications | Add production transactional email and SMS delivery, durable LogicHost in-app notifications, and optional bounded CameraAgent notifications. | [Epic #455](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/455) | Selects providers and CameraAgent delivery topology through an explicit decision checkpoint. Mailpit remains development/test-only and is excluded from official release installation; standalone CameraAgent correctness never depends on notifications, LogicHost, or an external provider. |
| `RM-018` | LogicHost network operations and distribution | Complete LogicHost as an independently validated and released central multi-observatory product, including immutable CameraAgent execution-evidence import, protected observatory and logical-camera workspaces, signed LogicHost lifecycle distribution, and exact-digest optional two-host acceptance. | [Epic #531](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/531) | Starts only after `RM-017` and its standalone completion milestone close. It consumes stable CameraAgent and shared contracts without reopening CameraAgent features or making LogicHost part of acquisition correctness. The `RM-016` disposition must complete and its owning epic #499 must close before #540 publishes a production LogicHost release; `RM-016` is not absorbed into this initiative. |
| `RM-019` | Remote and cloud object-storage profiles | Add independently qualified remote-filesystem, external S3, and native Azure Blob profiles behind LogicHost `IObjectStore` for larger LAN and cloud deployments. | [Epic #588](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/588); [#589](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/589)-[#591](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/591) | vNext after the `RM-016` application contract and local topology stabilize. It does not block `RM-016`, `RM-017`, or the first `RM-018` release. Each provider and topology requires independent security, recovery, backup, observability, and performance evidence; a same-host qualification does not certify NFS, SMB, NAS, or a remote service. |

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
| `RM-004` | Canonical layered capture products | Delivered immutable base imagery, durable projected scenes and analytical layers, deterministic on-demand presentation, and explicit materialization across standalone CameraAgent and LogicHost. | [Epic #436](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/436); CameraAgent PR #454 and LogicHost PR #456 |
| `RM-011` | Direct ZWO camera enablement | Added the optional direct ZWO CameraAgent adapter and retained Linux x64 ASI178MC and ARM64 ASI676MC functional deployment evidence. | [#265](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/265), [#266](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/266), and [#267](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/267) |
| `RM-014` | CameraAgent presentation experience | Delivered an authenticated image-led current view, accessible presentation-first capture detail and enlargement, bounded archive browsing, and a separate retained technical operations workspace. | [Epic #438](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/438); design checkpoint PR #453 |

Delivered does not imply broader physical calibration or field-performance
acceptance. The retained future hardware requirements remain in the
[requirements crosswalk](planning/requirements-crosswalk.md#5-deferred-hardware-evidence).

## Dependency View

The primary forward dependency is:

```text
RM-004 layered capture products
  +-> RM-005 processing graphs and runners
  +-> RM-006 extension platform
  +-> RM-007 native artifacts and formats
  +-> RM-008 stream transient research

RM-005 #422-#425 delivered CameraAgent edge checkpoint
  +-> RM-017 independent product, test, CI, and operator-workflow tranche

RM-005 #426-#427 delivered central graph execution, hardened by #547 and #549
  +-> RM-005 #428 delivered processing-runner-v1 and the self-hosted runner
    +-> RM-017 immutable execution-evidence export and release convergence
    +-> RM-005 #429 delivered fair scheduling and entitlements, and #430 delivered the elastic provider boundaries with the local-process adapter

RM-017 standalone CameraAgent product completion
  +-> #535 final standalone evidence
    +-> close milestone 2 and epic #513
      +-> RM-005 LogicHost-only closure tail #632 and #613
        +-> close epic #421
          +-> RM-016 provider-neutral LogicHost object storage
            +-> #584 provider selection and durable identity
              +-> #592 host-neutral filesystem durability primitives
                +-> #585 LogicHost filesystem provider
                  +-> #586 same-host Linux ext4 qualification
                    +-> #506 adoption and MinIO removal
            +-> RM-019 future remote-filesystem, external S3, and Azure Blob profiles
      +-> RM-018 LogicHost network operations and distribution

RM-003 deployment, RM-011 direct ZWO enablement, and RM-014 CameraAgent
presentation are delivered inputs to RM-017; RM-017 extends rather than
reopens them.

RM-009 environmental source research is independently plannable.
RM-010, RM-012, and RM-013 remain trigger- or capacity-dependent.
RM-015 follows the RM-003 deployment foundation but remains separate.
RM-005 and RM-016 are post-RM-017 work and do not block standalone CameraAgent
completion. Their remaining production changes are LogicHost or host-neutral
infrastructure work and must not change CameraAgent behavior, state, or
contracts. Shared CI, architecture tests, and combined fixtures still require
affected CameraAgent/combined regression evidence without becoming active
CameraAgent dependencies.
Post-RM-017 #587 may adopt the shared filesystem primitives without replacing
CameraAgent's storage contract. RM-016 epic #499 blocks RM-018 issue #540 and
must close before the first production LogicHost release; RM-019 does not.
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
