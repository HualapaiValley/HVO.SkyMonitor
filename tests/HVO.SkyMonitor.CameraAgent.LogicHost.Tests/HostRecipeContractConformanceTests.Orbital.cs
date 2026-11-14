using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
#if COMBINED_TESTS
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
#endif
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
#if COMBINED_TESTS
using HVO.SkyMonitor.TestSupport;
#endif

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

public sealed partial class HostRecipeContractConformanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("iss")]
    [DataRow("starlink-56838")]
    [DataRow("starlink-65283")]
    public async Task FrozenOrbitalControlReproducesCommittedGolden(string name)
    {
        var item = PrepareOrbital(name);
        var result = await item.Executor.ExecuteAsync(item.Fixture.Request() with
        { InputArtifactId = item.Fixture.Raw.ArtifactId }, TestContext.CancellationToken);
        await AssertOrbitalGolden(item, result);
    }

    [TestMethod]
    [DataRow("iss", CaptureOrbitalAssessmentMode.OriginalAvailability)]
    [DataRow("starlink-56838", CaptureOrbitalAssessmentMode.OriginalAvailability)]
    [DataRow("starlink-65283", CaptureOrbitalAssessmentMode.OriginalAvailability)]
    [DataRow("iss", CaptureOrbitalAssessmentMode.RetrospectiveReassessment)]
    [DataRow("starlink-56838", CaptureOrbitalAssessmentMode.RetrospectiveReassessment)]
    [DataRow("starlink-65283", CaptureOrbitalAssessmentMode.RetrospectiveReassessment)]
    public async Task FrozenOrbitalHostsReproduceGoldenAndRestoredPlan(string name, CaptureOrbitalAssessmentMode mode)
    {
        ProcessingProduct? baseline = null;
        CaptureOrbitalPredictionInput? priorInput = null;
        foreach (var edge in OrbitalHosts)
        {
            for (var repeat = 0; repeat < 2; repeat++)
            {
                // Every host and replay gets its own snapshot/time materialization, restored plan, recipe and executor.
                var item = PrepareOrbital(name, mode);
                Assert.AreNotSame(priorInput, item.Input);
                priorInput = item.Input;
                var result = await ExecuteOrbital(item, edge);
                await AssertOrbitalGolden(item, result);
                var product = Assert.ContainsSingle(result.Products);
                if (baseline is not null) AssertOrbitalEquivalentProducts(baseline, product);
                baseline = product;
            }
        }
    }

    [TestMethod]
    public async Task FrozenOrbitalGenericFactoriesRetainTheirExistingSemantics()
    {
        var item = PrepareOrbital(assertBindings: false);
        var request = item.Fixture.Request();
        var generic = await new LogicHostRecipeExecutionAdapter(item.Executor).ExecuteAsync(
            item.Fixture.Descriptor, item.Fixture.Raw.Payload, request.RecipeName, request.Options,
            request.Input, request.OutputVariant, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, generic.Status);
        Assert.AreEqual("capture-orbital-input-mismatch", generic.ReasonCode);
        Assert.HasCount(0, generic.Products);
        Assert.AreEqual(item.Fixture.Descriptor.Timing.ExposureStartedUtc, item.Executor.Request!.Inputs[0].CreatedUtc);
#if COMBINED_TESTS
        var edgeItem = PrepareOrbital(assertBindings: false);
        var raw = CreateOrbitalEdgeSource(edgeItem.Fixture, edgeItem.Fixture.Descriptor, edgeItem.Fixture.Raw.Payload, exact: false);
        Assert.AreEqual("application/x-hvo-frame", raw.MediaType);
        var edge = await new CameraAgentRecipeExecutionAdapter(edgeItem.Executor)
            .ExecuteAsync(edgeItem.Fixture.Request(raw), TestContext.CancellationToken);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, edge.Status);
        Assert.AreEqual("capture-orbital-input-mismatch", edge.ReasonCode);
        Assert.HasCount(0, edge.Products);
