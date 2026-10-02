using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class NightlyProductRecipeTests
{
    private static readonly ProcessingCompatibilityIdentity Compatibility = new(
        "rig-v1", "north-up", "none-v1", "full-v1", "sensor-v1", "night-v1", "pipeline-v1");

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
    public async Task KeogramRecipeProducesTimeAxisWithPatternedGapsAndLineage()
    {
        var first = Preview(Guid.Parse("40000000-0000-0000-0000-000000000001"), [1, 2, 3, 4], Origin);
        var second = Preview(Guid.Parse("40000000-0000-0000-0000-000000000002"), [5, 6, 7, 8], Origin.AddMinutes(1));
        var third = Preview(Guid.Parse("40000000-0000-0000-0000-000000000003"), [9, 10, 11, 12], Origin.AddMinutes(10));
        var request = KeogramRequest(
            [first, second, third],
            new KeogramRecipeOptions(SliceColumn: 0, MaximumGapSeconds: 300, GapColumnCount: 1));

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        Assert.AreEqual(FrameArtifactRole.Preview, product.Role);
        Assert.AreEqual("application/x-hvo-packed-image", product.MediaType);
        Assert.AreEqual(4, product.Layout!.Width);
        Assert.AreEqual(2, product.Layout.Height);
        Assert.AreEqual(4, product.Layout.StrideBytes);
        Assert.AreEqual(CameraPixelFormat.Mono8, product.Layout.PixelFormat);
        Assert.AreEqual(TimeSpan.FromSeconds(3), product.TotalIntegration);
        CollectionAssert.AreEqual(
            new byte[] { 1, 5, 0x20, 9, 3, 7, 0x60, 11 },
            product.Payload.ToArray());
        CollectionAssert.AreEqual(
            new[] { first.ArtifactId, second.ArtifactId, third.ArtifactId },
            product.SourceArtifactIds.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                new ProcessingAlgorithmIdentity("keogram-slice", KeogramComposer.AlgorithmVersion),
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
        var third = Preview(Guid.Parse("40000000-0000-0000-0000-000000000013"), [9, 10, 11, 12], Origin.AddMinutes(10));
        var request = KeogramRequest(
            [third, first, second],
            new KeogramRecipeOptions(SliceColumn: 0, MaximumGapSeconds: 300, GapColumnCount: 1));

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        CollectionAssert.AreEqual(
            new[] { first.ArtifactId, second.ArtifactId, third.ArtifactId },
            product.SourceArtifactIds.ToArray());
        CollectionAssert.AreEqual(
            new byte[] { 1, 5, 0x20, 9, 3, 7, 0x60, 11 },
            product.Payload.ToArray());
        ProcessingRecipeTests.AssertProductMatchesContract(request, product);
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
            new KeogramRecipeOptions(MaximumGapSeconds: 300, GapColumnCount: 1, MaximumColumnCount: 2));

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
            "keogram-v1");

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
