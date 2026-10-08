using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using static HVO.SkyMonitor.Processing.Tests.MeasuredStellarAssociationFixtures;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// Measured stellar associations on synthetic linear frames: predictions bound the search, measured centroids decide
/// the match, and a misplaced scene associates nothing rather than being fitted onto the stars.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class MeasuredStellarAssociationRecipeTests
{
    private static readonly (string Id, double East, double North, double Magnitude)[] Stars =
    [
        ("star:a", 0, 0, 2),
        ("star:b", 6, 3, 3),
        ("star:c", -6, -3, 3.5),
        ("star:d", 5, -4, 4)
    ];

    [TestMethod]
    public async Task MonoFrameAssociatesEveryPredictionOneToOneAsVersionedMetadataWithLineage()
    {
        var scene = await StarFieldAsync(Stars).ConfigureAwait(false);
        var source = RawArtifact(Render(scene, offsetX: 0));
        var request = Request(source, scene);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        Assert.AreEqual(FrameArtifactRole.Metadata, product.Role);
        Assert.AreEqual(ProcessingProductKind.Metadata, product.Kind);
        Assert.AreEqual(StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType, product.MediaType);
        Assert.AreEqual(MeasuredStellarAssociationsV1.CurrentSchemaVersion, product.SchemaVersion);
        CollectionAssert.AreEqual(new[] { source.ArtifactId }, product.SourceArtifactIds.ToArray());
        string[] algorithms = ["stellar-source-measurer", "measured-stellar-associator", "stellar-label-policy"];
        CollectionAssert.AreEqual(algorithms, product.Algorithms.Select(static item => item.Name).ToArray());
        ProcessingRecipeTests.AssertProductMatchesContract(request, product);

        var parsed = MeasuredStellarAssociationJson.Parse(product.Payload);
        Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.ReasonCode);
        var associations = parsed.Associations!;
        Assert.AreEqual(associations.AssociationIdentitySha256, product.ContentIdentitySha256);
        Assert.AreEqual(CaptureId, associations.Source.CaptureId);
        Assert.AreEqual(source.ArtifactId, associations.Source.ArtifactId);
        Assert.AreEqual(scene.SceneIdentitySha256.ToUpperInvariant(), associations.Scene.SceneIdentitySha256);
        Assert.AreEqual("mono-linear-v1", associations.Measurement.Reconstruction);
        // Astronomy/Imaging (the #1103 measurer) canonicalize identities in lowercase and Processing contracts in
        // uppercase, so the recipe uppercases the measurer identity once, where it enters, rather than rejecting it.
        var measurerIdentity = StellarSourceMeasurer.SettingsIdentity(new(MinimumPeakAboveBackground: 8,
            SaturationDilationPixels: 0, MaximumSaturatedSamples: 16, NoiseEstimator: StellarNoiseEstimator.AdjacentDifferences));
        Assert.IsTrue(measurerIdentity.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "the producer's lowercase form is pinned");
        Assert.AreEqual(measurerIdentity.ToUpperInvariant(), associations.Measurement.SettingsIdentitySha256);
        CollectionAssert.AreEqual(Stars.Select(static star => star.Id).ToArray(),
            associations.Associations.Select(static item => item.CatalogId).ToArray());
        Assert.AreEqual(associations.Associations.Count,
            associations.Associations.Select(static item => item.DetectionIndex).Distinct().Count());
        Assert.IsTrue(associations.Associations.All(static item => item.LabelEligible && item.ResidualPixels < .25),
            string.Join(", ", associations.Associations.Select(static item => $"{item.CatalogId}:{item.ResidualPixels}")));
        Assert.IsEmpty(associations.UnmatchedPredictions);
        Assert.AreEqual(associations.Measurement.DetectionCount,
            associations.Associations.Count + associations.UnassociatedDetections.Count);
    }

    [TestMethod]
    public async Task RepeatedExecutionIsByteIdentical()
    {
        var scene = await StarFieldAsync(Stars).ConfigureAwait(false);
        var request = Request(RawArtifact(Render(scene, offsetX: 0)), scene);

        var first = (await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false)).Products.Single();
        var second = (await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false)).Products.Single();

        CollectionAssert.AreEqual(first.Payload.ToArray(), second.Payload.ToArray());
        Assert.AreEqual(first.ContentIdentitySha256, second.ContentIdentitySha256);
        Assert.AreEqual(first.Recipe.IdentitySha256, second.Recipe.IdentitySha256);
    }

    [TestMethod]
    public async Task MisplacedSceneIsNeverFittedAndEveryLabelFailsClosed()
    {
        // The stars sit 3 px east of every prediction: inside the offset radius, outside the 2 px window.
        var scene = await StarFieldAsync(Stars).ConfigureAwait(false);
        var request = Request(RawArtifact(Render(scene, offsetX: 3)), scene);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var associations = MeasuredStellarAssociationJson.Parse(outcome.Products.Single().Payload).Associations!;
        Assert.IsEmpty(associations.Associations);
        CollectionAssert.AreEqual(Stars.Select(static star => star.Id).ToArray(),
            associations.UnmatchedPredictions.Select(static item => item.CatalogId).ToArray());
        Assert.IsTrue(associations.UnmatchedPredictions.All(static item =>
            item.ReasonCode == MeasuredStellarAssociationReasonCodes.OffsetMeasuredSource));
        Assert.IsTrue(associations.UnassociatedDetections.All(static item =>
            item.ReasonCode == MeasuredStellarAssociationReasonCodes.NearUnassociatedPrediction));

        var labels = StellarLabelPolicy.Apply(
            scene.Objects.Select(static item => new ProjectedAnnotationObject(item.Id, item.DisplayName, item.Pixel)).ToArray(),
            associations);
        Assert.IsTrue(labels.All(static item => !item.DrawLabel));
    }

    [TestMethod]
    public async Task CfaFrameMeasuresTheLinearLuminanceReconstruction()
    {
        var scene = await StarFieldAsync(Stars).ConfigureAwait(false);
        var request = Request(RawArtifact(Render(scene, offsetX: 0), CameraPixelFormat.BayerRggb16), scene);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        Assert.AreEqual("linear-bayer-reconstruction", product.Algorithms[0].Name);
        var associations = MeasuredStellarAssociationJson.Parse(product.Payload).Associations!;
        StringAssert.StartsWith(associations.Measurement.Reconstruction, LinearBayerReconstruction.AlgorithmVersion,
            StringComparison.Ordinal);
        CollectionAssert.AreEqual(Stars.Select(static star => star.Id).ToArray(),
            associations.Associations.Select(static item => item.CatalogId).ToArray());
    }

    [TestMethod]
    public async Task CrowdedPredictionsAreNeverAssociated()
    {
        // Two predictions about 2.8 px apart within the declared magnitude difference cannot be told apart.
        var scene = await StarFieldAsync([.. Stars, ("star:e", .4, 0, 2.5)]).ConfigureAwait(false);
        var request = Request(RawArtifact(Render(scene, offsetX: 0)), scene);

        var associations = MeasuredStellarAssociationJson.Parse(
            (await new ProcessingRecipeExecutor().ExecuteAsync(request).ConfigureAwait(false)).Products.Single().Payload)
            .Associations!;

        string[] crowded = ["star:a", "star:e"];
        CollectionAssert.AreEquivalent(crowded, associations.UnmatchedPredictions
            .Where(static item => item.ReasonCode == MeasuredStellarAssociationReasonCodes.CrowdedPrediction)
            .Select(static item => item.CatalogId).ToArray());
        Assert.IsFalse(associations.Associations.Any(static item => item.CatalogId is "star:a" or "star:e"));
    }

    [TestMethod]
    public async Task MissingSceneSkipsAndMismatchedOrOpaqueInputsFailTerminally()
    {
        var scene = await StarFieldAsync(Stars).ConfigureAwait(false);
        var source = RawArtifact(Render(scene, offsetX: 0));
        var executor = new ProcessingRecipeExecutor();

        var missing = await executor.ExecuteAsync(Request(source, scene) with { AuxiliaryInputs = [] }).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Skipped, missing.Status);
        Assert.AreEqual(ProcessingReasonCodes.MissingProjectedScene, missing.ReasonCode);

        var otherArtifact = await executor.ExecuteAsync(
            Request(source with { ArtifactId = Guid.Parse("52600000-0000-0000-0000-0000000000ff") }, scene))
            .ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, otherArtifact.Status);
        Assert.AreEqual(ProcessingReasonCodes.ProjectedSceneSourceMismatch, otherArtifact.ReasonCode);

        var opaque = await executor.ExecuteAsync(
            Request(source with { Layout = source.Layout! with { WhiteLevel = null } }, scene)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, opaque.Status);
        Assert.AreEqual(ProcessingReasonCodes.UnsupportedMeasurementInput, opaque.ReasonCode);
    }

    [TestMethod]
    [DataRow(false, "unknown-schema", ProcessingReasonCodes.InvalidProjectedScene)]
    [DataRow(true, "unknown-schema", ProcessingReasonCodes.InvalidProjectedScene)]
    [DataRow(false, "schema-mismatch", ProcessingReasonCodes.InvalidProjectedScene)]
    [DataRow(true, "schema-mismatch", ProcessingReasonCodes.InvalidProjectedScene)]
    [DataRow(false, "checksum", ProcessingReasonCodes.InvalidInput)]
    [DataRow(true, "checksum", ProcessingReasonCodes.InvalidInput)]
    [DataRow(false, "identity", ProcessingReasonCodes.InvalidProjectedScene)]
    [DataRow(true, "identity", ProcessingReasonCodes.InvalidProjectedScene)]
    [DataRow(false, "capture", ProcessingReasonCodes.ProjectedSceneSourceMismatch)]
    [DataRow(true, "capture", ProcessingReasonCodes.ProjectedSceneSourceMismatch)]
    [DataRow(false, "artifact", ProcessingReasonCodes.ProjectedSceneSourceMismatch)]
    [DataRow(true, "artifact", ProcessingReasonCodes.ProjectedSceneSourceMismatch)]
    [DataRow(false, "descriptor", ProcessingReasonCodes.ProjectedSceneDescriptorMismatch)]
    [DataRow(true, "descriptor", ProcessingReasonCodes.ProjectedSceneDescriptorMismatch)]
    [DataRow(false, "dimensions", ProcessingReasonCodes.ProjectedSceneDimensionMismatch)]
    [DataRow(true, "dimensions", ProcessingReasonCodes.ProjectedSceneDimensionMismatch)]
    public async Task SupportedScenesIndividuallyRejectMismatchedBindings(bool resolvedSun, string fault, string reason)
    {
        const int width = 400, height = 300;
        var original = await ProjectedSceneAnnotationTests.SceneAsync(60,
            resolvedSun ? new AltAzPoint(60, 0) : null, .25, ("star:vega", "Vega", new AltAzPoint(61, 0), 1))
            .ConfigureAwait(false);
        var scene = original with { Source = new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity) };
        scene = scene with { SceneIdentitySha256 = ProjectedSceneJson.ComputeIdentity(scene) };
        var pixels = Field(width, height);
        AddGaussian(pixels, scene.Objects.Single(static item => item.Id == "star:vega").Pixel, 1.2, 4000, width);
        AddNoise(pixels, 526, 6);
        var source = RawArtifact(pixels, width: width, height: height);
        var request = Request(source, scene);
        var executor = new ProcessingRecipeExecutor();
        var healthy = await executor.ExecuteAsync(request).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, healthy.Status, healthy.ReasonCode);
        var auxiliary = request.AuxiliaryInputs!.Single();
        switch (fault)
        {
            case "unknown-schema":
                auxiliary = auxiliary with { SchemaVersion = "projected-scene-v99" };
                break;
            case "schema-mismatch":
                auxiliary = auxiliary with { SchemaVersion = resolvedSun ? ProjectedSceneV1.CurrentSchemaVersion : ProjectedSceneV1.ResolvedFootprintSchemaVersion };
                break;
            case "checksum":
                auxiliary = auxiliary with { ChecksumSha256 = new string('0', 64) };
                break;
            case "identity":
                auxiliary = auxiliary with { IdentitySha256 = new string('E', 64) };
                break;
            case "capture":
                source = source with { CaptureId = Guid.NewGuid() };
                break;
            case "artifact":
                source = source with { ArtifactId = Guid.NewGuid() };
                break;
            case "descriptor":
                source = source with { DescriptorIdentitySha256 = new string('E', 64) };
                break;
            case "dimensions":
                source = source with { Layout = source.Layout! with { Width = width - 1 } };
                break;
            default:
                Assert.Fail("Unknown fault.");
                break;
        }
        var rejected = await executor.ExecuteAsync(request with { Inputs = [source], AuxiliaryInputs = [auxiliary] })
            .ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, rejected.Status, fault);
        Assert.AreEqual(reason, rejected.ReasonCode, fault);
        Assert.IsEmpty(rejected.Products);
    }

    /// <summary>Renders each projected star as a Gaussian, optionally displaced east of its prediction.</summary>
    private static double[] Render(ProjectedSceneV1 scene, double offsetX)
    {
        var pixels = Field();
        foreach (var star in scene.Objects.Where(static item => item.Kind == CelestialObjectKind.Star))
            AddGaussian(pixels, new PixelPoint(star.Pixel.X + offsetX, star.Pixel.Y), 1.2, 4000);
        AddNoise(pixels, seed: 526, sigma: 6);
        return pixels;
    }
}