#endif
    }

    [TestMethod]
    [DataRow("NoActiveSnapshot")]
    [DataRow("StorageUnavailable")]
    [DataRow("ObjectMissing")]
    public async Task FrozenOrbitalUnavailableInputsRetainBoundedNoResult(string unavailable)
    {
        ProcessingProduct? baseline = null;
        foreach (var edge in OrbitalHosts)
        {
            var reason = unavailable == "ObjectMissing" ? (OrbitalSnapshotUnavailableReason?)null
                : Enum.Parse<OrbitalSnapshotUnavailableReason>(unavailable);
            var item = PrepareOrbital(unavailable: reason, missingObject: reason is null);
            var result = await ExecuteOrbital(item, edge);
            var product = AssertOrbitalEnvelope(item, result);
            var output = CaptureOrbitalPredictionJson.Parse(product.Payload.Span, item.Input);
            Assert.AreEqual(OrbitalExecutionStatus.Unavailable, output.Status);
            Assert.HasCount(0, output.Tracks);
            Assert.AreEqual(reason, output.Input.SnapshotUnavailableReason);
            Assert.AreEqual("Unknown", output.PhysicalVisibility);
            if (reason is null)
            {
                Assert.AreEqual(OrbitalPredictionReason.ObjectMissing, output.Reason);
                Assert.AreEqual(OrbitalPredictionReason.ObjectMissing, Assert.ContainsSingle(output.Failures).Reason);
                Assert.AreEqual(OrbitalSelectionEligibility.MissingObject, Assert.ContainsSingle(output.Input.Selections).Eligibility);
            }
            else
            {
                Assert.AreEqual(OrbitalPredictionReason.SelectionWithheld, output.Reason);
                Assert.AreEqual(OrbitalPopulationCompleteness.Unknown, output.Input.PopulationCoverage.Completeness);
                Assert.AreEqual(0, output.PropagationEvaluations);
                Assert.HasCount(0, output.Input.Selections);
            }
            if (baseline is not null) AssertOrbitalEquivalentProducts(baseline, product);
            baseline = product;
        }
    }

    [TestMethod]
    [DataRow("invalid-capture")]
    [DataRow("capture")]
    [DataRow("descriptor")]
    [DataRow("observation")]
    [DataRow("payload")]
    [DataRow("options")]
    public async Task FrozenOrbitalMismatchedBindingsNeverProduce(string mismatch)
    {
        foreach (var edge in OrbitalHosts)
        {
            var item = PrepareOrbital(assertBindings: false);
            var descriptor = item.Fixture.Descriptor;
            var payload = item.Fixture.Raw.Payload;
            JsonElement? options = null;
            switch (mismatch)
            {
                case "invalid-capture": descriptor = descriptor with { Capture = descriptor.Capture with { CaptureId = Guid.Empty } }; break;
                case "capture":
                    descriptor = descriptor with
                    {
                        Capture = descriptor.Capture with
                        { CaptureId = Guid.Parse("52900000-0000-0000-0000-000000000002") }
                    }; break;
                case "descriptor":
                    descriptor = descriptor with
                    {
                        Profiles = descriptor.Profiles with
                        { Processing = descriptor.Profiles.Processing with { Sha256 = new('D', 64) } }
                    }; break;
                case "observation":
                    descriptor = descriptor with
                    {
                        Timing = descriptor.Timing with
                        { ExposureStartedUtc = descriptor.Timing.ExposureStartedUtc.AddTicks(1) }
                    }; break;
                case "payload": var bytes = payload.ToArray(); bytes[0] ^= 1; payload = bytes; break;
                case "options": options = JsonSerializer.SerializeToElement(new { currentSnapshot = true }); break;
            }
            var outcome = await ExecuteOrbital(item, edge, descriptor, payload, options);
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status, $"{edge}/{mismatch}");
            Assert.HasCount(0, outcome.Products);
            var expectedReason = mismatch == "options" ? ProcessingReasonCodes.InvalidOptions
                : !edge && mismatch is "invalid-capture" or "payload" ? ProcessingReasonCodes.InvalidInput
                : "capture-orbital-input-mismatch";
            Assert.AreEqual(expectedReason, outcome.ReasonCode, $"{edge}/{mismatch}");
            if (!edge && mismatch is "invalid-capture" or "payload") Assert.IsNull(item.Executor.Request,
                "Descriptor/payload validation must precede dispatch on the exact reconstruction path.");
        }
    }

    [TestMethod]
    public async Task FrozenOrbitalExactBindingsSurviveRunnerProjection()
    {
        var item = PrepareOrbital();
        var product = AssertOrbitalEnvelope(item, await ExecuteOrbital(item, edge: false));
        var source = Assert.ContainsSingle(item.Executor.Request!.Inputs);
        var metadata = ProcessingRunnerProjection.ProjectArtifact(source,
            Guid.Parse("52900000-0000-0000-0000-000000000003"), "/api/artifacts/raw", source.Payload.Length,
            ProcessingRunnerProtocol.ComputeSha256(source.Payload.Span));
        var restored = ProcessingRunnerProjection.DeserializeMetadata<ProcessingRunnerArtifactMetadata>(
            ProcessingRunnerProjection.SerializeMetadata(metadata));
        var rebuilt = ProcessingRunnerProjection.ReconstructArtifact(restored, source.Payload);
        Assert.AreEqual(source.CaptureId, rebuilt.CaptureId);
        Assert.AreEqual(source.DescriptorIdentitySha256, rebuilt.DescriptorIdentitySha256);
        Assert.AreEqual(source.CreatedUtc, rebuilt.CreatedUtc);
        Assert.AreEqual(source.MediaType, rebuilt.MediaType);
        Assert.AreEqual(source.ObservationStartedUtc, rebuilt.ObservationStartedUtc);
        Assert.AreEqual(source.ObservationEndedUtc, rebuilt.ObservationEndedUtc);
        var independent = PrepareOrbital();
        var result = await independent.Executor.ExecuteAsync(item.Executor.Request with { Inputs = [rebuilt] }, TestContext.CancellationToken);
        AssertOrbitalEquivalentProducts(product, AssertOrbitalEnvelope(independent, result));
    }

    [TestMethod]
    public async Task FrozenOrbitalAvailabilityAndPlanSubstitutionAreRejected()
    {
        foreach (var edge in OrbitalHosts)
        {
            var original = PrepareOrbital();
            var result = await ExecuteOrbital(original, edge);
            var product = AssertOrbitalEnvelope(original, result);
            var retrospective = PrepareOrbital(mode: CaptureOrbitalAssessmentMode.RetrospectiveReassessment);
            var reassessed = AssertOrbitalEnvelope(retrospective, await ExecuteOrbital(retrospective, edge));
            Assert.AreNotEqual(product.Recipe.IdentitySha256, reassessed.Recipe.IdentitySha256);
            Assert.AreNotEqual(product.ContentIdentitySha256, reassessed.ContentIdentitySha256);
            Assert.ThrowsExactly<ArgumentException>(() => CaptureOrbitalPredictionJson.Parse(product.Payload.Span, retrospective.Input));
            Assert.ThrowsExactly<ArgumentException>(() => CaptureOrbitalPredictionInput.Restore(original.Input.SerializePlan(),
                OrbitalSnapshotReadResult.Unavailable(OrbitalSnapshotUnavailableReason.NoActiveSnapshot)));
            var fixture = new CaptureOrbitalPredictionFixture();
            var other = OrbitalSnapshot.Create(fixture.Snapshot.Records.Take(1), fixture.Snapshot.Manifest.Sources, fixture.Snapshot.Manifest.Coverage);
            Assert.ThrowsExactly<ArgumentException>(() => CaptureOrbitalPredictionInput.Restore(original.Input.SerializePlan(),
                OrbitalSnapshotReadResult.Available(other)));
            Assert.ThrowsExactly<ArgumentException>(() => CaptureOrbitalPredictionJson.Parse(product.Payload.Span,
                fixture.Input(observer: new(1, 2, 3))));
            Assert.ThrowsExactly<ArgumentException>(() => fixture.Input(availability: fixture.Availability with
            { TimeDataAvailableUtc = fixture.Availability.OriginalCutoffUtc.AddTicks(1) }));
            var late = OrbitalSnapshot.Create(fixture.Snapshot.Records, fixture.Snapshot.Manifest.Sources.Select(s => s with
            { RetrievedUtc = fixture.Availability.OriginalCutoffUtc.AddHours(1) }), fixture.Snapshot.Manifest.Coverage);
            var read = OrbitalSnapshotReadResult.Available(late);
            Assert.ThrowsExactly<ArgumentException>(() => fixture.Input(snapshot: read));
            var availability = fixture.Availability with
            {
                Mode = CaptureOrbitalAssessmentMode.RetrospectiveReassessment,
                AssessmentCutoffUtc = fixture.Availability.OriginalCutoffUtc.AddDays(1),
                TimeDataAvailableUtc = fixture.Availability.OriginalCutoffUtc.AddHours(1)
            };
            var lateInput = fixture.Input(snapshot: read, availability: availability);
            var lateCase = RestoreOrbital(fixture, lateInput, read);
            var lateProduct = AssertOrbitalEnvelope(lateCase, await ExecuteOrbital(lateCase, edge));
            var lateOutput = CaptureOrbitalPredictionJson.Parse(lateProduct.Payload.Span, lateCase.Input);
            Assert.AreEqual(late.Manifest.ManifestSha256, lateOutput.Input.SnapshotManifest!.ManifestSha256);
            Assert.AreEqual(CaptureOrbitalAssessmentMode.RetrospectiveReassessment, lateOutput.Input.Availability.Mode);
            Assert.AreEqual(availability, lateOutput.Input.Availability);
            Assert.AreEqual(OrbitalExecutionStatus.Complete, lateOutput.Status);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FrozenOrbitalCallerCancellationNeverProduces(bool duringExecution)
    {
        foreach (var edge in OrbitalHosts)
        {
            using var cancellation = new CancellationTokenSource();
            var clock = duringExecution ? new OrbitalCancellingClock(cancellation) : null;
            var item = PrepareOrbital(clock: clock);
            if (!duringExecution) await cancellation.CancelAsync();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await ExecuteOrbital(item, edge, cancellationToken: cancellation.Token));
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.IsNull(item.Executor.Outcome, "Cancellation must not return a produced result or useful prefix.");
            if (duringExecution) Assert.IsNotNull(item.Executor.Request);
        }
    }

    [TestMethod]
    [DataRow("lineage")]
    [DataRow("output")]
    [DataRow("recipe")]
    public async Task FrozenOrbitalParityAssertionsDetectEnvelopeMutation(string field)
    {
        var item = PrepareOrbital();
        var product = AssertOrbitalEnvelope(item, await ExecuteOrbital(item, edge: false));
        var altered = field switch
        {
            "lineage" => product with { SourceArtifactIds = [Guid.Parse("52900000-0000-0000-0000-000000000001")] },
            "output" => product with { OutputIdentitySha256 = new('A', 64) },
            _ => product with { Recipe = product.Recipe with { IdentitySha256 = new('A', 64) } }
        };
        Assert.ThrowsExactly<AssertFailedException>(() => AssertOrbitalEquivalentProducts(product, altered));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DescriptorExactOptInPreservesGenericCreationAndInstantaneousObservationSemantics(bool instantaneous)
    {
        var fixture = new CaptureOrbitalPredictionFixture();
        var descriptor = fixture.Descriptor;
        if (instantaneous) descriptor = descriptor with
        {
            Timing = descriptor.Timing with
            { ExposureEndedUtc = descriptor.Timing.ExposureStartedUtc }
        };
        Assert.IsTrue(descriptor.Validate().IsValid);
        var generic = new RecordingExecutor();
        var exact = new RecordingExecutor();
        var request = fixture.Request();
        var genericResult = await new LogicHostRecipeExecutionAdapter(generic).ExecuteAsync(descriptor, fixture.Raw.Payload,
            BuiltInProcessingRecipes.NoOpAnalyzer, request.Options, request.Input, request.OutputVariant,
            cancellationToken: TestContext.CancellationToken);
        var exactResult = await new LogicHostRecipeExecutionAdapter(exact).ExecuteDescriptorExactAsync(descriptor, fixture.Raw.Payload,
            BuiltInProcessingRecipes.NoOpAnalyzer, request.Options, request.Input, request.OutputVariant, TestContext.CancellationToken);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, genericResult.Status);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, exactResult.Status);
        var before = Assert.ContainsSingle(generic.Request!.Inputs);
        var after = Assert.ContainsSingle(exact.Request!.Inputs);
        Assert.AreEqual(descriptor.Timing.ExposureStartedUtc, before.CreatedUtc);
        Assert.AreEqual(descriptor.Artifact.CreatedUtc, after.CreatedUtc);
        Assert.AreNotEqual(before.CreatedUtc, after.CreatedUtc);
        Assert.AreEqual(descriptor.Timing.ExposureStartedUtc.Add(descriptor.Controls.EffectiveExposure), before.ObservationEndedUtc);
        Assert.AreEqual(descriptor.Timing.ExposureEndedUtc, after.ObservationEndedUtc);
        Assert.AreEqual(before.CaptureId, after.CaptureId);
        Assert.AreEqual(before.DescriptorIdentitySha256, after.DescriptorIdentitySha256);
    }

    private static bool[] OrbitalHosts =>
