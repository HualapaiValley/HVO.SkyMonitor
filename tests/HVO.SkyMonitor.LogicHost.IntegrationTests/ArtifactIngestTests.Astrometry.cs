using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class ArtifactIngestTests
{
    [TestMethod]
    [DataRow("no-prior")]
    [DataRow("same-device-prior")]
    [DataRow("foreign-device-prior")]
    public async Task RetainedAstrometry_NativeIngestReconcilesAndReadsExactProducts(string scenario)
    {
        var fixture = AssemblyHooks.Fixture;
        var (deviceId, registrationId) = await SeedActiveDeviceAsync().ConfigureAwait(false);
        var rig = CreateRig("retained-astrometry-rig") with
        { Sensor = new("test-sensor", 512, 512, 4.8, SensorColorMode.Mono, CameraPixelFormat.Mono16) };
        await SeedRigProfileAsync(registrationId, rig).ConfigureAwait(false);
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetSystemTokenAsync(client).ConfigureAwait(false));
        var registrations = new List<Guid> { registrationId };
        try
        {
            AstrometricFrameAssessment? prior = null;
            ArtifactManifestV2? priorRaw = null;
            byte[]? priorBytes = null;
            if (scenario != "no-prior")
            {
                var priorDeviceId = deviceId;
                if (scenario == "foreign-device-prior")
                {
                    var foreign = await SeedActiveDeviceAsync().ConfigureAwait(false);
                    registrations.Add(foreign.RegistrationId);
                    priorDeviceId = foreign.DeviceId;
                    await SeedRigProfileAsync(foreign.RegistrationId, rig).ConfigureAwait(false);
                }
                var previous = await CreateNativeAstrometryAsync(priorDeviceId, rig, 1, null).ConfigureAwait(false);
                priorRaw = previous.Raw;
                priorBytes = previous.RawBytes;
                prior = MeasuredAstrometryJson.Parse(previous.Solution.Payload).Assessment;
                Assert.IsTrue(prior.HasMeasuredMapping, prior.Reason);
            }
            var current = await CreateNativeAstrometryAsync(deviceId, rig, 2, prior).ConfigureAwait(false);
            using var rawResponse = await PostAsync(client, current.Raw, current.RawBytes).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Accepted, rawResponse.StatusCode, await rawResponse.Content.ReadAsStringAsync().ConfigureAwait(false));

            // Submit descendants before parents. Source facts and rows must be created by real ingest.
            foreach (var product in new[] { current.Solution, current.Context, current.Measured, current.Scene })
            {
                using var response = await PostAsync(client, AstrometryUpload(current.Raw, product), product.Payload.ToArray()).ConfigureAwait(false);
                Assert.IsTrue(response.StatusCode == HttpStatusCode.Accepted || (int)response.StatusCode == 425,
                    $"{product.SchemaVersion}: {response.StatusCode} {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
            }
            if (priorRaw is not null)
            {
                using var priorResponse = await PostAsync(client, priorRaw, priorBytes!).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.Accepted, priorResponse.StatusCode);
            }
            using var telemetry = new CentralIngestTelemetry();
            var reconciler = new CentralArtifactReconciliationService(fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System, telemetry, NullLogger<CentralArtifactReconciliationService>.Instance);
            // Each pass admits the next source generation without changing stored states in the test.
            for (var pass = 0; pass < 4; pass++)
            {
                await using (var dueScope = fixture.Factory.Services.CreateAsyncScope())
                {
                    var db = dueScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await db.CentralArtifacts.Where(a => a.Frame!.RegistrationId == registrationId &&
                        a.ReconstructionState == CentralReconstructionState.PendingReference)
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.ReferenceRetryAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
                }
                _ = await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            using var owner = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
            foreach (var product in new[] { current.Context, current.Solution })
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var artifact = await db.CentralArtifacts.Include(a => a.Sources).ThenInclude(s => s.ResolvedArtifact)
                    .SingleAsync(a => a.ArtifactId == ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256)).ConfigureAwait(false);
                Assert.AreEqual(CentralArtifactObjectState.Available, artifact.ObjectState);
                if (scenario == "foreign-device-prior")
                {
                    Assert.AreEqual(CentralReconstructionState.PendingReference, artifact.ReconstructionState);
                    if (product == current.Context)
                        Assert.IsNull(artifact.Sources.Single(s => s.Ordinal == 3).ResolvedCentralArtifactId,
                            "A foreign device's raw cannot authenticate a prior slot.");
                    continue;
                }
                Assert.AreEqual(CentralReconstructionState.Complete, artifact.ReconstructionState, artifact.StateReasonCode);
                Assert.IsTrue(artifact.Sources.All(s => s.ResolvedCentralArtifactId.HasValue));
                if (product == current.Context && priorRaw is not null)
                    Assert.AreNotEqual(artifact.CentralFrameId, artifact.Sources.Single(s => s.Ordinal == 3).ResolvedArtifact!.CentralFrameId);
                using var retrieved = await owner.GetAsync(new Uri($"/api/v1.0/devices/{artifact.DevicePublicId:D}/artifacts/{artifact.ArtifactId:D}/content", UriKind.Relative)).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, retrieved.StatusCode);
                CollectionAssert.AreEqual(product.Payload.ToArray(), await retrieved.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            }
        }
        finally
        {
            // Raw ingest schedules background work. Leave only this fixture's jobs terminal
            // so subsequent tests using the shared global worker queue cannot claim them.
            await using var cleanupScope = fixture.Factory.Services.CreateAsyncScope();
            var db = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CentralDerivativeJobs.Where(j => registrations.Contains(j.SourceArtifact!.Frame!.RegistrationId))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, CentralDerivativeJobStatus.TerminalFailure)
                    .SetProperty(j => j.AvailableAtUtc, (DateTimeOffset?)null)).ConfigureAwait(false);
        }
    }

    private sealed record NativeAstrometry(ArtifactManifestV2 Raw, byte[] RawBytes, ProcessingProduct Scene,
        ProcessingProduct Measured, ProcessingProduct Context, ProcessingProduct Solution);

    private static async Task<NativeAstrometry> CreateNativeAstrometryAsync(string deviceId, CameraRigConfig rig,
        int sequence, AstrometricFrameAssessment? prior)
    {
        var reference = await MeasuredAstrometryFixture.CreateAtAsync(sequence).ConfigureAwait(false);
        var bytes = reference.Raw.Payload.ToArray();
        var raw = CreateManifestV2(deviceId, rig, bytes, sequence, capturedAtUtc: reference.Context.Frame.ExposureStartUtc);
        raw = raw with
        {
            Descriptor = raw.Descriptor with
            {
                Layout = reference.Raw.Layout!,
                Controls = raw.Descriptor.Controls with { RequestedExposure = TimeSpan.FromSeconds(1), EffectiveExposure = TimeSpan.FromSeconds(1) },
                Timing = new(reference.Context.Frame.ExposureStartUtc, reference.Context.Frame.ExposureStartUtc, reference.Context.Frame.ExposureEndUtc, reference.Context.Frame.ExposureEndUtc.AddMilliseconds(100), reference.Context.Frame.ExposureEndUtc.AddMilliseconds(200))
            }
        };
        var d = raw.Descriptor;
        var source = reference.Raw with
        {
            CaptureId = d.Capture.CaptureId,
            ArtifactId = d.Artifact.ArtifactId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(d),
            Variant = d.Artifact.Variant,
            RecipeIdentitySha256 = ProcessingIdentity.CreateRecipeIdentity(d.Artifact.Recipe).IdentitySha256,
            RecipeDescriptor = d.Artifact.Recipe,
            MediaType = d.Artifact.MediaType,
            Compatibility = reference.Raw.Compatibility with { LocationIdentitySha256 = d.Location!.IdentitySha256 }
        };
        var sceneValue = reference.Scene with { Source = new(d.Capture.CaptureId, d.Artifact.ArtifactId, source.DescriptorIdentitySha256) };
        sceneValue = sceneValue with { SceneIdentitySha256 = ProjectedSceneJson.ComputeIdentity(sceneValue) };
        var auxiliary = MeasuredStellarAssociationFixtures.SceneAuxiliary(sceneValue);
        var executor = new ProcessingRecipeExecutor();
        var scene = (await executor.ExecuteAsync(new(BuiltInProcessingRecipes.ProjectedScene, JsonSerializer.SerializeToElement(new { }),
            ProcessingInputSelector.Raw(source.Variant), [source], "scene", AuxiliaryInputs: [auxiliary])).ConfigureAwait(false)).Products.Single();
        var measured = (await executor.ExecuteAsync(MeasuredStellarAssociationFixtures.Request(source, sceneValue)).ConfigureAwait(false)).Products.Single();
        ProcessingArtifact Artifact(ProcessingProduct p) => new(ProcessingIdentity.CreateArtifactId(p.OutputIdentitySha256), p.Role,
            p.Variant, p.Recipe.IdentitySha256, p.MediaType, null, p.Payload, d.Artifact.CreatedUtc, p.TotalIntegration, p.Compatibility,
            SourceArtifactIds: p.SourceArtifactIds)
        { CaptureId = d.Capture.CaptureId, ProductKind = p.Kind, SchemaVersion = p.SchemaVersion, ContentIdentitySha256 = p.ContentIdentitySha256, RecipeDescriptor = p.Recipe.Descriptor };
        var contextValue = RetainedAstrometryProducts.CreateContext(source, Artifact(measured), Artifact(scene),
            new(reference.Context.CatalogMetadata, reference.Context.CatalogStars, reference.Context.CatalogPackage, true, 7),
            AstrometricClockFacts.NotSupplied, prior);
        var context = RetainedAstrometryProducts.CreateContextProduct(contextValue, source, Artifact(measured), Artifact(scene), "astrometry-context");
        var outcome = await executor.ExecuteAsync(RetainedAstrometryProducts.Request(source, Artifact(measured), Artifact(scene),
            Artifact(context), MeasuredAstrometryFixture.Options, "astrometry")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var solution = outcome.Products.Single();
        _ = ArtifactIngestService.ValidateAstrometryPayload(AstrometryUpload(raw, solution).Descriptor, solution.Payload);
        _ = ArtifactIngestService.ValidateAstrometryPayload(AstrometryUpload(raw, context).Descriptor, context.Payload);
        return new(raw, bytes, scene, measured, context, solution);
    }

    private static StructuredProcessingProductManifestV1 AstrometryUpload(ArtifactManifestV2 raw, ProcessingProduct p)
        => new(StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new(raw.Descriptor, new(ProcessingIdentity.CreateArtifactId(p.OutputIdentitySha256), p.Role, "native", p.Variant,
                raw.Descriptor.Artifact.CreatedUtc, p.SourceArtifactIds, p.Recipe.Descriptor, p.MediaType, p.ChecksumSha256),
                p.OutputIdentitySha256, p.Algorithms, p.Compatibility, p.TotalIntegration.Ticks, p.Payload.Length, p.Kind,
                p.SchemaVersion!, p.ContentIdentitySha256!), "derived/astrometry.json", "native");
}
