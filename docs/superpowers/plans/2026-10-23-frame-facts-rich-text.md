# Frame Facts rich-text implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Deliver #1552's explicitly selected, reproducible four-corner Frame Facts layer while preserving historical bytes and saved defaults.

**Architecture:** Processing owns the versioned frozen token snapshot, descriptors, validation and producer. Imaging owns bounded rich-text primitives and a single packaged-font path layout shared by SVG and raster. CameraAgent adds a non-auto-included versioned alias and captures structured retained inputs through the existing facts builder; LogicHost consumes the same canonical retained products through its existing parser/rendering seams. No editor or automatic conversion ships in this slice.

**Tech Stack:** .NET SDK 10.0.402, C#, MSTest, SkiaSharp, existing canonical JSON and graph option codecs.

Execution is inline as explicitly requested by the parent. No worktrees, subagents, pushes or PR operations.

### Task 1: Preserve and define acceptance

Files: `tests/HVO.SkyMonitor.Processing.Tests/FrameFactsPresentationTests.cs`, `tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Processing/PresentationMetadataFactsBuilderTests.cs`.

- [x] Add tests using frozen descriptors and existing fixtures. Pin the legacy metadata producer, default schema, canonical payload and rendered checksum. Exercise all four corners and all eight emphasis combinations on Mono regular/bold; reject bold Plex because only Plex regular is packaged.
- [x] Test snapshot canonical round trips, identities after fact/provenance/template/style/layout changes, malformed snapshots, source/line/output limits, placeholder/drop-line and empty/blank lines.
- [x] Test the new alias is opt-in, emits the new schema, and leaves raw bytes and default registrations unchanged. Test identical retained bytes through the CameraAgent renderer and shared LogicHost renderer seam.

Representative assertion:
```csharp
var payload = FrameFactsPresentation.Create(facts, options);
Assert.AreEqual(PresentationLayerPayloadV1.RichTextSchemaVersion, payload.SchemaVersion);
CollectionAssert.AreEqual(PresentationLayerPayloadJson.Serialize(payload),
    PresentationLayerPayloadJson.Serialize(FrameFactsPresentation.Create(facts, options)));
```

Run focused tests under `flock -o /tmp/hvo-heavy-gate.lock timeout 600 dotnet test` with `-nodeReuse:false -p:UseSharedCompilation=false`; expect missing API failures before implementation, then passing assertions.

### Task 2: Frozen retained facts and option validation

Files: create `src/HVO.SkyMonitor.Processing/FrameFactsSnapshot.cs`, `FrameFactsPresentation.cs`, `FrameFactsPresentationOptions.cs`; modify `PresentationLayerProducers.cs`, `PresentationProcessingProducts.cs`, `ProcessingOptionDescriptors.cs`.

- [x] Serialize one ordered scalar entry per retained catalogue key, including availability, typed value and provenance. Rehydrate only through existing `FrameFactsValue`/`FrameFactsTokenContext`; bound counts, scalar lengths and provenance; reject missing/duplicate/unknown keys or mismatched value kinds. Hash the entire canonical snapshot.
- [x] Add an omitted-when-null snapshot field and explicit new metadata facts schema; keep old schema constants/serialization unchanged. Select producer identity by schema, validate both versions and retain strict canonical parsing.
- [x] Add flat per-corner template/font/size/RGB/missing-policy options described by the existing descriptor API. Validate grammar, numeric bounds and unsupported Plex bold combinations server-side. Parse/evaluate through the existing evaluator only.

Representative integration:
```csharp
var context = FrameFactsRetainedContext.Create(retainedInput);
var snapshot = FrameFactsSnapshot.Create(context);
var evaluated = FrameFactsEvaluator.Evaluate(parsed.Template!, snapshot.ToContext(), missingPolicy);
```

### Task 3: Shared bounded rich-text payload and geometry

Files: create `src/HVO.SkyMonitor.Imaging/PresentationRichText.cs`; modify `PresentationFont.cs`, `PresentationLayerPayload.cs`, Processing `PresentationLayerPayloadJson.cs` and `GroupedSvgPresentationRenderer.cs`.

- [x] Add optional rich blocks gated on a new explicit payload schema; old creators retain v3. Deep-freeze nested runs and enforce four unique corners, eight lines, 64 Unicode scalars, legal styles, pinned faces and size/inset/spacing limits.
- [x] Lay out glyph paths using pinned fonts only; use MonoBold for bold Mono, fixed synthetic italic shear and underline rectangles. Use font advances rather than per-run ink normalization so style boundaries retain spacing. Reject overlapping/out-of-frame corner panels instead of clipping or truncating.
- [x] Render the same returned paths and dark panel rectangles into raster and SVG. Include rich primitives/text/geometry in admission budgets before full-frame allocation. Bind producer, grammar, catalogue, snapshot, source, font checksums and layout version in retained payload identity.

### Task 4: Opt-in CameraAgent host seam