#if COMBINED_TESTS
        [false, true];
#else
        [false];
#endif

    private static OrbitalCase PrepareOrbital(string name = "iss",
        CaptureOrbitalAssessmentMode mode = CaptureOrbitalAssessmentMode.OriginalAvailability,
        OrbitalSnapshotUnavailableReason? unavailable = null, bool missingObject = false,
        TimeProvider? clock = null, bool assertBindings = true)
    {
        var fixture = new CaptureOrbitalPredictionFixture(name);
        var read = unavailable is { } reason ? OrbitalSnapshotReadResult.Unavailable(reason)
            : OrbitalSnapshotReadResult.Available(fixture.Snapshot);
        var availability = fixture.Availability;
        if (mode == CaptureOrbitalAssessmentMode.RetrospectiveReassessment)
            availability = availability with { Mode = mode, AssessmentCutoffUtc = availability.OriginalCutoffUtc.AddDays(1) };
        var input = fixture.Input(snapshot: read, availability: availability,
            objects: missingObject ? [new("norad", "999999999")] : null);
        return RestoreOrbital(fixture, input, read, clock, assertBindings);
    }

    private static OrbitalCase RestoreOrbital(CaptureOrbitalPredictionFixture fixture, CaptureOrbitalPredictionInput original,
        OrbitalSnapshotReadResult read, TimeProvider? clock = null, bool assertBindings = true)
    {
        var input = CaptureOrbitalPredictionInput.Restore(original.SerializePlan(), read);
        Assert.AreNotSame(original, input);
        CollectionAssert.AreEqual(original.SerializePlan(), input.SerializePlan());
        return new(fixture, input, new(input, fixture.Time, clock, assertBindings));
    }

    private async Task<ProcessingOutcome> ExecuteOrbital(OrbitalCase item, bool edge,
        ReconstructionDescriptor? descriptor = null, ReadOnlyMemory<byte>? payload = null, JsonElement? options = null,
        CancellationToken? cancellationToken = null)
    {
        descriptor ??= item.Fixture.Descriptor;
        var bytes = payload ?? item.Fixture.Raw.Payload;
        var request = item.Fixture.Request();
        var cancellation = cancellationToken ?? TestContext.CancellationToken;
#if COMBINED_TESTS
        if (edge)
        {
            var raw = CreateOrbitalEdgeSource(item.Fixture, descriptor, bytes, exact: true);
            Assert.AreEqual(bytes, raw.Payload, "The shipped binding must retain the caller's payload memory.");
            return await new CameraAgentRecipeExecutionAdapter(item.Executor).ExecuteAsync(
                item.Fixture.Request(raw) with
                { Options = options ?? request.Options, InputArtifactId = raw.ArtifactId }, cancellation);
        }
#endif
        return await new LogicHostRecipeExecutionAdapter(item.Executor).ExecuteDescriptorExactAsync(
            descriptor, bytes, request.RecipeName, options ?? request.Options, request.Input, request.OutputVariant, cancellation);
    }

