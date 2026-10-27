using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Imaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed partial class PresentationMetadataFactsBuilderTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions ProductJson = new(JsonSerializerDefaults.Web);
    private string? _root;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "presentation-facts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (_root is not null && Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task BuildAsync_PreservesFreshStaleMissingAndContradictoryAssociationEvidence()
    {
        using var store = new SqliteEnvironmentalObservationOutbox(new FixedTimeProvider(Epoch.AddMinutes(1)));
        await CommitAsync(store, Fact(Guid.Parse("10000000-0000-0000-0000-000000000001"),
            EnvironmentalObservationKind.AirTemperature, 12, staleAfter: Epoch.AddSeconds(10))).ConfigureAwait(false);
        await CommitAsync(store, Fact(Guid.Parse("10000000-0000-0000-0000-000000000002"),
            EnvironmentalObservationKind.RelativeHumidity, 45, staleAfter: Epoch.AddSeconds(1))).ConfigureAwait(false);
        await CommitAsync(store, Fact(Guid.Parse("10000000-0000-0000-0000-000000000003"),
            EnvironmentalObservationKind.CloudCover, 0.1, uncertainty: 0.01, sourceId: "cloud-a")).ConfigureAwait(false);
        await CommitAsync(store, Fact(Guid.Parse("10000000-0000-0000-0000-000000000004"),
            EnvironmentalObservationKind.CloudCover, 0.9, uncertainty: 0.01, sourceId: "cloud-b")).ConfigureAwait(false);
        var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
        var kinds = new[]
        {
            EnvironmentalObservationKind.AirTemperature,
            EnvironmentalObservationKind.RelativeHumidity,
            EnvironmentalObservationKind.AtmosphericPressure,
            EnvironmentalObservationKind.CloudCover
        };
        var associations = await fixture.Associations.AssociateAsync(
            fixture.Descriptor.Capture.CaptureId, fixture.Descriptor.Capture.CaptureSequence,
            fixture.Descriptor.Timing.ExposureStartedUtc, fixture.Descriptor.Timing.ExposureEndedUtc,
            fixture.Descriptor.Capture.RigId, kinds, CancellationToken.None).ConfigureAwait(false);

        fixture.Context.BeginNode("environment-presentation", []);
        var facts = await fixture.Builder.BuildAsync(
            fixture.Context, fixture.Scene, fixture.Stack, SourceIds(fixture), kinds, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(facts);
        Assert.AreEqual("Fresh", FactFor(EnvironmentalObservationKind.AirTemperature).Status);
        Assert.IsNotNull(FactFor(EnvironmentalObservationKind.AirTemperature).ObservationId);
        Assert.AreEqual("Stale", FactFor(EnvironmentalObservationKind.RelativeHumidity).Status);
        Assert.AreEqual("Missing", FactFor(EnvironmentalObservationKind.AtmosphericPressure).Status);
        Assert.IsNull(FactFor(EnvironmentalObservationKind.AtmosphericPressure).ObservationId);
        Assert.AreEqual("Contradictory", FactFor(EnvironmentalObservationKind.CloudCover).Status);
        Assert.IsNull(FactFor(EnvironmentalObservationKind.CloudCover).ObservationId);
        CollectionAssert.AreEqual(
            new[]
            {
                Guid.Parse("10000000-0000-0000-0000-000000000003"),
                Guid.Parse("10000000-0000-0000-0000-000000000004")
            },
            FactFor(EnvironmentalObservationKind.CloudCover).ConflictingObservations
                .Select(static item => item.ObservationId).Order().ToArray());
        Assert.IsTrue(FactFor(EnvironmentalObservationKind.CloudCover).ConflictingObservations.All(
            static item => item.ContentSha256.Length == 64 && item.SourceIdentitySha256.Length == 64));
        foreach (var association in associations)
            Assert.AreEqual(association.PolicyIdentitySha256, FactFor(association.Kind).PolicyIdentitySha256);
        var payload = PresentationLayerProducers.FromMetadataFacts(facts.Corners, 1936, 1216);
        Assert.IsTrue(payload.TextBlocks.SelectMany(static block => block.Lines).All(
            static line => line.Length <= HVO.SkyMonitor.Imaging.PresentationLayerPayloadV1.MaximumLineCharacters));
        var rich = await fixture.Builder.BuildAsync(fixture.Context, fixture.Scene, fixture.Stack, SourceIds(fixture),
            kinds, CancellationToken.None, retainFrameFacts: true).ConfigureAwait(false);
        Assert.IsNotNull(rich?.FrameFacts);
        var statuses = FrameFactsTemplate.Parse("{{environment.airTemperature.status}} {{environment.relativeHumidity.status}} {{environment.atmosphericPressure.status}} {{environment.cloudCover.status}}",
            FrameFactsRetainedCatalog.Catalog).Template!;
        Assert.AreEqual("AVAILABLE STALE MISSING CONFLICTING", FrameFactsEvaluator.Evaluate(statuses, rich.FrameFacts.ToContext()).Text);

        PresentationEnvironmentalFactV1 FactFor(EnvironmentalObservationKind kind) =>
            facts.Environment.Single(item => item.Kind == kind.ToString());
    }

    [TestMethod]
    public async Task BuildAsync_UsesDurableCadenceStackAndCanonicalInputEvidence()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
        fixture.Context.BeginNode("environment-presentation", []);

        var facts = await fixture.Builder.BuildAsync(
            fixture.Context, fixture.Scene, fixture.Stack, SourceIds(fixture), [], CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(facts);
        Assert.AreEqual(CaptureCadenceMode.MinimumStartInterval,
            facts.Capture.GetProperty("CadenceMode").Deserialize<CaptureCadenceMode>());
        Assert.AreEqual(10, facts.Capture.GetProperty("CadenceSeconds").GetDouble());
        Assert.AreEqual("Available", facts.Stack.GetProperty("Status").GetString());
        Assert.AreEqual(5, facts.Stack.GetProperty("Count").GetInt32());
        Assert.AreEqual(TimeSpan.FromSeconds(25),
            facts.Stack.GetProperty("TotalIntegration").Deserialize<TimeSpan>());
        Assert.AreEqual(fixture.Stack.OutputIdentitySha256,
            facts.Stack.GetProperty("OutputIdentitySha256").GetString());
        Assert.IsEmpty(fixture.Context.GetCurrentInputEvidence());
    }

    [TestMethod]
    public async Task BuildAsync_RecordsCanonicalAssociationAndObservationEvidence()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        await CommitAsync(store, Fact(Guid.Parse("20000000-0000-0000-0000-000000000001"),
            EnvironmentalObservationKind.AirTemperature, 12)).ConfigureAwait(false);
        var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
        var kinds = new[] { EnvironmentalObservationKind.AirTemperature };
        var association = (await fixture.Associations.AssociateAsync(
            fixture.Descriptor.Capture.CaptureId, fixture.Descriptor.Capture.CaptureSequence,
            fixture.Descriptor.Timing.ExposureStartedUtc, fixture.Descriptor.Timing.ExposureEndedUtc,
            fixture.Descriptor.Capture.RigId, kinds, CancellationToken.None).ConfigureAwait(false)).Single();
        fixture.Context.BeginNode("environment-presentation", []);

        _ = await fixture.Builder.BuildAsync(
            fixture.Context, fixture.Scene, fixture.Stack, SourceIds(fixture), kinds, CancellationToken.None)
            .ConfigureAwait(false);

        var evidence = fixture.Context.GetCurrentInputEvidence();
        Assert.HasCount(2, evidence);
        Assert.AreEqual(association.AssociationIdentitySha256,
            evidence.Single(item => item.Name == "environment-association-AirTemperature").IdentitySha256);
        var observation = await store.ReadLocalDetailAsync(
            _root!, association.SelectedRecordId!.Value, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(observation!.ContentSha256,
            evidence.Single(item => item.Name == "environment-observation-AirTemperature").IdentitySha256);
    }

    [TestMethod]
    public async Task MetadataFactsProduct_HasImmediateSceneAndStackLineageAndDeterministicRestartIdentity()
    {
        string firstFactsIdentity;
        string firstOutputIdentity;
        using (var store = new SqliteEnvironmentalObservationOutbox())
        {
            await CommitAsync(store, Fact(Guid.Parse("50000000-0000-0000-0000-000000000001"),
                EnvironmentalObservationKind.AirTemperature, 12)).ConfigureAwait(false);
            var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
            await AssociateAirTemperatureAsync(fixture).ConfigureAwait(false);
            var result = await CreateFactsProductAsync(fixture).ConfigureAwait(false);
            firstFactsIdentity = result.Facts.FactsIdentitySha256;
            firstOutputIdentity = result.Product.OutputIdentitySha256;
            var persistedFacts = JsonSerializer.Deserialize<PresentationMetadataFactsProductV1>(
                result.Product.Payload.Span, ProductJson);
            Assert.IsNotNull(persistedFacts);
            Assert.AreEqual(firstFactsIdentity, persistedFacts.FactsIdentitySha256);
            CollectionAssert.AreEqual(SourceIds(fixture), persistedFacts.SourceArtifactIds!.ToArray());
            var mismatchedSources = WithRecomputedIdentity(result.Facts with
            {
                SourceArtifactIds = SourceIds(fixture).Reverse().ToArray()
            });
            Assert.Throws<ArgumentException>(() => PresentationProcessingProducts.CreateMetadataFactsProduct(
                mismatchedSources,
                "facts", [fixture.SceneArtifact, fixture.StackArtifact]));
            var retired = WithRecomputedIdentity(result.Facts with
            {
                SchemaVersion = "presentation-metadata-facts-v1",
                SourceArtifactIds = null
            });
            Assert.Throws<ArgumentException>(() => PresentationProcessingProducts.CreateMetadataFactsProduct(
                retired,
                "facts", [fixture.SceneArtifact, fixture.StackArtifact]));
            CollectionAssert.AreEqual(
                new[] { fixture.SceneArtifact.ArtifactId, fixture.StackArtifact.ArtifactId },
                result.Product.SourceArtifactIds.ToArray());
        }

        using var restartedStore = new SqliteEnvironmentalObservationOutbox();
        var restarted = await CreateFixtureAsync(restartedStore).ConfigureAwait(false);
        var replay = await CreateFactsProductAsync(restarted).ConfigureAwait(false);

        Assert.AreEqual(firstFactsIdentity, replay.Facts.FactsIdentitySha256);
        Assert.AreEqual(firstOutputIdentity, replay.Product.OutputIdentitySha256);
    }

    [TestMethod]
    public async Task RebuiltStoreWithDifferentLocalRecordIds_ProducesSamePortableFactsIdentity()
    {
        var firstRoot = Path.Combine(_root!, "first");
        var secondRoot = Path.Combine(_root!, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var selected = Fact(Guid.Parse("60000000-0000-0000-0000-000000000001"),
            EnvironmentalObservationKind.AirTemperature, 12);

        var first = await BuildAtRootAsync(firstRoot, selected, addUnrelatedFirst: true).ConfigureAwait(false);
        var second = await BuildAtRootAsync(secondRoot, selected, addUnrelatedFirst: false).ConfigureAwait(false);

        Assert.AreNotEqual(first.RecordId, second.RecordId);
        Assert.AreEqual(first.FactsIdentitySha256, second.FactsIdentitySha256);
        Assert.AreEqual(first.ObservationId, second.ObservationId);
        Assert.AreEqual(first.ObservationContentSha256, second.ObservationContentSha256);
    }

    [TestMethod]
    public async Task ChangedEnvironmentalKindSet_DoesNotReuseOldPolicyAndChangesFactsIdentity()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        await CommitAsync(store, Fact(Guid.Parse("70000000-0000-0000-0000-000000000001"),
            EnvironmentalObservationKind.AirTemperature, 12)).ConfigureAwait(false);
        var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
        await AssociateAirTemperatureAsync(fixture).ConfigureAwait(false);
        var first = await CreateFactsProductAsync(fixture).ConfigureAwait(false);
        var changedKinds = new[]
        {
            EnvironmentalObservationKind.AirTemperature,
            EnvironmentalObservationKind.RelativeHumidity
        };

        fixture.Context.BeginNode("changed-environment", []);
        var pending = await fixture.Builder.BuildAsync(
            fixture.Context, fixture.Scene, fixture.Stack, SourceIds(fixture), changedKinds, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNull(pending);
        var changedAssociations = await fixture.Associations.AssociateAsync(
            fixture.Descriptor.Capture.CaptureId, fixture.Descriptor.Capture.CaptureSequence,
            fixture.Descriptor.Timing.ExposureStartedUtc, fixture.Descriptor.Timing.ExposureEndedUtc,
            fixture.Descriptor.Capture.RigId, changedKinds, CancellationToken.None).ConfigureAwait(false);
        fixture.Context.BeginNode("changed-environment", []);
        var changed = await fixture.Builder.BuildAsync(
            fixture.Context, fixture.Scene, fixture.Stack, SourceIds(fixture), changedKinds, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(changed);
        Assert.AreNotEqual(first.Facts.FactsIdentitySha256, changed.FactsIdentitySha256);
        Assert.HasCount(2, changed.Environment);
        Assert.IsTrue(changedAssociations.All(item => item.PolicyIdentitySha256 ==
            EnvironmentalAssociationService.CreatePolicyIdentity(changedKinds)));
        Assert.IsTrue(changedAssociations.All(item => first.Facts.Environment.All(previous =>
            previous.PolicyIdentitySha256 != item.PolicyIdentitySha256)));
    }

    [TestMethod]
    public async Task RichFactsOptInRetainsTypedObservationAndPortableContextWithoutChangingLegacyFacts()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        await CommitAsync(store, Fact(Guid.Parse("15520000-0000-0000-0000-000000000001"),
            EnvironmentalObservationKind.AirTemperature, 12)).ConfigureAwait(false);
        var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
        await AssociateAirTemperatureAsync(fixture).ConfigureAwait(false);
        var legacy = await CreateFactsProductAsync(fixture).ConfigureAwait(false);
        var rich = await fixture.Builder.BuildAsync(fixture.Context, fixture.Scene, fixture.Stack, SourceIds(fixture),
            [EnvironmentalObservationKind.AirTemperature], CancellationToken.None, retainFrameFacts: true).ConfigureAwait(false);
        Assert.IsNotNull(rich);
        var product = PresentationProcessingProducts.CreateMetadataFactsProduct(rich, "facts-v3", [fixture.SceneArtifact, fixture.StackArtifact]);
        var retained = PresentationProcessingProducts.ParseMetadataFacts(product.Payload);
        Assert.AreEqual(PresentationMetadataFactsProductV1.RichTextSchemaVersion, retained.SchemaVersion);
        Assert.IsNotNull(retained.FrameFacts);
        var template = FrameFactsTemplate.Parse("{{environment.airTemperature}} {{stack.count}} {{acquisition.cadenceSeconds}}", FrameFactsRetainedCatalog.Catalog).Template!;
        Assert.AreEqual("12 5 10", FrameFactsEvaluator.Evaluate(template, retained.FrameFacts.ToContext()).Text);
        var missing = FrameFactsTemplate.Parse("{{time.sourceUtc.status}} {{time.processingUtc.status}} {{time.captureLocal.status}} {{location.latitude.status}}", FrameFactsRetainedCatalog.Catalog).Template!;
        Assert.AreEqual("MISSING MISSING MISSING MISSING", FrameFactsEvaluator.Evaluate(missing, retained.FrameFacts.ToContext()).Text);
        CollectionAssert.AreEqual(SourceIds(fixture), product.SourceArtifactIds.ToArray());
        Assert.IsNull(legacy.Facts.FrameFacts);
        using var oldJson = JsonDocument.Parse(legacy.Product.Payload);
        Assert.IsFalse(oldJson.RootElement.TryGetProperty("frameFacts", out _));
        var legacyAgain = await CreateFactsProductAsync(fixture).ConfigureAwait(false);
        CollectionAssert.AreEqual(legacy.Product.Payload.ToArray(), legacyAgain.Product.Payload.ToArray());
        Assert.AreEqual(legacy.Product.OutputIdentitySha256, legacyAgain.Product.OutputIdentitySha256);
        Assert.AreNotEqual(legacy.Product.OutputIdentitySha256, product.OutputIdentitySha256);
    }

    [TestMethod]
    public async Task RichRetainedInputHasCameraAgentAndLogicHostRenderingParity()
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
        Assert.IsTrue(fixture.Descriptor.Validate().IsValid, JsonSerializer.Serialize(fixture.Descriptor.Validate()));
        var facts = await fixture.Builder.BuildAsync(fixture.Context, fixture.Scene, fixture.Stack, SourceIds(fixture), [],
            CancellationToken.None, retainFrameFacts: true).ConfigureAwait(false);
        Assert.IsNotNull(facts);
        var factsProduct = PresentationProcessingProducts.CreateMetadataFactsProduct(facts, "facts-v3", [fixture.SceneArtifact, fixture.StackArtifact]);
        var retained = PresentationProcessingProducts.ParseMetadataFacts(factsProduct.Payload);
        var options = new FrameFactsPresentationOptions
        {
            WidthPixels = 1936,
            HeightPixels = 1216,
            TopLeftTemplate = "**CAPTURE {{capture.sequence}}**",
            TopRightTemplate = "*{{stack.count}}*",
            BottomLeftTemplate = "__{{time.captureUtc|date}}__",
            BottomRightTemplate = "{{acquisition.gain}}",
            TopLeftRed = 100,
            TopRightFont = "PlexRegular",
            BottomLeftFont = "MonoBold"
        };
        var edge = FrameFactsPresentation.Create(facts.FactsIdentitySha256, facts.FrameFacts!, options);
        var central = FrameFactsPresentation.Create(retained.FactsIdentitySha256, retained.FrameFacts!, options);
        CollectionAssert.AreEqual(PresentationLayerPayloadJson.Serialize(edge), PresentationLayerPayloadJson.Serialize(central));
        var compatibility = new PresentationCompatibilityDescriptor(1936, 1216, new string('A', 64), new string('B', 64));
        var layer = LayeredPresentationJson.CreateLayer("environment",
            new(Guid.Parse("15520000-0000-0000-0000-000000000002"), edge.ContentIdentitySha256, PresentationLayerPayloadJson.MediaType, compatibility),
            null, PresentationCoordinateSpace.ScenePixels, PresentationLayerCompositor.RichTextAlgorithmVersion,
            FrameFactsPresentation.ProducerVersion, 50, PresentationBlendMode.Normal, 1_000_000, true, JsonSerializer.SerializeToElement(new { }));
        var manifest = LayeredPresentationJson.CreateManifest(
            new(Guid.Parse("15520000-0000-0000-0000-000000000003"), new string('C', 64), "application/x-hvo-packed-image", compatibility), null, [layer]);
        var camera = CameraAgentLayeredPresentationService.Render(fixture.Descriptor.Capture.CaptureId, manifest, [edge], new string('D', 64));
        // CentralLayeredPresentationService invokes this exact shared seam on parsed retained bytes.
        var logicHost = GroupedSvgPresentationRenderer.Render(manifest, [central], new string('D', 64));
        Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, camera.Status);
        CollectionAssert.AreEqual(camera.Presentation!.Svg.ToArray(), logicHost.Svg.ToArray());
        Assert.AreEqual(camera.Presentation.SvgChecksumSha256, logicHost.SvgChecksumSha256);
        var layout = new ImageLayout(1936, 1216, CameraPixelFormat.Mono8, 1936);
        var raw = new byte[layout.RequiredByteLength];
        var edgeRaster = PresentationLayerCompositor.CompositeDisplay(layout, raw, [new(edge, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        var centralRaster = PresentationLayerCompositor.CompositeDisplay(layout, raw, [new(central, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        CollectionAssert.AreEqual(edgeRaster.Pixels, centralRaster.Pixels);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitRichStepProducesVersionedProductsOrFailsAtomicallyWithoutChangingSources(bool oversize)
    {
        using var store = new SqliteEnvironmentalObservationOutbox();
        var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
        var sceneBytes = ProjectedSceneJson.Serialize(fixture.Scene);
        var scene = fixture.Stack with
        {
            Role = FrameArtifactRole.Metadata,
            Variant = "scene",
            Kind = ProcessingProductKind.Metadata,
            OutputIdentitySha256 = new string('E', 64),
            SchemaVersion = fixture.Scene.SchemaVersion,
            Payload = sceneBytes,
            ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(sceneBytes),
            ContentIdentitySha256 = fixture.Scene.SceneIdentitySha256
        };
        fixture.Context.BeginNode("scene", []);
        fixture.Context.RegisterProcessingProduct(scene);
        fixture.Context.BeginNode("stack", []);
        fixture.Context.RegisterProcessingProduct(fixture.Stack);
        var preview = fixture.Stack with
        {
            Role = FrameArtifactRole.Preview,
            OutputIdentitySha256 = new string('F', 64),
            SourceArtifactIds = [ProcessingIdentity.CreateArtifactId(fixture.Stack.OutputIdentitySha256)],
            Recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                BuiltInProcessingRecipes.EncodedPreview, "1.0.0", "test", JsonSerializer.SerializeToElement(new { })))
        };
        fixture.Context.BeginNode("preview", []);
        fixture.Context.RegisterProcessingProduct(preview);
        fixture.Context.BeginNode("facts", ["scene", "preview"]);
        var sourceChecksum = fixture.Context.RawCapture!.Manifest.Descriptor.Artifact.ChecksumSha256;
        var step = new FrameFactsPresentationCaptureProcessingStep(new("facts", FrameFactsPresentation.StepAlias, 72),
            new() { WidthPixels = oversize ? 2 : 1936, HeightPixels = oversize ? 2 : 1216, TopLeftTemplate = "CAPTURE {{capture.sequence}}" }, fixture.Builder);
        Assert.AreEqual(FrameFactsPresentation.ProducerVersion, step.SharedStepVersion);
        if (oversize)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => step.ProcessAsync(fixture.Context, CancellationToken.None).AsTask()).ConfigureAwait(false);
            Assert.IsEmpty(fixture.Context.ProcessingProducts, "A failed optional layer must not publish partial facts.");
        }
        else
        {
            await step.ProcessAsync(fixture.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(2, fixture.Context.ProcessingProducts);
            foreach (var product in fixture.Context.ProcessingProducts)
                Assert.IsTrue(StructuredProcessingProductContracts.IsSupported(product.MediaType, product.SchemaVersion!));
            Assert.IsTrue(fixture.Context.ProcessingProducts.Any(product => product.SchemaVersion == PresentationMetadataFactsProductV1.RichTextSchemaVersion));
            Assert.IsTrue(fixture.Context.ProcessingProducts.Any(product => product.SchemaVersion == PresentationLayerPayloadV1.RichTextSchemaVersion));
        }
        Assert.AreEqual(sourceChecksum, fixture.Context.RawCapture.Manifest.Descriptor.Artifact.ChecksumSha256);
        CollectionAssert.AreEqual(sceneBytes, scene.Payload.ToArray());
        Assert.AreEqual(new string('D', 64), fixture.Stack.OutputIdentitySha256);
    }

    private async Task<(long RecordId, string FactsIdentitySha256, Guid? ObservationId,
        string? ObservationContentSha256)> BuildAtRootAsync(
        string root,
        EnvironmentalObservationFactV1 selected,
        bool addUnrelatedFirst)
    {
        var previousRoot = _root;
        _root = root;
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox();
            if (addUnrelatedFirst)
                await CommitAsync(store, Fact(Guid.Parse("60000000-0000-0000-0000-000000000002"),
                    EnvironmentalObservationKind.RelativeHumidity, 45)).ConfigureAwait(false);
            await CommitAsync(store, selected).ConfigureAwait(false);
            var fixture = await CreateFixtureAsync(store).ConfigureAwait(false);
            await AssociateAirTemperatureAsync(fixture).ConfigureAwait(false);
            var association = (await store.ReadAssociationsAsync(
                root, fixture.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false)).Single();
            var result = await CreateFactsProductAsync(fixture).ConfigureAwait(false);
            var environment = result.Facts.Environment.Single();
            return (association.SelectedRecordId!.Value, result.Facts.FactsIdentitySha256,
                environment.ObservationId, environment.ObservationContentSha256);
        }
        finally
        {
            _root = previousRoot;
        }
    }

    private static async Task AssociateAirTemperatureAsync(Fixture fixture) =>
        _ = await fixture.Associations.AssociateAsync(
            fixture.Descriptor.Capture.CaptureId, fixture.Descriptor.Capture.CaptureSequence,
            fixture.Descriptor.Timing.ExposureStartedUtc, fixture.Descriptor.Timing.ExposureEndedUtc,
            fixture.Descriptor.Capture.RigId, [EnvironmentalObservationKind.AirTemperature],
            CancellationToken.None).ConfigureAwait(false);

    private async Task<Fixture> CreateFixtureAsync(SqliteEnvironmentalObservationOutbox store,
        EnvironmentalAcquisitionOptions? acquisition = null)
    {
        var payload = new byte[8];
        var original = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var schedule = new CaptureScheduleDefinition("capture-schedule-v1",
            [new CaptureScheduleSetpointProfile("night", TimeSpan.FromSeconds(5), 82,
                TimeSpan.FromSeconds(10), CaptureCadenceMode.MinimumStartInterval)],
            [new CaptureWeeklyScheduleWindow(
                "weekly-night",
                DayOfWeek.Thursday,
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue),
                new CaptureScheduleBoundary(
                    CaptureScheduleBoundaryKind.FixedLocalTime,
                    TimeOnly.MinValue,
                    DayOffset: 1),
                "night")]);
        var config = new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"), new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                new SensorProfile("test", 2, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0), new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10), 82, 82)),
            CapturePipelineConfig.Empty,
            AgentId: "agent")
        {
            Schedule = schedule
        };
        var descriptor = original.Descriptor with
        {
            Capture = original.Descriptor.Capture with { AgentId = "agent", RigId = "rig", CaptureSequence = 7 },
            Timing = original.Descriptor.Timing with
            {
                RequestedStartUtc = Epoch,
                ExposureStartedUtc = Epoch,
                ExposureEndedUtc = Epoch.AddSeconds(5),
                ReadoutCompletedUtc = Epoch.AddSeconds(5),
                DurableIngressUtc = Epoch.AddSeconds(6),
                SetpointAppliedUtc = null
            },
            Controls = original.Descriptor.Controls with { EffectiveExposure = TimeSpan.FromSeconds(5) },
            Profiles = original.Descriptor.Profiles with
            {
                Calibration = new ProfileIdentityDescriptor(
                    "virtual-calibration-source-model", "virtual-calibration-source-model-v1", new string('A', 64))
            }
        };
        descriptor = descriptor with
        {
            Location = new("location", 1, "fixture", null, Epoch.AddDays(-1), null),
            CycleEvidence = CreateCycleEvidence(descriptor) with
            {
                CadenceMode = CaptureCadenceMode.MinimumStartInterval,
                ScheduleAdmission = CreateScheduleAdmission()
            }
        };
        var manifest = original with { Descriptor = descriptor };
        var path = Path.Combine(_root!, "raw.bin");
        await File.WriteAllBytesAsync(path, payload).ConfigureAwait(false);
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, manifest,
            new StoredFrameReference("raw.bin", path, Epoch, FrameArtifactRole.Raw),
            CaptureContractJson.ComputeManifestSha256(manifest));
        var submission = new CaptureLoopSubmission(new CaptureRequest(Epoch, TimeSpan.FromSeconds(5), CaptureMode.Still),
            new CaptureResult(null, new CaptureSetpoint(TimeSpan.FromSeconds(5), 82, null, null),
                TimeSpan.Zero, CaptureMode.Still, false), Epoch, TimeSpan.FromSeconds(10), TimeSpan.Zero);
        var context = new CaptureProcessingContext(config, submission, receipt);
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = _root!,
            EnvironmentalAcquisition = acquisition ?? new EnvironmentalAcquisitionOptions { Enabled = true }
        });
        var associations = new EnvironmentalAssociationService(
            store, store, options, new FixedTimeProvider(Epoch.AddMinutes(1)));
        var scene = await CreateSceneAsync(descriptor).ConfigureAwait(false);
        var compatibility = new ProcessingCompatibilityIdentity(
            "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing");
        var sceneArtifact = Artifact(Guid.Parse("30000000-0000-0000-0000-000000000001"),
            FrameArtifactRole.Metadata, TimeSpan.Zero, compatibility, new string('A', 64));
        var stackArtifact = Artifact(Guid.Parse("30000000-0000-0000-0000-000000000002"),
            FrameArtifactRole.Combined, TimeSpan.FromSeconds(25), compatibility, new string('B', 64));
        var stack = Product(stackArtifact, Enumerable.Range(1, 5)
            .Select(index => Guid.Parse($"40000000-0000-0000-0000-{index:D12}")).ToArray());
        return new(context, descriptor, associations,
            new PresentationMetadataFactsBuilder(associations, store, options), scene, stack,
            sceneArtifact, stackArtifact);
    }

    private static async Task<(PresentationMetadataFactsProductV1 Facts, ProcessingProduct Product)>
        CreateFactsProductAsync(Fixture fixture)
    {
        fixture.Context.BeginNode("environment-presentation", []);
        var facts = await fixture.Builder.BuildAsync(
            fixture.Context, fixture.Scene, fixture.Stack, SourceIds(fixture),
            [EnvironmentalObservationKind.AirTemperature],
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(facts);
        return (facts, PresentationProcessingProducts.CreateMetadataFactsProduct(
            facts, "facts", [fixture.SceneArtifact, fixture.StackArtifact]));
    }

    private static Guid[] SourceIds(Fixture fixture) =>
        [fixture.SceneArtifact.ArtifactId, fixture.StackArtifact.ArtifactId];

    private static PresentationMetadataFactsProductV1 WithRecomputedIdentity(
        PresentationMetadataFactsProductV1 facts)
    {
        facts = facts with
        {
            FactsIdentitySha256 = string.Empty,
            Corners = facts.Corners with { SourceIdentitySha256 = string.Empty }
        };
        var identity = PresentationProcessingProducts.ComputeMetadataFactsIdentity(facts);
        return facts with
        {
            FactsIdentitySha256 = identity,
            Corners = facts.Corners with { SourceIdentitySha256 = identity }
        };
    }

    private async Task CommitAsync(SqliteEnvironmentalObservationOutbox store, EnvironmentalObservationFactV1 fact) =>
        _ = await store.CommitLocalAsync(_root!, fact, CancellationToken.None).ConfigureAwait(false);

    private static ProcessingArtifact Artifact(Guid id, FrameArtifactRole role, TimeSpan integration,
        ProcessingCompatibilityIdentity compatibility, string identity) => new(
        id, role, role.ToString(), new string('C', 64), "application/octet-stream", null, new byte[] { 1 }, Epoch,
        integration, compatibility)
        {
            ProductKind = ProcessingProductKind.Metadata,
            ContentIdentitySha256 = identity
        };

    private static ProcessingProduct Product(ProcessingArtifact artifact, IReadOnlyList<Guid> sources) => new(
        artifact.Role, "combined-preview", new string('D', 64), artifact.MediaType, artifact.Layout,
        artifact.Payload, ProcessingIdentity.ComputePayloadSha256(artifact.Payload),
        ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            BuiltInProcessingRecipes.RollingMean, "1.0.0", "test", JsonSerializer.SerializeToElement(new { }))),
        [new ProcessingAlgorithmIdentity("test", "1")], sources, artifact.Integration, artifact.Compatibility);

    private static async Task<ProjectedSceneV1> CreateSceneAsync(ReconstructionDescriptor descriptor)
    {
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(
            new VisibleSceneRequest(Epoch, new ObserverLocation(0, 0, 0),
                new EquidistantProjectionContext(1, 1, 1, 1, WidthPixels: 2, HeightPixels: 2),
                new CatalogQuery(6.5, 10),
                new CatalogMetadata("test", "1", new Uri("https://example.invalid"), new string('0', 64), "test", "1"),
                projectionVersion: "projection-v1", algorithmVersion: "astronomy-v1")).ConfigureAwait(false);
        return ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative, visible,
            ProjectedSceneImageTransformV1.Identity(2, 2),
            new ProjectedSceneSource(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
                CaptureContractJson.ComputeDescriptorSha256(descriptor)), "calibration-v1", "projection-v1");
    }

    private static CaptureCycleEvidence CreateCycleEvidence(ReconstructionDescriptor descriptor) => new(
        CaptureCadenceMode.MinimumStartInterval, CaptureStartReason.DeadlineReached,
        AutomaticControlOwnership.Disabled, AutomaticControlOwnership.Disabled, null,
        descriptor.Timing.RequestedStartUtc, TimeSpan.FromSeconds(10), null,
        new CaptureControlDecisionEvidence(descriptor.Timing.ReadoutCompletedUtc, descriptor.Timing.ReadoutCompletedUtc,
            descriptor.Controls.EffectiveExposure, descriptor.Controls.EffectiveGain,
            descriptor.Controls.EffectiveExposure, descriptor.Controls.EffectiveGain,
            CaptureControlDecisionReason.Disabled), descriptor.Timing.ReadoutCompletedUtc);

    private static CaptureScheduleAdmissionEvidence CreateScheduleAdmission() => new(
        CaptureScheduleAdmissionEvidence.CurrentSchemaVersion, "revision", new string('1', 64), new string('2', 64),
        "night", CaptureScheduleAdmissionReason.WeeklyWindow, CaptureScheduleIntervalSource.WeeklyWindow,
        Epoch, Epoch, Epoch.AddDays(1), "interval", "expansion-v1", new string('3', 64), new string('4', 64),
        "location", 1);

    private static EnvironmentalObservationFactV1 Fact(Guid id, EnvironmentalObservationKind kind, double value,
        DateTimeOffset? staleAfter = null, double? uncertainty = null, string sourceId = "weather")
    {
        var parameters = JsonSerializer.SerializeToElement(new { });
        return new(EnvironmentalObservationSchemaVersions.V1, id,
            new EnvironmentalObservationSource("test", sourceId, "1", EnvironmentalObservationSourceKind.Measured,
                new EnvironmentalObservationProvenance(new ProcessingAlgorithmIdentity("test", "1"), parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            Epoch, null, null, Epoch, Epoch.AddMinutes(1), staleAfter ?? Epoch.AddMinutes(1),
            new EnvironmentalObservationValue(kind, kind switch
            {
                EnvironmentalObservationKind.AirTemperature => EnvironmentalObservationUnit.DegreesCelsius,
                EnvironmentalObservationKind.RelativeHumidity => EnvironmentalObservationUnit.Percent,
                EnvironmentalObservationKind.CloudCover => EnvironmentalObservationUnit.Fraction,
                _ => EnvironmentalObservationUnit.Fraction
            }, value, null, EnvironmentalObservationQuality.Good, uncertainty), []);
    }

    private sealed record Fixture(CaptureProcessingContext Context, ReconstructionDescriptor Descriptor,
        EnvironmentalAssociationService Associations, PresentationMetadataFactsBuilder Builder,
        ProjectedSceneV1 Scene, ProcessingProduct Stack, ProcessingArtifact SceneArtifact,
        ProcessingArtifact StackArtifact);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