Files: create `src/HVO.SkyMonitor.CameraAgent.Common/Capture/Processing/FrameFactsPresentationCaptureProcessingStep.cs`; modify `PresentationMetadataFactsBuilder.cs`, `DependencyInjection/CameraAgentServiceCollectionExtensions.cs`, `LayeredPresentationCaptureProcessingSteps.cs`.

- [x] Register only `FrameFactsPresentationLayerV1`, `AutoInclude: false`. Require the same explicit scene/stack dependencies and preserve the old recipe identity path.
- [x] Extend the existing facts builder with an opt-in switch. Copy selected structured environmental facts and association status, descriptor, retained source timestamp/cadence and stack lineage into the shared context; leave unavailable optional location/local-time/completion facts unavailable.
- [x] Admit new schemas only when manifest/materializer consumers explicitly set `useRichFrameFacts: true`; omit the false default and preserve exact legacy dependency sets, serialized effective options and graph identity. Preserve pipeline optional-node failure handling.

### Task 5: Evidence and candidate gate

Files: `CHANGELOG.md`, `docs/contracts/frame-facts-presentation-v1.md`, test inventories and `docs/runbooks/ci-pipeline.md` as required.

- [x] Record operator-visible opt-in capability under Unreleased ending `#1552`. Document aliases, schemas, unsupported combinations, geometry and source retention.
- [x] Measure nearest legacy versus rich path at W1 1936x1216 and W6 3552x3552, five warmups and 30 measured operations each, concurrency 1. Record wall median/p95, CPU, allocations, peak working set, canonical sizes/checksums and full-frame buffer ownership. No I/O or queues are introduced; backlog is zero.
- [x] Commit intended implementation, run `scripts/ci:classify --help` then classify exact `523d43d9..HEAD`; record output for development-v1 profile.
- [x] Run the complete Tier C gate in AGENTS.md once on the stable candidate: tool restore, solution restore, Debug/Release warning-clean builds, format verification, package audit, four Bash 5 CI guards, Docker-disabled Unit plus separate Integration selection, and classifier-selected affected boundaries. All heavy commands use the host flock/timeout and no shared compilation/node reuse.
- [x] Update this checklist and return SHA, exact files, all commands/results, performance and limitations to the parent. Parent owns all PR/review/finalization actions.

### Compatibility correction and evidence status

The initial candidate `46d7f896` broadened default consumer schema requirements and changed the pinned W6 effective graph identity. The requirements participate in graph hashing. Correction `0ec4adda` adds an omitted-when-false consumer opt-in and restores the original default schema sets. The unchanged W6 pin passes in the 24-case focused selection; the registration reproducer passes separately with the intentionally added alias excluded from automatic and existing graph selection.

The initial full Unit attempt ran all 13,370 cases: 11,685 passed, 146 failed, 1,539 skipped. The two implementation failures have passing reproducers and are absent from the corrected full affected selection. Corrected CameraAgent Unit is 6,900 passed / 7 failed / 65 skipped; exact base is 6,895 / 7 / 65 with the identical seven-case failure set, including both focus failures under load. The five new host cases pass. The other four affected Unit roots pass all 114 cases. All 144 remaining original Unit failures have exact-base reproduction evidence; no original strict golden or unrelated assertion was changed.

Updated Debug, Release, format and all four control guards pass. The complete Integration attempt is 924 passed / 15 failed / 0 skipped across 939 cases. The authorized Acceptance-only rerun uses a short canonical temp root to resolve macOS's 104-character Unix socket limit: 18 passed / 1 failed. Combining that replacement with the unchanged passing boundary reports yields 932 passed / 7 failed. Exact base reproduces all six central failures (four missing `setsid`, HTTP 503 health, notification-dispatch timeout) and the remaining unsupported owner-recovery Acceptance case. Candidate and base short-temp runs observed 18 and one fixture directories respectively; fixture disposal removed all observed directories. SQL/Redis/Mailpit test containers were disposed, and final Docker inventory is empty.

The exact 23-slot canonical coverage merge and all five selected component merges complete. CameraAgent, LogicHost and combined floors pass; shared, delivery and aggregate floors fail. Aggregate coverage is 83.6286% line / 70.0770% branch, below the 84.3590% line floor. The recorded failures identify Linux storage and delivery paths; no threshold was changed. Per-file changed-source coverage is retained, including the new host step's 57.32% line / 57.69% branch coverage and the new shared producer/snapshot/layout files' 94–100% line coverage. Tier C is **not green**. The parent owns platform disposition and independent review.

The correction classifier selects CameraAgent and combined. Its invalidated evidence was rerun: both solution builds, format, full affected host/combined Unit selections, Integration and coverage. Shared, LogicHost and delivery Unit evidence is retained with exact-base failure comparisons. The benchmark's Processing/Imaging sources and harness are byte-identical across the correction; its cost evidence is retained. Full commands, exact selector output, source archive manifests, failures, resource observations and cost records are in the issue's registered scratch evidence. No push, PR operation, claim change, deployment or unrelated source fix was performed.