#if COMBINED_TESTS
    private static ProcessingArtifact CreateOrbitalEdgeSource(CaptureOrbitalPredictionFixture fixture,
        ReconstructionDescriptor descriptor, ReadOnlyMemory<byte> payload, bool exact)
    {
        // Deliberately use the original frame time; the shipped satellite binding must restore descriptor time.
        var frame = new CameraFrame(fixture.Descriptor.Timing.ExposureStartedUtc, descriptor.Layout.Width,
            descriptor.Layout.Height, descriptor.Layout.PixelFormat, payload,
            new(descriptor.Controls.EffectiveExposure, descriptor.Controls.EffectiveGain, 0),
            descriptor.Layout.StrideBytes)
        { Layout = descriptor.Layout };
        var artifact = new FrameArtifact(descriptor.Artifact.ArtifactId, descriptor.Artifact.Role,
            frame, descriptor.Artifact.SourceArtifactIds);
        return exact ? CaptureSatelliteTracksProcessingStep.CreateSource(ProcessingConformanceFixture.CameraConfig,
            artifact, descriptor) : CameraAgentRecipeExecutionAdapter.CreateArtifact(ProcessingConformanceFixture.CameraConfig,
            artifact, descriptor.Artifact.Variant, reconstructionDescriptor: descriptor);
    }
