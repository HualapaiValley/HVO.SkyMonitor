using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class NightlyProductRecipeTests
{
    private static readonly string RigProfileSha256 = new('B', 64);

    private static readonly ProcessingCompatibilityIdentity Compatibility = new(
        RigProfileSha256, RigProfileSha256, "none-v1", "full-v1", "sensor-v1", "night-v1", "pipeline-v1");

    // A zenith fisheye whose horizon sits just inside a 2x2 preview: north at the top edge, south at the bottom.
    private static readonly KeogramGeometryV1 Geometry = new(
        KeogramGeometryV1.CurrentSchemaVersion,
        RigProfileSha256,
        new ProjectionContext(
            ProjectionModel.EquidistantFisheye, 1, 1, 0.99 * 2 / Math.PI, 0.99 * 2 / Math.PI, 2, 2,
            ProjectionAperture.Circular, 1),
        3);

    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse(
        "2026-08-31T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private const string PreviewVariant = "preview";
    private static readonly string PreviewRecipe = new('A', 64);
    private static readonly string SegmentRecipe = new('D', 64);

    [TestMethod]
    public void NightlyRecipeDefinitionsAreRegisteredAsWindows()
    {
        Assert.IsTrue(BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.Keogram, out var keogram));
        Assert.IsTrue(BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.StarTrail, out var starTrail));
        Assert.IsTrue(BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.KeogramAssembly, out var assembly));
        Assert.AreEqual(ProcessingOperationKind.Window, keogram!.OperationKind);
        Assert.AreEqual(ProcessingOperationKind.Window, starTrail!.OperationKind);
        Assert.AreEqual(ProcessingOperationKind.Window, assembly!.OperationKind);
    }

    [TestMethod]
    public async Task KeogramRecipeSamplesMeridianWithProportionalGapsAndLineage()
    {
        var first = Preview(Guid.Parse("40000000-0000-0000-0000-000000000001"), [1, 2, 3, 4], Origin);
        var second = Preview(Guid.Parse("40000000-0000-0000-0000-000000000002"), [5, 6, 7, 8], Origin.AddMinutes(1));
        var third = Preview(Guid.Parse("40000000-0000-0000-0000-000000000003"), [9, 10, 11, 12], Origin.AddMinutes(5));
        var request = KeogramRequest(
            [first, second, third],
            new KeogramRecipeOptions(MaximumGapSeconds: 90));

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        Assert.AreEqual(FrameArtifactRole.Preview, product.Role);
        Assert.AreEqual("application/x-hvo-packed-image", product.MediaType);
        Assert.AreEqual(6, product.Layout!.Width);
        Assert.AreEqual(3, product.Layout.Height);
        Assert.AreEqual(6, product.Layout.StrideBytes);
        Assert.AreEqual(CameraPixelFormat.Mono8, product.Layout.PixelFormat);
        Assert.AreEqual(TimeSpan.FromSeconds(3), product.TotalIntegration);

        // Rows are the north horizon (top-edge mean), the zenith (centre mean), and the south horizon (bottom-edge
        // mean); the 240 s interval at a 60 s cadence renders three patterned gap columns.
        CollectionAssert.AreEqual(
            new byte[]
            {
                2, 6, 0x20, 0x60, 0x20, 10,
                3, 7, 0x60, 0x20, 0x60, 11,
                4, 8, 0x20, 0x60, 0x20, 12
            },
            product.Payload.ToArray());
        CollectionAssert.AreEqual(
            new[] { first.ArtifactId, second.ArtifactId, third.ArtifactId },
            product.SourceArtifactIds.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                new ProcessingAlgorithmIdentity("meridian-path", MeridianSamplePath.AlgorithmVersion),
                new ProcessingAlgorithmIdentity("keogram-path", KeogramComposer.AlgorithmVersion),
                new ProcessingAlgorithmIdentity("row-packing", "packed-copy-v1")
            },
            product.Algorithms.ToArray());
        ProcessingRecipeTests.AssertProductMatchesContract(request, product);
    }

    [TestMethod]
    public async Task KeogramRecipeOrdersSourcesByObservationTimeRegardlessOfRequestOrder()
    {
        var first = Preview(Guid.Parse("40000000-0000-0000-0000-000000000011"), [1, 2, 3, 4], Origin);
        var second = Preview(Guid.Parse("40000000-0000-0000-0000-000000000012"), [5, 6, 7, 8], Origin.AddMinutes(1));
        var third = Preview(Guid.Parse("40000000-0000-0000-0000-000000000013"), [9, 10, 11, 12], Origin.AddMinutes(5));
        var request = KeogramRequest(
            [third, first, second],
            new KeogramRecipeOptions(MaximumGapSeconds: 90));

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        CollectionAssert.AreEqual(
            new[] { first.ArtifactId, second.ArtifactId, third.ArtifactId },
            product.SourceArtifactIds.ToArray());
        CollectionAssert.AreEqual(
            new byte[] { 2, 6, 10 },
            new[] { product.Payload.Span[0], product.Payload.Span[1], product.Payload.Span[5] });
        ProcessingRecipeTests.AssertProductMatchesContract(request, product);
    }

    [TestMethod]
    public async Task KeogramRecipeSkipsWithoutCapturedGeometry()
    {
        var first = Preview(Guid.Parse("40000000-0000-0000-0000-000000000071"), [1, 2, 3, 4], Origin);
        var request = KeogramRequest([first], new KeogramRecipeOptions()) with { AuxiliaryInputs = null };

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Skipped, outcome.Status);
        Assert.AreEqual(ProcessingReasonCodes.MissingKeogramGeometry, outcome.ReasonCode);
    }

    [TestMethod]
    public async Task KeogramRecipeRejectsGeometryForAnotherRigOrReadout()
    {
        var first = Preview(Guid.Parse("40000000-0000-0000-0000-000000000081"), [1, 2, 3, 4], Origin);
        var otherRig = Geometry with { RigProfileSha256 = new string('C', 64) };
        var otherReadout = Geometry with { Projection = Geometry.Projection with { WidthPixels = 4, HeightPixels = 4 } };

        foreach (var geometry in new[] { otherRig, otherReadout })
        {
            var request = KeogramRequest([first], new KeogramRecipeOptions()) with
            {
                AuxiliaryInputs = [KeogramGeometryJson.CreateAuxiliaryInput(geometry)]
            };

            var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
            Assert.AreEqual(ProcessingReasonCodes.KeogramGeometryMismatch, outcome.ReasonCode);
        }
    }

    [TestMethod]
    public async Task KeogramRecipeRejectsMalformedGeometry()
    {
        var first = Preview(Guid.Parse("40000000-0000-0000-0000-000000000091"), [1, 2, 3, 4], Origin);
        var invalid = Geometry with { SampleCount = 1 };
        var request = KeogramRequest([first], new KeogramRecipeOptions()) with
        {
            AuxiliaryInputs = [KeogramGeometryJson.CreateAuxiliaryInput(invalid)]
        };

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
        Assert.AreEqual(ProcessingReasonCodes.InvalidKeogramGeometry, outcome.ReasonCode);
    }

    [TestMethod]
    public void KeogramGeometryRoundTripsCanonicallyWithStableIdentity()
    {
        var payload = KeogramGeometryJson.Serialize(Geometry);
        var parsed = KeogramGeometryJson.Parse(payload);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(Geometry, parsed);
        CollectionAssert.AreEqual(payload, KeogramGeometryJson.Serialize(parsed));
        Assert.AreEqual(
            ProcessingIdentity.ComputePayloadSha256(payload),
            KeogramGeometryJson.ComputeIdentitySha256(Geometry));
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(payload), "\"EquidistantFisheye\"", StringComparison.Ordinal);
        Assert.IsNull(KeogramGeometryJson.Parse("{\"SchemaVersion\":\"keogram-meridian-geometry-v1\"}"u8));
    }

    [TestMethod]
    public async Task StarTrailRecipeProducesLightenCompositeWithLineage()
    {
        var first = Preview(Guid.Parse("40000000-0000-0000-0000-000000000021"), [1, 5, 3, 4], Origin);
        var second = Preview(Guid.Parse("40000000-0000-0000-0000-000000000022"), [2, 4, 9, 0], Origin.AddMinutes(1));
        var request = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.StarTrail,
            JsonSerializer.SerializeToElement(new StarTrailRecipeOptions()),
            ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, PreviewVariant, PreviewRecipe),
            [first, second],
            "star-trail-v1");

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        Assert.AreEqual(FrameArtifactRole.Preview, product.Role);
        Assert.AreEqual(2, product.Layout!.Width);
        Assert.AreEqual(2, product.Layout.Height);
        CollectionAssert.AreEqual(new byte[] { 2, 5, 9, 4 }, product.Payload.ToArray());
        CollectionAssert.AreEqual(
            new[] { first.ArtifactId, second.ArtifactId },
            product.SourceArtifactIds.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                new ProcessingAlgorithmIdentity("star-trail-lighten", StarTrailComposer.AlgorithmVersion),
                new ProcessingAlgorithmIdentity("row-packing", "packed-copy-v1")
            },
            product.Algorithms.ToArray());
        ProcessingRecipeTests.AssertProductMatchesContract(request, product);
    }

    [TestMethod]
    public async Task KeogramRecipeRejectsIncompatibleFrameDimensions()
    {
        var first = Preview(Guid.Parse("40000000-0000-0000-0000-000000000031"), [1, 2, 3, 4], Origin);
        var second = Preview(Guid.Parse("40000000-0000-0000-0000-000000000032"), [1], Origin.AddMinutes(1), 1, 1);
        var request = KeogramRequest([first, second], new KeogramRecipeOptions());

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
        Assert.AreEqual(ProcessingReasonCodes.IncompatibleInput, outcome.ReasonCode);
    }

    [TestMethod]
    public async Task StarTrailRecipeRejectsNonPreviewRole()
    {
        var combined = Combined(Guid.Parse("40000000-0000-0000-0000-000000000041"), [1, 2, 3, 4], Origin);
        var request = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.StarTrail,
            JsonSerializer.SerializeToElement(new StarTrailRecipeOptions()),
            ProcessingInputSelector.Combined(),
            [combined],
            "star-trail-v1");

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
        Assert.AreEqual(ProcessingReasonCodes.InvalidSelector, outcome.ReasonCode);
    }

    [TestMethod]
    public async Task KeogramRecipeRejectsColumnBoundOverflow()
    {
        var first = Preview(Guid.Parse("40000000-0000-0000-0000-000000000051"), [1, 2, 3, 4], Origin);
        var second = Preview(Guid.Parse("40000000-0000-0000-0000-000000000052"), [5, 6, 7, 8], Origin.AddMinutes(10));
        var request = KeogramRequest(
            [first, second],
            new KeogramRecipeOptions(MaximumGapSeconds: 300, MaximumGapColumnCount: 1, MaximumColumnCount: 2));

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLineage, outcome.ReasonCode);
    }

    [TestMethod]
    public async Task StarTrailRecipeRejectsFrameCountOverOption()
    {
        var first = Preview(Guid.Parse("40000000-0000-0000-0000-000000000061"), [1, 2, 3, 4], Origin);
        var second = Preview(Guid.Parse("40000000-0000-0000-0000-000000000062"), [5, 6, 7, 8], Origin.AddMinutes(1));
        var request = new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.StarTrail,
            JsonSerializer.SerializeToElement(new StarTrailRecipeOptions(MaximumFrameCount: 1)),
            ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, PreviewVariant, PreviewRecipe),
            [first, second],
            "star-trail-v1");

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcome.Status);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLineage, outcome.ReasonCode);
    }

    [TestMethod]
    public async Task KeogramAssemblyReproducesDirectNightKeogramFromSegments()
    {
        var frames = Enumerable.Range(0, 7)
            .Select(index => Preview(
                Guid.Parse($"40000000-0000-0000-0000-0000000001{index:D2}"),
                [(byte)(index * 4), (byte)(index * 4 + 1), (byte)(index * 4 + 2), (byte)(index * 4 + 3)],
                Origin.AddMinutes(index is < 3 ? index : index + 6)))
            .ToArray();
        var options = new KeogramRecipeOptions(MaximumGapSeconds: 90);
        var direct = (await new ProcessingRecipeExecutor().ExecuteAsync(KeogramRequest(frames, options)).ConfigureAwait(false))
            .Products.Single();

        var segments = new List<ProcessingArtifact>();
        var axes = new List<KeogramSegmentAxisV1>();
        foreach (var chunk in new[] { frames[..2], frames[2..5], frames[5..] })
        {
            var (segment, axis) = await ComposeSegmentAsync(chunk, options).ConfigureAwait(false);
            segments.Add(segment);
            axes.Add(axis);
        }
        var request = AssemblyRequest(
            [segments[2], segments[0], segments[1]],
            options,
            new KeogramSegmentAxesV1(KeogramSegmentAxesV1.CurrentSchemaVersion, axes));

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        Assert.AreEqual(direct.Layout, product.Layout);
        CollectionAssert.AreEqual(direct.Payload.ToArray(), product.Payload.ToArray());
        Assert.AreEqual(direct.TotalIntegration, product.TotalIntegration);
        CollectionAssert.AreEqual(
            segments.Select(static segment => segment.ArtifactId).ToArray(),
            product.SourceArtifactIds.ToArray());
        Assert.AreEqual(
            new ProcessingAlgorithmIdentity("keogram-segment-assembly", "keogram-segment-assembly-source-order-v2"),
            product.Algorithms[^1]);
        ProcessingRecipeTests.AssertProductMatchesContract(request, product);
    }

    [TestMethod]
    public async Task PlannedKeogramAssemblyMatchesDirectCompositionAcrossPartBoundariesAndMissingCoverage()
    {
        var frames = Enumerable.Range(0, 7).Select(index => Preview(
            Guid.Parse($"40000000-0000-0000-0000-0000000008{index:D2}"),
            [(byte)(index * 4), (byte)(index * 4 + 1), (byte)(index * 4 + 2), (byte)(index * 4 + 3)],
            Origin.AddMinutes(index is < 3 ? index : index + 30))).ToArray();
        var partOptions = new KeogramRecipeOptions(MaximumGapSeconds: 90);
        var planned = partOptions with { PlannedAxis = new(Origin.AddMinutes(-10), Origin.AddHours(1), TimeSpan.FromMinutes(1)) };
        var directOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(KeogramRequest(frames, planned)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, directOutcome.Status, directOutcome.ReasonCode);
        var parts = new List<ProcessingArtifact>();
        var axes = new List<KeogramSegmentAxisV1>();
        foreach (var chunk in new[] { frames[..2], frames[2..5], frames[5..] })
        {
            var (part, axis) = await ComposeSegmentAsync(chunk, partOptions).ConfigureAwait(false);
            parts.Add(part);
            axes.Add(axis);
        }
        var request = AssemblyRequest([parts[2], parts[0], parts[1]], planned,
            new KeogramSegmentAxesV1(KeogramSegmentAxesV1.CurrentSchemaVersion, axes));

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        Assert.AreEqual(70, outcome.Products.Single().Layout!.Width);
        Assert.AreEqual(directOutcome.Products.Single().Layout, outcome.Products.Single().Layout);
        CollectionAssert.AreEqual(directOutcome.Products.Single().Payload.ToArray(), outcome.Products.Single().Payload.ToArray());
        ProcessingRecipeTests.AssertProductMatchesContract(request, outcome.Products.Single());
    }

    [TestMethod]
    public async Task KeogramAssemblyRequiresBoundSegmentAxesAndMatchingGeometry()
    {
        var frames = new[]
        {
            Preview(Guid.Parse("40000000-0000-0000-0000-000000000201"), [1, 2, 3, 4], Origin),
            Preview(Guid.Parse("40000000-0000-0000-0000-000000000202"), [5, 6, 7, 8], Origin.AddMinutes(1))
        };
        var options = new KeogramRecipeOptions();
        var (segment, axis) = await ComposeSegmentAsync(frames, options).ConfigureAwait(false);
        var axes = new KeogramSegmentAxesV1(KeogramSegmentAxesV1.CurrentSchemaVersion, [axis]);

        var missing = AssemblyRequest([segment], options, axes) with
        {
            AuxiliaryInputs = [KeogramGeometryJson.CreateAuxiliaryInput(Geometry)]
        };
        var unbound = AssemblyRequest([segment], options, axes with { Segments = [axis with { ArtifactId = Guid.NewGuid() }] });
        var forgedPayload = AssemblyRequest([segment], options, axes);
        forgedPayload = forgedPayload with
        {
            AuxiliaryInputs =
            [
                forgedPayload.AuxiliaryInputs![0],
                forgedPayload.AuxiliaryInputs[1] with
                {
                    Payload = KeogramSegmentAxesJson.Serialize(axes with
                    {
                        Segments = [axis with { Frames = [axis.Frames[0] with { Column = 1 }, axis.Frames[1]] }]
                    })
                }
            ]
        };
        var otherRig = AssemblyRequest([segment], options, axes) with
        {
            AuxiliaryInputs =
            [
                KeogramGeometryJson.CreateAuxiliaryInput(Geometry with { RigProfileSha256 = new string('C', 64) }),
                KeogramSegmentAxesJson.CreateAuxiliaryInput(axes)
            ]
        };
        var collidingColumns = AssemblyRequest(
            [segment],
            options,
            axes with { Segments = [axis with { Frames = [axis.Frames[0], axis.Frames[1] with { Column = 0 }] }] });

        var executor = new ProcessingRecipeExecutor();
        var outcomes = new[]
        {
            await executor.ExecuteAsync(missing).ConfigureAwait(false),
            await executor.ExecuteAsync(unbound).ConfigureAwait(false),
            await executor.ExecuteAsync(forgedPayload).ConfigureAwait(false),
            await executor.ExecuteAsync(otherRig).ConfigureAwait(false),
            await executor.ExecuteAsync(collidingColumns).ConfigureAwait(false),
            await executor.ExecuteAsync(AssemblyRequest([segment with { SourceArtifactIds = null }], options, axes)).ConfigureAwait(false)
        };

        Assert.AreEqual(ProcessingOutcomeStatus.Skipped, outcomes[0].Status);
        Assert.AreEqual(ProcessingReasonCodes.MissingKeogramSegmentAxes, outcomes[0].ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidKeogramSegmentAxes, outcomes[1].ReasonCode);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, outcomes[2].Status);
        Assert.AreEqual(ProcessingReasonCodes.KeogramGeometryMismatch, outcomes[3].ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLineage, outcomes[4].ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidKeogramSegmentAxes, outcomes[5].ReasonCode);
    }

    [TestMethod]
    public void KeogramSegmentAxesRoundTripCanonically()
    {
        var axes = new KeogramSegmentAxesV1(
            KeogramSegmentAxesV1.CurrentSchemaVersion,
            [new KeogramSegmentAxisV1(Guid.Parse("40000000-0000-0000-0000-000000000301"), [new KeogramSegmentFrameV1(0, Origin)])]);

        var payload = KeogramSegmentAxesJson.Serialize(axes);
        var parsed = KeogramSegmentAxesJson.Parse(payload);

        Assert.IsNotNull(parsed);
        CollectionAssert.AreEqual(payload, KeogramSegmentAxesJson.Serialize(parsed));
        Assert.AreEqual(
            ProcessingIdentity.ComputePayloadSha256(payload),
            KeogramSegmentAxesJson.CreateAuxiliaryInput(axes).IdentitySha256);
        Assert.IsNull(KeogramSegmentAxesJson.Parse("{\"SchemaVersion\":\"keogram-segment-axes-v1\",\"Segments\":[],\"Extra\":1}"u8));
    }

    private static async Task<(ProcessingArtifact Segment, KeogramSegmentAxisV1 Axis)> ComposeSegmentAsync(
        ProcessingArtifact[] frames,
        KeogramRecipeOptions options)
    {
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(KeogramRequest(frames, options)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        var segment = new ProcessingArtifact(
            Guid.NewGuid(),
            FrameArtifactRole.Preview,
            "keogram-v1",
            SegmentRecipe,
            product.MediaType,
            product.Layout,
            product.Payload,
            frames[^1].ObservationStartedUtc!.Value,
            product.TotalIntegration,
            product.Compatibility,
            SourceArtifactIds: product.SourceArtifactIds,
            ObservationStartedUtc: frames[0].ObservationStartedUtc,
            ObservationEndedUtc: frames[^1].ObservationStartedUtc);
        return (segment, KeogramSegmentAxesJson.CreateSegment(
            segment.ArtifactId, [.. frames.Select(static frame => frame.ObservationStartedUtc!.Value)], options));
    }

    private static ProcessingExecutionRequest AssemblyRequest(
        IReadOnlyList<ProcessingArtifact> segments,
        KeogramRecipeOptions options,
        KeogramSegmentAxesV1 axes) =>
        new(
            BuiltInProcessingRecipes.KeogramAssembly,
            JsonSerializer.SerializeToElement(options),
            ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, "keogram-v1", SegmentRecipe),
            segments,
            "keogram-night-v1",
            AuxiliaryInputs:
            [
                KeogramGeometryJson.CreateAuxiliaryInput(Geometry),
                KeogramSegmentAxesJson.CreateAuxiliaryInput(axes)
            ]);

    private static ProcessingExecutionRequest KeogramRequest(
        IReadOnlyList<ProcessingArtifact> inputs,
        KeogramRecipeOptions options) =>
        new(
            BuiltInProcessingRecipes.Keogram,
            JsonSerializer.SerializeToElement(options),
            ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, PreviewVariant, PreviewRecipe),
            inputs,
            "keogram-v1",
            AuxiliaryInputs: [KeogramGeometryJson.CreateAuxiliaryInput(Geometry)]);

    private static ProcessingArtifact Preview(
        Guid artifactId,
        byte[] pixels,
        DateTimeOffset startedUtc,
        int width = 2,
        int height = 2) =>
        new(
            artifactId,
            FrameArtifactRole.Preview,
            PreviewVariant,
            PreviewRecipe,
            "application/x-hvo-packed-image",
            CreateLayout(width, height),
            pixels,
            startedUtc,
            TimeSpan.FromSeconds(1),
            Compatibility,
            ObservationStartedUtc: startedUtc);

    private static ProcessingArtifact Combined(Guid artifactId, byte[] pixels, DateTimeOffset startedUtc) =>
        new(
            artifactId,
            FrameArtifactRole.Combined,
            "combined",
            PreviewRecipe,
            "application/x-hvo-packed-image",
            CreateLayout(2, 2),
            pixels,
            startedUtc,
            TimeSpan.FromSeconds(1),
            Compatibility,
            ObservationStartedUtc: startedUtc);

    private static FrameLayoutDescriptor CreateLayout(int width, int height) =>
        new(
            width,
            height,
            width,
            CameraPixelFormat.Mono8,
            FrameByteOrder.NotApplicable,
            8,
            8,
            FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.None,
            null,
            byte.MaxValue,
            checked(width * height));
}
