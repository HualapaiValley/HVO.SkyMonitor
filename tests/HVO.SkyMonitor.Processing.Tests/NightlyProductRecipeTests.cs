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

    [TestMethod]
    public void NightlyRecipeDefinitionsAreRegisteredAsWindows()
    {
        Assert.IsTrue(BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.Keogram, out var keogram));
        Assert.IsTrue(BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.StarTrail, out var starTrail));
        Assert.AreEqual(ProcessingOperationKind.Window, keogram!.OperationKind);
        Assert.AreEqual(ProcessingOperationKind.Window, starTrail!.OperationKind);
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
