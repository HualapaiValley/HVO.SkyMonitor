using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ArtifactIngestTests
{
    [TestMethod]
    [DataRow("raw,scene,derivative", false)]
    [DataRow("derivative,raw,scene", false)]
    [DataRow("scene,derivative,raw", false)]
    [DataRow("raw,derivative,scene", false)]
    [DataRow("raw,scene,derivative", true)]
    [DataRow("derivative,raw,scene", true)]
    [DataRow("scene,derivative,raw", true)]
    [DataRow("raw,derivative,scene", true)]
    public async Task CompactSceneArrivalsFreezeOneSourceBoundAnnotationAndPreservePixels(string order, bool graph)
    {
        ArgumentNullException.ThrowIfNull(order);
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig($"compact-scene-{Guid.NewGuid():N}");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        if (graph) await AssignCompactSceneAnnotationGraphAsync(registrationId).ConfigureAwait(false);
        var utc = DateTimeOffset.UnixEpoch;
        var pixels = new byte[] { 1, 32, 128, 255 };
        var raw = CreateManifestV2(deviceId, rig, pixels, 1055);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("zenith", "Zenith", AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15, 0, 1)
        ])).BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, 1, 1, 1, 1, 2, 2,
                ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"),
                new string('C', 64), "test", "v1"), projectionVersion: "perspective-v1")).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(2, 2),
            new ProjectedSceneSource(raw.Descriptor.Capture.CaptureId, raw.Descriptor.Artifact.ArtifactId,
                raw.IdempotencyKey), "calibration-v1", visible.Request.ProjectionVersion);
        var full = new SceneProvenance(new string('D', 64), rig.ProfileVersion, scene.Catalog.Name,
            scene.Catalog.Version, scene.Catalog.ChecksumSha256, "Perspective", scene.Projection.AlgorithmVersion,
            scene.AstronomyAlgorithmVersion, "sensor-v1",
            Objects: scene.Objects.Select(item => new ProjectedObjectProvenance(
                item.Id, item.DisplayName, item.Pixel.X, item.Pixel.Y, item.Magnitude)).ToArray(),
            Segments: [], ConstellationIds: [], RigProfileHashSha256: CameraRigProfileIdentity.ComputeSha256(rig),
            ProjectionCalibrationVersion: "calibration-v1", SceneUtc: utc,
            ProjectedSceneStageSchemaVersion: "projected-scene-stage-v1", ProjectedSceneStageKey: new string('E', 64));
        raw = raw with { Scene = full.WithoutProjectedGeometry() };
        var derivative = CreateManifestV2(deviceId, rig, pixels, 1055, role: FrameArtifactRole.Preview,
            sourceArtifactIds: [raw.Descriptor.Artifact.ArtifactId], captureId: raw.Descriptor.Capture.CaptureId)
            with
        { Scene = raw.Scene };
        var sceneBytes = ProjectedSceneJson.Serialize(scene);
        var recipe = RecipeIdentityDescriptor.Create(BuiltInProcessingRecipes.ProjectedScene, "1.0.0", "integration-v1",
            JsonSerializer.SerializeToElement(new { }));
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, "projected-scene",
            ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256, [raw.Descriptor.Artifact.ArtifactId]);
        var sceneArtifact = new ArtifactDescriptor(ProcessingIdentity.CreateArtifactId(outputIdentity),
            FrameArtifactRole.Metadata, "projected-scene-step", "projected-scene", raw.Descriptor.Timing.ReadoutCompletedUtc,
            [raw.Descriptor.Artifact.ArtifactId], recipe, StructuredProcessingProductContracts.ProjectedSceneMediaType,
            Convert.ToHexString(SHA256.HashData(sceneBytes)));
        var sceneManifest = new StructuredProcessingProductManifestV1(StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new StructuredProcessingProductDescriptorV1(raw.Descriptor, sceneArtifact, outputIdentity,
                [new("projected-scene", "integration-v1")], new("rig", "orientation", "calibration", "mask", "sensor", "night", "processing"),
                raw.Descriptor.Controls.EffectiveExposure.Ticks, sceneBytes.Length, ProcessingProductKind.Metadata,
                scene.SchemaVersion, scene.SceneIdentitySha256), "derived/projected-scene.json", "projected-scene-step");
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await GetSystemTokenAsync(client).ConfigureAwait(false));
        var received = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in order.Split(','))
        {
            using var response = item == "scene"
                ? await PostAsync(client, sceneManifest, sceneBytes).ConfigureAwait(false)
                : await PostAsync(client, item == "raw" ? raw : derivative, pixels).ConfigureAwait(false);
            response.StatusCode.Should().Be(
                item != "raw" && !received.Contains("raw") ? (HttpStatusCode)425 : HttpStatusCode.Accepted,
                $"arrival {item}: {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
            received.Add(item);
            if (!received.Contains("scene") || !received.Contains("raw"))
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                (await db.CentralDerivativeJobs.CountAsync(job =>
                    job.SourceArtifact!.Frame!.FrameId == raw.Descriptor.Capture.CaptureId &&
                    job.RecipeName == BuiltInProcessingRecipes.Annotation).ConfigureAwait(false)).Should().Be(0);
            }
        }
        // Durable 425 arrivals become acknowledgable once their exact raw source arrives.
        using (var duplicate = await PostAsync(client, sceneManifest, sceneBytes).ConfigureAwait(false))
            duplicate.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using (var duplicate = await PostAsync(client, derivative, pixels).ConfigureAwait(false))
            duplicate.StatusCode.Should().Be(HttpStatusCode.Accepted);

        Guid jobId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var job = await db.CentralDerivativeJobs.Include(item => item.InputRequirements).Include(item => item.Inputs)
                .SingleAsync(item => item.SourceArtifact!.Frame!.FrameId == raw.Descriptor.Capture.CaptureId &&
                    item.RecipeName == BuiltInProcessingRecipes.Annotation).ConfigureAwait(false);
            jobId = job.Id;
            job.GraphExecutionId.HasValue.Should().Be(graph);
            var reference = CentralProjectedSceneResolver.ReadReference(job);
            reference.Should().NotBeNull();
            reference!.Source.ArtifactIdentitySha256.Should().Be(raw.IdempotencyKey);
            job.InputRequirements.Single(item => item.BindingName == CentralProjectedSceneResolver.BindingName)
                .SelectorJson.Length.Should().BeLessThanOrEqualTo(2048);
            (await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionReferences>()
                .IsHeldAsync(reference.CentralArtifactId, CancellationToken.None).ConfigureAwait(false)).Should().BeTrue();
            await db.CentralDerivativeJobs.Where(item => item.Id != jobId &&
                    (item.Status == CentralDerivativeJobStatus.Pending || item.Status == CentralDerivativeJobStatus.RetryableFailure))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
        }
        await using var workerScope = fixture.Factory.Services.CreateAsyncScope();
        var lease = await workerScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
            .ClaimNextAsync("compact-scene-worker", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        lease.Should().NotBeNull();
        lease!.JobId.Should().Be(jobId);
        lease.ProjectedScene.Should().NotBeNull();
        using var options = JsonDocument.Parse(lease.RecipeOptionsJson);
        var expected = await workerScope.ServiceProvider.GetRequiredService<LogicHostRecipeExecutionAdapter>()
            .ExecuteAsync(raw.Descriptor, pixels, lease.RecipeName, options.RootElement, ProcessingInputSelector.Raw(),
                lease.TargetVariant, CreateExpectedAnnotation(full)).ConfigureAwait(false);
        expected.Products.Should().ContainSingle();
        lease.ExpectedRecipeIdentitySha256.Should().Be(expected.Products[0].Recipe.IdentitySha256);
        var result = await workerScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
            .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        result.Status.Should().Be(ProcessingOutcomeStatus.Produced, result.ReasonCode);
        using var owner = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password)
            .ConfigureAwait(false);
        var actual = await ReadDerivativeAsync(owner, lease.SourceDevicePublicId, result.ArtifactId!.Value).ConfigureAwait(false);
        actual.Should().Equal(expected.Products[0].Payload.ToArray());
    }

    private static async Task AssignCompactSceneAnnotationGraphAsync(Guid registrationId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
        var camera = new LogicalCamera
        {
            ObservatoryId = registration.ObservatoryId,
            Slug = $"compact-{Guid.NewGuid():N}",
            Name = "Compact scene camera",
            CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-2),
            CreatedByUserId = registration.OwnerUserId
        };
        var installation = new LogicalCameraInstallation
        {
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            RegistrationId = registration.Id,
            InstallationPublicId = Guid.NewGuid(),
            AssignedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
            AssignedByUserId = registration.OwnerUserId,
            AssignmentReasonCode = "compact-scene-test"
        };
        camera.Installations.Add(installation);
        var registry = scope.ServiceProvider.GetRequiredService<ICentralProcessingGraphNodeRegistry>();
        var seeded = DatabaseSeeder.CreateBasicCentralProcessingGraph();
        var definition = seeded with
        {
            Name = $"compact-annotation-{Guid.NewGuid():N}",
            Nodes = [.. seeded.Nodes.Where(node => node.Id == "Annotation")]
        };
        var portable = ProcessingGraphCompiler.Compile(definition);
        var central = ProcessingGraphCompiler.Compile(definition, new(ProcessingGraphHosts.LogicHost, registry.Capabilities));
        central.IsValid.Should().BeTrue();
        var revision = new CentralProcessingGraphRevision
        {
            Name = definition.Name,
            Revision = definition.Revision,
            DefinitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)),
            DefinitionIdentitySha256 = portable.Plan!.DefinitionIdentitySha256,
            PortablePlanIdentitySha256 = portable.Plan.PlanIdentitySha256,
            CentralPlanIdentitySha256 = central.Plan!.PlanIdentitySha256,
            CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-2),
            CreatedByUserId = registration.OwnerUserId,
            PublishedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
            PublishedByUserId = registration.OwnerUserId
        };
        var assignment = new CentralProcessingGraphAssignment
        {
            Revision = revision,
            RevisionId = revision.Id,
            TargetHost = CentralProcessingGraphTargetHost.Central,
            Scope = CentralProcessingGraphAssignmentScope.LogicalCamera,
            ObservatoryId = registration.ObservatoryId,
            LogicalCamera = camera,
            LogicalCameraId = camera.Id,
            EffectiveFromUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
            CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(-1),
            ActorUserId = registration.OwnerUserId,
            ReasonCode = "compact-scene-test"
        };
        revision.Assignments.Add(assignment);
        db.AddRange(camera, installation, revision, assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
