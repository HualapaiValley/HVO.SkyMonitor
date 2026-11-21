using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Tests.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task ReferenceAstrometryEndpointPreservesLegacyReceiptsAndRefusesCenteredCreates()
    {
        const string actor = "owner";
        const string reason = "reference preset mode transition";
        const double referenceMagnitude = 3.25;
        const string issuedKey = "issued-preset";
        const string pendingKey = "pending-preset";
        const string centeredKey = "centered-preset";
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-reference-preset-mode");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        try
        {
            var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var original = CreateReferenceCatalog();
            var catalog = new Mock<IAstrometricCatalogSource>(MockBehavior.Strict);
            var holdNextRead = 0;
            catalog.Setup(source => source.ReadAsync(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(async (double magnitude, int count, CancellationToken cancellationToken) =>
                {
                    if (Interlocked.Exchange(ref holdNextRead, 0) != 0)
                    {
                        readEntered.SetResult();
                        await allowRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return await original.ReadAsync(magnitude, count, cancellationToken).ConfigureAwait(false);
                });
            using var provider = CreateProvider(root, configureServices: services => services.AddSingleton(catalog.Object));
            var ordinary = StoragePolicyRawIngressIntegrationTests.Profile(root);
            var camera = await LoadAstrometryProfileAsync("Fixtures/installer-controlled-night.json").ConfigureAwait(false);
            var legacy = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, ordinary with
            {
                Rig = camera.Rig with { ControlPolicy = ordinary.Rig.ControlPolicy },
                Pipeline = ordinary.Pipeline with
                {
                    Steps =
                    [
                        new("ProjectedScene", "scene", DependsOn: ["$raw"], Required: false,
                            Publication: new(CaptureProcessingPersistenceMode.DurableLocal)),
                        .. ordinary.Pipeline.Steps
                    ]
                }
            }).ConfigureAwait(false);
            Assert.AreEqual(CameraPixelFormat.Mono16, legacy.Rig.Readout?.PixelFormat);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var app = CameraAgentProcessingGraphOperationsEndpointsTests.CreateApp(
                operations, provider.GetRequiredService<ICameraAgentArtifactService>());
            await using var appLifetime = app.ConfigureAwait(false);

            var start = await operations.GetRegistryAsync(token).ConfigureAwait(false);
            var legacyBasis = start.ConfiguredBasicRevisionId;
            var initialCommands = CountRows(root, "processing_graph_commands");
            var created = await CreatePresetAsync(legacyBasis, issuedKey).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, created.Status, created.Body);
            Assert.AreEqual(created, await CreatePresetAsync(legacyBasis, issuedKey).ConfigureAwait(false));
            Assert.AreEqual(initialCommands + 1, CountRows(root, "processing_graph_commands"));
            var issued = await ReadReceiptAsync(legacyBasis, issuedKey).ConfigureAwait(false)
                ?? throw new AssertFailedException("Legacy creation did not record a SQLite receipt.");
            var id = issued.Revision.State.RevisionId;
            using var body = JsonDocument.Parse(created.Body);
            Assert.AreEqual(id, body.RootElement.GetProperty("revisionId").GetString());
            Assert.AreEqual(referenceMagnitude, body.RootElement.GetProperty("catalogSelection").GetProperty("maximumMagnitude").GetDouble());
            var pipeline = issued.Revision.Pipeline;
            var prediction = pipeline.Steps.Single(step => step.Type == "AstrometryPrediction");
            var measured = pipeline.Steps.Single(step => step.Type == "MeasuredStellarAssociations");
            CollectionAssert.AreEqual(new[] { prediction.Id }, measured.DependsOn!.ToArray());
            var solve = pipeline.Steps.Single(step => step.Type == "MeasuredAstrometry");
            CollectionAssert.AreEqual(new[] { prediction.Id, measured.Id }, solve.DependsOn!.ToArray());
            var route = new RouteValueDictionary { ["revisionId"] = id };
            var validated = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(app,
                "ValidateCameraAgentProcessingGraphRevision", HttpMethods.Post, new { }, route,
                "validate-preset", antiforgeryValidated: true, cancellationToken: token).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, validated.Status, validated.Body);
            var beforeActivate = await operations.GetRegistryAsync(token).ConfigureAwait(false);
            var activated = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(app,
                "ActivateCameraAgentProcessingGraphRevision", HttpMethods.Post, new { expectedVersion = beforeActivate.StateVersion },
                route, "activate-preset", antiforgeryValidated: true, cancellationToken: token).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, activated.Status, activated.Body);
            var active = await operations.GetRegistryAsync(token).ConfigureAwait(false);
            Assert.AreEqual(id, active.ActiveRevisionId);
            Assert.AreEqual(beforeActivate.StateVersion + 1, active.StateVersion);
            Assert.HasCount(start.Revisions.Count + 1, active.Revisions);

            Interlocked.Exchange(ref holdNextRead, 1);
            var pending = CreatePresetAsync(legacyBasis, pendingKey);
            try
            {
                await readEntered.Task.WaitAsync(token).ConfigureAwait(false);
                var schedule = provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>();
                var profile = LocalCaptureProfileDefinition.CreateV2(
                    legacy with { CenteredExposure = new("fixture-camera", CenteredExposureProfile.CurrentExecutionVersion) },
                    legacy.Schedule!);
                var scheduleState = await schedule.GetOperatorStateAsync(token).ConfigureAwait(false);
                var staged = await schedule.StageAsync(profile, "stage-centered", scheduleState.StateVersion, actor, reason, token)
                    .ConfigureAwait(false);
                await schedule.ActivateAsync(staged.PendingRevision!.RevisionId, "activate-centered", staged.Version,
                    actor, reason, token).ConfigureAwait(false);
                var centered = schedule.Snapshot!.Configuration;
                Assert.IsNotNull(centered.CenteredExposure);
                Assert.AreEqual(CameraPixelFormat.Mono16, centered.Rig.Readout?.PixelFormat);
                var commandCount = CountRows(root, "processing_graph_commands");

                // Only the active schedule has changed. The configured-basic graph has not been refreshed.
                allowRead.SetResult();
                AssertIncompatible(await pending.ConfigureAwait(false));
                Assert.IsNull(await ReadReceiptAsync(legacyBasis, pendingKey).ConfigureAwait(false));
                Assert.AreEqual(commandCount, CountRows(root, "processing_graph_commands"));
                Assert.HasCount(active.Revisions.Count,
                    (await store.ReadRegistryAsync(token).ConfigureAwait(false)).Revisions);

                var centeredRegistry = await operations.EnsureConfiguredBasicAsync(centered, token).ConfigureAwait(false);
                AssertIncompatible(await CreatePresetAsync(centeredRegistry.ConfiguredBasicRevisionId, centeredKey).ConfigureAwait(false));
                Assert.IsNull(await ReadReceiptAsync(centeredRegistry.ConfiguredBasicRevisionId, centeredKey).ConfigureAwait(false));
                Assert.AreEqual(created, await CreatePresetAsync(legacyBasis, issuedKey).ConfigureAwait(false));
                var retained = await ReadReceiptAsync(legacyBasis, issuedKey).ConfigureAwait(false)
                    ?? throw new AssertFailedException("The legacy receipt disappeared after the profile changed.");
                Assert.AreEqual(id, retained.Revision.State.RevisionId);
                Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(pipeline),
                    CaptureContractJson.ComputeCanonicalJsonSha256(retained.Revision.Pipeline));
                Assert.AreEqual(commandCount, CountRows(root, "processing_graph_commands"));
                var after = await operations.GetRegistryAsync(token).ConfigureAwait(false);
                Assert.AreEqual(centeredRegistry.StateVersion, after.StateVersion);
                Assert.AreEqual(centeredRegistry.ActiveRevisionId, after.ActiveRevisionId);
                Assert.HasCount(centeredRegistry.Revisions.Count, after.Revisions);
            }
            finally
            {
                allowRead.TrySetResult();
                await pending.ConfigureAwait(false);
            }

            Task<(int Status, string Body)> CreatePresetAsync(string basisRevisionId, string key)
                => CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(app,
                    "CreateCameraAgentReferenceAstrometryRevision", HttpMethods.Post,
                    new { basisRevisionId, revision = key, referenceMagnitude, reason },
                    idempotencyKey: key, antiforgeryValidated: true, cancellationToken: token);

            ValueTask<(ProcessingGraphRevisionSnapshot Revision, bool Legacy)?> ReadReceiptAsync(string basisRevisionId, string key)
                => store.ReadReferenceAstrometryCreationAsync(key, CaptureContractJson.ComputeCanonicalJsonSha256(
                    new { basisRevisionId, revision = key, referenceMagnitude, actor, reason }), actor, reason, token);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static void AssertIncompatible((int Status, string Body) response)
        {
            Assert.AreEqual(StatusCodes.Status409Conflict, response.Status, response.Body);
            using var problem = JsonDocument.Parse(response.Body);
            Assert.AreEqual(ReferenceAstrometryPresetModeConflictException.SafeMessage, problem.RootElement.GetProperty("title").GetString());
        }
    }
}
