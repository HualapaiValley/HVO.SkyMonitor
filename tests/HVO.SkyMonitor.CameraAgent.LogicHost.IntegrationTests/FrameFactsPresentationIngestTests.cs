using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class FrameFactsPresentationIngestTests
{
    private static readonly string[] SceneVariants =
        ["scene-annotation-layer-v1", "scene-cardinal-layer-v1", "scene-image-circle-layer-v1", "scene-constellation-layer-v1"];
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    [DataRow("ordered")]
    [DataRow("manifest-first")]
    [DataRow("duplicate-resolution")]
    [DataRow("reconciliation")]
    public async Task RichGraphProductsAndManifestIngestAndRetrieveCentralPresentation(string arrival)
    {
        var graph = await CreateGraphAsync().ConfigureAwait(false);
        var fixture = HVO.SkyMonitor.IntegrationTests.AssemblyHooks.Fixture;
        var captureId = await SeedSourcesAsync(fixture, graph).ConfigureAwait(false);
        var products = arrival == "manifest-first"
            ? graph.Products.TakeLast(1).Concat(graph.Products.SkipLast(1)) : graph.Products;
        foreach (var product in products)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            using var payload = new MemoryStream(product.Payload.ToArray(), writable: false);
            var manifest = Upload(graph.Context, product);
            Assert.IsTrue(manifest.Descriptor.Validate().IsValid,
                JsonSerializer.Serialize(manifest.Descriptor.Validate()));
            TestContext.WriteLine($"Ingesting {product.Variant} ({product.SchemaVersion})");
            await scope.ServiceProvider.GetRequiredService<IArtifactIngestService>()
                .IngestAsync(manifest, payload, CancellationToken.None).ConfigureAwait(false);
            TestContext.WriteLine($"Ingested {product.Variant}");
        }

        if (arrival is "duplicate-resolution" or "reconciliation")
        {
            await using (var pending = fixture.Factory.Services.CreateAsyncScope())
            {
                var pendingDb = pending.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var manifestId = ProcessingIdentity.CreateArtifactId(graph.Products[^1].OutputIdentitySha256);
                var retained = await pendingDb.CentralArtifacts.Include(item => item.Sources)
                    .SingleAsync(item => item.ArtifactId == manifestId).ConfigureAwait(false);
                retained.ReconstructionState = CentralReconstructionState.PendingReference;
                retained.StateReasonCode = "lineage.source-not-found";
                var richId = ProcessingIdentity.CreateArtifactId(graph.Products.Single(product =>
                    product.SchemaVersion == PresentationLayerPayloadV1.RichTextSchemaVersion).OutputIdentitySha256);
                retained.Sources.Single(source => source.SourceArtifactId == richId).ResolvedCentralArtifactId = null;
                await pendingDb.SaveChangesAsync().ConfigureAwait(false);
            }
            if (arrival == "duplicate-resolution")
            {
                await IngestAsync(fixture, graph, graph.Products[^1]).ConfigureAwait(false);
            }
            else
            {
                using var telemetry = new CentralIngestTelemetry();
                using var reconciler = new CentralArtifactReconciliationService(
                    fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
                    telemetry, NullLogger<CentralArtifactReconciliationService>.Instance);
                await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        await using var retrieval = fixture.Factory.Services.CreateAsyncScope();
        var db = retrieval.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ownerId = await db.Users.Where(user => user.Email == TestUsers.Operator.Email)
            .Select(user => user.Id).SingleAsync().ConfigureAwait(false);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, ownerId), new Claim("account_type", "User")], "Identity.Application"));
        var result = await retrieval.ServiceProvider.GetRequiredService<ICentralLayeredPresentationService>()
            .GetAsync(captureId, principal).ConfigureAwait(false);
        Assert.AreEqual(CentralLayeredPresentationStatus.Found, result.Status);
        Assert.IsNotNull(result.Presentation);
        Assert.AreEqual(5, result.Presentation.Layers.Count);
        var overlay = LayeredPresentationJson.ParseManifest(graph.Products[^1].Payload).Document!;
        var richProduct = graph.Products.Single(product => product.SchemaVersion == PresentationLayerPayloadV1.RichTextSchemaVersion);
        var factsProduct = graph.Products.Single(product => product.SchemaVersion == PresentationMetadataFactsProductV1.RichTextSchemaVersion);
        var richArtifactId = ProcessingIdentity.CreateArtifactId(richProduct.OutputIdentitySha256);
        var retainedRich = await db.CentralArtifacts.Include(artifact => artifact.StructuredProduct)
            .SingleAsync(artifact => artifact.CentralFrameId == captureId && artifact.ArtifactId == richArtifactId).ConfigureAwait(false);
        var manifestArtifactId = ProcessingIdentity.CreateArtifactId(graph.Products[^1].OutputIdentitySha256);
        var retainedReference = await db.CentralArtifactSources.SingleAsync(source => source.Artifact!.CentralFrameId == captureId &&
            source.Artifact.ArtifactId == manifestArtifactId && source.SourceArtifactId == richArtifactId).ConfigureAwait(false);
        Assert.AreEqual(factsProduct.ContentIdentitySha256, retainedRich.StructuredProduct!.SourceIdentitySha256);
        Assert.AreEqual(retainedReference.ExpectedCoordinateIdentitySha256, graph.Scene.ContentIdentitySha256);
        Assert.AreNotEqual(retainedReference.ExpectedCoordinateIdentitySha256, retainedRich.StructuredProduct.SourceIdentitySha256);
        Assert.AreEqual(overlay.ManifestIdentitySha256, result.Presentation.ManifestIdentitySha256);
        var payloads = overlay.Layers.Select(layer => PresentationLayerPayloadJson.Parse(graph.Products.Single(product =>
            ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256) == layer.SourceProduct.ArtifactId).Payload).Payload!).ToArray();
        var expected = GroupedSvgPresentationRenderer.Render(overlay, payloads, graph.Preview.ChecksumSha256);
        CollectionAssert.AreEqual(expected.Svg.ToArray(), result.Presentation.Svg);
    }

    [TestMethod]
    [DataRow("facts-source")]
    [DataRow("scene-coordinate")]
    public async Task RichIngestRejectsIndependentFactsOrSceneIdentityMismatch(string mismatch)
    {
        var graph = await CreateGraphAsync().ConfigureAwait(false);
        var fixture = HVO.SkyMonitor.IntegrationTests.AssemblyHooks.Fixture;
        var captureId = await SeedSourcesAsync(fixture, graph).ConfigureAwait(false);
        var rich = graph.Products.Single(product => product.SchemaVersion == PresentationLayerPayloadV1.RichTextSchemaVersion);
        foreach (var product in graph.Products.SkipLast(1).Where(product => product != rich))
            await IngestAsync(fixture, graph, product).ConfigureAwait(false);
        ProcessingProduct rejected;
        if (mismatch == "facts-source")
        {
            var payload = PresentationLayerPayloadJson.Parse(rich.Payload).Payload! with { SourceIdentitySha256 = new string('D', 64) };
            payload = payload with { ContentIdentitySha256 = PresentationLayerPayloadJson.ComputeIdentity(payload) };
            var bytes = PresentationLayerPayloadJson.Serialize(payload);
            rejected = rich with
            {
                Payload = bytes,
                ContentIdentitySha256 = payload.ContentIdentitySha256,
                ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(bytes)
            };
        }
        else
        {
            await IngestAsync(fixture, graph, rich).ConfigureAwait(false);
            var manifest = LayeredPresentationJson.ParseManifest(graph.Products[^1].Payload).Document!;
            var compatibility = manifest.BaseProduct.Compatibility with { CoordinateIdentitySha256 = new string('D', 64) };
            var original = manifest.Layers.Single(layer => layer.LayerKind == "environment");
            var layer = LayeredPresentationJson.CreateLayer(original.LayerKind,
                original.SourceProduct with { Compatibility = compatibility }, compatibility.CoordinateIdentitySha256,
                original.CoordinateSpace, original.RendererVersion, original.StyleVersion, original.ZOrder,
                original.BlendMode, original.OpacityMillionths, original.EnabledByDefault, original.Options);
            rejected = PresentationProcessingProducts.CreateManifestProduct(manifest.BaseProduct with { Compatibility = compatibility },
                compatibility.CoordinateIdentitySha256, [new(layer, CameraAgentRecipeExecutionAdapter.CreateArtifact(graph.Context, rich))],
                "wrong-coordinate-manifest", CameraAgentRecipeExecutionAdapter.CreateArtifact(graph.Context, graph.Preview));
        }
        var exception = await Assert.ThrowsAsync<ArtifactIntegrityException>(() => IngestAsync(fixture, graph, rejected)).ConfigureAwait(false);
        Assert.AreEqual("Structured product source facts do not match its lineage.", exception.Message);
        await using var verification = fixture.Factory.Services.CreateAsyncScope();
        var rejectedId = ProcessingIdentity.CreateArtifactId(rejected.OutputIdentitySha256);
        Assert.AreEqual(0, await verification.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
            .CountAsync(artifact => artifact.CentralFrameId == captureId && artifact.ArtifactId == rejectedId).ConfigureAwait(false));
    }

    private static async Task IngestAsync(IntegrationTestFixture fixture, Graph graph, ProcessingProduct product)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        using var bytes = new MemoryStream(product.Payload.ToArray(), writable: false);
        await scope.ServiceProvider.GetRequiredService<IArtifactIngestService>()
            .IngestAsync(Upload(graph.Context, product), bytes, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<Graph> CreateGraphAsync()
    {
        var config = ProcessingConformanceFixture.CameraConfig with
        {
            Rig = new(new SensorProfile("fixture", 320, 240, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                ProcessingConformanceFixture.CameraConfig.Rig.Optics, ProcessingConformanceFixture.CameraConfig.Rig.Orientation,
                ProcessingConformanceFixture.CameraConfig.Rig.Pipeline)
        };
        var raw = new byte[320 * 240];
        var layout = new FrameLayoutDescriptor(320, 240, 320, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable,
            8, 8, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 255, raw.Length);
        var descriptor = ProcessingConformanceFixture.CreateDescriptor() with
        {
            Capture = new($"rich-ingest-{Guid.NewGuid():N}", "rig", 1, Guid.NewGuid()),
            Layout = layout,
            Artifact = ProcessingConformanceFixture.CreateDescriptor().Artifact with
            { ArtifactId = Guid.NewGuid(), ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(raw) },
            Location = new("fixture", 1, "integration-test", null, DateTimeOffset.UnixEpoch, null),
            Profiles = ProcessingConformanceFixture.CreateDescriptor().Profiles with
            {
                Rig = new("rig", config.Rig.ProfileVersion, CameraRigProfileIdentity.ComputeSha256(config.Rig))
            }
        };
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed,
            new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, descriptor, "raw.bin"),
            new StoredFrameReference("raw.bin", "raw.bin", descriptor.Timing.DurableIngressUtc, FrameArtifactRole.Raw),
            new string('A', 64));
        var submission = new CaptureLoopSubmission(
            new CaptureRequest(descriptor.Timing.ExposureStartedUtc, TimeSpan.FromSeconds(20), CaptureMode.Still),
            new CaptureResult(null, new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null), TimeSpan.Zero,
                CaptureMode.Still, false), descriptor.Timing.ExposureStartedUtc, TimeSpan.FromSeconds(30), TimeSpan.Zero);
        var context = new CaptureProcessingContext(config, submission, receipt);
        var source = ProcessingConformanceFixture.CreateProcessingArtifact() with
        {
            ArtifactId = descriptor.Artifact.ArtifactId,
            Layout = layout,
            Payload = raw,
            CaptureId = descriptor.Capture.CaptureId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor)
        };
        var stackRecipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            BuiltInProcessingRecipes.RollingMean, "1.0.0", "fixture-v1", JsonSerializer.SerializeToElement(new { })));
        var stack = new ProcessingProduct(FrameArtifactRole.Combined, "combined-preview",
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Combined, "combined-preview", stackRecipe.IdentitySha256, [source.ArtifactId]),
            source.MediaType, source.Layout, source.Payload, ProcessingIdentity.ComputePayloadSha256(source.Payload),
            stackRecipe, [], [source.ArtifactId], source.Integration, source.Compatibility);
        context.BeginNode("stack", []);
        context.RegisterProcessingProduct(stack);
        var stackArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, stack);
        var encoded = await new ProcessingRecipeExecutor().ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.EncodedPreview, JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.Combined(), [stackArtifact], "preview"), CancellationToken.None).ConfigureAwait(false);
        var preview = encoded.Products.Single();
        context.BeginNode("preview", ["stack"]);
        context.RegisterProcessingProduct(preview);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(new VisibleSceneRequest(
            descriptor.Timing.ExposureStartedUtc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.EquidistantFisheye, 160, 120, 120, 120, 320, 240, ProjectionAperture.Circular, ImageCircleRadiusPixels: 120),
            new CatalogQuery(6, 10), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1")),
            CancellationToken.None).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible, ProjectedSceneImageTransformV1.Identity(320, 240),
            new ProjectedSceneSource(descriptor.Capture.CaptureId, source.ArtifactId, source.DescriptorIdentitySha256!),
            "fixture-v1", visible.Request.ProjectionVersion);
        var sceneRecipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            BuiltInProcessingRecipes.ProjectedScene, "1.0.0", "fixture-v1", JsonSerializer.SerializeToElement(new { })));
        var sceneBytes = ProjectedSceneJson.Serialize(scene);
        var sceneProduct = new ProcessingProduct(FrameArtifactRole.Metadata, "scene",
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, "scene", sceneRecipe.IdentitySha256, [source.ArtifactId]),
            StructuredProcessingProductContracts.ProjectedSceneMediaType, null, sceneBytes, ProcessingIdentity.ComputePayloadSha256(sceneBytes),
            sceneRecipe, [], [source.ArtifactId], TimeSpan.Zero, source.Compatibility)
        { Kind = ProcessingProductKind.Metadata, SchemaVersion = scene.SchemaVersion, ContentIdentitySha256 = scene.SceneIdentitySha256 };
        context.BeginNode("scene", []);
        context.RegisterProcessingProduct(sceneProduct);
        using var observations = new SqliteEnvironmentalObservationOutbox();
        var options = Options.Create(new CameraAgentHostOptions());
        var builder = new PresentationMetadataFactsBuilder(
            new EnvironmentalAssociationService(observations, observations, options, TimeProvider.System), observations, options);
        context.BeginNode("rich", ["scene", "preview"]);
        await new FrameFactsPresentationCaptureProcessingStep(new("rich", FrameFactsPresentation.StepAlias, 72),
            new()
            {
                WidthPixels = 320,
                HeightPixels = 240,
                TopLeftTemplate = "**FRAME {{capture.sequence}}**",
                TopRightTemplate = "*STACK {{stack.count}}*",
                BottomLeftTemplate = "__UTC__",
                BottomRightTemplate = "GAIN {{acquisition.gain}}",
                TopRightFont = "PlexRegular",
                BottomLeftFont = "MonoBold",
                TopLeftSizeMilliPixels = 8_000,
                TopRightSizeMilliPixels = 8_000,
                BottomLeftSizeMilliPixels = 8_000,
                BottomRightSizeMilliPixels = 8_000
            }, builder)
            .ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        var richProducts = context.ProcessingProducts.ToArray();
        foreach (var product in richProducts) context.RegisterProcessingProduct(product);
        var rich = richProducts.Single(product => product.SchemaVersion == PresentationLayerPayloadV1.RichTextSchemaVersion);
        Assert.AreNotEqual(scene.SceneIdentitySha256, PresentationLayerPayloadJson.Parse(rich.Payload).Payload!.SourceIdentitySha256,
            "F1 requires distinct facts-source and scene-coordinate identities.");
        var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene);
        var sceneArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, sceneProduct);
        var sceneLayers = new[] { groups.StarAnnotations, groups.CardinalDirections, groups.ImageCircle, groups.Constellations }
            .Zip(SceneVariants,
                (payload, variant) => PresentationProcessingProducts.CreateLayerProduct(payload, variant, [sceneArtifact], PresentationLayerProducers.SceneProducerVersion)).ToArray();
        context.BeginNode("scene-layers", []);
        foreach (var product in sceneLayers) context.RegisterProcessingProduct(product);
        context.BeginNode("manifest", ["preview", "scene-layers", "rich"]);
        await new OverlayManifestCaptureProcessingStep(new("manifest", "OverlayManifest", 80),
            new()
            {
                BasePreviewVariant = "preview",
                UseRichFrameFacts = true,
                EnvironmentVariant = rich.Variant,
                EnvironmentFactsVariant = richProducts.Single(product => product.SchemaVersion == PresentationMetadataFactsProductV1.RichTextSchemaVersion).Variant
            })
            .ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        return new(context, sceneProduct, stack, preview, [.. sceneLayers, .. richProducts,
            context.ProcessingProducts.Single(product => product.SchemaVersion == OverlayManifestV1.CurrentSchemaVersion)]);
    }

    private static StructuredProcessingProductManifestV1 Upload(CaptureProcessingContext context, ProcessingProduct product)
    {
        var artifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, product);
        var descriptor = new ArtifactDescriptor(artifact.ArtifactId, product.Role, "rich-ingest", product.Variant,
            context.ReconstructionDescriptor!.Timing.DurableIngressUtc, product.SourceArtifactIds, product.Recipe.Descriptor,
            product.MediaType, product.ChecksumSha256);
        return new(StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new(context.ReconstructionDescriptor, descriptor, product.OutputIdentitySha256, product.Algorithms, product.Compatibility,
                product.TotalIntegration.Ticks, product.Payload.Length, product.Kind, product.SchemaVersion!, product.ContentIdentitySha256!),
            $"derived/{product.Variant}.json");
    }

    private static async Task<Guid> SeedSourcesAsync(IntegrationTestFixture fixture, Graph graph)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var descriptor = graph.Context.ReconstructionDescriptor!;
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var observatory = new Observatory { Name = "Rich ingestion", OwnerUserId = owner.Id, TimeZoneId = "UTC", IsActive = true };
        var registration = new DeviceRegistration
        {
            DeviceId = descriptor.Capture.AgentId,
            DevicePublicId = Guid.NewGuid(),
            ObservatoryId = observatory.Id,
            OwnerUserId = owner.Id,
            Status = DeviceRegistrationStatus.Active,
            FriendlyName = "Rich ingestion",
            ObservatoryName = observatory.Name,
            ObservatoryTimeZoneId = "UTC",
            VerificationCodeHash = new string('A', 64),
            DeviceKeyHash = new string('B', 64),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        };
        var profile = new DeviceRigProfile
        {
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId.Value,
            ObservatoryId = observatory.Id,
            Version = 1,
            ConfigHash = new string('A', 64),
            ConfigJson = JsonSerializer.Serialize(graph.Context.Config.Rig),
            ProfileName = descriptor.Profiles.Rig.Name,
            ProfileVersion = descriptor.Profiles.Rig.Version,
            ProfileSha256 = descriptor.Profiles.Rig.Sha256,
            EffectiveFromUtc = DateTimeOffset.UnixEpoch
        };
        var frame = new CentralFrame
        {
            RegistrationId = registration.Id,
            DevicePublicId = registration.DevicePublicId.Value,
            ObservatoryId = observatory.Id,
            AgentId = descriptor.Capture.AgentId,
            FrameId = descriptor.Capture.CaptureId,
            CapturedAtUtc = descriptor.Timing.ExposureStartedUtc,
            DeviceRigProfileId = profile.Id,
            RigProfileVersion = 1
        };
        db.AddRange(observatory, registration, profile, frame, new ObservatoryMembership
        { ObservatoryId = observatory.Id, UserId = owner.Id, Role = ObservatoryMembershipRole.Owner });
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var rawBytes = new byte[checked((int)descriptor.Layout.ByteLength)];
        var rawRecipe = ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe);
        var raw = new ProcessingProduct(FrameArtifactRole.Raw, descriptor.Artifact.Variant,
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Raw, descriptor.Artifact.Variant, rawRecipe.IdentitySha256, []),
            descriptor.Artifact.MediaType, descriptor.Layout, rawBytes, descriptor.Artifact.ChecksumSha256,
            rawRecipe, [], [], descriptor.Controls.EffectiveExposure, graph.Stack.Compatibility);
        var seeded = new Dictionary<Guid, CentralArtifact>();
        foreach (var product in new[] { raw, graph.Scene, graph.Stack, graph.Preview })
        {
            var artifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(graph.Context, product);
            var artifactId = product == raw ? descriptor.Artifact.ArtifactId : artifact.ArtifactId;
            var key = $"rich-ingest/{frame.DevicePublicId:D}/{artifactId:D}";
            using var bytes = new MemoryStream(product.Payload.ToArray(), writable: false);
            await store.PutAsync("skymonitor-artifacts", key, bytes, product.Payload.Length, product.MediaType, CancellationToken.None).ConfigureAwait(false);
            var row = new CentralArtifact
            {
                Frame = frame,
                DevicePublicId = frame.DevicePublicId,
                ArtifactId = artifactId,
                Role = product.Role,
                Variant = product.Variant,
                SourceId = "rich-ingest",
                RecipeVersion = product.Recipe.Descriptor.ImplementationVersion,
                ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
                MediaType = product.MediaType,
                ByteLength = product.Payload.Length,
                ChecksumSha256 = product.ChecksumSha256,
                StorageReference = $"object://skymonitor-artifacts/{key}",
                IdempotencyKey = product == raw
                    ? CaptureContractJson.ComputeCanonicalJsonSha256(new { frame.DevicePublicId, product.OutputIdentitySha256 })
                    : product.OutputIdentitySha256,
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete,
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                ObjectVerifiedAtUtc = DateTimeOffset.UtcNow,
                Recipe = new()
                {
                    Name = product.Recipe.Descriptor.Name,
                    SemanticVersion = product.Recipe.Descriptor.SemanticVersion,
                    ImplementationVersion = product.Recipe.Descriptor.ImplementationVersion,
                    OptionsJson = product.Recipe.Descriptor.Options.GetRawText(),
                    OptionsSha256 = product.Recipe.Descriptor.OptionsSha256
                }
            };
            foreach (var id in product.SourceArtifactIds) row.Sources.Add(new()
            {
                SourceArtifactId = id,
                ResolvedCentralArtifactId = seeded[id].Id,
                ResolvedArtifact = seeded[id]
            });
            if (product.Layout is { } layout) row.Layout = new()
            {
                Width = layout.Width,
                Height = layout.Height,
                StrideBytes = layout.StrideBytes,
                PixelFormat = layout.PixelFormat.ToString(),
                ByteOrder = layout.ByteOrder.ToString(),
                SampleDepthBits = layout.SampleDepthBits,
                ContainerDepthBits = layout.ContainerDepthBits,
                Packing = layout.Packing.ToString(),
                CfaPattern = layout.CfaPattern.ToString(),
                BlackLevel = layout.BlackLevel,
                WhiteLevel = layout.WhiteLevel,
                ByteLength = layout.ByteLength
            };
            if (product.Kind == ProcessingProductKind.Metadata) row.StructuredProduct = new()
            {
                OutputIdentitySha256 = product.OutputIdentitySha256,
                ProductKind = product.Kind.ToString(),
                ProductSchemaVersion = product.SchemaVersion!,
                ContentIdentitySha256 = product.ContentIdentitySha256!,
                AlgorithmsJson = "[]",
                CompatibilityJson = JsonSerializer.Serialize(product.Compatibility),
                DescriptorJson = JsonSerializer.Serialize(Upload(graph.Context, product).Descriptor)
            };
            db.Add(row);
            seeded.Add(artifactId, row);
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        return frame.Id;
    }

    private sealed record Graph(CaptureProcessingContext Context, ProcessingProduct Scene, ProcessingProduct Stack,
        ProcessingProduct Preview, ProcessingProduct[] Products);
}