#endif

    private async Task AssertOrbitalGolden(OrbitalCase item, ProcessingOutcome outcome)
    {
        var product = AssertOrbitalEnvelope(item, outcome);
        var output = CaptureOrbitalPredictionJson.Parse(product.Payload.Span, item.Input);
        var fixture = item.Fixture;
        Assert.AreEqual(OrbitalExecutionStatus.Complete, output.Status);
        Assert.AreEqual(OrbitalPredictionReason.None, output.Reason);
        Assert.AreEqual(item.Input.IdentitySha256, output.InputIdentitySha256);
        Assert.AreEqual(item.Input.Plan.Availability, output.Input.Availability);
        Assert.AreEqual(OrbitalPopulationCompleteness.Partial, output.Input.PopulationCoverage.Completeness);
        Assert.AreEqual("Unknown", output.PhysicalVisibility);
        var track = Assert.ContainsSingle(output.Tracks);
        CollectionAssert.AreEqual(OrbitalPredictionJson.SerializeTrack(fixture.Track), OrbitalPredictionJson.SerializeTrack(track));
        Assert.IsTrue(track.Samples.All(static sample => sample.Illumination == OrbitalIllumination.Unknown));
        Assert.IsNull(track.Uncertainty.Orbit.Value);
        Assert.IsNull(track.Uncertainty.Clock.Value);
        Assert.IsNull(track.Uncertainty.Calibration.Value);
        Assert.AreEqual(fixture.Snapshot.Manifest.ManifestSha256, track.Lineage.SnapshotManifestSha256);
        Assert.AreEqual(fixture.Track.Lineage.RecordSha256, Assert.ContainsSingle(output.Input.Selections).SelectedRecord!.RecordSha256);
        Assert.AreEqual(fixture.Track.Model, track.Model);
        if (item.Input.Plan.Availability.Mode == CaptureOrbitalAssessmentMode.OriginalAvailability)
        {
            var name = fixture.Track.Lineage.ObjectIdentity.CatalogNumber switch
            { "25544" => "iss", "56838" => "starlink-56838", _ => "starlink-65283" };
            var expected = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures",
                $"capture-orbital-{name}-v1.json"), TestContext.CancellationToken);
            CollectionAssert.AreEqual(expected, product.Payload.ToArray());
        }
    }

    private static ProcessingProduct AssertOrbitalEnvelope(OrbitalCase item, ProcessingOutcome outcome)
    {
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = Assert.ContainsSingle(outcome.Products);
        Assert.AreEqual(FrameArtifactRole.Metadata, product.Role);
        Assert.AreEqual(ProcessingProductKind.Metadata, product.Kind);
        Assert.AreEqual(CaptureOrbitalPredictionJson.MediaType, product.MediaType);
        Assert.AreEqual(CaptureOrbitalPredictionJson.SchemaVersion, product.SchemaVersion);
        Assert.AreEqual("predicted-satellites", product.Variant);
        Assert.IsNull(product.Layout);
        Assert.AreEqual(ProcessingIdentity.ComputePayloadSha256(product.Payload), product.ChecksumSha256);
        Assert.AreEqual(product.ChecksumSha256, product.ContentIdentitySha256);
        Assert.AreEqual(CaptureOrbitalPredictionRecipe.Name, product.Recipe.Descriptor.Name);
        Assert.AreEqual("1.0.0", product.Recipe.Descriptor.SemanticVersion);
        Assert.AreEqual(CaptureOrbitalPredictionRecipe.AlgorithmVersion, product.Recipe.Descriptor.ImplementationVersion);
        CollectionAssert.AreEqual(new[] { item.Fixture.Descriptor.Artifact.ArtifactId }, product.SourceArtifactIds.ToArray());
        Assert.AreEqual(ProcessingIdentity.CreateOutputIdentity(product.Role, product.Variant,
            product.Recipe.IdentitySha256, product.SourceArtifactIds), product.OutputIdentitySha256);
        Assert.AreEqual(item.Fixture.Descriptor.Controls.EffectiveExposure, product.TotalIntegration);
        return product;
    }

    private static void AssertOrbitalEquivalentProducts(ProcessingProduct expected, ProcessingProduct actual)
    {
        CollectionAssert.AreEqual(expected.Payload.ToArray(), actual.Payload.ToArray());
        Assert.AreEqual(expected.ChecksumSha256, actual.ChecksumSha256);
        Assert.AreEqual(expected.ContentIdentitySha256, actual.ContentIdentitySha256);
        Assert.AreEqual(expected.OutputIdentitySha256, actual.OutputIdentitySha256);
        Assert.AreEqual(expected.Recipe.IdentitySha256, actual.Recipe.IdentitySha256);
        Assert.AreEqual(expected.Recipe.Descriptor.Name, actual.Recipe.Descriptor.Name);
        Assert.AreEqual(expected.Recipe.Descriptor.SemanticVersion, actual.Recipe.Descriptor.SemanticVersion);
        Assert.AreEqual(expected.Recipe.Descriptor.ImplementationVersion, actual.Recipe.Descriptor.ImplementationVersion);
        Assert.AreEqual(expected.Recipe.Descriptor.OptionsSha256, actual.Recipe.Descriptor.OptionsSha256);
        Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(expected.Recipe.Descriptor.Options),
            CaptureContractJson.ComputeCanonicalJsonSha256(actual.Recipe.Descriptor.Options));
        CollectionAssert.AreEqual(expected.SourceArtifactIds.ToArray(), actual.SourceArtifactIds.ToArray());
        CollectionAssert.AreEqual(expected.Algorithms.ToArray(), actual.Algorithms.ToArray());
        Assert.AreEqual(expected.Role, actual.Role);
        Assert.AreEqual(expected.Variant, actual.Variant);
        Assert.AreEqual(expected.Kind, actual.Kind);
        Assert.AreEqual(expected.MediaType, actual.MediaType);
        Assert.AreEqual(expected.SchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(expected.Layout, actual.Layout);
        Assert.AreEqual(expected.TotalIntegration, actual.TotalIntegration);
        Assert.AreEqual(expected.Compatibility, actual.Compatibility);
    }

    private sealed record OrbitalCase(CaptureOrbitalPredictionFixture Fixture,
        CaptureOrbitalPredictionInput Input, OrbitalBindingExecutor Executor);

    private sealed class OrbitalBindingExecutor(CaptureOrbitalPredictionInput input, OrbitalTimeData time,
        TimeProvider? clock, bool assertBindings) : IProcessingRecipeExecutor
    {
        private readonly ProcessingRecipeExecutor executor = new([new CaptureOrbitalPredictionRecipe(input, time, clock)]);
        internal ProcessingExecutionRequest? Request { get; private set; }
        internal ProcessingOutcome? Outcome { get; private set; }

        public async ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            if (assertBindings)
            {
                var source = Assert.ContainsSingle(request.Inputs);
                var descriptor = input.Plan.Descriptor;
                Assert.AreEqual(source.ArtifactId, request.InputArtifactId);
                Assert.AreEqual(descriptor.Capture.CaptureId, source.CaptureId);
                Assert.AreEqual(descriptor.Capture.CaptureSequence, source.CaptureSequence);
                Assert.AreEqual(descriptor.Artifact.ArtifactId, source.ArtifactId);
                Assert.AreEqual(input.Plan.DescriptorSha256, source.DescriptorIdentitySha256);
                Assert.AreEqual(descriptor.Artifact.Role, source.Role);
                Assert.AreEqual(descriptor.Artifact.Variant, source.Variant);
                Assert.AreEqual(descriptor.Artifact.MediaType, source.MediaType);
                Assert.AreEqual(descriptor.Artifact.CreatedUtc, source.CreatedUtc);
                Assert.AreEqual(ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                    source.RecipeIdentitySha256);
                Assert.AreEqual(descriptor.Layout, source.Layout);
                Assert.AreEqual(input.Plan.Timing.Exposure.StartUtc, source.ObservationStartedUtc);
                Assert.AreEqual(input.Plan.Timing.Exposure.EndUtc, source.ObservationEndedUtc);
                Assert.AreEqual(descriptor.Controls.EffectiveExposure, source.Integration);
                Assert.AreEqual(descriptor.Artifact.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(source.Payload));
            }
            Outcome = await executor.ExecuteAsync(request, cancellationToken);
            return Outcome;
        }
    }

    private sealed class OrbitalCancellingClock(CancellationTokenSource cancellation) : TimeProvider
    {
        public override long GetTimestamp() { cancellation.Cancel(); return 0; }
    }
}
