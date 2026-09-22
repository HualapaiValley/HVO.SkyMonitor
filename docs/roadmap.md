# HVO SkyMonitor Product Roadmap

Status date: 2026-09-16

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
| Current | Highest-priority delivery outcome; not an exclusive implementation slot. |
| Next | Accepted follow-on outcomes; explicitly approved independent work may start before Current closes. |
| Future | Intended direction without a committed full-delivery start date; explicitly approved preparation or discovery may proceed. |
| Research | Investigation or architecture work required before implementation is approved. |
| Deferred | Retained work with an explicit reason not to schedule it now. |
| Delivered | Completed outcome retained for context and dependency history. |

Horizons and table order express portfolio priority, not contributor ownership
or repository-wide implementation slots. An assigned developer owns the complete
issue lifecycle and records the claim on that issue as defined in AGENTS.md.
Technical dependencies and explicit scope exclusions remain binding. The approved
multi-developer rollout [#847](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/847)
authorizes the independent scopes below and removes their scheduling-only holds;
it does not grant ownership of an already claimed issue or blanket activation of
Research, Deferred, or other unapproved work. Start authorization is separate from
integration and release qualification, not conditional on coordinator enrollment.

## Current

| ID | Initiative | Outcome | Owning epic or issue | Dependencies and boundary |
| --- | --- | --- | --- | --- |
| `RM-016` | Provider-neutral LogicHost object storage | Replace the archived MinIO local baseline with an HVO-owned durable filesystem provider behind `IObjectStore`, while retaining optional S3 portability and qualifying one exact same-host Linux topology. | [Epic #499](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/499); required children [#584](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/584), [#592](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/592), [#585](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/585), [#586](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/586), and [#506](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/506) | The technical chain #584 -> #592 -> #585 -> #586 -> #506 is delivered; #499 closes once its disposition is recorded. Production scope is LogicHost plus narrow host-neutral filesystem primitives. No legacy MinIO migration, CameraAgent production/state change, remote filesystem certification, bundled replacement S3 server, or cloud dependency is included. |

Standalone CameraAgent completion (`RM-017`) is Delivered. Its retained follow-ups
are ordinary component issues, not an initiative: the real Phase 14 import at the
pinned head ([#971](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/971),
blocked by [#968](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/968)),
replay-evidence projection of publication and execution route
([#973](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/973)), and the
#197 summary schema bump ([#974](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/974)).
Shared CI, architecture-test, or combined-fixture changes still require affected
regression evidence, but that evidence does not transfer product ownership between
hosts. The detailed provider decision and support envelope are recorded in
[`planning/object-storage-provider-decision.md`](planning/object-storage-provider-decision.md).

## Next

| ID | Initiative | Outcome | Owning epic or issue | Dependencies and boundary |
| --- | --- | --- | --- | --- |
| `RM-018` | LogicHost network operations and distribution | Complete LogicHost as an independently validated and released central multi-observatory product, including immutable CameraAgent execution-evidence import, protected observatory and logical-camera workspaces, signed LogicHost lifecycle distribution, and exact-digest optional two-host acceptance. | [Epic #531](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/531) | The standalone qualification gate (`RM-017`) is satisfied. #847 authorizes #538 importer work consuming delivered #537, independent #855 API/UI discovery for #539 separate from imported views, and independent #856 packaging preparation for #540 only. It consumes stable CameraAgent and shared contracts without reopening CameraAgent features or making LogicHost part of acquisition correctness. Production release remains blocked on completion of the `RM-016` disposition with #499 closed; `RM-016` is not absorbed into this initiative. |

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
| `RM-019` | Remote and cloud object-storage profiles | Add independently qualified remote-filesystem, external S3, and native Azure Blob profiles behind LogicHost `IObjectStore` for larger LAN and cloud deployments. | [Epic #588](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/588); [#589](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/589)-[#591](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/591) | vNext after the `RM-016` application contract and local topology stabilize. It does not block `RM-016`, `RM-017`, or the first `RM-018` release. Each provider and topology requires independent security, recovery, backup, observability, and performance evidence; a same-host qualification does not certify NFS, SMB, NAS, or a remote service. |

For `RM-018`, imported API/UI views integrate only after the #538 importer
contracts and evidence are available; independent #855 discovery for #539 does not
waive that gate. Independent #856 packaging preparation for #540 is not production
release authorization. Combined
two-host acceptance still requires independently qualified CameraAgent and
LogicHost release artifacts and evidence bound to their exact digests; neither
preparation nor a combined run substitutes for standalone qualification.

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

Deferred celestial-fidelity epic [#520](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/520)
and children [#518](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/518) and
[#521](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/521)-[#526](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/526)
also inherit delivered `RM-001`. They are retained product improvements, not
approved automatic-start work; with `RM-017` delivered they are the leading
candidates for the next CameraAgent product tranche and await explicit
scheduling.

## Delivered

| ID | Initiative | Delivered outcome | Owning epic or issues |
| --- | --- | --- | --- |
| `RM-001` | Virtual-first SkyMonitor platform | Standalone CameraAgent plus optional reconstructable LogicHost processing, weather/cloud/transient workflows, UI, recovery, and production-readiness foundations. | [Epic #89](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/89). Nested epics #59, #60, #62, #64, #65, #109, #205, and #243 inherit this ID. |
| `RM-002` | Deferred Phase 14 evidence campaign | Imported, executed, aggregated, and dispositioned exhaustive evidence without reopening virtual-first completion. | [#305](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/305); children #318-#322 inherit this ID. |
| `RM-003` | Installable and lifecycle-managed deployment | Delivered multi-instance-safe persistent layout, a self-contained installer, transactional upgrade/rollback/uninstall, and signed release/catalog distribution without adding physical-camera discovery or vendor SDK installation. | [#414](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/414), [#415](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/415), [#416](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/416), and [#417](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/417) |
| `RM-005` | Local-first processing graphs and distributed runners | Delivered CameraAgent edge processing graphs, central graph execution, processing-runner-v1 with the self-hosted runner, fair scheduling and entitlements, and elastic provider boundaries with the local-process adapter; the LogicHost-only test and heterogeneous-fleet follow-ups #632, #854, and #613 are dispositioned. | [Epic #421](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/421); children #422-#430 |
| `RM-004` | Canonical layered capture products | Delivered immutable base imagery, durable projected scenes and analytical layers, deterministic on-demand presentation, and explicit materialization across standalone CameraAgent and LogicHost. | [Epic #436](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/436); CameraAgent PR #454 and LogicHost PR #456 |
| `RM-011` | Direct ZWO camera enablement | Added the optional direct ZWO CameraAgent adapter and retained Linux x64 ASI178MC and ARM64 ASI676MC functional deployment evidence. | [#265](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/265), [#266](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/266), and [#267](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/267) |
| `RM-017` | Standalone CameraAgent product completion | Delivered the standalone-complete acceptance record: campaign `issue-211-final-20260922T033329Z` generation 1 on head `8388002ce69aa1fa194e7644bd8f35352ac10acf` with `acceptanceReady=true` (five W6 trials, Mono8, both W6 replay profiles, #197 dual-agent, four component lanes, exact-head protected CI, zero central traffic), assembled and validated by the #961 tooling and published as an immutable owner-private generation. | [Epic #513](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/513); [#535](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/535); [Standalone CameraAgent Product Completion milestone](https://github.com/RoySalisbury/HVO.SkyMonitor/milestone/2) |
| `RM-014` | CameraAgent presentation experience | Delivered an authenticated image-led current view, accessible presentation-first capture detail and enlargement, bounded archive browsing, and a separate retained technical operations workspace. | [Epic #438](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/438); design checkpoint PR #453 |

Delivered does not imply broader physical calibration or field-performance
acceptance; `RM-017`'s completion statement excludes physical field qualification
(`RM-012`, #288) and native ARM64 CI qualification (`RM-013`, #381). The retained
future hardware requirements remain in the
[requirements crosswalk](planning/requirements-crosswalk.md#5-deferred-hardware-evidence).

## Dependency View

The technical dependencies and separate start, integration, and release gates are:

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

RM-017 standalone CameraAgent product completion: delivered (#535 generation 1)
  +-> retained follow-ups #971 (blocked by #968), #973, #974
  +-> optional #587 CameraAgent adoption of the shared filesystem primitives

#847 approved independent starts
  +-> RM-005 #632, #854, #613: delivered; epic #421 closed
  +-> RM-016 #584 provider selection and durable identity
    +-> #592 host-neutral filesystem durability primitives
      +-> #585 LogicHost filesystem provider
        +-> #586 same-host Linux ext4 qualification
          +-> #506 adoption and MinIO removal -> close epic #499
  +-> RM-018 #538 importer work <- delivered #537 export contracts
  +-> RM-018 #855 independent API/UI discovery for #539, separate from imported views
  +-> RM-018 #856 independent packaging preparation for #540 only

Integration and release gates (not global start gates)
  #538 importer contracts and evidence -> #539 imported views integration
  #499 closure -> #540 production release (RM-017 qualification is satisfied)
  Qualified standalone CameraAgent + LogicHost release artifacts
    -> RM-018 exact-digest combined two-host acceptance
  RM-016 stable application contract and local topology -> RM-019 future profiles

RM-003 deployment, RM-011 direct ZWO enablement, and RM-014 CameraAgent
presentation are delivered inputs to RM-017; RM-017 extends rather than
reopens them.

RM-009 environmental source research is independently plannable.
RM-010, RM-012, and RM-013 remain trigger- or capacity-dependent.
RM-015 follows the RM-003 deployment foundation but remains separate.
RM-016's remaining production changes are LogicHost or host-neutral
infrastructure work and must not change CameraAgent behavior, state, or
contracts. Shared CI, architecture tests, and combined fixtures still require
affected CameraAgent/combined regression evidence without becoming active
CameraAgent dependencies.
Optional #587 is no longer gated; it may adopt the shared filesystem primitives
without replacing CameraAgent's storage contract when scheduled.
RM-016 epic #499 blocks #540 production release, not #856 packaging preparation;
RM-019 does not block that release. Research and Deferred work remain unactivated.
```

Formal issue dependencies retain technical start, integration, and release gates
according to their scope. #847 removes the scheduling-only holds identified above;
portfolio order and release gates must not be reinterpreted as global start rules.
Linked issues own the detailed contracts, coordination, and acceptance evidence.

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
