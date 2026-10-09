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

/// <summary>
/// The central measured-association node through the real path (#526 C2/C3): multipart ingest of a linear Mono16 raw
/// frame and its edge projected scene, the frame's descriptor rebuilt by <see cref="CentralReconstructionDescriptorFactory"/>
/// from the persisted rows, the shared <see cref="LogicHostRecipeExecutionAdapter.CreateArtifact"/> and the real
/// executor. No descriptor is built by hand on the central side.
/// </summary>
public sealed partial class ArtifactIngestTests
{
    private const int MeasuredWidth = 96, MeasuredHeight = 64;

    [TestMethod]
    public async Task MeasuredAssociationsBindTheIngestedSceneThroughTheReconstructedDescriptor()
    {
        var data = await CreateMeasuredAssociationCaseAsync().ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        try
        {
            // C2: the descriptor central rebuilds from its own rows hashes to the manifest identity the edge scene
            // recorded, and CreateArtifact carries that identity and the capture into the recipe's source.
            var raw = await db.CentralArtifacts.AsNoTracking()
                .Include(artifact => artifact.Layout)
                .Include(artifact => artifact.Recipe)
                .Include(artifact => artifact.Sources)
                .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Timing)
                .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Control)
                .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Profiles)
                .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Location)
                .SingleAsync(artifact => artifact.ArtifactId == data.Raw.Descriptor.Artifact.ArtifactId).ConfigureAwait(false);
            var persisted = CentralReconstructionDescriptorFactory.Create(raw.Frame!, raw);
            CaptureContractJson.ComputeDescriptorSha256(persisted).Should().Be(data.Raw.IdempotencyKey);
            var source = LogicHostRecipeExecutionAdapter.CreateArtifact(persisted);
            source.DescriptorIdentitySha256.Should().Be(data.Raw.IdempotencyKey);
            source.CaptureId.Should().Be(data.Raw.Descriptor.Capture.CaptureId);

            await using var worker = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var lease = await ClaimMeasuredLeaseAsync(worker, data).ConfigureAwait(false);
            lease.RecipeName.Should().Be(BuiltInProcessingRecipes.MeasuredStellarAssociations);
            lease.ProjectedScene!.Source.ArtifactIdentitySha256.Should().Be(data.Raw.IdempotencyKey);
            var result = await worker.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);

            result.Status.Should().Be(ProcessingOutcomeStatus.Produced, result.ReasonCode);
            result.ReasonCode.Should().BeNull("the happy path raises neither a descriptor nor a scene identity mismatch");
            using var owner = await ArtifactRetrievalTests.CreateUserClientAsync(
                TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
            var bytes = await ReadDerivativeAsync(owner, lease.SourceDevicePublicId, result.ArtifactId!.Value)
                .ConfigureAwait(false);
            var associations = MeasuredStellarAssociationJson.Parse(bytes).Associations;
            associations.Should().NotBeNull();
            associations!.Source.DescriptorIdentitySha256.Should().Be(data.Raw.IdempotencyKey.ToUpperInvariant());
            associations.Associations.Select(static item => item.CatalogId).Should().BeEquivalentTo("alpha", "beta");

            // The edge-equivalent execution, from the uploaded manifest descriptor and the scene bytes, produces the
            // same product under the same recipe identity.
            using var options = JsonDocument.Parse(lease.RecipeOptionsJson);
            var selector = JsonSerializer.Deserialize<ProcessingInputSelector>(lease.InputSelectorJson, SceneSelectorOptions);
            var scene = CentralMeasuredAssociationSceneReader.Verify(lease.ProjectedScene!, data.SceneBytes);
            scene.FailureReasonCode.Should().BeNull();
            var expected = await worker.ServiceProvider.GetRequiredService<LogicHostRecipeExecutionAdapter>()
                .ExecuteAsync(data.Raw.Descriptor, data.Pixels, lease.RecipeName, options.RootElement, selector!,
                    lease.TargetVariant, auxiliaryInputs: [scene.Input!]).ConfigureAwait(false);
            expected.Status.Should().Be(ProcessingOutcomeStatus.Produced, expected.ReasonCode);
            lease.ExpectedRecipeIdentitySha256.Should().Be(expected.Products.Single().Recipe.IdentitySha256);
            bytes.Should().Equal(expected.Products.Single().Payload.ToArray());

            // The annotation then binds the produced product as its auxiliary.
            await worker.ServiceProvider.GetRequiredService<ICentralProcessingGraphScheduler>()
                .ConvergeAsync(lease.GraphExecutionId!.Value, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            var annotation = await ClaimMeasuredLeaseAsync(worker, data).ConfigureAwait(false);
            annotation.RecipeName.Should().Be(BuiltInProcessingRecipes.Annotation);
            annotation.Inputs.Should().Contain(item =>
                item.BindingName == BuiltInProcessingRecipes.MeasuredStellarAssociationsInputName
                && item.ArtifactId == result.ArtifactId);
            var annotated = await worker.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(annotation, CancellationToken.None).ConfigureAwait(false);
            annotated.Status.Should().Be(ProcessingOutcomeStatus.Produced, annotated.ReasonCode);
        }
        finally
        {
            await TerminalizeCaptureAsync(db, data.Raw.Descriptor.Capture.CaptureId).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task DescriptorDriftFailsMeasuredAssociationsClosedAndTheAnnotationOmitsThem()
    {
        var data = await CreateMeasuredAssociationCaseAsync().ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        try
        {
            // C3: after the scene was frozen, a persisted raw row drifts. Durable ingress time is hashed into the
            // reconstructed descriptor but never into the pixels or any recipe identity, so only the descriptor
            // identity the recipe checks against the scene source moves.
            var captureId = data.Raw.Descriptor.Capture.CaptureId;
            (await db.CentralCaptureTimings.Where(item => item.Frame!.FrameId == captureId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DurableIngressUtc,
                    item => item.DurableIngressUtc.AddMilliseconds(1))).ConfigureAwait(false)).Should().Be(1);

            await using var worker = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var lease = await ClaimMeasuredLeaseAsync(worker, data).ConfigureAwait(false);
            lease.RecipeName.Should().Be(BuiltInProcessingRecipes.MeasuredStellarAssociations);
            var result = await worker.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);

            result.Status.Should().Be(ProcessingOutcomeStatus.TerminalFailure);
            result.ReasonCode.Should().Be(ProcessingReasonCodes.ProjectedSceneDescriptorMismatch, "the executor result");
            result.ArtifactId.Should().BeNull();
            var job = await db.CentralDerivativeJobs.AsNoTracking().Include(item => item.Outputs)
                .SingleAsync(item => item.Id == lease.JobId).ConfigureAwait(false);
            job.Status.Should().Be(CentralDerivativeJobStatus.TerminalFailure);
            // A leased job's failure reason is LastError; StateReasonCode carries scheduler waiting state only.
            job.LastError.Should().Be(ProcessingReasonCodes.ProjectedSceneDescriptorMismatch, "the durable job row");
            job.StateReasonCode.Should().BeNull("the job failed under its lease, it did not wait");
            job.Outputs.Should().OnlyContain(item => item.ResultCentralArtifactId == null);
            (await db.CentralArtifacts.AsNoTracking().CountAsync(item => item.Frame!.FrameId == captureId &&
                    item.Variant == CentralDerivativeRecipeCatalog.MeasuredStellarAssociationsVariant)
                .ConfigureAwait(false)).Should().Be(0, "no partial association is published");

            // Condition 6: the annotation omits the failed auxiliary and renders with every star label suppressed.
            await worker.ServiceProvider.GetRequiredService<ICentralProcessingGraphScheduler>()
                .ConvergeAsync(lease.GraphExecutionId!.Value, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            var annotation = await ClaimMeasuredLeaseAsync(worker, data).ConfigureAwait(false);
            annotation.RecipeName.Should().Be(BuiltInProcessingRecipes.Annotation);
            annotation.Inputs.Should().NotContain(item =>
                item.BindingName == BuiltInProcessingRecipes.MeasuredStellarAssociationsInputName);
            var annotated = await worker.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(annotation, CancellationToken.None).ConfigureAwait(false);
            annotated.Status.Should().Be(ProcessingOutcomeStatus.Produced, annotated.ReasonCode);
        }
        finally
        {
            await TerminalizeCaptureAsync(db, data.Raw.Descriptor.Capture.CaptureId).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task ConcurrentSceneDeliveryAndLiveSchedulingExpandTheMeasuredAssociationsOnceWithTheSceneFrozen()
    {
        // Condition 3 contention: the raw is already ingested and awaiting its scene, then three duplicate scene
        // deliveries race two direct live schedules of the raw. One execution expands, the association node carries
        // exactly one scene requirement resolved at expansion, and the lease binds that frozen scene.
        var outcomes = new List<CentralProcessingGraphScheduleOutcome>();
        var data = await CreateMeasuredAssociationCaseAsync(async (client, raw, sceneManifest, sceneBytes) =>
        {
            Guid rawArtifactId;
            await using (var lookup = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope())
                rawArtifactId = await lookup.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralArtifacts
                    .Where(item => item.Frame!.FrameId == raw.Descriptor.Capture.CaptureId && item.Role == FrameArtifactRole.Raw)
                    .Select(static item => item.Id).SingleAsync().ConfigureAwait(false);
            async Task<CentralProcessingGraphScheduleOutcome> ScheduleAsync()
            {
                await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
                return (await scope.ServiceProvider.GetRequiredService<ICentralProcessingGraphScheduler>()
                    .ScheduleLiveAsync(rawArtifactId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false)).Outcome;
            }
            async Task DeliverAsync()
            {
                using var response = await PostAsync(client, sceneManifest, sceneBytes).ConfigureAwait(false);
                response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            (await ScheduleAsync().ConfigureAwait(false)).Should().Be(CentralProcessingGraphScheduleOutcome.AwaitingSources);
            var schedules = new[] { ScheduleAsync(), ScheduleAsync() };
            await Task.WhenAll(DeliverAsync(), DeliverAsync(), DeliverAsync(), Task.WhenAll(schedules)).ConfigureAwait(false);
            outcomes.AddRange(schedules.Select(static task => task.Result));
        }).ConfigureAwait(false);
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        try
        {
            outcomes.Should().OnlyContain(item => item == CentralProcessingGraphScheduleOutcome.Created ||
                item == CentralProcessingGraphScheduleOutcome.Existing || item == CentralProcessingGraphScheduleOutcome.AwaitingSources);
            var captureId = data.Raw.Descriptor.Capture.CaptureId;
            var jobs = await db.CentralDerivativeJobs.AsNoTracking().Include(item => item.InputRequirements)
                .Include(item => item.Inputs)
                .Where(item => item.SourceArtifact!.Frame!.FrameId == captureId).ToListAsync().ConfigureAwait(false);
            jobs.Select(static item => item.GraphExecutionId).Distinct().Should().ContainSingle()
                .Which.Should().NotBeNull();
            var measured = jobs.Single(static item => item.RecipeName == BuiltInProcessingRecipes.MeasuredStellarAssociations);
            var requirement = measured.InputRequirements
                .Should().ContainSingle(item => item.BindingName == CentralProjectedSceneResolver.BindingName).Which;
            requirement.ResolutionState.Should().Be(CentralDerivativeInputResolutionState.Resolved);
            // Convergence materialized exactly one frozen input row for that requirement, bound to the expected scene.
            measured.Inputs.Should().ContainSingle(item => item.CentralDerivativeJobInputRequirementId == requirement.Id)
                .Which.CentralArtifactId.Should().Be(requirement.ExpectedCentralArtifactId!.Value);
            CentralProjectedSceneResolver.ReadReference(measured)!.Source.ArtifactIdentitySha256
                .Should().Be(data.Raw.IdempotencyKey);

            await using var worker = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
            var lease = await ClaimMeasuredLeaseAsync(worker, data).ConfigureAwait(false);
            lease.JobId.Should().Be(measured.Id);
            lease.ExpectedRecipeIdentitySha256.Should().Be(measured.ExpectedRecipeIdentitySha256);
            CentralMeasuredAssociationSceneReader.Verify(lease.ProjectedScene!, data.SceneBytes).Input.Should().NotBeNull();
            var result = await worker.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            result.Status.Should().Be(ProcessingOutcomeStatus.Produced, result.ReasonCode);
        }
        finally
        {
            await TerminalizeCaptureAsync(db, data.Raw.Descriptor.Capture.CaptureId).ConfigureAwait(false);
        }
    }

    private sealed record MeasuredAssociationCase(ArtifactManifestV2 Raw, byte[] Pixels, byte[] SceneBytes);

    private static async Task<CentralDerivativeJobLease> ClaimMeasuredLeaseAsync(
        AsyncServiceScope worker, MeasuredAssociationCase data)
    {
        var lease = await worker.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
            .ClaimNextAsync("measured-associations", TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        lease.Should().NotBeNull();
        lease!.FrameId.Should().Be(data.Raw.Descriptor.Capture.CaptureId);
        return lease;
    }

    private static Task<int> TerminalizeCaptureAsync(ApplicationDbContext db, Guid captureId)
        // Tests in this assembly claim the next job from the shared database; leave none of this capture's claimable.
        => db.CentralDerivativeJobs.Where(item => item.SourceArtifact!.Frame!.FrameId == captureId &&
                (item.Status == CentralDerivativeJobStatus.Pending || item.Status == CentralDerivativeJobStatus.RetryableFailure))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null));

    /// <summary>
    /// Ingests a 96x64 linear Mono16 raw frame with two rendered stars and the edge projected scene that predicts them,
    /// on a camera assigned the seeded measured-association and annotation nodes. Every other claimable job in the
    /// shared database is terminalized so the next claim belongs to this capture.
    /// </summary>
    private static async Task<MeasuredAssociationCase> CreateMeasuredAssociationCaseAsync(
        Func<HttpClient, ArtifactManifestV2, StructuredProcessingProductManifestV1, byte[], Task>? deliverScene = null)
    {
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig($"measured-{Guid.NewGuid():N}");
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        await AssignMeasuredAssociationGraphAsync(registrationId).ConfigureAwait(false);

        var utc = DateTimeOffset.UnixEpoch;
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15;
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("alpha", "ALPHA", (siderealHours + 2d / 15 + 24) % 24, 1.5, 1.2),
            new CelestialCatalogObject("beta", "BETA", (siderealHours - 3d / 15 + 24) % 24, -1, 1.8)
        ])).BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, MeasuredWidth / 2d, MeasuredHeight / 2d, 400, 400,
                MeasuredWidth, MeasuredHeight, ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"),
                new string('C', 64), "test", "v1"), projectionVersion: "perspective-v1")).ConfigureAwait(false);

        var samples = Enumerable.Repeat(1000d, MeasuredWidth * MeasuredHeight).ToArray();
        foreach (var star in visible.Objects)
        {
            for (var index = 0; index < samples.Length; index++)
            {
                var dx = (index % MeasuredWidth + 0.5 - star.Pixel.X) / 1.4;
                var dy = (index / MeasuredWidth + 0.5 - star.Pixel.Y) / 1.4;
                var exponent = 0.5 * (dx * dx + dy * dy);
                if (exponent < 40) samples[index] += 3000 * Math.Exp(-exponent);
            }
        }
        // Deterministic low-amplitude texture so the background noise estimate is finite and non-zero.
        for (var index = 0; index < samples.Length; index++) samples[index] += (index * 7919 % 13) - 6;
        var pixels = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            var value = (ushort)Math.Clamp(Math.Round(samples[index]), 0, ushort.MaxValue);
            pixels[index * 2] = (byte)value;
            pixels[index * 2 + 1] = (byte)(value >> 8);
        }

        var raw = CreateManifestV2(deviceId, rig, pixels, 5260);
        raw = raw with
        {
            Descriptor = raw.Descriptor with
            {
                Layout = new FrameLayoutDescriptor(MeasuredWidth, MeasuredHeight, MeasuredWidth * 2, CameraPixelFormat.Mono16,
                    FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None,
                    0, ushort.MaxValue, pixels.LongLength),
                Artifact = raw.Descriptor.Artifact with { MediaType = "application/x-skymonitor-mono16" }
            }
        };
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(MeasuredWidth, MeasuredHeight),
            new ProjectedSceneSource(raw.Descriptor.Capture.CaptureId, raw.Descriptor.Artifact.ArtifactId, raw.IdempotencyKey),
            "calibration-v1", visible.Request.ProjectionVersion);
        scene.Objects.Should().HaveCount(2).And.OnlyContain(item =>
            item.Pixel.X > 8 && item.Pixel.X < MeasuredWidth - 8 && item.Pixel.Y > 8 && item.Pixel.Y < MeasuredHeight - 8);
        var full = new SceneProvenance(new string('D', 64), rig.ProfileVersion, scene.Catalog.Name,
            scene.Catalog.Version, scene.Catalog.ChecksumSha256, "Perspective", scene.Projection.AlgorithmVersion,
            scene.AstronomyAlgorithmVersion, "sensor-v1",
            Objects: scene.Objects.Select(item => new ProjectedObjectProvenance(
                item.Id, item.DisplayName, item.Pixel.X, item.Pixel.Y, item.Magnitude)).ToArray(),
            Segments: [], ConstellationIds: [], RigProfileHashSha256: CameraRigProfileIdentity.ComputeSha256(rig),
            ProjectionCalibrationVersion: "calibration-v1", SceneUtc: utc,
            ProjectedSceneStageSchemaVersion: "projected-scene-stage-v1", ProjectedSceneStageKey: new string('E', 64));
        raw = raw with { Scene = full.WithoutProjectedGeometry() };

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

        using var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await GetSystemTokenAsync(client).ConfigureAwait(false));
        using (var response = await PostAsync(client, raw, pixels).ConfigureAwait(false))
            response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (deliverScene is not null)
        {
            await deliverScene(client, raw, sceneManifest, sceneBytes).ConfigureAwait(false);
        }
        else
        {
            using var response = await PostAsync(client, sceneManifest, sceneBytes).ConfigureAwait(false);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var captureId = raw.Descriptor.Capture.CaptureId;
        await db.CentralDerivativeJobs.Where(item => item.SourceArtifact!.Frame!.FrameId != captureId &&
                (item.Status == CentralDerivativeJobStatus.Pending || item.Status == CentralDerivativeJobStatus.RetryableFailure))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(item => item.AvailableAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
        var jobs = await db.CentralDerivativeJobs.AsNoTracking()
            .Where(item => item.SourceArtifact!.Frame!.FrameId == captureId)
            .Select(item => new { item.RecipeName, item.Status }).ToListAsync().ConfigureAwait(false);
        jobs.Should().BeEquivalentTo([
            new { RecipeName = BuiltInProcessingRecipes.MeasuredStellarAssociations, Status = CentralDerivativeJobStatus.Pending },
            new { RecipeName = BuiltInProcessingRecipes.Annotation, Status = CentralDerivativeJobStatus.Waiting }
        ]);
        return new(raw, pixels, sceneBytes);
    }

    private static async Task AssignMeasuredAssociationGraphAsync(Guid registrationId)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registration = await db.DeviceRegistrations.SingleAsync(item => item.Id == registrationId).ConfigureAwait(false);
        var camera = new LogicalCamera
        {
            ObservatoryId = registration.ObservatoryId,
            Slug = $"measured-{Guid.NewGuid():N}",
            Name = "Measured association camera",
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
            AssignmentReasonCode = "measured-association-test"
        };
        camera.Installations.Add(installation);
        // The seeded revision-2 nodes, verbatim: the raw-only association node and the annotation that optionally
        // consumes it.
        var registry = scope.ServiceProvider.GetRequiredService<ICentralProcessingGraphNodeRegistry>();
        var seeded = DatabaseSeeder.CreateBasicCentralProcessingGraph();
        var definition = seeded with
        {
            Name = $"measured-associations-{Guid.NewGuid():N}",
            Sources = [.. seeded.Sources.Where(static source => source.Id == "$raw")],
            Nodes = [.. seeded.Nodes.Where(static node => node.Id is "MeasuredStellarAssociations" or "Annotation")]
        };
        var portable = ProcessingGraphCompiler.Compile(definition);
        portable.IsValid.Should().BeTrue(string.Join(Environment.NewLine, portable.Diagnostics));
        var central = ProcessingGraphCompiler.Compile(definition, new(ProcessingGraphHosts.LogicHost, registry.Capabilities));
        central.IsValid.Should().BeTrue(string.Join(Environment.NewLine, central.Diagnostics));
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
            ReasonCode = "measured-association-test"
        };
        revision.Assignments.Add(assignment);
        db.AddRange(camera, installation, revision, assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
