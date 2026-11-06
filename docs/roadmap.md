# HVO SkyMonitor Product Roadmap

Status date: 2026-10-06

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
multi-developer rollout [#847](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/847)
authorizes the independent scopes below and removes their scheduling-only holds;
it does not grant ownership of an already claimed issue or blanket activation of
Research, Deferred, or other unapproved work. Start authorization is separate from
integration and release qualification, not conditional on coordinator enrollment.

## Current

`RM-016` is delivered; its final storage disposition and evidence are recorded
under Delivered. This closeout does not promote another initiative's horizon or
authorize new implementation or publication. The approved `RM-018` preparation
and remaining product outcomes retain their scope under Next.

Standalone CameraAgent completion (`RM-017`) is Delivered. Its retained follow-ups
are ordinary component issues, not an initiative: the real Phase 14 import at the
pinned head ([#971](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/971),
dependency-ready after [#981](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/981)),
with the closed replay-evidence projection
([#973](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/973)) and #197 summary
schema bump ([#974](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/974))
retained as delivered history.
Shared CI, architecture-test, or combined-fixture changes still require affected
regression evidence, but that evidence does not transfer product ownership between
hosts. The detailed provider decision and support envelope are recorded in
[`planning/object-storage-provider-decision.md`](planning/object-storage-provider-decision.md).

## Next

| ID | Initiative | Outcome | Owning epic or issue | Dependencies and boundary |
| --- | --- | --- | --- | --- |
| `RM-018` | LogicHost network operations and distribution | Complete LogicHost as an independently validated and released central multi-observatory product, including immutable CameraAgent execution-evidence import, protected observatory and logical-camera workspaces, signed LogicHost lifecycle distribution, and exact-digest optional two-host acceptance. | [Epic #531](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/531) | The standalone qualification gate (`RM-017`) is satisfied. #847 authorizes #538 importer work consuming delivered #537, independent #855 API/UI discovery for #539 separate from imported views, and independent #856 packaging preparation for #540 only. It consumes stable CameraAgent and shared contracts without reopening CameraAgent features or making LogicHost part of acquisition correctness. The final `RM-016` storage disposition is recorded below; #499 closes after its reviewed documentation closeout. That satisfies the storage release gate while #538–#541 retain their own implementation and exact-artifact acceptance. `RM-016` is not absorbed into this initiative. |

The deferred CameraAgent storage/upload naming cleanup
[#142](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/142) inherits
delivered `RM-004`. Its #433 vocabulary prerequisite is complete, but the
naming-only residual is not approved for automatic start.

Celestial-fidelity epic [#520](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/520)
inherits delivered `RM-001`. Its delivered foundation is retained history:
the bounded virtual astrometry and optical-calibration milestone
[#1098](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1098) (#1100-#1106),
completed child #522, #1088 and #1131. The
[scope, implementation order and acceptance map](planning/virtual-astrometry-qualification.md)
and the [qualified envelope and consumer handoff](astronomy/virtual-astrometry-qualification-v1.md#consumer-handoff)
remain authoritative for that delivered scope. On 2026-10-06 the operator limited
#520 to software and VirtualSky work that runs without physical camera hardware,
and moved physical qualification to the downstream epic
[#1166](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1166). Physical
camera and lens acceptance [#1107](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1107)
moved there from #523, and Raspberry Pi target qualification
[#1171](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1171) sits under it.
The operator then
[assigned the remaining software children](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/520#issuecomment-6025439060)
as an opt-in queue targeting `development/v1`: whole-pipeline performance
[#1170](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1170), projection
families [#1126](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1126),
measured associations [#526](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/526),
astrometry recipes and calculators [#523](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/523),
deeper HYG selections [#1167](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1167),
long exposures [#1168](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1168),
registered stacking [#1169](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1169),
Sun and Moon footprints [#518](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/518),
HYG 4.4 [#521](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/521), OpenNGC
[#525](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/525) and extended
deep-sky rendering [#524](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/524).
This activation keeps the technical dependencies (#526 before #523; #523 and
#1168 before #1169; #521 and #518 before #525, then #524), existing claims,
per-issue claims and the draft-first review cycle. It does not change unrelated
portfolio priority, promote `main`, deploy, or require GPU hardware. #1166, #1107
and #1171 keep their own hardware and scheduling gates. Software acceptance does
not wait for them, and #520 may close with #1166 open. Virtual acceptance does not
imply physical accuracy.

On 2026-10-13 the operator adopted the
[scope and merge audit](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/520#issuecomment-6141500980)
and [reduced the active delivery](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/520#issuecomment-6148994671)
to [#525](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/525): a curated
NGC/Messier selection, expected-position labels and basic approximate outlines
in annotated images, with measured millisecond-scale projection work. The merged
catalog/projection foundations and their safety contracts remain. New work starts
from `development/v1` and includes only the source identity, transforms, readers
and presentation needed by this delivery; the broad unfinished branches are not
its integration stack.

This decision supersedes the broader automatic queue and #520 closure criteria
above. Expanded catalog presentation and sourced contours remain in
[#1192](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1192) and #1191;
measured associations/publication (#526/#1187), calculators (#523), raw DSO pixels
(#524), full F7 (#1188), whole-pipeline/native/ARM work (#1170), deeper astrometry
(#1167), registered stacking (#1169) and uncertainty follow-up (#1179) remain
separate open RM-001 outcomes. They do not block the reduced #525/#520 delivery.
Their own technical dependencies and unresolved findings still apply when they
ship. In particular, #526 precedes #523 and #1191 remains a geometry dependency
of #524. Small annotation correctness and focused cost evidence are still
required; this split supplies no runtime qualification or completion credit for
the deferred features. Physical qualification remains separate.

## Future

| ID | Initiative | Outcome | Owning epic or issue | Dependencies and boundary |
| --- | --- | --- | --- | --- |
| `RM-006` | Certified processing extension platform | Add signed, versioned extension lifecycle and a bounded external-processing bridge without exposing host infrastructure or weakening reproducibility. | [#140](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/140) | Consumes layered producer/compositor/encoder contracts from `RM-004` and complements, but does not duplicate, `RM-005` runners. |
| `RM-007` | Native camera artifacts and pluggable formats | Preserve camera-native and proprietary source bytes, decode them into canonical processing frames, and produce versioned scientific, display, and export formats. | [#141](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/141) | Builds on certified extensions and layered composition. It does not identify demosaiced, corrected, or lossy data as raw evidence. |
| `RM-015` | Production multichannel notifications | Add production transactional email and SMS delivery, durable LogicHost in-app notifications, and optional bounded CameraAgent notifications. | [Epic #455](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/455) | Selects providers and CameraAgent delivery topology through an explicit decision checkpoint. Mailpit remains development/test-only and is excluded from official release installation; standalone CameraAgent correctness never depends on notifications, LogicHost, or an external provider. |
| `RM-019` | Remote and cloud object-storage profiles | Add independently qualified remote-filesystem, external S3, and native Azure Blob profiles behind LogicHost `IObjectStore` for larger LAN and cloud deployments. | [Epic #588](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/588); [#589](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/589)-[#591](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/591) | vNext after the `RM-016` application contract and local topology stabilize. It does not block `RM-016` or the first `RM-018` release. Each provider and topology requires independent security, recovery, backup, observability, and performance evidence; a same-host qualification does not certify NFS, SMB, NAS, or a remote service. |

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
| `RM-008` | Stream-native fireball detection V2 | Produce a reviewed design for buffered streams, temporal evidence, event promotion, clips/stills, and optional accelerated or external assessment. | [#143](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/143) | Broader V2 remains research. The bounded satellite software slice below is separately activated; no physical sensitivity claim is committed. |
| `RM-009` | Physical environmental source acquisition | Define CameraAgent-local adapters, schedules, provenance, health, and evidence for weather stations, sky-temperature sensors, SQM devices, and online providers. | [#158](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/158) | Broader provider selection remains research. The bounded #1398 space-weather source/fixture and approved local-notice software slice below is separately activated. LogicHost does not poll local devices or become part of acquisition correctness. |

### Bounded satellite and sky-context activation — 2026-10-19

The operator issued [Batch A's launch assignment](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1391#issuecomment-6274827157)
under [#1391](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1391).
This authorizes the listed software/virtual work and its ordinary
`development/v1` implementation, review, CI and merge lifecycle. It supersedes
the scheduling-only requirement to finish all #143 research, promote all
RM-008, or obtain optional fleet candidate-ready enrollment for this slice.
It does not resolve the technical decisions by itself.

- #527 remains research-only. Its reviewed source/model/policy/contract
  checkpoint, with explicit accepted and unresolved decisions, precedes
  #1402's minimal executable contracts and portable conformance fixtures.
  Remaining #527 research is retained rather than closed by an early handoff.
- After that approved checkpoint and #1402 delivery (or an explicitly
  coordinated stack), #528 acquisition and #529 propagation against frozen
  fixtures may proceed in parallel. #528 remains a live-integration dependency
  of #529; it is no longer a start gate on the offline implementation.
- #1392 consumes #528, and #1393 consumes #529. #530 retains actual
  snapshot/track and approved measured-geometry prerequisites. #1395 retains
  integrated, hardware/resolution-specific cadence and accuracy qualification.
  Configured-rig simulation does not imply accepted measured calibration.
- Batch A owns #527, #1402, #529, #1393, #530 and #1395 in that dependency-aware
  queue, claiming each current issue separately. Batch B owns its separately
  issued #1398 source/policy/fixture checkpoint and #528/#1392 queue; approved
  #1398 cache/local-notice software is bounded to its selected products.
  Neither lane creates a general feed or notification platform.
- Source/data/software terms, numerical reference tolerances, retained-input
  lineage, existing claims, #1133's hold, and physical/release gates still
  apply. #523 calibration and #1170 performance ownership do not transfer.
  Broader V2, other environmental adapters, main promotion, external messaging,
  automatic camera actions and live deployment are outside this activation.

The [orbital decision checkpoint](planning/orbital-data-decision-527.md)
records policies, contracts, reference inputs and remaining research. Cross-host
handoffs use remotely accessible immutable commits/artifacts and hashes, never
an assumption that a local POC directory exists on the other host.

### Bounded capture satellite and measured-astrometry UI priority — 2026-10-23

Roy's [planning ledger](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1391#issuecomment-6380982877)
and [explicit authorization](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1391#issuecomment-6381026928)
activate this exact bounded software/virtual sequence on `development/v1`:

1. [#1640](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1640): capture-bound satellite prediction recipe/output and this reviewed roadmap checkpoint.
2. After #1640, [#1641](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1641) capture satellite consumer and [#1642](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1642) measured-astrometry integration may proceed in parallel. A1 starts only after this checkpoint and the shared Processing registration collision are settled.
3. [#1643](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1643) follows **both** #1641 and #1642.
4. [#1644](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1644) follows #1643.

This supersedes the historical scheduling-only manual reservations on #523,
#529 and #1393 for these five issues. Existing claims and each issue's technical
acceptance still govern ownership and execution. The approved #527 checkpoint,
#1402 contracts and #528 acquisition are delivered prerequisites; the wider
#527 source/coverage/physical research remains research, and wider #526/#527/#530
work is not blanket-activated. Configured-rig predictions do not establish
accepted measured calibration, detection, physical visibility or sensitivity.
Deferred performance, native ARM, hardware/resolution qualification, broader V2,
main/release promotion, automatic calibration write-back, automatic camera
actions and external messaging retain their separate gates. This priority does
not change an installed rig, silently enable a shipped/default profile, or
authorize live deployment. The [#1641 source/schema/fixture handoff](contracts/capture-orbital-prediction-v1.md)
keeps host registration/publication, source-reference retention and UI in the
consumer slice.

## Deferred

| ID | Initiative | Retained outcome | Owning issue | Reason deferred |
| --- | --- | --- | --- | --- |
| `RM-010` | Production profiling and observability operations | Add bounded always-on signals and a secure escalation path to short .NET/Linux profiling with measured telemetry overhead. | [#244](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/244) | Existing evidence is sufficient for current delivery; broad profiling remains low priority unless an active defect requires a focused prerequisite. |
| `RM-012` | Physical camera soak and USB qualification | Produce sustained matched ARM64/x64 acquisition and multi-camera USB isolation evidence or a deterministic failing-layer diagnosis. | [#288](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/288) | Requires suitable physical hardware, stable power/cooling, and controlled USB topology. |
| `RM-013` | Native ARM64 CI evidence | Add advisory native Linux ARM64 build, publish, container, catalog, and VirtualSky smoke evidence before deciding whether it becomes required. | [#381](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/381) | Depends on available native runner capacity and remains advisory until measured evidence supports a required gate. |

Deferred validation follow-ups [#252](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/252)
and [#262](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/262)
inherit `RM-001`; they validate delivered boundaries and are not separate
portfolio initiatives.

## Delivered

| ID | Initiative | Delivered outcome | Owning epic or issues |
| --- | --- | --- | --- |
| `RM-016` | Provider-neutral LogicHost object storage | Delivered the provider-neutral `IObjectStore` boundary, host-neutral filesystem primitives, qualified one-writer native Linux same-host ext4 profile and supported filesystem deployment/fixture adoption without MinIO server/control-plane ownership. The [final qualification disposition](validation/logichost-filesystem-qualification-586.md#final-disposition) preserves exact measurement/image identities and topology limits. Optional #587 and future RM-019 profiles are separate. | [Epic #499](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/499); #584, #592, #585, #586 and #506 are closed. |
| `RM-001` | Virtual-first SkyMonitor platform | Standalone CameraAgent plus optional reconstructable LogicHost processing, weather/cloud/transient workflows, UI, recovery, and production-readiness foundations. | [Epic #89](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/89). Nested epics #59, #60, #62, #64, #65, #109, #205, and #243 inherit this ID. |
| `RM-002` | Deferred Phase 14 evidence campaign | Imported, executed, aggregated, and dispositioned exhaustive evidence without reopening virtual-first completion. | [#305](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/305); children #318-#322 inherit this ID. |
| `RM-003` | Installable and lifecycle-managed deployment | Delivered multi-instance-safe persistent layout, a self-contained installer, transactional upgrade/rollback/uninstall, and signed release/catalog distribution without adding physical-camera discovery or vendor SDK installation. | [#414](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/414), [#415](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/415), [#416](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/416), and [#417](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/417) |
| `RM-005` | Local-first processing graphs and distributed runners | Delivered CameraAgent edge processing graphs, central graph execution, processing-runner-v1 with the self-hosted runner, fair scheduling and entitlements, and elastic provider boundaries with the local-process adapter; the LogicHost-only test and heterogeneous-fleet follow-ups #632, #854, and #613 are dispositioned. | [Epic #421](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/421); children #422-#430 |
| `RM-004` | Canonical layered capture products | Delivered immutable base imagery, durable projected scenes and analytical layers, deterministic on-demand presentation, and explicit materialization across standalone CameraAgent and LogicHost. | [Epic #436](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/436); CameraAgent PR #454 and LogicHost PR #456 |
| `RM-011` | Direct ZWO camera enablement | Added the optional direct ZWO CameraAgent adapter and retained Linux x64 ASI178MC and ARM64 ASI676MC functional deployment evidence. | [#265](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/265), [#266](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/266), and [#267](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/267) |
| `RM-017` | Standalone CameraAgent product completion | Delivered the standalone-complete acceptance record: campaign `issue-211-final-20260922T033329Z` generation 1 on head `8388002ce69aa1fa194e7644bd8f35352ac10acf` with `acceptanceReady=true` (five W6 trials, Mono8, both W6 replay profiles, #197 dual-agent, four component lanes, exact-head protected CI, zero central traffic), assembled by the #961 tooling, validated by the #535 final-head validator, and published as an immutable owner-private generation. Epic #513 and milestone 2 are closed. | [Epic #513](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/513); [#535](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/535); [Standalone CameraAgent Product Completion milestone](https://github.com/HualapaiValley/HVO.SkyMonitor/milestone/2) |
| `RM-014` | CameraAgent presentation experience | Delivered an authenticated image-led current view, accessible presentation-first capture detail and enlargement, bounded archive browsing, and a separate retained technical operations workspace. | [Epic #438](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/438); design checkpoint PR #453 |

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
  +-> retained import #971: dependency-ready after delivered #981; import remains undone
  +-> closed follow-ups #973 and #974: delivered history
  +-> optional #587 CameraAgent adoption of the shared filesystem primitives

#847 approved independent starts
  +-> RM-005 #632, #854, #613: delivered; epic #421 closed (PR #975)
  +-> RM-016 #584 provider selection and durable identity
    +-> #592 host-neutral filesystem durability primitives
      +-> #585 LogicHost filesystem provider
        +-> #586 same-host Linux ext4 qualification
          +-> #506 adoption and MinIO removal: delivered
            +-> epic #499 final disposition: reviewed documentation closeout
  +-> RM-018 #538 importer work <- delivered #537 export contracts
  +-> RM-018 #855 independent API/UI discovery for #539, separate from imported views
  +-> RM-018 #856 independent packaging preparation for #540 only

Integration and release gates (not global start gates)
  #538 importer contracts and evidence -> #539 imported views integration
  #499 final storage disposition -> #540 storage release gate
    (#499 closes after reviewed closeout; RM-017 qualification is satisfied)
  Qualified standalone CameraAgent + LogicHost release artifacts
    -> RM-018 exact-digest combined two-host acceptance
  RM-016 stable application contract and local topology -> RM-019 future profiles

RM-003 deployment, RM-011 direct ZWO enablement, and RM-014 CameraAgent
presentation are delivered inputs to RM-017; RM-017 extends rather than
reopens them.

RM-009 environmental source research is independently plannable.
RM-010, RM-012, and RM-013 remain trigger- or capacity-dependent.
RM-015 follows the RM-003 deployment foundation but remains separate.
RM-016 is delivered. Its product scope is LogicHost and narrow host-neutral
filesystem mechanisms; later work retains the independent CameraAgent behavior,
state and storage contracts. Shared CI, architecture tests and combined fixtures
still require affected CameraAgent/combined regression evidence without becoming
active CameraAgent product dependencies.
Optional #587 is no longer gated; it may adopt the shared filesystem primitives
without replacing CameraAgent's storage contract when scheduled.
The final RM-016 disposition satisfies #540's storage gate when #499 closes;
#856 packaging preparation was already independent. RM-019 does not block that
release. Research and Deferred work retain their separate activation gates.
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
