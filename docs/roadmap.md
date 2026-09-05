# HVO SkyMonitor Product Roadmap

Status date: 2026-09-05

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

| ID | Initiative | Outcome | Owning epic or issue | Dependencies and boundary |
| --- | --- | --- | --- | --- |
| `RM-005` | Local-first processing graphs and distributed runners | Generalize immutable graphs, durable CameraAgent jobs and replay, central graph execution, self-hosted runners, fairness, and optional elastic providers. | [Epic #421](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/421); [#422](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/422)-[#430](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/430) | Consumes `RM-004` product semantics and the delivered provider-neutral object-store application boundary. Current MinIO deployment and test infrastructure is an accepted temporary baseline; any backend change invalidates affected central evidence. Live CameraAgent processing remains immediate and in-process; only explicit archived replay or central work may use external runners. #430 delivered the `elastic-provider-v1` boundaries with the `local-process` proof adapter (cloud adapters deferred behind the same boundary), completing the epic. |
| `RM-017` | Standalone CameraAgent product completion | Advance the delivered standalone foundation into independently testable, installable, operable, recoverable, observable, and releasable CameraAgent software, including coherent authenticated local workflows, named graph and replay operation, bounded immutable execution-evidence export, component-scoped quality gates, and signed multi-architecture distribution. | [Epic #513](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513); [Standalone CameraAgent Product Completion milestone](https://github.com/RoySalisbury/HVO.SkyMonitor/milestone/2) | #532 delivered explicit product/test ownership after the `RM-005` CameraAgent edge checkpoint through #425, #515 delivered the offline observatory shell, account workflows, and owner-only local recovery foundation (PR #550; review discoveries continue in #554 and #555), #514 delivered current sky facts, observing-day archive browsing, product listing and lineage detail, and transient date-range review over the existing durable schema (PR #560), and #517 delivered the Operations workspace with typed schedule and rig forms, pipeline summary, automations, data and storage, and sky map and catalog sections over the existing durable contracts (PR #565; prerequisites recorded in #563 and #564), and #507 delivered the `cameraagent-state-v2` compatibility contract, the consolidated deployment preflight, runtime-identity bind-source creation, and the operator-approved CameraAgent-only state reset (PR #561), #516 delivered the named-graph, execution, and exact archived-replay workflows over the delivered `RM-005` contracts (PR #568), #536 delivered the `hvo-cameraagent-execution-evidence-v1` export contract with its read-only projection, golden fixtures, and W6 bounds (PR #569), #533 delivered component-scoped CI lanes, coverage floors, and the plan-derived Required CI aggregation (PR #572), #564 delivered the audited local mutation contract for manual observer coordinates (PR #577), and #537 delivered the bounded durable graph-execution evidence export lane with conformance-sink validation and W6 evidence (PR #576), and #563 delivered the versioned durable local automation contract, its separate rollback-safe SQLite store, the runner, owner-only endpoints, and the operations automations section (PR #583), and #534 delivered the signed `image` distribution train with derive-from-bytes identity verification, signed installer image acquisition that fails before any Docker contact, and the release workflow image branch (PR #595; acceptance partially met, carried by #597, #598, #599, #602, and #603). The retained [operations prototype at `fdca7f1`](https://github.com/RoySalisbury/HVO.SkyMonitor/commit/fdca7f10e7b2771a90fe02e23a3107d56f93cc06) guides visual hierarchy and interaction only, not runtime contracts. The export contract and release convergence also require #426-#428; #429 and #430 do not block completion. LogicHost product work is excluded, and existing optional integration remains compatibility and regression evidence only. Separately managed extensions, formats, notifications, hardware qualification, environmental-source acquisition, profiling, and native-CI evidence remain outside this completion claim. |

`RM-005` remains Current beside `RM-017`. #422-#425 delivered the CameraAgent
edge checkpoint, #426-#427 delivered central processing graph delivery and
durable graph execution (PR #546), #547 and #549 hardened that merged execution
boundary (PRs #548, #552), and #428 delivered the `processing-runner-v1`
protocol, LogicHost runner registry and endpoints, and the self-hosted
`HVO.SkyMonitor.ProcessingRunner` service and image, and #429 delivered
per-observatory entitlements, weighted fair scheduling with starvation
prevention, runner pools, usage records, and per-observatory signals. The
RM-005 tail (#430 elastic provider adapters, delivered as the local-process proof adapter behind provider-neutral boundaries)
continues on the LogicHost side without touching active `RM-017` files. #428's
runner identity unblocks #536; #430 does not block `RM-017`.

`RM-017` is Current beside the partially paused `RM-005`; #532, #515, #514,
#517, #507, #516, #536, #533, #564, #537, #563, and #534 are delivered. The
follow-ups found during #534 evidence precede #535: #603 (x86-64 `open(2)` flag
values break owner recovery and installer directory opens on arm64; claimed),
#602 (dual standalone smoke 403; claimed), #598 (signed upgrade and rollback in
the installer campaign), #597 (component-level image SBOM and registry
attestation), and #599 (signed release in the standalone preflight). #535
closes the initiative after them. One coordinator owns both queues and the shared
two-issue global capacity.
`RM-018` remains Future and blocked by completed `RM-017`.

## Next

| ID | Initiative | Outcome | Owning epic or issue | Dependencies and boundary |
| --- | --- | --- | --- | --- |
| `RM-016` | Provider-neutral LogicHost object storage | Replace the archived MinIO local baseline with an HVO-owned durable filesystem provider behind `IObjectStore`, while retaining optional S3 portability and qualifying one exact same-host Linux topology. | [Epic #499](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/499); required children [#584](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/584), [#592](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/592), [#585](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/585), [#586](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/586), and [#506](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/506) | Starts after `RM-005` closes; production scope is LogicHost plus host-neutral filesystem primitives beside `RM-017`. #504 delivered the initial boundary and #505 recorded the SeaweedFS 4.44 no-go. The active order is #584 -> #592 -> #585 -> #586 -> #506. The default is one LogicHost replica with a same-host Linux ext4 bind mount; no legacy MinIO migration, CameraAgent production/state change, remote filesystem certification, bundled replacement S3 server, or cloud dependency is included. The first release exposes only the qualified filesystem profile; S3 remains development/qualification-only until #589. Shared CI and combined Testcontainers edits require explicit ownership coordination and affected CameraAgent/combined regression evidence. |

`RM-016` is the approved automatic successor to `RM-005`. It may begin while
`RM-017` continues because its required path does not change CameraAgent
production behavior, durable state, installer, or release artifacts. Any shared
project, CI-classifier, architecture-test, or combined-fixture edit must be
claimed and coordinated with the `RM-017` owner and rerun affected CameraAgent
and combined evidence. The detailed provider decision and support envelope are recorded in
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
  +-> RM-018 LogicHost network operations and distribution

RM-005 completion
  +-> RM-016 provider-neutral LogicHost object storage
    +-> #584 provider selection and durable identity
      +-> #592 host-neutral filesystem durability primitives
        +-> #585 LogicHost filesystem provider
          +-> #586 same-host Linux ext4 qualification
            +-> #506 adoption and MinIO removal
    +-> RM-019 future remote-filesystem, external S3, and Azure Blob profiles

RM-005 #429-#430 fairness and optional provider work does not block RM-017.

RM-003 deployment, RM-011 direct ZWO enablement, and RM-014 CameraAgent
presentation are delivered inputs to RM-017; RM-017 extends rather than
reopens them.

RM-009 environmental source research is independently plannable.
RM-010, RM-012, and RM-013 remain trigger- or capacity-dependent.
RM-015 follows the RM-003 deployment foundation but remains separate.
RM-016 is the approved successor to RM-005 and does not block standalone
RM-017. Its required production changes are LogicHost or host-neutral
infrastructure work and must not change CameraAgent behavior, state, or
contracts. Shared CI, architecture tests, and combined fixtures still require
ownership coordination and affected CameraAgent/combined regression evidence.
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
