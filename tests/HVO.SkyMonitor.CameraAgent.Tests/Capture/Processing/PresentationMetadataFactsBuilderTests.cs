using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class PresentationMetadataFactsBuilderTests
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
            fixture.Context, fixture.Scene, fixture.Stack, kinds, CancellationToken.None).ConfigureAwait(false);

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
        var payload = PresentationLayerProducers.FromMetadataFacts(facts.Corners, 2, 2);
        Assert.IsTrue(payload.TextBlocks.SelectMany(static block => block.Lines).All(
            static line => line.Length <= HVO.SkyMonitor.Imaging.PresentationLayerPayloadV1.MaximumLineCharacters));

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
            fixture.Context, fixture.Scene, fixture.Stack, [], CancellationToken.None).ConfigureAwait(false);

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
            fixture.Context, fixture.Scene, fixture.Stack, kinds, CancellationToken.None).ConfigureAwait(false);

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
            fixture.Context, fixture.Scene, fixture.Stack, changedKinds, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(pending);
        var changedAssociations = await fixture.Associations.AssociateAsync(
            fixture.Descriptor.Capture.CaptureId, fixture.Descriptor.Capture.CaptureSequence,
            fixture.Descriptor.Timing.ExposureStartedUtc, fixture.Descriptor.Timing.ExposureEndedUtc,
            fixture.Descriptor.Capture.RigId, changedKinds, CancellationToken.None).ConfigureAwait(false);
        fixture.Context.BeginNode("changed-environment", []);
        var changed = await fixture.Builder.BuildAsync(
            fixture.Context, fixture.Scene, fixture.Stack, changedKinds, CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(changed);
        Assert.AreNotEqual(first.Facts.FactsIdentitySha256, changed.FactsIdentitySha256);
        Assert.HasCount(2, changed.Environment);
        Assert.IsTrue(changedAssociations.All(item => item.PolicyIdentitySha256 ==
            EnvironmentalAssociationService.CreatePolicyIdentity(changedKinds)));
        Assert.IsTrue(changedAssociations.All(item => first.Facts.Environment.All(previous =>
            previous.PolicyIdentitySha256 != item.PolicyIdentitySha256)));
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

    private async Task<Fixture> CreateFixtureAsync(SqliteEnvironmentalObservationOutbox store)
    {
        var payload = new byte[8];
        var original = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono16, 2, 2, 4, payload);
        var schedule = new CaptureScheduleDefinition("capture-schedule-v1",
            [new CaptureScheduleSetpointProfile("night", TimeSpan.FromSeconds(5), 82,
                TimeSpan.FromSeconds(10), CaptureCadenceMode.MinimumStartInterval)], [],
            LegacyAlwaysOpen: true, LegacySetpointProfileId: "night");
        var config = new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"), new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                new SensorProfile("test", 2, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0), new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10), 82, 82)), AgentId: "agent")
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
                ReadoutCompletedUtc = Epoch.AddSeconds(5)
            },
            Controls = original.Descriptor.Controls with { EffectiveExposure = TimeSpan.FromSeconds(5) },
            Profiles = original.Descriptor.Profiles with
            {
                Calibration = new ProfileIdentityDescriptor(
                    "virtual-calibration-source-model", "virtual-calibration-source-model-v1", new string('A', 64))
            },
            CycleEvidence = CreateCycleEvidence(original.Descriptor) with
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
            EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions { Enabled = true }
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
            fixture.Context, fixture.Scene, fixture.Stack, [EnvironmentalObservationKind.AirTemperature],
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(facts);
        return (facts, PresentationProcessingProducts.CreateMetadataFactsProduct(
            facts, "facts", [fixture.SceneArtifact, fixture.StackArtifact]));
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
        new CaptureControlDecisionEvidence(descriptor.Timing.RequestedStartUtc, descriptor.Timing.RequestedStartUtc,
            descriptor.Controls.EffectiveExposure, descriptor.Controls.EffectiveGain,
            descriptor.Controls.EffectiveExposure, descriptor.Controls.EffectiveGain,
            CaptureControlDecisionReason.Disabled), descriptor.Timing.RequestedStartUtc);

    private static CaptureScheduleAdmissionEvidence CreateScheduleAdmission() => new(
        CaptureScheduleAdmissionEvidence.CurrentSchemaVersion, "revision", new string('1', 64), new string('2', 64),
        "night", CaptureScheduleAdmissionReason.LegacyCompatibility, CaptureScheduleIntervalSource.LegacyCompatibility,
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
