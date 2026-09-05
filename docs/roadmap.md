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
| `RM-005` | Local-first processing graphs and distributed runners | Generalize immutable graphs, durable CameraAgent jobs and replay, central graph execution, self-hosted runners, fairness, and optional elastic providers. | [Epic #421](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/421); [#422](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/422)-[#430](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/430) | Consumes `RM-004` product semantics and the delivered provider-neutral object-store application boundary. Current MinIO deployment and test infrastructure is an accepted temporary baseline; any backend change invalidates affected central evidence. Live CameraAgent processing remains immediate and in-process; only explicit archived replay or central work may use external runners. #430 delivered the `elastic-provider-v1` boundaries with the `local-process` proof adapter (cloud adapters deferred behind the same boundary), and #600 hardened the autoscaler's multi-replica ownership, lease-aware retirement, backlog sizing, and startup validation. Epic #421 stays open only for its LogicHost tail: #613 (heterogeneous-fleet slot allocation) and #632 (an intermittent elastic-provider integration test). |
| `RM-017` | Standalone CameraAgent product completion | Advance the delivered standalone foundation into independently testable, installable, operable, recoverable, observable, and releasable CameraAgent software, including coherent authenticated local workflows, named graph and replay operation, bounded immutable execution-evidence export, component-scoped quality gates, and signed multi-architecture distribution. | [Epic #513](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513); [Standalone CameraAgent Product Completion milestone](https://github.com/RoySalisbury/HVO.SkyMonitor/milestone/2) | Extends delivered `RM-003`, `RM-011`, and `RM-014` and starts after the `RM-005` CameraAgent edge checkpoint through #425. Nineteen children are delivered (listed below); every remaining CameraAgent issue is in the milestone and is queued in the two execution lanes recorded in epic #513. The export contract and release convergence also require #426-#428; #429 and #430 do not block completion. The retained [operations prototype at `fdca7f1`](https://github.com/RoySalisbury/HVO.SkyMonitor/commit/fdca7f10e7b2771a90fe02e23a3107d56f93cc06) guides visual hierarchy and interaction only, not runtime contracts. LogicHost product work is excluded, and existing optional integration remains compatibility and regression evidence only. Separately managed extensions, formats, notifications, hardware qualification, environmental-source acquisition, profiling, and native-CI evidence remain outside this completion claim. |

`RM-005` remains Current beside `RM-017`. #422-#425 delivered the CameraAgent
edge checkpoint, #426-#427 delivered central processing graph delivery and
durable graph execution (PR #546), #547 and #549 hardened that merged execution
boundary (PRs #548, #552), #428 delivered the `processing-runner-v1` protocol,
LogicHost runner registry and endpoints, and the self-hosted
`HVO.SkyMonitor.ProcessingRunner` service and image, #429 delivered
per-observatory entitlements, weighted fair scheduling with starvation
prevention, runner pools, usage records, and per-observatory signals, and #430
and #600 delivered and hardened the elastic provider boundaries with the
local-process proof adapter. The remaining tail, #613 and #632, is LogicHost-only
and is queued in lane 2 below. `RM-005` moves to Delivered, and `RM-016` #584
receives its `READY` signal, when epic #421 closes.

`RM-017` is Current beside that `RM-005` tail. Its delivered children are #532,
#515, #514, #517, #507, #516, #536, #533, #564, #537, #563, #534, #603, #602,
#614, #608, #599, #598, and #597 (PRs #550 through #644); per-issue delivery
notes live in [epic #513](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513)
and are not repeated here.

The remaining work runs as two independent execution lanes so that two
coordinators on separate systems proceed without blocking each other. Every
remaining CameraAgent issue is in the
[Standalone CameraAgent Product Completion milestone](https://github.com/RoySalisbury/HVO.SkyMonitor/milestone/2)
and carries a `lane:` label; the ordered queues, claims, and handoffs live in
epic #513, and the lane rules are in
[`planning/agent-execution.md`](planning/agent-execution.md) section 4.
CameraAgent work always outranks LogicHost work.

- Lane 1 (`lane:1-release`) owns the release path: #641, #645, #642, #638,
  #629, #628, #626, #627, #649, #650, #618, #651, and finally #535. These
  change the deployment CLI, the installer campaign, release tooling, the
  arm64 workflow, and their runbooks. Their classifier plans do not select the
  CameraAgent component lane, so #640 does not gate them. #651 needs an aarch64
  Docker host.
- Lane 2 (`lane:2-runtime`) owns CameraAgent runtime and test hygiene, then
  LogicHost. #640 goes first: the reduced CameraAgent component plan fails its
  critical-file branch-coverage gate on current `main`, which blocks protected
  CI for every PR confined to CameraAgent projects. #632 follows because that
  intermittent LogicHost integration test can fail any complete-matrix plan,
  including lane 1's script changes. Then #596 (resume PR #636), #624, #643,
  #619, #621, #620, #625, #554, #555, #630, #610, #609, #558, #574, #582, and
  #611. When its CameraAgent queue is empty, lane 2 continues to #613, closes
  epic #421, and starts `RM-016` in the recorded order #584, #592, #585, #586,
  #506; #587 follows only after `RM-017` closes.

#535 starts only after every other milestone issue closes; it consumes #624 and
#651 from both lanes. A lane whose CameraAgent queue is empty may take an
unclaimed CameraAgent issue from the other lane only when it shares no files
with that lane's active claim; otherwise it proceeds to LogicHost work.
Delivery status is recorded in epic #513 after each merge, and this document
changes only when a horizon, owning epic, lane definition, or boundary changes.
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
| `RM-008` | Stream-native fireball detection V2 | Produce a reviewed design for buffered streams, temporal evidence, event promotion, clips/stills, and optional accelerated or external assessment. | [#143](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/143); orbital-element decomposition [#527](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/527)-[#530](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/530) | Research and issue decomposition only; no production implementation or physical sensitivity claim is committed. #527-#530 record the decomposed satellite-track work and are not approved for automatic start. |
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
portfolio initiatives. The retained celestial-fidelity epic
[#520](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/520) and its
children #518 and #521-#526 also inherit `RM-001`. They are accepted direction
for catalog-grounded positioning but are not approved for automatic start and
do not interrupt the `RM-017` or `RM-016` execution paths.

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

RM-005 completion (#613 and #632 close epic #421)
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
