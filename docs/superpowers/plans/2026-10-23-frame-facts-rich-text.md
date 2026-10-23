# Frame Facts rich-text implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver #1552's explicitly selected, reproducible four-corner Frame Facts layer while preserving historical bytes and saved defaults.

**Architecture:** Processing owns the versioned frozen token snapshot, descriptors, validation and producer. Imaging owns bounded rich-text primitives and a single packaged-font path layout shared by SVG and raster. CameraAgent adds a non-auto-included versioned alias and captures structured retained inputs through the existing facts builder; LogicHost consumes the same canonical retained products through its existing parser/rendering seams. No editor or automatic conversion ships in this slice.

**Tech Stack:** .NET SDK 10.0.402, C#, MSTest, SkiaSharp, existing canonical JSON and graph option codecs.

Execution is inline as explicitly requested by the parent. No worktrees, subagents, pushes or PR operations.

### Task 1: Preserve and define acceptance

Files: `tests/HVO.SkyMonitor.Processing.Tests/FrameFactsPresentationTests.cs`, `tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Processing/PresentationMetadataFactsBuilderTests.cs`.

- [ ] Add tests using frozen descriptors and existing fixtures. Pin the legacy metadata producer, default schema, canonical payload and rendered checksum. Exercise all four corners and all eight emphasis combinations on Mono regular/bold; reject bold Plex because only Plex regular is packaged.
- [ ] Test snapshot canonical round trips, identities after fact/provenance/template/style/layout changes, malformed snapshots, source/line/output limits, placeholder/drop-line and empty/blank lines.
- [ ] Test the new alias is opt-in, emits the new schema, and leaves raw bytes and default registrations unchanged. Test identical retained bytes through the CameraAgent renderer and shared LogicHost renderer seam.

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

- [ ] Serialize one ordered scalar entry per retained catalogue key, including availability, typed value and provenance. Rehydrate only through existing `FrameFactsValue`/`FrameFactsTokenContext`; bound counts, scalar lengths and provenance; reject missing/duplicate/unknown keys or mismatched value kinds. Hash the entire canonical snapshot.
- [ ] Add an omitted-when-null snapshot field and explicit new metadata facts schema; keep old schema constants/serialization unchanged. Select producer identity by schema, validate both versions and retain strict canonical parsing.
- [ ] Add flat per-corner template/font/size/RGB/missing-policy options described by the existing descriptor API. Validate grammar, numeric bounds and unsupported Plex bold combinations server-side. Parse/evaluate through the existing evaluator only.

Representative integration:
```csharp
var context = FrameFactsRetainedContext.Create(retainedInput);
var snapshot = FrameFactsSnapshot.Create(context);
var evaluated = FrameFactsEvaluator.Evaluate(parsed.Template!, snapshot.ToContext(), missingPolicy);
```

### Task 3: Shared bounded rich-text payload and geometry

Files: create `src/HVO.SkyMonitor.Imaging/PresentationRichText.cs`; modify `PresentationFont.cs`, `PresentationLayerPayload.cs`, Processing `PresentationLayerPayloadJson.cs` and `GroupedSvgPresentationRenderer.cs`.

- [ ] Add optional rich blocks gated on a new explicit payload schema; old creators retain v3. Deep-freeze nested runs and enforce four unique corners, eight lines, 64 Unicode scalars, legal styles, pinned faces and size/inset/spacing limits.
- [ ] Lay out glyph paths using pinned fonts only; use MonoBold for bold Mono, fixed synthetic italic shear and underline rectangles. Use font advances rather than per-run ink normalization so style boundaries retain spacing. Reject overlapping/out-of-frame corner panels instead of clipping or truncating.
- [ ] Render the same returned paths and dark panel rectangles into raster and SVG. Include rich primitives/text/geometry in admission budgets before full-frame allocation. Bind producer, grammar, catalogue, snapshot, source, font checksums and layout version in retained payload identity.

### Task 4: Opt-in CameraAgent host seam

Files: create `src/HVO.SkyMonitor.CameraAgent.Common/Capture/Processing/FrameFactsPresentationCaptureProcessingStep.cs`; modify `PresentationMetadataFactsBuilder.cs`, `DependencyInjection/CameraAgentServiceCollectionExtensions.cs`, `LayeredPresentationCaptureProcessingSteps.cs`.

- [ ] Register only `FrameFactsPresentationLayerV1`, `AutoInclude: false`. Require the same explicit scene/stack dependencies and preserve the old recipe identity path.
- [ ] Extend the existing facts builder with an opt-in switch. Copy selected structured environmental facts and association status, descriptor, retained source timestamp/cadence and stack lineage into the shared context; leave unavailable optional location/local-time/completion facts unavailable.
- [ ] Allow explicit new schemas at manifest/materializer dependencies without changing default variants or existing graph JSON. Preserve pipeline optional-node failure handling.

### Task 5: Evidence and candidate gate

Files: `CHANGELOG.md`, `docs/contracts/frame-facts-presentation-v1.md`, test inventories and `docs/runbooks/ci-pipeline.md` as required.

- [ ] Record operator-visible opt-in capability under Unreleased ending `#1552`. Document aliases, schemas, unsupported combinations, geometry and source retention.
- [ ] Measure nearest legacy versus rich path at W1 1936x1216 and W6 3552x3552, five warmups and 30 measured operations each, concurrency 1. Record wall median/p95, CPU, allocations, peak working set, canonical sizes/checksums and full-frame buffer ownership. No I/O or queues are introduced; backlog is zero.
- [ ] Commit intended implementation, run `scripts/ci:classify --help` then classify exact `523d43d9..HEAD`; record output for development-v1 profile.
- [ ] Run the complete Tier C gate in AGENTS.md once on the stable candidate: tool restore, solution restore, Debug/Release warning-clean builds, format verification, package audit, four Bash 5 CI guards, Docker-disabled Unit plus separate Integration selection, and classifier-selected affected boundaries. All heavy commands use the host flock/timeout and no shared compilation/node reuse.
- [ ] Update this checklist and return SHA, exact files, all commands/results, performance and limitations to the parent. Parent owns all PR/review/finalization actions.
