#pragma warning disable CA5394 // Seeded virtual reference field, never security material.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>Networkless reference: 512-square geometric equidistant field; real linear measurement/association.</summary>
internal sealed record MeasuredAstrometryFixture(ProcessingArtifact Raw, ProjectedSceneV1 Scene,
    ProcessingArtifact Measured, MeasuredStellarAssociationsV1 Associations, MeasuredAstrometryContextV1 Context,
    AstrometricCatalogData Catalog, AstrometricCalibration Calibration)
{
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    internal static readonly MeasuredAstrometryOptions Options = new(new(MaximumCatalogMagnitude: 7, ColdBudgetMilliseconds: 15000, WarmBudgetMilliseconds: 3000));

    internal static Task<MeasuredAstrometryFixture> CreateAsync() => CreateAtAsync(0);

    internal static async Task<MeasuredAstrometryFixture> CreateAtAsync(int offsetSeconds)
    {
        var random = new Random(934817);
        var stars = Enumerable.Range(0, 560).Select(i => new CelestialCatalogObject($"SYN{i:0000}", $"Artificial {i}",
            random.NextDouble() * 24, Math.Asin(2 * random.NextDouble() - 1) * 180 / Math.PI, 1.8 + 3.4 * random.NextDouble())).ToArray();
        var metadata = new CatalogMetadata("generated-reference", "1", new Uri("https://example.test/reference"), Hash("seed934817"), "test-generated", "1");
        var package = new AstrometricCatalogProvenance("generated-reference", "1", "fixture", "unspecified");
        var catalog = new AstrometricCatalogData(metadata, stars, true, 7, provenance: package);
        var projection = new ProjectionContext(ProjectionModel.EquidistantFisheye, 256, 256, 144, 144, 512, 512,
            ProjectionAperture.Circular, 232, 72, 243, 17);
        var start = DateTimeOffset.Parse("2026-08-25T00:00:00Z", CultureInfo.InvariantCulture).AddSeconds(offsetSeconds);
        var captureId = offsetSeconds == 0 ? MeasuredStellarAssociationFixtures.CaptureId : new Guid(Convert.FromHexString(Hash("capture" + offsetSeconds.ToString(CultureInfo.InvariantCulture))).AsSpan(0, 16));
        var artifactId = offsetSeconds == 0 ? MeasuredStellarAssociationFixtures.ArtifactId : new Guid(Convert.FromHexString(Hash("artifact" + offsetSeconds.ToString(CultureInfo.InvariantCulture))).AsSpan(0, 16));
        var descriptorIdentity = offsetSeconds == 0 ? MeasuredStellarAssociationFixtures.DescriptorIdentity : Hash("descriptor" + offsetSeconds.ToString(CultureInfo.InvariantCulture));
        var observer = new ObserverLocation(0, 0, 0);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(stars)).BuildAsync(new VisibleSceneRequest(
            start.AddSeconds(.5), observer, projection, new CatalogQuery(7, 2500), metadata, projectionVersion: "equidistant-reference-v1")).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(512, 512), new(captureId, artifactId, descriptorIdentity), "calibration-v1", visible.Request.ProjectionVersion);
        var pixels = MeasuredStellarAssociationFixtures.Field(512, 512);
        // Actual pixel samples, not injected solver detections. Small independent centroid error is caused by a
        // 0.04-degree roll offset from the input calibration. No renderer/truth object crosses the recipe boundary.
        var shifted = ProjectorFactory.Create(projection with { RollDegrees = 17.04 });
        foreach (var item in visible.Objects.Where(o => o.Kind == CelestialObjectKind.Star))
        {
            if (shifted.Project(item.GeometricHorizontal) is not { } point) continue;
            MeasuredStellarAssociationFixtures.AddGaussian(pixels, point, 1.15, 5000 * Math.Pow(10, -.4 * (item.Magnitude - 2)), 512);
        }
        MeasuredStellarAssociationFixtures.AddNoise(pixels, 1205, 4);
        var raw = MeasuredStellarAssociationFixtures.RawArtifact(pixels, width: 512, height: 512) with
        {
            CaptureId = captureId,
            ArtifactId = artifactId,
            DescriptorIdentitySha256 = descriptorIdentity,
            ObservationStartedUtc = start,
            ObservationEndedUtc = start.AddSeconds(1)
        };
        var produced = await new ProcessingRecipeExecutor().ExecuteAsync(MeasuredStellarAssociationFixtures.Request(raw, scene)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, produced.Status, produced.ReasonCode);
        var product = produced.Products.Single();
        var measured = new ProcessingArtifact(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.Role, product.Variant,
            product.Recipe.IdentitySha256, product.MediaType, null, product.Payload, start, raw.Integration, raw.Compatibility,
            SourceArtifactIds: product.SourceArtifactIds)
        {
            ProductKind = product.Kind,
            SchemaVersion = product.SchemaVersion,
            ContentIdentitySha256 = product.ContentIdentitySha256,
            RecipeDescriptor = product.Recipe.Descriptor,
            CaptureId = raw.CaptureId
        };
        var associations = MeasuredStellarAssociationJson.Parse(product.Payload).Associations!;
        Console.WriteLine($"reference associations={associations.Associations.Count} eligible={associations.Associations.Count(a => a.LabelEligible)} saturated={associations.Associations.Count(a => a.Saturated)} trailed={associations.Associations.Count(a => a.Trailed)} reasons={string.Join(",", associations.UnmatchedPredictions.GroupBy(a => a.ReasonCode).Select(g => $"{g.Key}:{g.Count()}"))}");
        var readoutHash = MeasuredAstrometryJson.ComputeReadoutIdentity(raw.Layout!);
        var context = new MeasuredAstrometryContextV1(MeasuredAstrometryContextV1.CurrentSchemaVersion,
            new(raw.CaptureId!.Value, raw.ArtifactId, raw.DescriptorIdentitySha256!, ProcessingIdentity.ComputePayloadSha256(raw.Payload),
                Hash("reference-observer"), observer, start, start.AddSeconds(1), associations.Measurement.AlgorithmVersion,
                associations.Measurement.SettingsIdentitySha256), projection, "calibration-v1", readoutHash, metadata, stars, true, 7,
            AstrometricConventions.CoordinateModel, package, measured.ArtifactId, associations.AssociationIdentitySha256,
            measured.RecipeIdentitySha256, AstrometricClockFacts.NotSupplied, null);
        return new(raw, scene, measured, associations, context, catalog, new(projection, "calibration-v1", readoutHash));
    }

    internal ProcessingExecutionRequest Request(MeasuredAstrometryContextV1? context = null,
        ProcessingArtifact? raw = null, ProcessingArtifact? measured = null, MeasuredAstrometryOptions? options = null)
    {
        measured ??= Measured;
        return new(BuiltInProcessingRecipes.MeasuredAstrometry, Json(options ?? Options), ProcessingInputSelector.Raw("source"),
            [raw ?? Raw, measured], "astrometry", AuxiliaryInputs:
            [MeasuredAstrometryJson.ContextInput(context ?? Context), MeasuredStellarAssociationFixtures.SceneAuxiliary(Scene),
                new(BuiltInProcessingRecipes.MeasuredStellarAssociationsInputName, ProcessingAuxiliaryInputKind.Artifact,
                    ProcessingInputSelector.RecipeResult(FrameArtifactRole.Metadata, measured.Variant, measured.RecipeIdentitySha256), ArtifactId: measured.ArtifactId)]);
    }

    internal MeasuredAstrometryFixture WithAssociations(MeasuredStellarAssociationsV1 value)
    {
        var measured = Measured with { Payload = MeasuredStellarAssociationJson.Serialize(value), ContentIdentitySha256 = value.AssociationIdentitySha256 };
        return this with
        {
            Associations = value,
            Measured = measured,
            Context = Context with
            {
                MeasuredIdentitySha256 = value.AssociationIdentitySha256,
                Frame = Context.Frame with { DetectionAlgorithmVersion = value.Measurement.AlgorithmVersion, DetectionSettingsIdentitySha256 = value.Measurement.SettingsIdentitySha256 }
            }
        };
    }

    internal MeasuredAstrometryFixture Keep(Func<MeasuredStellarAssociationV1, bool> predicate)
    {
        var kept = Associations.Associations.Where(predicate).ToArray();
        var remaining = Associations.Associations.Where(a => !predicate(a)).Select(a => new MeasuredStellarUnassociatedDetectionV1(
            a.DetectionIndex, a.MeasuredX, a.MeasuredY, a.Flux, a.SignalToNoise, MeasuredStellarAssociationReasonCodes.NearUnassociatedPrediction));
        var unmatched = Associations.UnmatchedPredictions.Concat(Associations.Associations.Where(a => !predicate(a)).Select(a =>
            new MeasuredStellarUnmatchedPredictionV1(a.CatalogId, a.Magnitude, a.ExpectedX, a.ExpectedY, MeasuredStellarAssociationReasonCodes.NoMeasuredSource)))
            .OrderBy(a => a.CatalogId, StringComparer.Ordinal).ToArray();
        return WithAssociations(Associations with
        {
            Associations = kept,
            UnmatchedPredictions = unmatched,
            UnassociatedDetections = Associations.UnassociatedDetections.Concat(remaining).OrderBy(a => a.DetectionIndex).ToArray()
        });
    }
}
