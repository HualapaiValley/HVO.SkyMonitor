using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ProcessingRecipeTests
{
    private static readonly ProcessingCompatibilityIdentity Compatibility = new(
        "rig-v1", "north-up", "none-v1", "full-v1", "sensor-v1", "night-v1", "pipeline-v1");

    [TestMethod]
    [TestCategory("Unit")]
    public void PublicApiContainsNoHostInfrastructureOrDisposableImageTypes()
    {
        var forbidden = new[]
        {
            "SkiaSharp", "AspNetCore", "EntityFramework", "Minio", "StackExchange.Redis",
            "Microsoft.Extensions.Logging", "System.IO.Stream"
        };
        var exposed = typeof(IProcessingRecipeExecutor).Assembly.ExportedTypes
            .SelectMany(type => type.GetMembers().SelectMany(member => member switch
            {
                System.Reflection.MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType),
                System.Reflection.ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
                System.Reflection.PropertyInfo property => [property.PropertyType],
                System.Reflection.FieldInfo field => [field.FieldType],
                System.Reflection.EventInfo eventInfo when eventInfo.EventHandlerType is { } handler => [handler],
                _ => []
            }))
            .SelectMany(FlattenType)
            .Distinct()
            .ToArray();

        Assert.IsFalse(exposed.Any(type => forbidden.Any(name =>
            type.FullName?.Contains(name, StringComparison.Ordinal) == true)));
        Assert.IsFalse(exposed.Any(type => typeof(IDisposable).IsAssignableFrom(type)));

        static IEnumerable<Type> FlattenType(Type type)
        {
            yield return type;
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments().SelectMany(FlattenType))
                {
                    yield return argument;
                }
            }
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CanonicalIdentityIsStableAcrossOptionOrderAndChangesWithEffectiveOptions()
    {
        var executor = new ProcessingRecipeExecutor();
        var input = CreateArtifact(FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono16, 2, 2,
            [0, 0, 0, 64, 0, 128, 255, 255]);
        var first = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.EncodedPreview,
            Json("""{"jpegQuality":80,"asinhStrength":4,"whitePercentile":0.9999,"blackPercentile":0.5}"""),
            ProcessingInputSelector.Raw(), [input], "display")).ConfigureAwait(false);
        var reordered = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.EncodedPreview,
            Json("""{"blackPercentile":0.5,"whitePercentile":0.9999,"asinhStrength":4,"jpegQuality":80}"""),
            ProcessingInputSelector.Raw(), [input], "display")).ConfigureAwait(false);
        var changed = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.EncodedPreview,
            Json("""{"jpegQuality":81}"""),
            ProcessingInputSelector.Raw(), [input], "display")).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, first.Status);
        Assert.AreEqual(first.Products[0].Recipe.IdentitySha256, reordered.Products[0].Recipe.IdentitySha256);
        Assert.AreNotEqual(first.Products[0].Recipe.IdentitySha256, changed.Products[0].Recipe.IdentitySha256);
        Assert.AreNotEqual(first.Products[0].OutputIdentitySha256, changed.Products[0].OutputIdentitySha256);
        Assert.AreEqual(64, first.Products[0].Recipe.IdentitySha256.Length);
        Assert.AreEqual(64, first.Products[0].ChecksumSha256.Length);

        var uppercaseProducer = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.NoOpAnalyzer, EmptyOptions(), ProcessingInputSelector.Raw(),
            [input with { RecipeIdentitySha256 = new string('A', 64) }], "case")).ConfigureAwait(false);
        var lowercaseProducer = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.NoOpAnalyzer, EmptyOptions(), ProcessingInputSelector.Raw(),
            [input with { RecipeIdentitySha256 = new string('a', 64) }], "case")).ConfigureAwait(false);
        Assert.AreEqual(
            uppercaseProducer.Products[0].Recipe.IdentitySha256,
            lowercaseProducer.Products[0].Recipe.IdentitySha256);
        var sourceIds = new[] { input.ArtifactId };
        Assert.AreEqual(
            ProcessingIdentity.CreateOutputIdentity(
                FrameArtifactRole.Preview, "case", new string('A', 64), sourceIds),
            ProcessingIdentity.CreateOutputIdentity(
                FrameArtifactRole.Preview, "case", new string('a', 64), sourceIds));
        Assert.ThrowsExactly<ArgumentException>(() => ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.Preview, "case", "invalid", sourceIds));
        Assert.ThrowsExactly<ArgumentException>(() => ProcessingIdentity.CreateArtifactId("invalid"));
        Assert.ThrowsExactly<ArgumentException>(() => ProcessingIdentity.CreateArtifactId(new string('G', 64)));

        var requestedWithUndefinedOptions = BuiltInProcessingRecipes.CreateRequestedIdentity(
            BuiltInProcessingRecipes.NoOpAnalyzer,
            default,
            ProcessingInputSelector.Raw());
        var requestedWithNullOptions = BuiltInProcessingRecipes.CreateRequestedIdentity(
            BuiltInProcessingRecipes.NoOpAnalyzer,
            Json("null"),
            ProcessingInputSelector.Raw());
        Assert.AreEqual(requestedWithUndefinedOptions.IdentitySha256, requestedWithNullOptions.IdentitySha256);
        Assert.ThrowsExactly<ArgumentException>(() => BuiltInProcessingRecipes.CreateRequestedIdentity(
            "unknown", EmptyOptions(), ProcessingInputSelector.Raw()));
        Assert.ThrowsExactly<ArgumentNullException>(() => BuiltInProcessingRecipes.CreateRequestedIdentity(
            BuiltInProcessingRecipes.NoOpAnalyzer, EmptyOptions(), null!));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ProjectedSceneProducesTypedMetadataWithoutRawPixelsAndBindsSemanticIdentity()
    {
        var sourceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var captureId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var descriptorIdentity = new string('A', 64);
        var source = CreateArtifact(
            FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono8, 2, 2, []) with
        {
            ArtifactId = sourceId,
            CaptureId = captureId,
            DescriptorIdentitySha256 = descriptorIdentity
        };
        var predicted = await CreateProjectedSceneAsync(
            ProjectedSceneKind.Predicted, captureId, sourceId, descriptorIdentity).ConfigureAwait(false);
        var authoritative = await CreateProjectedSceneAsync(
            ProjectedSceneKind.VirtualRenderAuthoritative, captureId, sourceId, descriptorIdentity).ConfigureAwait(false);

        var first = await ExecuteProjectedSceneAsync(source, predicted).ConfigureAwait(false);
        var changed = await ExecuteProjectedSceneAsync(source, authoritative).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, first.Status);
        var product = first.Products.Single();
        Assert.AreEqual(ProcessingProductKind.Metadata, product.Kind);
        Assert.AreEqual(FrameArtifactRole.Metadata, product.Role);
        Assert.IsNull(product.Layout);
        Assert.AreEqual(ProjectedSceneV1.CurrentSchemaVersion, product.SchemaVersion);
        Assert.AreEqual(predicted.SceneIdentitySha256, product.ContentIdentitySha256);
        Assert.AreEqual(ProcessingIdentity.ComputePayloadSha256(product.Payload), product.ChecksumSha256);
        Assert.AreNotEqual(product.ContentIdentitySha256, product.ChecksumSha256);
        Assert.AreNotEqual(product.Recipe.IdentitySha256, changed.Products.Single().Recipe.IdentitySha256);
        Assert.AreNotEqual(product.OutputIdentitySha256, changed.Products.Single().OutputIdentitySha256);

        var projectedArtifact = new ProcessingArtifact(
            ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.Role, product.Variant,
            product.Recipe.IdentitySha256, product.MediaType, product.Layout, product.Payload,
            DateTimeOffset.Parse("2026-08-25T00:00:00Z", CultureInfo.InvariantCulture), product.TotalIntegration,
            product.Compatibility)
        {
            ProductKind = product.Kind,
            SchemaVersion = product.SchemaVersion,
            ContentIdentitySha256 = product.ContentIdentitySha256
        };
        var presentation = PresentationLayerProducers.FromProjectedScene(predicted,
            new PresentationAnnotationStyleV1(ConstellationIds: []));
        var layerProduct = PresentationProcessingProducts.CreateLayerProduct(
            presentation, "scene-presentation", [projectedArtifact], PresentationLayerProducers.SceneProducerVersion);
        Assert.AreEqual(presentation.ContentIdentitySha256, layerProduct.ContentIdentitySha256);
        var w6Style = new PresentationAnnotationStyleV1(ConstellationIds: []);
        Assert.AreEqual(new PresentationColor(96, 96, 96), w6Style.ImageCircleColor ?? new(96, 96, 96));
        Assert.AreEqual(new PresentationColor(255, 255, 255), w6Style.CardinalColor ?? new(255, 255, 255));
        Assert.AreEqual(2, w6Style.CardinalScale);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PresentationLayerProducers.FromProjectedScene(
            predicted, w6Style with { CardinalScale = 9 }));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ProjectedSceneRejectsInvalidSourceDescriptorAndDimensionsWithStableReasons()
    {
        var sourceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var captureId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var descriptorIdentity = new string('A', 64);
        var source = CreateArtifact(
            FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono8, 2, 2, []) with
        {
            ArtifactId = sourceId,
            CaptureId = captureId,
            DescriptorIdentitySha256 = descriptorIdentity
        };
        var scene = await CreateProjectedSceneAsync(
            ProjectedSceneKind.Predicted, captureId, sourceId, descriptorIdentity).ConfigureAwait(false);

        var missing = await new ProcessingRecipeExecutor().ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.ProjectedScene, EmptyOptions(), ProcessingInputSelector.Raw("source"),
            [source], "scene")).ConfigureAwait(false);
        var sourceMismatch = await ExecuteProjectedSceneAsync(
            source with { CaptureId = Guid.NewGuid() }, scene).ConfigureAwait(false);
        var descriptorMismatch = await ExecuteProjectedSceneAsync(
            source with { DescriptorIdentitySha256 = new string('B', 64) }, scene).ConfigureAwait(false);
        var dimensionMismatch = await ExecuteProjectedSceneAsync(
            source with { Layout = source.Layout! with { Width = 3, StrideBytes = 3, ByteLength = 6 } }, scene)
            .ConfigureAwait(false);

        Assert.AreEqual(ProcessingReasonCodes.MissingProjectedScene, missing.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.ProjectedSceneSourceMismatch, sourceMismatch.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.ProjectedSceneDescriptorMismatch, descriptorMismatch.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.ProjectedSceneDimensionMismatch, dimensionMismatch.ReasonCode);

        var payload = ProjectedSceneJson.Serialize(scene);
        var badChecksum = await new ProcessingRecipeExecutor().ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.ProjectedScene, EmptyOptions(), ProcessingInputSelector.Raw("source"), [source], "scene",
            AuxiliaryInputs:
            [
                new ProcessingAuxiliaryInput(
                    "scene", ProcessingAuxiliaryInputKind.CanonicalJson,
                    SchemaVersion: ProjectedSceneV1.CurrentSchemaVersion,
                    IdentitySha256: scene.SceneIdentitySha256,
                    Payload: payload)
                {
                    ChecksumSha256 = new string('F', 64)
                }
            ])).ConfigureAwait(false);
        var badSemanticIdentity = await new ProcessingRecipeExecutor().ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.ProjectedScene, EmptyOptions(), ProcessingInputSelector.Raw("source"), [source], "scene",
            AuxiliaryInputs:
            [
                new ProcessingAuxiliaryInput(
                    "scene", ProcessingAuxiliaryInputKind.CanonicalJson,
                    SchemaVersion: ProjectedSceneV1.CurrentSchemaVersion,
                    IdentitySha256: new string('F', 64),
                    Payload: payload)
                {
                    ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(payload)
                }
            ])).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.InvalidInput, badChecksum.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidProjectedScene, badSemanticIdentity.ReasonCode);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ProcessingProductPreservesOriginalPositionalConstructorAndDeconstructShape()
    {
        var source = CreateArtifact(FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono8, 1, 1, [1]);
        var recipe = BuiltInProcessingRecipes.CreateRequestedIdentity(
            BuiltInProcessingRecipes.NoOpAnalyzer, EmptyOptions(), ProcessingInputSelector.Raw("source"));
        var product = new ProcessingProduct(
            FrameArtifactRole.Metadata, "facts", new string('A', 64), "application/json", null, new byte[] { 1 },
            new string('B', 64), recipe, [], [source.ArtifactId], TimeSpan.Zero, Compatibility)
        {
            Kind = ProcessingProductKind.Metadata,
            SchemaVersion = "facts-v1",
            ContentIdentitySha256 = new string('C', 64)
        };

        var (_, _, _, _, _, _, _, _, _, _, _, compatibility) = product;

        Assert.AreEqual(Compatibility, compatibility);
        Assert.AreEqual(ProcessingProductKind.Metadata, product.Kind);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ProcessingArtifactPreservesOriginalPositionalConstructorAndDeconstructShape()
    {
        var artifact = new ProcessingArtifact(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            FrameArtifactRole.Raw,
            "source",
            new string('A', 64),
            "application/x-hvo-frame",
            CreateLayout(1, 1, CameraPixelFormat.Mono8),
            new byte[] { 1 },
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(1),
            Compatibility,
            1,
            [],
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            new ProcessingCaptureConditions(1, 0, 0))
        {
            CaptureId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
            DescriptorIdentitySha256 = new string('B', 64)
        };

        var (_, _, _, _, _, _, _, _, _, _, _, _, _, _, conditions) = artifact;

        Assert.AreEqual(1, conditions!.Gain);
        Assert.IsNotNull(artifact.CaptureId);
        Assert.AreEqual(new string('B', 64), artifact.DescriptorIdentitySha256);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ProcessingAuxiliaryInputPreservesOriginalPositionalConstructorAndDeconstructShape()
    {
        var artifactId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var auxiliary = new ProcessingAuxiliaryInput(
            "scene",
            ProcessingAuxiliaryInputKind.CanonicalJson,
            null,
            "scene-v1",
            new string('A', 64),
            new byte[] { 1 },
            artifactId)
        {
            ChecksumSha256 = new string('B', 64)
        };

        var (name, kind, selector, schema, identity, payload, deconstructedArtifactId) = auxiliary;

        Assert.AreEqual("scene", name);
        Assert.AreEqual(ProcessingAuxiliaryInputKind.CanonicalJson, kind);
        Assert.IsNull(selector);
        Assert.AreEqual("scene-v1", schema);
        Assert.AreEqual(new string('A', 64), identity);
        Assert.AreEqual(1, payload.Length);
        Assert.AreEqual(artifactId, deconstructedArtifactId);
        Assert.AreEqual(new string('B', 64), auxiliary.ChecksumSha256);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task LinearNormalizationPacksRowsAndRecordsNoCorrectionProvenance()
    {
        var source = CreateArtifact(
            FrameArtifactRole.Raw,
            "source",
            CameraPixelFormat.Mono16,
            2,
            2,
            [1, 0, 2, 0, 99, 99, 3, 0, 4, 0, 88, 88],
            stride: 6) with
        {
            Layout = CreateLayout(2, 2, CameraPixelFormat.Mono16, 6) with
            {
                BlackLevel = 64,
                WhiteLevel = 16_383
            }
        };
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(
            BuiltInProcessingRecipes.LinearNormalization,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            [source],
            "none")).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        var product = outcome.Products[0];
        Assert.AreEqual(FrameArtifactRole.Calibrated, product.Role);
        Assert.AreEqual("none", product.Variant);
        CollectionAssert.AreEqual(new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 }, product.Payload.ToArray());
        Assert.AreEqual(4, product.Layout!.StrideBytes);
        Assert.AreEqual(64d, product.Layout.BlackLevel);
        Assert.AreEqual(16_383d, product.Layout.WhiteLevel);
        Assert.AreEqual("linear-normalization-none-v1", product.Recipe.Descriptor.ImplementationVersion);
        Assert.AreEqual(source.ArtifactId, product.SourceArtifactIds[0]);
        Assert.AreEqual(source.Compatibility, product.Compatibility);

        var executor = new ProcessingRecipeExecutor();
        var missing = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.LinearNormalization, EmptyOptions(), ProcessingInputSelector.Raw("missing"),
            [source], "none")).ConfigureAwait(false);
        var invalidLayout = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.LinearNormalization, EmptyOptions(), ProcessingInputSelector.Raw(),
            [source with { Layout = null }], "none")).ConfigureAwait(false);
        var calibrated = source with { ArtifactId = Guid.NewGuid(), Role = FrameArtifactRole.Calibrated };
        var invalidRole = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.LinearNormalization, EmptyOptions(), ProcessingInputSelector.Calibrated(),
            [calibrated], "none")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.MissingInput, missing.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLayout, invalidLayout.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidSelector, invalidRole.ReasonCode);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PreviewSupportsMonoAndBayerWithoutChangingSources()
    {
        var monoBytes = new byte[] { 0, 0, 0, 64, 0, 128, 255, 255 };
        var bayerBytes = new byte[]
        {
            0, 16, 0, 32,
            0, 48, 0, 64
        };
        var mono = CreateArtifact(FrameArtifactRole.Raw, "mono", CameraPixelFormat.Mono16, 2, 2, monoBytes);
        var bayer = CreateArtifact(FrameArtifactRole.Calibrated, "color", CameraPixelFormat.BayerRggb16, 2, 2, bayerBytes);
        var executor = new ProcessingRecipeExecutor();

        var monoOutcome = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.EncodedPreview, EmptyOptions(), ProcessingInputSelector.Raw("mono"), [mono], "mono-jpeg")).ConfigureAwait(false);
        var colorOutcome = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.EncodedPreview, EmptyOptions(), ProcessingInputSelector.Calibrated("color"), [bayer], "color-jpeg")).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, monoOutcome.Status);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, colorOutcome.Status);
        Assert.AreEqual(JpegImageCodec.MediaType, monoOutcome.Products[0].MediaType);
        Assert.AreEqual(CameraPixelFormat.Mono8, JpegImageCodec.DecodeJpeg(monoOutcome.Products[0].Payload).PixelFormat);
        Assert.AreEqual(CameraPixelFormat.Rgb24, JpegImageCodec.DecodeJpeg(colorOutcome.Products[0].Payload).PixelFormat);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 64, 0, 128, 255, 255 }, monoBytes);
        CollectionAssert.AreEqual(new byte[] { 0, 16, 0, 32, 0, 48, 0, 64 }, bayerBytes);
        Assert.IsTrue(colorOutcome.Products[0].Algorithms.Any(item => item.Name == "rggb-demosaic"));

        var mono8 = CreateArtifact(FrameArtifactRole.Raw, "mono8", CameraPixelFormat.Mono8, 2, 1, [1, 2, 99],
            stride: 3);
        var rgb24 = CreateArtifact(FrameArtifactRole.Calibrated, "rgb", CameraPixelFormat.Rgb24, 1, 1, [3, 4, 5]);
        var packedMono = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.EncodedPreview,
            Json("""{"outputEncoding":"Packed"}"""),
            ProcessingInputSelector.Raw("mono8"),
            [mono8],
            "mono-packed")).ConfigureAwait(false);
        var packedRgb = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.EncodedPreview,
            Json("""{"outputEncoding":"Packed"}"""),
            ProcessingInputSelector.Calibrated("rgb"),
            [rgb24],
            "rgb-packed")).ConfigureAwait(false);
        var invalidRole = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.EncodedPreview,
            EmptyOptions(),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Metadata, "invalid", mono.RecipeIdentitySha256),
            [mono with { ArtifactId = Guid.NewGuid(), Role = FrameArtifactRole.Metadata, Variant = "invalid" }],
            "invalid")).ConfigureAwait(false);
        var missing = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.EncodedPreview,
            EmptyOptions(),
            ProcessingInputSelector.Raw("missing"),
            [mono],
            "missing")).ConfigureAwait(false);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, packedMono.Products[0].Payload.ToArray());
        CollectionAssert.AreEqual(new byte[] { 3, 4, 5 }, packedRgb.Products[0].Payload.ToArray());
        Assert.AreEqual(CameraPixelFormat.Mono8, packedMono.Products[0].Layout!.PixelFormat);
        Assert.AreEqual(CameraPixelFormat.Rgb24, packedRgb.Products[0].Layout!.PixelFormat);
        Assert.AreEqual(ProcessingReasonCodes.InvalidSelector, invalidRole.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.MissingInput, missing.ReasonCode);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task JpegEncodingProducesFullSizeAndBoundedAnnotatedVariants()
    {
        var source = CreateArtifact(
            FrameArtifactRole.AnnotatedPreview,
            "packed-annotated",
            CameraPixelFormat.Mono8,
            4,
            2,
            [0, 32, 64, 96, 128, 160, 192, 255]);
        var selector = ProcessingInputSelector.RecipeResult(
            source.Role,
            source.Variant,
            source.RecipeIdentitySha256);
        var executor = new ProcessingRecipeExecutor();

        var full = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.JpegEncoding,
            Json("""{"jpegQuality":90}"""),
            selector,
            [source],
            "annotated-final-jpeg")).ConfigureAwait(false);
        var thumbnail = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.JpegEncoding,
            Json("""{"jpegQuality":80,"maximumDimension":2}"""),
            selector,
            [source],
            "annotated-thumbnail-2-jpeg")).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, full.Status);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, thumbnail.Status);
        Assert.AreEqual(FrameArtifactRole.AnnotatedPreview, full.Products[0].Role);
        Assert.AreEqual(JpegImageCodec.MediaType, full.Products[0].MediaType);
        Assert.IsNull(full.Products[0].Layout);
        Assert.AreEqual(4, JpegImageCodec.DecodeJpeg(full.Products[0].Payload).Width);
        var decodedThumbnail = JpegImageCodec.DecodeJpeg(thumbnail.Products[0].Payload);
        Assert.AreEqual(2, decodedThumbnail.Width);
        Assert.AreEqual(1, decodedThumbnail.Height);
        Assert.IsTrue(thumbnail.Products[0].Algorithms.Any(static algorithm => algorithm.Name == "downsample"));
        CollectionAssert.AreEqual(new byte[] { 0, 32, 64, 96, 128, 160, 192, 255 }, source.Payload.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task AnnotationAcceptsExactPreviewRecipeResultAndBindsGeometryProvenance()
    {
        var raw = CreateArtifact(FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono16, 8, 8,
            Enumerable.Range(0, 64).SelectMany(value => new[] { (byte)value, (byte)0 }).ToArray());
        var executor = new ProcessingRecipeExecutor();
        var preview = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.EncodedPreview, EmptyOptions(), ProcessingInputSelector.Raw(), [raw], "display")).ConfigureAwait(false);
        var previewProduct = preview.Products[0];
        var previewArtifact = new ProcessingArtifact(
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            previewProduct.Role,
            previewProduct.Variant,
            previewProduct.Recipe.IdentitySha256,
            previewProduct.MediaType,
            previewProduct.Layout,
            previewProduct.Payload,
            DateTimeOffset.Parse("2025-01-15T08:00:01Z", CultureInfo.InvariantCulture),
            previewProduct.TotalIntegration,
            previewProduct.Compatibility);
        var annotation = new ProcessingAnnotationInput(
            [new ProjectedAnnotationObject("star:1", "STAR", new PixelPoint(4, 4))],
            [new ProjectedAnnotationSegment("ORI", new PixelPoint(0, 0), new PixelPoint(7, 7))],
            new PreviewTransform(1, 1),
            null,
            new string('A', 64));

        var outcome = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.Annotation,
            EmptyOptions(),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview,
                previewProduct.Variant,
                previewProduct.Recipe.IdentitySha256),
            [previewArtifact],
            "stars",
            annotation)).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        Assert.AreEqual(FrameArtifactRole.AnnotatedPreview, outcome.Products[0].Role);
        Assert.AreEqual(previewArtifact.ArtifactId, outcome.Products[0].SourceArtifactIds[0]);
        Assert.IsTrue(outcome.Products[0].Algorithms.Any(item => item.Name == "jpeg-decode"));
        Assert.AreEqual(8, JpegImageCodec.DecodeJpeg(outcome.Products[0].Payload).Width);

        var sceneArtifact = previewArtifact with
        {
            ArtifactId = Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Role = FrameArtifactRole.Metadata,
            Variant = "projected-scene-v1",
            RecipeIdentitySha256 = new string('C', 64),
            MediaType = "application/json",
            Layout = null,
            Payload = "{}"u8.ToArray()
        };
        var sceneAuxiliary = new ProcessingAuxiliaryInput(
            "projected-scene", ProcessingAuxiliaryInputKind.Artifact,
            ProcessingInputSelector.RecipeResult(
                sceneArtifact.Role, sceneArtifact.Variant, sceneArtifact.RecipeIdentitySha256),
            ArtifactId: sceneArtifact.ArtifactId);
        var withScene = await executor.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.Annotation,
            EmptyOptions(),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview, previewProduct.Variant, previewProduct.Recipe.IdentitySha256),
            [previewArtifact, sceneArtifact],
            "stars",
            annotation,
            AuxiliaryInputs: [sceneAuxiliary],
            InputArtifactId: previewArtifact.ArtifactId)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, withScene.Status);
        CollectionAssert.AreEqual(
            new[] { previewArtifact.ArtifactId, sceneArtifact.ArtifactId },
            withScene.Products[0].SourceArtifactIds.ToArray());
        Assert.AreNotEqual(outcome.Products[0].Recipe.IdentitySha256, withScene.Products[0].Recipe.IdentitySha256);
        Assert.AreNotEqual(outcome.Products[0].OutputIdentitySha256, withScene.Products[0].OutputIdentitySha256);

        var metadata = new MetadataCornerOverlay(["identity"], ["schedule"], ["environment"], ["provenance"]);
        var metadataOutcome = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.Annotation,
            EmptyOptions(),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview,
                previewProduct.Variant,
                previewProduct.Recipe.IdentitySha256),
            [previewArtifact],
            "metadata",
            annotation with { MetadataOverlay = metadata })).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, metadataOutcome.Status);

        MetadataCornerOverlay[] invalidMetadata =
        [
            metadata with { Scale = 0 },
            metadata with { Inset = 65 },
            metadata with { LineSpacing = 17 },
            metadata with { TopLeft = null! },
            metadata with { TopLeft = Enumerable.Repeat("line", 9).ToArray() },
            metadata with { TopLeft = [""] },
            metadata with { TopLeft = [new string('X', 65)] },
            metadata with { TopLeft = ["control\nline"] }
        ];
        foreach (var invalid in invalidMetadata)
        {
            var invalidMetadataOutcome = await executor.ExecuteAsync(Request(
                BuiltInProcessingRecipes.Annotation,
                EmptyOptions(),
                ProcessingInputSelector.RecipeResult(
                    FrameArtifactRole.Preview,
                    previewProduct.Variant,
                    previewProduct.Recipe.IdentitySha256),
                [previewArtifact],
                "invalid-metadata",
                annotation with { MetadataOverlay = invalid })).ConfigureAwait(false);
            Assert.AreEqual(ProcessingReasonCodes.InvalidAnnotation, invalidMetadataOutcome.ReasonCode);
        }

        var differentGeometry = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.Annotation,
            EmptyOptions(),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview,
                previewProduct.Variant,
                previewProduct.Recipe.IdentitySha256),
            [previewArtifact],
            "stars",
            annotation with { ProvenanceSha256 = new string('B', 64) })).ConfigureAwait(false);
        Assert.AreNotEqual(outcome.Products[0].Recipe.IdentitySha256, differentGeometry.Products[0].Recipe.IdentitySha256);

        var lowercaseProvenance = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.Annotation,
            EmptyOptions(),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview,
                previewProduct.Variant,
                previewProduct.Recipe.IdentitySha256),
            [previewArtifact],
            "stars",
            annotation with { ProvenanceSha256 = new string('a', 64) })).ConfigureAwait(false);
        Assert.AreEqual(outcome.Products[0].Recipe.IdentitySha256, lowercaseProvenance.Products[0].Recipe.IdentitySha256);

        var differentTransform = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.Annotation,
            EmptyOptions(),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview,
                previewProduct.Variant,
                previewProduct.Recipe.IdentitySha256),
            [previewArtifact],
            "stars",
            annotation with { Transform = new PreviewTransform(0.5, 0.5) })).ConfigureAwait(false);
        Assert.AreNotEqual(outcome.Products[0].Recipe.IdentitySha256, differentTransform.Products[0].Recipe.IdentitySha256);

        var linearInputs = new[]
        {
            (Artifact: raw, Selector: ProcessingInputSelector.Raw()),
            (Artifact: raw with { ArtifactId = Guid.NewGuid(), Role = FrameArtifactRole.Calibrated },
                Selector: ProcessingInputSelector.Calibrated()),
            (Artifact: raw with { ArtifactId = Guid.NewGuid(), Role = FrameArtifactRole.Combined },
                Selector: ProcessingInputSelector.Combined())
        };
        foreach (var (artifact, selector) in linearInputs)
        {
            var linearAnnotation = await executor.ExecuteAsync(Request(
                BuiltInProcessingRecipes.Annotation,
                Json("""{"outputEncoding":"Packed"}"""),
                selector,
                [artifact],
                "linear-annotation",
                annotation)).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, linearAnnotation.Status);
            Assert.AreEqual("application/x-hvo-packed-image", linearAnnotation.Products[0].MediaType);
        }

        var formatInputs = new[]
        {
            CreateArtifact(FrameArtifactRole.Raw, "bayer", CameraPixelFormat.BayerRggb16, 2, 2,
                [0, 16, 0, 32, 0, 48, 0, 64]),
            CreateArtifact(FrameArtifactRole.Calibrated, "mono8", CameraPixelFormat.Mono8, 2, 2,
                [0, 64, 128, 255]) with { ArtifactId = Guid.NewGuid() },
            CreateArtifact(FrameArtifactRole.Combined, "rgb", CameraPixelFormat.Rgb24, 2, 2,
                [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]) with { ArtifactId = Guid.NewGuid() }
        };
        foreach (var artifact in formatInputs)
        {
            var selector = artifact.Role switch
            {
                FrameArtifactRole.Raw => ProcessingInputSelector.Raw(artifact.Variant),
                FrameArtifactRole.Calibrated => ProcessingInputSelector.Calibrated(artifact.Variant),
                _ => ProcessingInputSelector.Combined(artifact.Variant)
            };
            var formatted = await executor.ExecuteAsync(Request(
                BuiltInProcessingRecipes.Annotation,
                Json("""{"outputEncoding":"Packed"}"""),
                selector,
                [artifact],
                "formatted",
                annotation)).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, formatted.Status);
        }

        var missingAnnotation = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.Annotation,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            [raw],
            "missing")).ConfigureAwait(false);
        var invalidRecipeResult = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.Annotation,
            EmptyOptions(),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Metadata, "metadata", new string('A', 64)),
            [raw with
            {
                ArtifactId = Guid.NewGuid(),
                Role = FrameArtifactRole.Metadata,
                Variant = "metadata",
                RecipeIdentitySha256 = new string('A', 64)
            }],
            "invalid",
            annotation)).ConfigureAwait(false);
        var missingInput = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.Annotation,
            EmptyOptions(),
            ProcessingInputSelector.Raw("missing"),
            [raw],
            "missing",
            annotation)).ConfigureAwait(false);
        var invalidLayout = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.Annotation,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            [raw with { Layout = null }],
            "invalid-layout",
            annotation)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.MissingAnnotation, missingAnnotation.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidSelector, invalidRecipeResult.ReasonCode);

        var jpegWithLayout = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.Annotation,
            EmptyOptions(),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview, previewProduct.Variant, previewProduct.Recipe.IdentitySha256),
            [previewArtifact with { Layout = raw.Layout }],
            "invalid-jpeg",
            annotation)).ConfigureAwait(false);
        var rawJpeg = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.Annotation,
            EmptyOptions(),
            ProcessingInputSelector.Raw("source"),
            [previewArtifact with { Role = FrameArtifactRole.Raw, Variant = "source" }],
            "invalid-jpeg",
            annotation)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLayout, jpegWithLayout.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLayout, rawJpeg.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.MissingInput, missingInput.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLayout, invalidLayout.ReasonCode);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RollingMeanUsesNewestCompatibleWindowAndOrderedLineage()
    {
        var ids = new[]
        {
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000002"),
            Guid.Parse("30000000-0000-0000-0000-000000000003")
        };
        var start = DateTimeOffset.Parse("2025-01-15T08:00:00Z", CultureInfo.InvariantCulture);
        var mutableNewest = new byte[] { 40, 0 };
        var inputs = new[]
        {
            CreateArtifact(FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono16, 1, 1, [10, 0], ids[0], start),
            CreateArtifact(FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono16, 1, 1, [20, 0], ids[1], start.AddSeconds(1)),
            CreateArtifact(FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono16, 1, 1, mutableNewest, ids[2], start.AddSeconds(2))
        };
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean,
            Json("""{"maximumFrameCount":2}"""),
            ProcessingInputSelector.Raw("source"),
            inputs,
            "mean-2")).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        CollectionAssert.AreEqual(new byte[] { 30, 0 }, outcome.Products[0].Payload.ToArray());
        CollectionAssert.AreEqual(ids[1..], outcome.Products[0].SourceArtifactIds.ToArray());
        Assert.AreEqual(TimeSpan.FromSeconds(2), outcome.Products[0].TotalIntegration);
        Assert.AreEqual("linear16-arithmetic-mean-v1", outcome.Products[0].Recipe.Descriptor.ImplementationVersion);

        var sequenceOrdered = await new ProcessingRecipeExecutor().ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean,
            EmptyOptions(),
            ProcessingInputSelector.Raw("source"),
            [inputs[0] with { CaptureSequence = 10, CreatedUtc = start.AddSeconds(5) },
             inputs[1] with { CaptureSequence = 11, CreatedUtc = start }],
            "sequence-ordered")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, sequenceOrdered.Status);
        CollectionAssert.AreEqual(ids[..2], sequenceOrdered.Products[0].SourceArtifactIds.ToArray());
        var mixedSequence = await new ProcessingRecipeExecutor().ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean,
            EmptyOptions(),
            ProcessingInputSelector.Raw("source"),
            [inputs[0] with { CaptureSequence = 10 }, inputs[1]],
            "mixed-sequence")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLineage, mixedSequence.ReasonCode);

        var integrationBounded = await new ProcessingRecipeExecutor().ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean,
            Json("""{"maximumFrameCount":3,"maximumIntegrationMilliseconds":1500}"""),
            ProcessingInputSelector.Raw("source"),
            inputs,
            "integration-bounded")).ConfigureAwait(false);
        var ageBounded = await new ProcessingRecipeExecutor().ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean,
            Json("""{"maximumFrameCount":3,"maximumAgeMilliseconds":500}"""),
            ProcessingInputSelector.Raw("source"),
            inputs,
            "age-bounded")).ConfigureAwait(false);
        Assert.ContainsSingle(integrationBounded.Products[0].SourceArtifactIds);
        Assert.ContainsSingle(ageBounded.Products[0].SourceArtifactIds);
        Assert.AreEqual(ids[2], integrationBounded.Products[0].SourceArtifactIds[0]);
        Assert.AreEqual(ids[2], ageBounded.Products[0].SourceArtifactIds[0]);

        var bayerInputs = new[]
        {
            CreateArtifact(FrameArtifactRole.Calibrated, "bayer", CameraPixelFormat.BayerRggb16, 2, 2,
                [0, 0, 10, 0, 20, 0, 30, 0], Guid.NewGuid(), start),
            CreateArtifact(FrameArtifactRole.Calibrated, "bayer", CameraPixelFormat.BayerRggb16, 2, 2,
                [10, 0, 20, 0, 30, 0, 40, 0], Guid.NewGuid(), start.AddSeconds(1))
        };
        var bayerMean = await new ProcessingRecipeExecutor().ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean,
            EmptyOptions(),
            ProcessingInputSelector.Calibrated("bayer"),
            bayerInputs,
            "bayer-mean")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, bayerMean.Status);
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, bayerMean.Products[0].Layout!.PixelFormat);

        var producedBytes = outcome.Products[0].Payload.ToArray();
        mutableNewest[0] = 255;
        CollectionAssert.AreEqual(producedBytes, outcome.Products[0].Payload.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RollingMeanRejectsEveryCompatibilityMismatchWithoutRawFallback()
    {
        var first = CreateArtifact(FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono16, 1, 1, [10, 0]);
        var changed = first with
        {
            ArtifactId = Guid.NewGuid(),
            Compatibility = first.Compatibility with { Calibration = "dark-v2" }
        };
        var executor = new ProcessingRecipeExecutor();

        var compatibilityMismatches = new[]
        {
            changed,
            changed with { Compatibility = first.Compatibility with { Rig = "rig-v2" } },
            changed with { Compatibility = first.Compatibility with { Orientation = "east-up" } },
            changed with { Compatibility = first.Compatibility with { Mask = "crop-v2" } },
            changed with { Compatibility = first.Compatibility with { Sensor = "sensor-v2" } },
            changed with { Compatibility = first.Compatibility with { SetpointRegime = "day-v1" } },
            changed with { Compatibility = first.Compatibility with { ProcessingProfile = "pipeline-v2" } }
        };
        foreach (var mismatch in compatibilityMismatches)
        {
            var incompatible = await executor.ExecuteAsync(Request(
                BuiltInProcessingRecipes.RollingMean, EmptyOptions(), ProcessingInputSelector.Raw(), [first, mismatch], "mean")).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, incompatible.Status);
            Assert.AreEqual(ProcessingReasonCodes.IncompatibleInput, incompatible.ReasonCode);
        }

        var strideMismatch = changed with
        {
            Compatibility = first.Compatibility,
            Layout = changed.Layout! with { StrideBytes = 4, ByteLength = 4 },
            Payload = new byte[] { 10, 0, 0, 0 }
        };
        var incompatibleLayout = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean, EmptyOptions(), ProcessingInputSelector.Raw(), [first, strideMismatch], "mean")).ConfigureAwait(false);
        var combinedMissing = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.ImageQuality, EmptyOptions(), ProcessingInputSelector.Combined(), [first], "combined-quality")).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, incompatibleLayout.Status);
        Assert.AreEqual(ProcessingReasonCodes.IncompatibleInput, incompatibleLayout.ReasonCode);
        Assert.AreEqual(ProcessingOutcomeStatus.Skipped, combinedMissing.Status);
        Assert.AreEqual(ProcessingReasonCodes.MissingInput, combinedMissing.ReasonCode);

        var outOfOrder = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean, EmptyOptions(), ProcessingInputSelector.Raw(),
            [changed with { CreatedUtc = first.CreatedUtc.AddSeconds(1) }, first], "mean")).ConfigureAwait(false);
        var duplicate = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean, EmptyOptions(), ProcessingInputSelector.Raw(),
            [first, first], "mean")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLineage, outOfOrder.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLineage, duplicate.ReasonCode);

        var missing = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean, EmptyOptions(), ProcessingInputSelector.Raw("missing"),
            [first], "mean")).ConfigureAwait(false);
        var invalidSelector = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean,
            EmptyOptions(),
            new ProcessingInputSelector(ProcessingInputKind.Raw, FrameArtifactRole.Calibrated),
            [first],
            "mean")).ConfigureAwait(false);
        var recipeResultSource = first with
        {
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Combined,
            Variant = "prior-mean",
            RecipeIdentitySha256 = new string('A', 64)
        };
        var recipeResult = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean,
            EmptyOptions(),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Combined, recipeResultSource.Variant, recipeResultSource.RecipeIdentitySha256),
            [recipeResultSource],
            "mean")).ConfigureAwait(false);
        var missingLayout = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean, EmptyOptions(), ProcessingInputSelector.Raw(),
            [first with { Layout = null }], "mean")).ConfigureAwait(false);
        var mono8 = CreateArtifact(FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono8, 1, 1, [1]);
        var unsupportedLinear = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.RollingMean, EmptyOptions(), ProcessingInputSelector.Raw(), [mono8], "mean")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.MissingInput, missing.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidSelector, invalidSelector.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidSelector, recipeResult.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.IncompatibleInput, missingLayout.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.IncompatibleInput, unsupportedLinear.ReasonCode);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SameRoleVariantsAndExactRecipeSelectorsAreUnambiguous()
    {
        var first = CreateArtifact(FrameArtifactRole.Preview, "wide", CameraPixelFormat.Mono8, 1, 1, [1]) with
        {
            RecipeIdentitySha256 = new string('A', 64)
        };
        var second = CreateArtifact(FrameArtifactRole.Preview, "wide", CameraPixelFormat.Mono8, 1, 1, [2]) with
        {
            ArtifactId = Guid.NewGuid(),
            RecipeIdentitySha256 = new string('B', 64)
        };
        var executor = new ProcessingRecipeExecutor();
        var exact = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.NoOpAnalyzer,
            EmptyOptions(),
            ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, "wide", new string('B', 64)),
            [first, second],
            "noop")).ConfigureAwait(false);
        var ambiguous = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.NoOpAnalyzer,
            EmptyOptions(),
            new ProcessingInputSelector(ProcessingInputKind.RecipeResult, FrameArtifactRole.Preview, "wide", null),
            [first, second],
            "noop")).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, exact.Status);
        Assert.AreEqual(second.ArtifactId, exact.Products[0].SourceArtifactIds[0]);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, ambiguous.Status);
        Assert.AreEqual(ProcessingReasonCodes.InvalidSelector, ambiguous.ReasonCode);

        var raw = CreateArtifact(FrameArtifactRole.Raw, "raw", CameraPixelFormat.Mono8, 1, 1, [1]);
        var rawDuplicate = raw with { ArtifactId = Guid.NewGuid() };
        var genuinelyAmbiguous = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.NoOpAnalyzer,
            EmptyOptions(),
            ProcessingInputSelector.Raw("raw"),
            [raw, rawDuplicate],
            "noop")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.AmbiguousInput, genuinelyAmbiguous.ReasonCode);

        var invalidSelectors = new[]
        {
            new ProcessingInputSelector(ProcessingInputKind.Raw, FrameArtifactRole.Calibrated),
            new ProcessingInputSelector(ProcessingInputKind.Raw, FrameArtifactRole.Raw, " "),
            new ProcessingInputSelector(ProcessingInputKind.Raw, FrameArtifactRole.Raw, RecipeIdentitySha256: new string('A', 64)),
            new ProcessingInputSelector(ProcessingInputKind.Calibrated, FrameArtifactRole.Raw),
            new ProcessingInputSelector(ProcessingInputKind.Calibrated, FrameArtifactRole.Calibrated, " "),
            new ProcessingInputSelector(ProcessingInputKind.Calibrated, FrameArtifactRole.Calibrated,
                RecipeIdentitySha256: new string('A', 64)),
            new ProcessingInputSelector(ProcessingInputKind.Combined, FrameArtifactRole.Raw),
            new ProcessingInputSelector(ProcessingInputKind.Combined, FrameArtifactRole.Combined, " "),
            new ProcessingInputSelector(ProcessingInputKind.Combined, FrameArtifactRole.Combined,
                RecipeIdentitySha256: new string('A', 64)),
            ProcessingInputSelector.RecipeResult(FrameArtifactRole.Raw, "raw", new string('A', 64)),
            ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, " ", new string('A', 64)),
            new ProcessingInputSelector(ProcessingInputKind.RecipeResult, FrameArtifactRole.Preview, "wide", "bad"),
            new ProcessingInputSelector((ProcessingInputKind)999, FrameArtifactRole.Raw)
        };
        foreach (var selector in invalidSelectors)
        {
            var invalid = await executor.ExecuteAsync(Request(
                BuiltInProcessingRecipes.NoOpAnalyzer, EmptyOptions(), selector, [raw], "noop")).ConfigureAwait(false);
            Assert.AreEqual(ProcessingReasonCodes.InvalidSelector, invalid.ReasonCode);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ImageQualityAndNoOpProduceDeterministicStructuredProducts()
    {
        var input = CreateArtifact(FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono8, 2, 2, [0, 1, 254, 255]);
        var executor = new ProcessingRecipeExecutor();
        var quality = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.ImageQuality, EmptyOptions(), ProcessingInputSelector.Raw(), [input], "quality")).ConfigureAwait(false);
        var noOp = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.NoOpAnalyzer, EmptyOptions(), ProcessingInputSelector.Raw(), [input], "noop")).ConfigureAwait(false);

        using var qualityJson = JsonDocument.Parse(quality.Products[0].Payload);
        Assert.AreEqual(4L, qualityJson.RootElement.GetProperty("sampleCount").GetInt64());
        Assert.AreEqual(510UL, qualityJson.RootElement.GetProperty("sum").GetUInt64());
        Assert.AreEqual(1L, qualityJson.RootElement.GetProperty("zeroCount").GetInt64());
        Assert.AreEqual(1L, qualityJson.RootElement.GetProperty("saturatedCount").GetInt64());
        Assert.AreEqual("{\"status\":\"ok\"}", System.Text.Encoding.UTF8.GetString(noOp.Products[0].Payload.Span));
        Assert.AreEqual(FrameArtifactRole.Metadata, noOp.Products[0].Role);

        var formats = new[]
        {
            CreateArtifact(FrameArtifactRole.Raw, "mono16", CameraPixelFormat.Mono16, 1, 1, [10, 0]),
            CreateArtifact(FrameArtifactRole.Raw, "bayer", CameraPixelFormat.BayerRggb16, 2, 2,
                [0, 0, 10, 0, 20, 0, 30, 0]),
            CreateArtifact(FrameArtifactRole.Raw, "rgb", CameraPixelFormat.Rgb24, 1, 1, [1, 2, 3])
        };
        foreach (var artifact in formats)
        {
            var formatQuality = await executor.ExecuteAsync(Request(
                BuiltInProcessingRecipes.ImageQuality,
                EmptyOptions(),
                ProcessingInputSelector.Raw(artifact.Variant),
                [artifact with { Layout = artifact.Layout! with { WhiteLevel = null } }],
                "quality")).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, formatQuality.Status);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ExecutorRejectsMalformedAuxiliariesAndMismatchedInputArtifact()
    {
        var input = CreateArtifact(FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono16, 1, 1, [1, 0]);
        var other = input with { ArtifactId = Guid.NewGuid(), Variant = "other" };
        var payload = Encoding.UTF8.GetBytes("{\"a\":[{\"b\":1}]}");
        var identity = ProcessingIdentity.ComputePayloadSha256(payload);
        var validCanonical = new ProcessingAuxiliaryInput(
            "context", ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: "context-v1", IdentitySha256: identity, Payload: payload);
        var validArtifact = new ProcessingAuxiliaryInput(
            "artifact", ProcessingAuxiliaryInputKind.Artifact,
            ProcessingInputSelector.Raw("other"), ArtifactId: other.ArtifactId);
        var invalidJson = Encoding.UTF8.GetBytes("{");
        var duplicateJson = Encoding.UTF8.GetBytes("{\"a\":1,\"A\":2}");
        var noncanonicalJson = Encoding.UTF8.GetBytes("{ \"a\": 1 }");
        var invalidAuxiliaryCases = new IReadOnlyList<ProcessingAuxiliaryInput>[]
        {
            Enumerable.Range(0, 33).Select(index => validCanonical with { Name = $"context-{index}" }).ToArray(),
            new ProcessingAuxiliaryInput[] { null! },
            [validCanonical with { Name = " " }],
            [validCanonical with { Name = new string('a', 65) }],
            [validCanonical, validCanonical with { Name = "CONTEXT" }],
            [validArtifact with { Selector = null }],
            [validArtifact with { SchemaVersion = "invalid" }],
            [validArtifact with { IdentitySha256 = new string('A', 64) }],
            [validArtifact with { Payload = payload }],
            [validArtifact with { ArtifactId = Guid.Empty }],
            [validCanonical with { Selector = ProcessingInputSelector.Raw() }],
            [validCanonical with { SchemaVersion = " " }],
            [validCanonical with { ArtifactId = Guid.NewGuid() }],
            [validCanonical with { IdentitySha256 = "invalid" }],
            [validCanonical with { IdentitySha256 = new string('G', 64) }],
            [validCanonical with
                {
                    IdentitySha256 = ProcessingIdentity.ComputePayloadSha256(ReadOnlyMemory<byte>.Empty),
                    Payload = default
                }],
            [validCanonical with { IdentitySha256 = new string('A', 64) }],
            [validCanonical with
                {
                    IdentitySha256 = ProcessingIdentity.ComputePayloadSha256(invalidJson),
                    Payload = invalidJson
                }],
            [validCanonical with
                {
                    IdentitySha256 = ProcessingIdentity.ComputePayloadSha256(duplicateJson),
                    Payload = duplicateJson
                }],
            [validCanonical with
                {
                    IdentitySha256 = ProcessingIdentity.ComputePayloadSha256(noncanonicalJson),
                    Payload = noncanonicalJson
                }],
            [validCanonical with { Kind = (ProcessingAuxiliaryInputKind)999 }]
        };
        var executor = new ProcessingRecipeExecutor();
        foreach (var auxiliaries in invalidAuxiliaryCases)
        {
            var outcome = await executor.ExecuteAsync(new ProcessingExecutionRequest(
                BuiltInProcessingRecipes.NoOpAnalyzer,
                EmptyOptions(),
                ProcessingInputSelector.Raw(),
                [input, other],
                "invalid",
                AuxiliaryInputs: auxiliaries,
                InputArtifactId: input.ArtifactId)).ConfigureAwait(false);
            Assert.AreEqual(ProcessingReasonCodes.InvalidInput, outcome.ReasonCode);
        }

        var valid = await executor.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.NoOpAnalyzer,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            [input, other],
            "valid",
            AuxiliaryInputs: [validArtifact, validCanonical],
            InputArtifactId: input.ArtifactId)).ConfigureAwait(false);
        var mismatched = await executor.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.NoOpAnalyzer,
            EmptyOptions(),
            ProcessingInputSelector.Raw("source"),
            [input, other],
            "invalid",
            InputArtifactId: other.ArtifactId)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, valid.Status);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, mismatched.Status);
        Assert.AreEqual(ProcessingReasonCodes.InvalidInput, mismatched.ReasonCode);
        Assert.AreEqual(nameof(ProcessingExecutionRequest.InputArtifactId), mismatched.Field);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task InvalidRecipeOptionsFormatsAndCancellationHaveDeterministicBehavior()
    {
        var input = CreateArtifact(FrameArtifactRole.Raw, "source", CameraPixelFormat.Mono16, 1, 1, [1, 0]);
        var executor = new ProcessingRecipeExecutor();
        var unknown = await executor.ExecuteAsync(Request(
            "not-a-recipe", EmptyOptions(), ProcessingInputSelector.Raw(), [input], "x")).ConfigureAwait(false);
        var blankRecipe = await executor.ExecuteAsync(Request(
            " ", EmptyOptions(), ProcessingInputSelector.Raw(), [input], "x")).ConfigureAwait(false);
        var missingInputs = await executor.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.NoOpAnalyzer,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            null!,
            "x")).ConfigureAwait(false);
        var missingSelector = await executor.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.NoOpAnalyzer,
            EmptyOptions(),
            null!,
            [input],
            "x")).ConfigureAwait(false);
        var missingVariant = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.NoOpAnalyzer,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            [input],
            " ")).ConfigureAwait(false);
        var invalidOptions = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.EncodedPreview,
            Json("""{"jpegQuality":0}"""),
            ProcessingInputSelector.Raw(), [input], "x")).ConfigureAwait(false);
        var unsupported = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.EncodedPreview,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            [input with { Layout = input.Layout! with { PixelFormat = (CameraPixelFormat)999 } }],
            "x")).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, unknown.Status);
        Assert.AreEqual(ProcessingReasonCodes.UnknownRecipe, unknown.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.UnknownRecipe, blankRecipe.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidOptions, missingInputs.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidOptions, missingSelector.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidOptions, missingVariant.ReasonCode);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, invalidOptions.Status);
        Assert.AreEqual(ProcessingReasonCodes.InvalidOptions, invalidOptions.ReasonCode);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, unsupported.Status);

        var acceptedUndefinedOptions = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.NoOpAnalyzer, default, ProcessingInputSelector.Raw(), [input], "undefined")).ConfigureAwait(false);
        var acceptedNullOptions = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.NoOpAnalyzer, Json("null"), ProcessingInputSelector.Raw(), [input], "null")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, acceptedUndefinedOptions.Status);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, acceptedNullOptions.Status);

        var invalidOptionCases = new (string Recipe, string Options)[]
        {
            (BuiltInProcessingRecipes.LinearNormalization, """{"mode":"Corrected"}"""),
            (BuiltInProcessingRecipes.EncodedPreview, "[]"),
            (BuiltInProcessingRecipes.EncodedPreview, """{"unknown":1}"""),
            (BuiltInProcessingRecipes.EncodedPreview, """{"jpegQuality":101}"""),
            (BuiltInProcessingRecipes.EncodedPreview, """{"outputEncoding":"Png"}"""),
            (BuiltInProcessingRecipes.EncodedPreview, """{"blackPercentile":-0.1}"""),
            (BuiltInProcessingRecipes.Annotation, """{"jpegQuality":0}"""),
            (BuiltInProcessingRecipes.Annotation, """{"jpegQuality":101}"""),
            (BuiltInProcessingRecipes.Annotation, """{"outputEncoding":"Png"}"""),
            (BuiltInProcessingRecipes.Annotation, """{"markRadius":-1}"""),
            (BuiltInProcessingRecipes.Annotation, """{"markRadius":33}"""),
            (BuiltInProcessingRecipes.Annotation, """{"labelScale":0}"""),
            (BuiltInProcessingRecipes.Annotation, """{"labelScale":9}"""),
            (BuiltInProcessingRecipes.Annotation, """{"cardinalScale":0}"""),
            (BuiltInProcessingRecipes.Annotation, """{"cardinalScale":9}"""),
            (BuiltInProcessingRecipes.Annotation, """{"constellationLineThickness":0}"""),
            (BuiltInProcessingRecipes.Annotation, """{"constellationLineThickness":9}"""),
            (BuiltInProcessingRecipes.Annotation, """{"constellationLineOpacity":-0.1}"""),
            (BuiltInProcessingRecipes.Annotation, """{"constellationLineOpacity":1.1}"""),
            (BuiltInProcessingRecipes.RollingMean, """{"maximumFrameCount":0}"""),
            (BuiltInProcessingRecipes.RollingMean, """{"maximumFrameCount":101}"""),
            (BuiltInProcessingRecipes.RollingMean, """{"maximumIntegrationMilliseconds":0}"""),
            (BuiltInProcessingRecipes.RollingMean, """{"maximumAgeMilliseconds":0}"""),
            (BuiltInProcessingRecipes.ImageQuality, """{"unexpected":true}"""),
            (BuiltInProcessingRecipes.NoOpAnalyzer, """{"unexpected":true}""")
        };
        foreach (var (recipe, options) in invalidOptionCases)
        {
            var invalid = await executor.ExecuteAsync(Request(
                recipe, Json(options), ProcessingInputSelector.Raw(), [input], "invalid-options")).ConfigureAwait(false);
            Assert.AreEqual(ProcessingReasonCodes.InvalidOptions, invalid.ReasonCode, $"{recipe}: {options}");
        }

        var invalidProducer = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.NoOpAnalyzer,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            [input with { RecipeIdentitySha256 = "invalid" }],
            "invalid")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.InvalidInput, invalidProducer.ReasonCode);

        var malformedInputs = new[]
        {
            input with { Variant = " " },
            input with { MediaType = " " },
            input with { CreatedUtc = DateTimeOffset.Parse("2025-01-15T08:00:00+01:00", CultureInfo.InvariantCulture) },
            input with { Integration = TimeSpan.FromTicks(-1) },
            input with { Compatibility = null! },
            input with { Compatibility = Compatibility with { Sensor = " " } }
        };
        foreach (var malformedInput in malformedInputs)
        {
            var malformed = await executor.ExecuteAsync(Request(
                BuiltInProcessingRecipes.NoOpAnalyzer,
                EmptyOptions(),
                ProcessingInputSelector.Raw(),
                [malformedInput],
                "invalid")).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, malformed.Status);
            Assert.AreEqual(ProcessingReasonCodes.InvalidInput, malformed.ReasonCode);
        }

        var validAnnotation = new ProcessingAnnotationInput(
            [new ProjectedAnnotationObject("star:1", "STAR", new PixelPoint(0, 0))],
            [new ProjectedAnnotationSegment("ORI", new PixelPoint(0, 0), new PixelPoint(1, 1))],
            new PreviewTransform(1, 1),
            null,
            new string('A', 64));
        var malformedAnnotations = new[]
        {
            validAnnotation with { ProvenanceSha256 = "invalid" },
            validAnnotation with { Transform = new PreviewTransform(0, 1) },
            validAnnotation with { Objects = null! },
            validAnnotation with { Objects = [new ProjectedAnnotationObject(" ", "STAR", new PixelPoint(0, 0))] },
            validAnnotation with { Objects = [new ProjectedAnnotationObject("star:1", null!, new PixelPoint(0, 0))] },
            validAnnotation with { Segments = [new ProjectedAnnotationSegment(" ", new PixelPoint(0, 0), new PixelPoint(1, 1))] },
            validAnnotation with
            {
                ProjectionOverlay = new ProjectedAnnotationOverlay(
                    new PixelPoint(0, 0), 0, new PixelPoint(0, 0), new PixelPoint(0, 0),
                    new PixelPoint(0, 0), new PixelPoint(0, 0))
            }
        };
        foreach (var malformedAnnotation in malformedAnnotations)
        {
            var malformed = await executor.ExecuteAsync(Request(
                BuiltInProcessingRecipes.NoOpAnalyzer,
                EmptyOptions(),
                ProcessingInputSelector.Raw(),
                [input],
                "invalid",
                malformedAnnotation)).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, malformed.Status);
            Assert.AreEqual(ProcessingReasonCodes.InvalidAnnotation, malformed.ReasonCode);
        }

        var jsonFailure = await ExecuteSyntheticFailureAsync(
            new JsonException(), throwDuringNormalization: true, input).ConfigureAwait(false);
        var optionFailure = await ExecuteSyntheticFailureAsync(
            new ArgumentException(), throwDuringNormalization: true, input).ConfigureAwait(false);
        var layoutFailure = await ExecuteSyntheticFailureAsync(
            new ArgumentException(), throwDuringNormalization: false, input).ConfigureAwait(false);
        var overflowFailure = await ExecuteSyntheticFailureAsync(
            new OverflowException(), throwDuringNormalization: false, input).ConfigureAwait(false);
        var executionFailure = await ExecuteSyntheticFailureAsync(
            new InvalidOperationException(), throwDuringNormalization: false, input).ConfigureAwait(false);
        var normalizationFailure = await ExecuteSyntheticFailureAsync(
            new InvalidOperationException(), throwDuringNormalization: true, input).ConfigureAwait(false);
        var unexpectedExecutionFailure = await ExecuteSyntheticFailureAsync(
            new NotSupportedException(), throwDuringNormalization: false, input).ConfigureAwait(false);
        var unexpectedNormalizationFailure = await ExecuteSyntheticFailureAsync(
            new NotSupportedException(), throwDuringNormalization: true, input).ConfigureAwait(false);
        var explicitRetryable = await new ProcessingRecipeExecutor([new RetryableRecipe()]).ExecuteAsync(Request(
            RetryableRecipe.Name,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            [input],
            "retry")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.InvalidOptions, jsonFailure.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidOptions, optionFailure.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLayout, layoutFailure.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLayout, overflowFailure.ReasonCode);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, executionFailure.Status);
        Assert.AreEqual(ProcessingReasonCodes.ExecutionFailed, executionFailure.ReasonCode);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, normalizationFailure.Status);
        Assert.AreEqual(ProcessingReasonCodes.ExecutionFailed, normalizationFailure.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.ExecutionFailed, unexpectedExecutionFailure.ReasonCode);
        Assert.AreEqual(ProcessingReasonCodes.ExecutionFailed, unexpectedNormalizationFailure.ReasonCode);
        Assert.AreEqual(ProcessingOutcomeStatus.RetryableFailure, explicitRetryable.Status);
        Assert.AreEqual(ProcessingReasonCodes.ExecutionFailed, explicitRetryable.ReasonCode);

        var semanticLayouts = new[]
        {
            input.Layout! with { ByteOrder = FrameByteOrder.BigEndian },
            input.Layout! with { SampleDepthBits = 8 },
            input.Layout! with { ContainerDepthBits = 8 },
            input.Layout! with { Packing = FrameSamplePacking.Packed },
            input.Layout! with { CfaPattern = ColorFilterArrayPattern.Rggb },
            input.Layout! with { BlackLevel = -1 },
            input.Layout! with { WhiteLevel = 70_000 },
            input.Layout! with { BlackLevel = 2, WhiteLevel = 1 },
            input.Layout! with { BlackLevel = double.NaN },
            input.Layout! with { WhiteLevel = double.PositiveInfinity },
            input.Layout! with { StrideBytes = 1 },
            input.Layout! with { ByteLength = 3 }
        };
        foreach (var layout in semanticLayouts)
        {
            var invalidLayout = await executor.ExecuteAsync(Request(
                BuiltInProcessingRecipes.ImageQuality,
                EmptyOptions(),
                ProcessingInputSelector.Raw(),
                [input with { Layout = layout }],
                "invalid")).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, invalidLayout.Status);
            Assert.AreEqual(ProcessingReasonCodes.InvalidLayout, invalidLayout.ReasonCode);
        }

        var lowerDepth = input.Layout! with
        {
            SampleDepthBits = 12,
            WhiteLevel = 4095,
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample
        };
        var lowerDepthResult = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.ImageQuality,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            [input with { Layout = lowerDepth }],
            "lower-depth")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, lowerDepthResult.Status);

        foreach (var unsupportedTransform in new[]
        {
            FrameStoredCodeTransform.LeftShiftedV1,
            FrameStoredCodeTransform.FullRangeScaledV1,
            FrameStoredCodeTransform.OpaqueContainerV1
        })
        {
            var unsupportedLayout = lowerDepth with { StoredCodeTransform = unsupportedTransform };
            var unsupportedResult = await executor.ExecuteAsync(Request(
                BuiltInProcessingRecipes.ImageQuality,
                EmptyOptions(),
                ProcessingInputSelector.Raw(),
                [input with { Layout = unsupportedLayout }],
                "unsupported-native-level-space")).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, unsupportedResult.Status);
            Assert.AreEqual(ProcessingReasonCodes.InvalidLayout, unsupportedResult.ReasonCode);
        }

        var shiftedStored = lowerDepth with
        {
            StoredCodeTransform = FrameStoredCodeTransform.LeftShiftedV1,
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer,
            WhiteLevel = 65_520
        };
        var shiftedStoredResult = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.ImageQuality,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            [input with { Layout = shiftedStored }],
            "shifted-stored-level-space")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, shiftedStoredResult.Status);

        var malformedFormatArtifacts = new[]
        {
            input with { Layout = null },
            input with { Layout = input.Layout! with { Width = 0 } },
            input with { Layout = input.Layout! with { Height = 0 } },
            CreateArtifact(FrameArtifactRole.Raw, "mono8", CameraPixelFormat.Mono8, 1, 1, [1]) with
            {
                Layout = CreateLayout(1, 1, CameraPixelFormat.Mono8) with { ByteOrder = FrameByteOrder.BigEndian }
            },
            CreateArtifact(FrameArtifactRole.Raw, "rgb", CameraPixelFormat.Rgb24, 1, 1, [1, 2, 3]) with
            {
                Layout = CreateLayout(1, 1, CameraPixelFormat.Rgb24) with { CfaPattern = ColorFilterArrayPattern.Rggb }
            },
            CreateArtifact(FrameArtifactRole.Raw, "bayer", CameraPixelFormat.BayerRggb16, 2, 2,
                [0, 0, 0, 0, 0, 0, 0, 0]) with
            {
                Layout = CreateLayout(2, 2, CameraPixelFormat.BayerRggb16) with
                {
                    CfaPattern = ColorFilterArrayPattern.None
                }
            }
        };
        foreach (var malformedInput in malformedFormatArtifacts)
        {
            var invalidLayout = await executor.ExecuteAsync(Request(
                BuiltInProcessingRecipes.ImageQuality,
                EmptyOptions(),
                ProcessingInputSelector.Raw(malformedInput.Variant),
                [malformedInput],
                "invalid")).ConfigureAwait(false);
            Assert.AreEqual(ProcessingReasonCodes.InvalidLayout, invalidLayout.ReasonCode);
        }

        var trailingBytes = await executor.ExecuteAsync(Request(
            BuiltInProcessingRecipes.ImageQuality,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            [input with { Payload = new byte[] { 1, 0, 2 } }],
            "invalid")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingReasonCodes.InvalidLayout, trailingBytes.ReasonCode);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(Request(
                BuiltInProcessingRecipes.NoOpAnalyzer,
                EmptyOptions(),
                ProcessingInputSelector.Raw(),
                [input],
                "x"), cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);

        using var duringExecution = new CancellationTokenSource();
        var cancelingExecutor = new ProcessingRecipeExecutor([new CancelingRecipe(duringExecution)]);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await cancelingExecutor.ExecuteAsync(Request(
                CancelingRecipe.Name,
                EmptyOptions(),
                ProcessingInputSelector.Raw(),
                [input],
                "x"), duringExecution.Token).ConfigureAwait(false)).ConfigureAwait(false);

        using var duringNormalization = new CancellationTokenSource();
        var normalizationExecutor = new ProcessingRecipeExecutor([new CancelingNormalizationRecipe(duringNormalization)]);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await normalizationExecutor.ExecuteAsync(Request(
                CancelingNormalizationRecipe.Name,
                EmptyOptions(),
                ProcessingInputSelector.Raw(),
                [input],
                "x"), duringNormalization.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static ValueTask<ProcessingOutcome> ExecuteSyntheticFailureAsync(
        Exception exception,
        bool throwDuringNormalization,
        ProcessingArtifact input)
    {
        const string recipeName = "synthetic-failure";
        var recipe = new SyntheticRecipe(recipeName, exception, throwDuringNormalization);
        return new ProcessingRecipeExecutor([recipe]).ExecuteAsync(Request(
            recipeName,
            EmptyOptions(),
            ProcessingInputSelector.Raw(),
            [input],
            "failure"));
    }

    private static ProcessingExecutionRequest Request(
        string recipe,
        JsonElement options,
        ProcessingInputSelector input,
        IReadOnlyList<ProcessingArtifact> inputs,
        string variant,
        ProcessingAnnotationInput? annotation = null) =>
        new(recipe, options, input, inputs, variant, annotation);

    private static async Task<ProcessingOutcome> ExecuteProjectedSceneAsync(
        ProcessingArtifact source,
        ProjectedSceneV1 scene)
    {
        var payload = ProjectedSceneJson.Serialize(scene);
        return await new ProcessingRecipeExecutor().ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.ProjectedScene,
            EmptyOptions(),
            ProcessingInputSelector.Raw("source"),
            [source],
            "scene",
            AuxiliaryInputs:
            [
                new ProcessingAuxiliaryInput(
                    "scene",
                    ProcessingAuxiliaryInputKind.CanonicalJson,
                    SchemaVersion: ProjectedSceneV1.CurrentSchemaVersion,
                    IdentitySha256: scene.SceneIdentitySha256,
                    Payload: payload)
                {
                    ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(payload)
                }
            ])).ConfigureAwait(false);
    }

    private static async Task<ProjectedSceneV1> CreateProjectedSceneAsync(
        ProjectedSceneKind kind,
        Guid captureId,
        Guid artifactId,
        string descriptorIdentity)
    {
        var utc = DateTimeOffset.Parse("2025-01-15T08:00:00Z", CultureInfo.InvariantCulture);
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15;
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("zenith", "Zenith", siderealHours, 0, 1)
        ])).BuildAsync(new VisibleSceneRequest(
            utc,
            new ObserverLocation(0, 0, 0),
            new ProjectionContext(
                ProjectionModel.Perspective, 1, 1, 1, 1, 2, 2, ProjectionAperture.Rectangular,
                BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10),
            new CatalogMetadata(
                "fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "perspective-v1")).ConfigureAwait(false);
        return ProjectedSceneJson.Create(
            kind,
            visible,
            ProjectedSceneImageTransformV1.Identity(2, 2),
            new ProjectedSceneSource(captureId, artifactId, descriptorIdentity),
            "calibration-v1",
            visible.Request.ProjectionVersion);
    }

    private static ProcessingArtifact CreateArtifact(
        FrameArtifactRole role,
        string variant,
        CameraPixelFormat format,
        int width,
        int height,
        byte[] bytes,
        Guid? id = null,
        DateTimeOffset? createdUtc = null,
        int? stride = null)
    {
        var layout = CreateLayout(width, height, format, stride);
        return new ProcessingArtifact(
            id ?? Guid.Parse("10000000-0000-0000-0000-000000000001"),
            role,
            variant,
            new string('0', 64),
            "application/x-hvo-frame",
            layout,
            bytes,
            createdUtc ?? DateTimeOffset.Parse("2025-01-15T08:00:00Z", CultureInfo.InvariantCulture),
            TimeSpan.FromSeconds(1),
            Compatibility);
    }

    private static FrameLayoutDescriptor CreateLayout(
        int width,
        int height,
        CameraPixelFormat format,
        int? stride = null)
    {
        var bytesPerPixel = ImageLayout.BytesPerPixel(format);
        var actualStride = stride ?? checked(width * bytesPerPixel);
        var is16Bit = format is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16;
        return new FrameLayoutDescriptor(
            width,
            height,
            actualStride,
            format,
            is16Bit ? FrameByteOrder.LittleEndian : FrameByteOrder.NotApplicable,
            is16Bit ? 16 : 8,
            is16Bit ? 16 : 8,
            FrameSamplePacking.ByteAligned,
            format == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            null,
            is16Bit ? ushort.MaxValue : byte.MaxValue,
            checked((long)actualStride * height));
    }

    private static JsonElement EmptyOptions() => Json("{}");

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class SyntheticRecipe(
        string name,
        Exception exception,
        bool throwDuringNormalization) : IProcessingRecipe
    {
        public ProcessingRecipeDefinition Definition { get; } = new(
            name, "1.0.0", "synthetic-v1", ProcessingOperationKind.Gate);

        public JsonElement NormalizeOptions(JsonElement options)
        {
            if (throwDuringNormalization)
            {
                throw exception;
            }
            return options;
        }

        public ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            ProcessingRecipeIdentity identity,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ProcessingOutcome>(exception);
    }

    private sealed class CancelingRecipe(CancellationTokenSource cancellation) : IProcessingRecipe
    {
        internal const string Name = "synthetic-cancel";

        public ProcessingRecipeDefinition Definition { get; } = new(
            Name, "1.0.0", "synthetic-v1", ProcessingOperationKind.Gate);

        public JsonElement NormalizeOptions(JsonElement options) => options;

        public async ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            ProcessingRecipeIdentity identity,
            CancellationToken cancellationToken)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private sealed class CancelingNormalizationRecipe(CancellationTokenSource cancellation) : IProcessingRecipe
    {
        internal const string Name = "synthetic-normalization-cancel";

        public ProcessingRecipeDefinition Definition { get; } = new(
            Name, "1.0.0", "synthetic-v1", ProcessingOperationKind.Gate);

        public JsonElement NormalizeOptions(JsonElement options)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }

        public ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            ProcessingRecipeIdentity identity,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ProcessingOutcome.Produced());
    }

    private sealed class RetryableRecipe : IProcessingRecipe
    {
        internal const string Name = "synthetic-retryable";

        public ProcessingRecipeDefinition Definition { get; } = new(
            Name, "1.0.0", "synthetic-v1", ProcessingOperationKind.Gate);

        public JsonElement NormalizeOptions(JsonElement options) => options;

        public ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            ProcessingRecipeIdentity identity,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ProcessingOutcome.RetryableFailure(ProcessingReasonCodes.ExecutionFailed));
    }
}
