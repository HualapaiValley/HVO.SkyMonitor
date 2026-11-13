# Descriptor-exact frozen satellite host conformance — #529

This is the bounded in-process software slice assigned by Roy on 2026-11-13,
amended by the [coordinator disposition](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/529#issuecomment-6863628450)
and [same-owner acknowledgement](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/529#issuecomment-6863671944).
The retained owner is `codex:cliproxy:home-dev-02:a7186bdb`, actual
Codex/CLIProxy `gpt-6.1-sol`, xhigh. Fast was authorized; transport tier is not
independently attested. Branch `t3/satellite-host-adapter-parity`, isolated
worktree `/home/roys/.t3/worktrees/HVO.SkyMonitor/t3-ddd27ea6-45a1bdab0e07e61e`,
base `b961cd6a4dca75bba606cd4b66674983d3312320`, target `development/v1`.
Fresh claim/PR/T3/capacity reads confirmed the same sole claim, no competing
adapter implementation PR, only this active local T3 thread, 12 CPUs,
approximately 18 GiB available RAM and no running containers. Other worktrees,
#526 scientific custody, retained UI owners and hvo-dev03/M5 review capacity
were preserved. #1764 owns the shared stale Unicode fixture correction.

## Corrected interpretation and implementation

The original reproducer, raw TRXs and blocked packet remain immutable at
`25847588c1800296b2db65562384d32e3e793ee8`, archive SHA-256
`b4a2d81623e4ba03ffe9864948bb861b60cc23f8954cfa7d02fce5d176cd75cc`.
That experiment called the generic edge factory alone. The shipped
`CaptureSatelliteTracksProcessingStep.ProcessAsync` already restored descriptor
media type, artifact creation UTC and exact exposure bounds afterwards. Thus its
generic-factory failure did **not** establish a shipped satellite defect. The
generic LogicHost factory's observation-start creation time is also intentional
for generic ordering/age semantics; it must not be changed globally.

The step's existing binding is extracted unchanged into its internal static
`CreateSource`. Both the shipped step and conformance corpus call that method.
Tests deliberately supply an original frame timestamp different from descriptor
artifact creation, and apply no test-only metadata overrides after binding.

`LogicHostRecipeExecutionAdapter.ExecuteDescriptorExactAsync` is an explicit
opt-in for an already supplied capture-bound recipe. It uses the existing
descriptor/payload reconstruction validation before dispatch, then retains the
descriptor's creation time and exact observed exposure interval. It forwards
caller cancellation. Generic `ExecuteAsync`, `CreateRequest` and `CreateArtifact`
are unchanged; no recipe-name special case, schema, registration, source
acquisition, persistent state or central deployed satellite feature is introduced.
The same opt-in also executes a non-orbital built-in recipe in regression tests.
This internal seam/refactoring has no operator-visible default change and needs
no operator changelog entry.

## Original-source requirement map

Sources: [#529 original issue and all discussion](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/529),
the [remaining-scope clarification](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/529#issuecomment-6298543561),
Roy's exact original and resumed Hub assignments (retained in the external
packet), the coordinator disposition above, accepted
[#1640](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1640)/[PR #1645](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1645)
and [#1641](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1641)/[PR #1649](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1649),
the [capture contract](../contracts/capture-orbital-prediction-v1.md),
[orbital contract](../contracts/orbital-elements-v1.md), and
[#526 shared-adapter conditions](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/526#issuecomment-6037593368).
The packet retains source snapshots and read times, not just this mapping.

All new methods are in `HostRecipeContractConformanceTests.Orbital.cs`, linked
into the standalone LogicHost project as well as compiled in the combined host
project. The unchanged original fixture source is linked, without a test-project
reference or duplicate fixture schema. The [original hash inventory](issue-529-host-adapter-parity-fixtures.sha256)
retains the twelve input/golden files, fixture source and provenance/licenses.
Expected bytes were never regenerated from the candidate.

| ID / required outcome | Implementation and behavioral evidence |
| --- | --- |
| R1 — reuse exact ISS/two-Starlink data, snapshots/records, actual EOP/leap bytes, configured rig/readout, descriptor/raw, policy and expected products | `FrozenOrbitalControlReproducesCommittedGolden` compares all three existing complete wrappers and embedded tracks byte for byte; source/fixture SHA inventory verifies unchanged input bytes and provenance. |
| R2 — independent restored plans and real host bindings | `FrozenOrbitalHostsReproduceGoldenAndRestoredPlan` constructs fresh fixture/snapshot/time, restored input, recipe and executor for every host and repeat. The edge uses the shipped `CreateSource`; central receives descriptor/payload through the explicit validated entry. The recording executor asserts capture, sequence, artifact, descriptor, role, variant, media, creation, raw recipe, layout/readout, observed bounds, integration and checksum before recipe execution. |
| R3 — exact payload, checksum/content/output/recipe identities, schema/media/kind and ordered lineage; truthful geometry | `AssertOrbitalEnvelope`, `AssertOrbitalEquivalentProducts` and `AssertOrbitalGolden` bind those facts, algorithm order, compatibility, input/selection/snapshot/record/model identities and availability. Exact golden tracks retain Unknown physical visibility/illumination and null unsupported orbit/clock/calibration uncertainty; metadata is not detection. |
| R4 — repeat frozen plans and distinguish OriginalAvailability/RetrospectiveReassessment | Six named ISS/Starlink/mode rows run each host twice with separate restorations. Originally available wrappers match the committed goldens. Retrospective tracks retain the same approved track bytes while wrapper/recipe identities and labeled cutoffs differ. `FrozenOrbitalAvailabilityAndPlanSubstitutionAreRejected` verifies those differences and rejects later snapshot/time data as originally available; explicit retrospective execution retains the actual later snapshot/cutoffs. |
| R5 — bounded unavailable/missing, mismatch, cancellation and negative controls | `FrozenOrbitalUnavailableInputsRetainBoundedNoResult` covers NoActiveSnapshot, StorageUnavailable and missing object, retaining exact reasons/Unknown population rather than fabricated tracks. `FrozenOrbitalMismatchedBindingsNeverProduce` covers invalid and different captures, descriptor drift, exposure drift, payload corruption and invalid options, with exact accepted reason codes and zero products. Plan/snapshot/observer substitution is rejected. `FrozenOrbitalCallerCancellationNeverProduces` covers pre-cancel and cancellation inside engine work for every host, requiring no returned outcome. `FrozenOrbitalParityAssertionsDetectEnvelopeMutation` rejects source-lineage/output/recipe mutations; the external production-code mutation control deliberately breaks exact central creation-time binding and requires the six nominal host cases to fail. |
| R6 — bounded software acceptance and parent scope | This evidence supplies frozen in-process host-adapter reproduction only. #529 remains OPEN for representative full-population resource acceptance, wider qualified physical/illumination/uncertainty claims, and separately owned #1395 integrated cadence. No deployed two-service adoption, registration, full-scale performance or physical accuracy is claimed. |
| R7 — preserve generic and #526 foundation semantics | `FrozenOrbitalGenericFactoriesRetainTheirExistingSemantics` retains the original generic rejection/creation/media behavior. `DescriptorExactOptInPreservesGenericCreationAndInstantaneousObservationSemantics` verifies observation-start generic creation and effective-exposure expansion, alongside descriptor-exact opt-in behavior. `FrozenOrbitalExactBindingsSurviveRunnerProjection` preserves capture/descriptor/media/time through the actual runner codec and reproduces the same result with a fresh executor. Existing rolling-window, time-lapse, descriptor-drift, identity-neutrality, measured-association, annotation and runner suites are separately recorded regressions; #526 scientific work is not changed. |

Normal, negative, unavailable/failure, cancellation, repeatability and cross-host
interactions have explicit cases above. Durable publication, crash recovery and
reference retention are N/A to this in-process seam and stay with accepted #1641
and its owners. Physical accuracy, full population/cadence and hardware operation
are outside the authorized slice. No unchanged numerical/population campaign or
new performance qualification is asserted. The central opt-in adds a record and
single-element array; retained raw memory is passed through without a pixel copy.

## Validation and immutable evidence

The corrected inner loop passed 28/28 conformance cases in the combined lane,
then 44/44 combined conformance/foundation regressions and 42/42 standalone
conformance/adapter/time-lapse regressions, with no skips. The deliberate central
creation-time mutation built successfully and failed all six nominal rows;
source restoration matched its original SHA-256 before the passing rebuilds.
These are focused behavioral results, not substitutes for the final full gate.

The external packet names the immutable candidate SHA, exact classifier output,
commands/exits/durations, actual inventories, all TRXs/skips, canonical coverage
slot/attachment/consumption manifests and native gate results, source and evidence
archive hashes, owner completeness verdict and residual blockers. It retains
original failures and corrected invocations, including an initial runner-codec
compile error and the following stale-binary test invocation, which is explicitly
invalidated and is not credited as a candidate pass.

Assigned classification remains **Tier C / HIGH / deep**. `scripts/ci:classify`
selects the affected lanes on the exact base..candidate range. Complete local
Tier C evidence includes tools/restore, Debug/Release warning-clean builds,
whole-solution formatting, package audit, all four control guards, actual category
discovery, Docker-disabled complete Unit and separate real-Docker Integration
selections, canonical coverage, and applicable architecture/publish/migration
gates. Per-project invocations reproduce the complete positive selections and
canonical result slots from `ci.yml`; no missing, skipped or stale evidence is
called passed. Target hosted profile remains **Development v1 / Preflight** plus
**Development v1 / Build and Unit**; no full workflow is dispatched.

Reproduce the focused corpus with a Release build of each affected test project,
then `dotnet test <project> --no-build --configuration Release --filter
"FullyQualifiedName~FrozenOrbital|FullyQualifiedName~DescriptorExactOptIn"`.
The combined project executes both hosts; standalone LogicHost executes the same
central corpus. The full evidence packet retains original-source completeness
and concrete assertions, rather than treating a common-executor call as parity.

Independent Sol **and** Opus deep CLEAN reports on that same candidate remain
required. Max routes both through the Hub; no local or duplicate reviewer is
launched. No push, PR, infrastructure, deployment, hardware or branch deletion
is authorized. The retained #529 claim continues through the review handoff.
