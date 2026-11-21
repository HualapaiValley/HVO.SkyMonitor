using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task CenteredWorkerPublishesActualRecipeEvidenceAfterFileCommitInterruption(CameraPixelFormat format)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-retained");
        try
        {
            var token = CancellationToken.None;
            var start = DateTimeOffset.UtcNow.AddMinutes(-1);
            var fault = new CenteredPublicationFault();
            var clock = new CenteredClock();
            RawCaptureReceipt receipt;
            using (var previous = CenteredRuntimeProvider(root, clock))
                receipt = await CaptureCenteredFixtureAsync(previous, root, start, format, token).ConfigureAwait(false);

            using var provider = ProcessingGraphOperationsTests.CreateProvider(root, timeProvider: clock,
                configureServices: services => services.AddSingleton<ICaptureProcessingFaultInjector>(fault));
            var reference = receipt.Manifest.Descriptor;
            var key = new ExposureKey(new(reference.Capture.AgentId, "virtual-centered"), Guid.NewGuid(), reference.Capture.CaptureId);
            var rawData = await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath, token).ConfigureAwait(false);
            Assert.AreEqual(reference.Artifact.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(rawData));
            var raw = BuildCenteredFixtureRaw(reference, rawData);
            var normalized = await new ProcessingRecipeExecutor().ExecuteAsync(new(BuiltInProcessingRecipes.LinearNormalization,
                default, ProcessingInputSelector.Raw(raw.Variant), [raw], "centered-test-linear"), token).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, normalized.Status);
            var linearProduct = Assert.ContainsSingle(normalized.Products);
            var linearId = ProcessingIdentity.CreateArtifactId(linearProduct.OutputIdentitySha256);
            var linear = raw with
            {
                ArtifactId = linearId,
                Role = FrameArtifactRole.Calibrated,
                Variant = linearProduct.Variant,
                RecipeIdentitySha256 = linearProduct.Recipe.IdentitySha256,
                MediaType = linearProduct.MediaType,
                Layout = linearProduct.Layout,
                Payload = linearProduct.Payload,
                SourceArtifactIds = linearProduct.SourceArtifactIds,
                RecipeDescriptor = linearProduct.Recipe.Descriptor,
                DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(
                    DerivativeDescriptorFactory.Create(reference, linearId, "centered-fixture-calibration", linearProduct))
            };
            var mask = CenteredStackValidityMask.Create(raw, linear,
                Enumerable.Repeat((byte)1, reference.Layout.Width * reference.Layout.Height).ToArray());
            var geometry = new ExposureGeometryV1(reference.Layout, new(90, 180, 0), reference.Profiles.Rig.Sha256, "fixture", null);
            var exposure = new ExposureInputV1(key, reference, geometry);
            var provenance = new LinearInputProvenanceV1(LinearExposureDisposition.QualifiedNormalizationOnly,
                raw.ArtifactId, raw.DescriptorIdentitySha256!, reference.Artifact.ChecksumSha256,
                linearId, linear.DescriptorIdentitySha256!, linearProduct.ChecksumSha256, linearProduct.OutputIdentitySha256,
                linearProduct.Recipe, [], new(raw.ArtifactId, StoredWhite(reference.Layout)), new(linearId, StoredWhite(linear.Layout!)));
            var productRef = new ExposureProductRef(key, new(Guid.NewGuid(), "linear", "fixture", linear.Variant),
                linearId, linearProduct.OutputIdentitySha256, linearProduct.ChecksumSha256, null);
            var settlement = new LinearExposureLookup(exposure, provenance.Disposition, 1, productRef, provenance, null);
            var measurement = CenteredStackRetainedInputReader.BindCommittedMeasurement(settlement, linear.Layout!, new(null, null));
            var qualified = new CenteredStackQualifiedExposure(new(exposure, linear, provenance), measurement);
            var pin = CenteredStackExposurePin.Create(qualified, raw, mask, null);
            var source = new CenteredFileFixtureSource(qualified, raw);
            var recipe = new CenteredRegisteredStackRecipe(source);
            var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipe.Definition, recipe.NormalizeOptions(default));
            var referenceStart = reference.Timing.ExposureStartedUtc;
            var deadline = referenceStart.AddSeconds(10);
            var schedule = new CenteredWindowSchedule(key.Camera, key.RunId, "test-generation", referenceStart,
                TimeSpan.FromMilliseconds(100), deadline, recipeIdentity.IdentitySha256, deadline.AddSeconds(30));
            var times = Enumerable.Range(-2, 5).Select(offset => new CenteredWindowSlotTime(
                referenceStart.AddSeconds(offset * 2), "fixed", ExpectedIntegration: reference.Controls.EffectiveExposure)).ToImmutableArray();
            var checkpoint = new CenteredWindowCheckpoint(schedule, times, ReferenceCapture: key);
            var admitted = reference.Timing.ExposureEndedUtc.AddMilliseconds(100);
            var observation = new CenteredWindowSlotObservation(times[2],
                new(key, reference.Capture.CaptureSequence, reference.Timing, raw.ArtifactId,
                    raw.DescriptorIdentitySha256!, reference.Artifact.ChecksumSha256, admitted),
                new(admitted, LinearExposureDisposition.QualifiedNormalizationOnly,
                    productRef, null, null, RegistrationPin: pin));
            var journal = new CenteredFileFixtureJournal(new(schedule.GenerationId, key, [], [observation]));
            clock.UtcNow = deadline;
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            await store.InitializeAsync(token).ConfigureAwait(false);
            var recoveredRaw = await store.ReadReplaySourceAsync(key.CaptureId, raw.ArtifactId, token).ConfigureAwait(false);
            Assert.AreEqual(reference.Artifact.ChecksumSha256, recoveredRaw.PayloadSha256);
            Assert.AreEqual(raw.DescriptorIdentitySha256, recoveredRaw.DescriptorSha256);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                Assert.IsTrue(await SqliteCaptureProcessingStore.TrackCenteredWindowAsync(transaction, checkpoint, 2, token).ConfigureAwait(false));
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }

            using (var closer = new CenteredWindowCoordinator(store, journal, clock, NullLogger<CenteredWindowCoordinator>.Instance))
                await closer.CloseDueAsync(token).ConfigureAwait(false);
            using (var worker = new CenteredWindowRegistrationWorker(store, provider.GetRequiredService<CaptureProcessingPersistence>(),
                journal, source, clock, NullLogger<CenteredWindowRegistrationWorker>.Instance))
                await worker.RegisterPendingAsync(token).ConfigureAwait(false);
            var initial = await store.ReadCenteredWindowByReferenceAsync(key.CaptureId, token).ConfigureAwait(false);
            Assert.IsTrue(fault.Interrupted, initial?.FailureReason);
            Assert.IsFalse(initial!.IsTerminal);
            Assert.IsFalse(journal.Released);

            using var reopened = ProcessingGraphOperationsTests.CreateProvider(root, timeProvider: clock);
            var reopenedStore = reopened.GetRequiredService<SqliteCaptureProcessingStore>();
            var persistence = reopened.GetRequiredService<CaptureProcessingPersistence>();
            using (var restarted = new CenteredWindowRegistrationWorker(reopenedStore, persistence, journal, source, clock,
                NullLogger<CenteredWindowRegistrationWorker>.Instance))
                await restarted.RegisterPendingAsync(token).ConfigureAwait(false);

            var receiptAfterRestart = await reopenedStore.ReadCenteredWindowByReferenceAsync(key.CaptureId, token).ConfigureAwait(false);
            Assert.IsNotNull(receiptAfterRestart?.Publication);
            var published = receiptAfterRestart.Publication;
            Assert.AreEqual(CenteredStackDisposition.SingleExposure, published.StackDisposition);
            Assert.AreEqual(key.CaptureId, Assert.ContainsSingle(published.ContributingCaptureIds));
            Assert.AreEqual(published.EvidenceArtifactId, Assert.ContainsSingle(published.RegistrationArtifactIds));
            Assert.IsTrue(journal.Released);
            var result = await CenteredWindowRegistrationResult.ExecuteAsync(receiptAfterRestart.Closure, source, token).ConfigureAwait(false);
            var product = Assert.ContainsSingle(result.Outcome.Products);
            var retainedOutput = await reopenedStore.ReadOutputByArtifactIdAsync(published.EvidenceArtifactId, token).ConfigureAwait(false);
            Assert.IsNotNull(retainedOutput);
            var evidence = await persistence.ReadCenteredRetainedProductAsync(key,
                CenteredStackArtifactPin.FromHeader(
                    CenteredStackArtifactHeader.FromArtifact(CenteredWindowArtifactHeader.FromOutput(retainedOutput)), product.ChecksumSha256),
                token).ConfigureAwait(false);
            Assert.IsNotNull(evidence);
            Assert.AreEqual(product.ChecksumSha256, ProcessingIdentity.ComputePayloadSha256(evidence.Payload));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The fixture uses only the canonical historical processing schema.")]
    private static async Task CreatePriorCenteredFixtureSchemaAsync(string root, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = SqliteCaptureProcessingStore.LegacySchema9SqlForTests;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RawCaptureReceipt> CaptureCenteredFixtureAsync(ServiceProvider provider, string root,
        DateTimeOffset start, CameraPixelFormat format, CancellationToken token)
    {
        var file = CenteredRuntimeProfile(root, start, format) with { CenteredExposure = null };
        // Use legacy raw admission without activating a modern processing graph.
        using var ingress = new RawCaptureIngress(provider.GetRequiredService<IOptions<CameraAgentHostOptions>>(),
            provider.GetRequiredService<IStorageCapacityProvider>(), provider.GetRequiredService<RawIngressState>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<RawIngressTelemetry>(),
            NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector(),
            projectedSceneStaging: provider.GetRequiredService<IProjectedSceneStagingReconciler>());
        await ingress.InitializeAsync(token).ConfigureAwait(false);
        await CreatePriorCenteredFixtureSchemaAsync(root, token).ConfigureAwait(false);
        var module = new VirtualSkyCameraModule(ExposureTestTimeProvider.ForRendering(), CenteredRuntimeCatalog(start),
            new ProjectedSceneStore(), stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(file, token).ConfigureAwait(false);
        var request = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, new(TimeSpan.FromSeconds(1), 1, null, null));
        var captured = await module.CaptureAsync(request, token).ConfigureAwait(false);
        Assert.IsNotNull(captured.Frame);
        var receipt = await ingress.AcceptAsync(file, new(request, captured, start, request.TargetInterval, request.TargetInterval), token)
            .ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        return receipt;
    }

    private static ProcessingArtifact BuildCenteredFixtureRaw(ReconstructionDescriptor descriptor, byte[] data) =>
        new(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256, descriptor.Artifact.MediaType,
            descriptor.Layout, data, descriptor.Artifact.CreatedUtc, descriptor.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor), descriptor.Capture.CaptureSequence, [],
            descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc,
            new(descriptor.Controls.EffectiveGain, descriptor.Controls.EffectiveOffset, descriptor.Controls.EffectiveTemperatureC))
        {
            CaptureId = descriptor.Capture.CaptureId,
            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor),
            RecipeDescriptor = descriptor.Artifact.Recipe
        };

    private static double StoredWhite(FrameLayoutDescriptor layout)
    {
        var white = Math.Ceiling(layout.WhiteLevel!.Value);
        if (layout.LevelCodeSpace == FrameLevelCodeSpace.StoredContainer)
            return white;
        return layout.StoredCodeTransform switch
        {
            FrameStoredCodeTransform.LeftShiftedV1 => white * (1 << (layout.ContainerDepthBits - layout.SampleDepthBits)),
            FrameStoredCodeTransform.FullRangeScaledV1 =>
                Math.Round(white * ((1 << layout.ContainerDepthBits) - 1) / ((1 << layout.SampleDepthBits) - 1)),
            _ => white
        };
    }

    private sealed class CenteredClock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class CenteredPublicationFault : ICaptureProcessingFaultInjector
    {
        internal bool Interrupted { get; private set; }
        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point != CaptureProcessingFaultPoint.AfterOutputsPublishedBeforeNodeCommit ||
                nodeId != CenteredWindowCoordinator.RegistrationProducer)
                return;
            Interrupted = true;
            throw new IOException("Interrupt between centered artifact writes and SQLite commit.");
        }
    }

    private sealed class CenteredFileFixtureJournal(CenteredWindowJournalSnapshot snapshot) : ICenteredWindowJournal
    {
        internal bool Released { get; private set; }
        public ValueTask<CenteredWindowJournalSnapshot> ReadWindowEvidenceAsync(SqliteTransaction transaction,
            CenteredWindowCheckpoint checkpoint, DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => ValueTask.FromResult(snapshot);
        public ValueTask PinClosedInputsAsync(SqliteTransaction transaction, CenteredWindowClosure closure, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask ReleaseWindowInputsAsync(SqliteTransaction transaction, string windowIdentity, CancellationToken cancellationToken)
        {
            Released = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CenteredFileFixtureSource(CenteredStackQualifiedExposure qualified, ProcessingArtifact raw) : ICenteredWindowRegistrationSource
    {
        public ValueTask<CenteredStackQualifiedExposure?> ReadLinearAsync(ExposureKey key, ProcessingArtifact linear,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<CenteredStackQualifiedExposure?>(key == qualified.Linear.Exposure.Key &&
                linear.ArtifactId == qualified.Linear.Linear.ArtifactId ? qualified : null);

        public ValueTask<ExposureInputV1?> ReadExposureAsync(ExposureKey key, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ExposureInputV1?>(key == qualified.Linear.Exposure.Key ? qualified.Linear.Exposure : null);

        public ValueTask<ProcessingArtifact?> ReadArtifactAsync(ExposureKey exposure, CenteredStackArtifactPin pin,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                new[] { raw, qualified.Linear.Linear }.SingleOrDefault(artifact => artifact.ArtifactId == pin.ArtifactId));
    }
}
