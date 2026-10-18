using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[DoNotParallelize]
public sealed class DerivedProductReconcilerTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task RunOnceAsync_EmitsSafeSuccessAndFailureActivities()
    {
        var successRoot = CreateRoot();
        var rawFailureRoot = CreateRoot();
        var failurePath = Path.Combine(CreateRoot(), "not-a-directory");
        await File.WriteAllTextAsync(failurePath, "failure").ConfigureAwait(false);
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CaptureProcessingTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            await (await CreateReconciliationServiceAsync(successRoot).ConfigureAwait(false))
                .RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
            await (await CreateReconciliationServiceAsync(failurePath).ConfigureAwait(false))
                .RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
            await (await CreateReconciliationServiceAsync(rawFailureRoot, new FailingIngress()).ConfigureAwait(false))
                .RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

            var activities = stopped.Where(activity => activity.OperationName == "processing-artifact.reconcile").ToArray();
            Assert.HasCount(3, activities);
            Assert.AreEqual(ActivityStatusCode.Ok, activities[0].Status);
            Assert.AreEqual(ActivityStatusCode.Error, activities[1].Status);
            Assert.AreEqual("reconciliation-failed", activities[1].StatusDescription);
            Assert.AreEqual("InvalidOperationException", activities[1].GetTagItem("error.type"));
            Assert.AreEqual(ActivityStatusCode.Error, activities[2].Status);
            Assert.AreEqual("InvalidDataException", activities[2].GetTagItem("error.type"));
            Assert.IsFalse(activities.SelectMany(static activity => activity.TagObjects).Any(static tag =>
                tag.Key is "path" or "capture_id" or "artifact_id" or "exception" or "payload"));
        }
        finally
        {
            Directory.Delete(successRoot, recursive: true);
            Directory.Delete(rawFailureRoot, recursive: true);
            Directory.Delete(Path.GetDirectoryName(failurePath)!, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RunAsync_CleansTemporaryEvidenceAndQuarantinesPayloadOnlyIdempotently()
    {
        var root = CreateRoot();
        try
        {
            var derived = Path.Combine(root, "derived", "2026", "08", "25", "Metadata");
            Directory.CreateDirectory(derived);
            var temporary = Path.Combine(derived, "publication.tmp");
            var payload = Path.Combine(derived, "orphan.json");
            await File.WriteAllTextAsync(temporary, "temporary").ConfigureAwait(false);
            await File.WriteAllTextAsync(payload, "{}").ConfigureAwait(false);
            using var store = await CreateStoreAsync(root).ConfigureAwait(false);
            var reconciler = new DerivedProductReconciler(root, store);

            var first = await reconciler.RunAsync(CancellationToken.None).ConfigureAwait(false);
            var second = await reconciler.RunAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, first.Cleaned);
            Assert.AreEqual(1, first.Quarantined);
            Assert.AreEqual(0, second.Quarantined);
            Assert.IsFalse(File.Exists(temporary));
            Assert.IsFalse(File.Exists(payload));
            Assert.AreEqual(1, Directory.EnumerateFiles(
                Path.Combine(root, "processing-quarantine"), "orphan.json", SearchOption.AllDirectories).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RunAsync_CurrentManifestPublishedBeforeNodeCommitSurvivesRestartForDeterministicRetry()
    {
        var root = CreateRoot();
        try
        {
            byte[] payload = [1, 2, 3, 4];
            var template = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono8, payload.Length, 1, payload.Length, payload);
            var relativePayloadPath = $"derived/2026/08/25/Calibrated/{template.Descriptor.Artifact.ArtifactId:N}.bin";
            var manifest = template with
            {
                RelativeArtifactPath = relativePayloadPath,
                ProducerStepId = "normalize"
            };
            var payloadPath = Path.Combine(root, relativePayloadPath.Replace('/', Path.DirectorySeparatorChar));
            var sidecarPath = Path.ChangeExtension(payloadPath, ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
            await File.WriteAllBytesAsync(payloadPath, payload).ConfigureAwait(false);
            await File.WriteAllBytesAsync(sidecarPath, CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);

            using (var store = await CreateStoreAsync(root).ConfigureAwait(false))
            {
                var first = await new DerivedProductReconciler(root, store)
                    .RunAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(1, first.Recoverable);
                Assert.AreEqual(0, first.Quarantined);
            }

            using (var restartedStore = await CreateStoreAsync(root).ConfigureAwait(false))
            {
                var restarted = await new DerivedProductReconciler(root, restartedStore)
                    .RunAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(1, restarted.Recoverable);
                Assert.AreEqual(0, restarted.Quarantined);
            }
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(payloadPath).ConfigureAwait(false));
            Assert.IsTrue(CaptureContractJson.ParseManifest(
                await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false)).IsValid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RunAsync_AfterASnapshotRollback_KeepsAProductWrittenSinceTheSnapshotThroughTheWindowThenQuarantinesIt()
    {
        // An installer snapshot rollback (#1314) rewinds the processing store while the derived files written since
        // stay on disk, unowned. They are kept through the orphan recovery window, in case reprocessing claims them
        // again, and then moved to processing quarantine rather than deleted.
        var root = CreateRoot();
        try
        {
            byte[] payload = [7, 7, 7, 7];
            var template = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono8, payload.Length, 1, payload.Length, payload);
            var relativePayloadPath = $"derived/2026/08/25/Calibrated/{template.Descriptor.Artifact.ArtifactId:N}.bin";
            var manifest = template with { RelativeArtifactPath = relativePayloadPath, ProducerStepId = "normalize" };
            var payloadPath = Path.Combine(root, relativePayloadPath.Replace('/', Path.DirectorySeparatorChar));
            var sidecarPath = Path.ChangeExtension(payloadPath, ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
            await File.WriteAllBytesAsync(payloadPath, payload).ConfigureAwait(false);
            await File.WriteAllBytesAsync(sidecarPath, CaptureContractJson.Serialize(manifest)).ConfigureAwait(false);
            var clock = new OffsetTimeProvider();
            var options = new DerivedProductLifecycleOptions();

            using (var store = await CreateStoreAsync(root).ConfigureAwait(false))
            {
                var first = await new DerivedProductReconciler(root, store, options, clock)
                    .RunAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(1, first.Recoverable);
                Assert.AreEqual(0, first.Quarantined);
                Assert.IsTrue(File.Exists(payloadPath));

                clock.Offset = TimeSpan.FromMinutes(options.OrphanRecoveryWindowMinutes + 1);
                var expired = await new DerivedProductReconciler(root, store, options, clock)
                    .RunAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(1, expired.Quarantined);
            }

            Assert.IsFalse(File.Exists(payloadPath));
            Assert.IsFalse(File.Exists(sidecarPath));
            var quarantined = Directory.EnumerateFiles(Path.Combine(root, "processing-quarantine"), "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName).ToArray();
            CollectionAssert.Contains(quarantined, Path.GetFileName(payloadPath), "The unowned product was deleted rather than quarantined.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class OffsetTimeProvider : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RunAsync_ResumesPlannedQuarantineAfterFilesMoved()
    {
        var root = CreateRoot();
        try
        {
            var derived = Path.Combine(root, "derived");
            var destination = Path.Combine(root, "processing-quarantine", "resume");
            Directory.CreateDirectory(derived);
            Directory.CreateDirectory(destination);
            var source = Path.Combine(derived, "payload.json");
            var moved = Path.Combine(destination, "payload.json");
            await File.WriteAllTextAsync(source, "{}").ConfigureAwait(false);
            using var store = await CreateStoreAsync(root).ConfigureAwait(false);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operation = new ProcessingLifecycleOperation(
                "quarantine:resume", "quarantine", null, "derived/payload.json", null,
                "processing-quarantine/resume", "payload-without-sidecar", 2);
            await store.PlanLifecycleOperationAsync(operation, CancellationToken.None).ConfigureAwait(false);
            File.Move(source, moved);

            var result = await new DerivedProductReconciler(root, store)
                .RunAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, result.Quarantined);
            Assert.IsTrue(File.Exists(moved));
            Assert.AreEqual(0, (await store.ReadActionableLifecyclePageAsync(
                null, 16, CancellationToken.None).ConfigureAwait(false)).Items.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RunAsync_ManifestPathMismatchQuarantinesOnlySidecarAndNeverTouchesRawReference()
    {
        var root = CreateRoot();
        try
        {
            var derived = Path.Combine(root, "derived");
            var raw = Path.Combine(root, "frames", "2026", "08", "25", "Raw", "raw.bin");
            Directory.CreateDirectory(derived);
            Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
            await File.WriteAllTextAsync(raw, "raw-evidence").ConfigureAwait(false);
            var sidecar = Path.Combine(derived, "malicious.manifest.json");
            await File.WriteAllTextAsync(sidecar, $$"""{"schemaVersion":"durable-processing-product-v1","relativeArtifactPath":"{{Path.GetRelativePath(root, raw).Replace('\\', '/')}}"}""").ConfigureAwait(false);
            using var store = await CreateStoreAsync(root).ConfigureAwait(false);

            await new DerivedProductReconciler(root, store).RunAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(File.Exists(raw));
            Assert.AreEqual("raw-evidence", await File.ReadAllTextAsync(raw).ConfigureAwait(false));
            Assert.IsFalse(File.Exists(sidecar));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RunAsync_MoreThan4096PayloadsProgressAcrossBoundedPasses()
    {
        var root = CreateRoot();
        try
        {
            var derived = Path.Combine(root, "derived");
            Directory.CreateDirectory(derived);
            for (var index = 0; index < 4100; index++)
                await File.WriteAllTextAsync(Path.Combine(derived, $"orphan-{index:D4}.json"), "{}").ConfigureAwait(false);
            using var store = await CreateStoreAsync(root).ConfigureAwait(false);
            var options = new DerivedProductLifecycleOptions { ReconciliationBatchSize = 4096 };
            var reconciler = new DerivedProductReconciler(root, store, options);

            var first = await reconciler.RunAsync(CancellationToken.None).ConfigureAwait(false);
            var second = await reconciler.RunAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(4096, first.Quarantined);
            Assert.AreEqual(4, second.Quarantined);
            Assert.IsFalse(Directory.EnumerateFiles(derived).Any());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task LifecyclePagesSeparateMoreThan4096OrphansFromActionableWork()
    {
        var root = CreateRoot();
        try
        {
            using var store = await CreateStoreAsync(root).ConfigureAwait(false);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
                for (var index = 0; index < 4100; index++)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = """
                        INSERT INTO processing_lifecycle_operations(
                            operation_id, kind, output_identity_sha256, source_relative_path,
                            companion_relative_path, destination_relative_path, reason,
                            observed_bytes, planned_unix_ms, phase)
                        VALUES($id, $kind, NULL, NULL, NULL, '', 'test', 0, 1, 'planned');
                        """;
                    command.Parameters.AddWithValue("$id", $"{(index == 4099 ? "quarantine" : "orphan")}:{index:D5}");
                    command.Parameters.AddWithValue("$kind", index == 4099 ? "delete" : "orphan");
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                await transaction.CommitAsync().ConfigureAwait(false);
            }

            var actionable = await store.ReadActionableLifecyclePageAsync(null, 16, CancellationToken.None).ConfigureAwait(false);
            var firstOrphans = await store.ReadOrphanLifecyclePageAsync(null, 4096, CancellationToken.None).ConfigureAwait(false);
            var remainingOrphans = await store.ReadOrphanLifecyclePageAsync(
                firstOrphans.NextOperationId, 4096, CancellationToken.None).ConfigureAwait(false);

            Assert.HasCount(1, actionable.Items);
            Assert.HasCount(4096, firstOrphans.Items);
            Assert.HasCount(3, remainingOrphans.Items);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<SqliteCaptureProcessingStore> CreateStoreAsync(string root)
    {
        var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
        await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        return new(Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RunOnceAsync_DurablyPausedAdmission_CleansNothingUntilCaptureResumes()
    {
        // An upgrade candidate starts on the journal the installer paused; reconciliation must not remove derived
        // files the previous image may still need until the installer resumes capture.
        using var hold = new HVO.SkyMonitor.CameraAgent.Tests.Capture.MaintenanceHoldRoot();
        using var admission = await hold.AdmissionAsync(paused: true).ConfigureAwait(false);
        var temporary = Path.Combine(hold.Path, "derived", "2026-10-16", "partial.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        await File.WriteAllTextAsync(temporary, "in flight").ConfigureAwait(false);
        var service = await CreateReconciliationServiceAsync(hold.Path, admission: admission).ConfigureAwait(false);

        await service.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(temporary));

        await admission.ResumeAsync("derived-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        await service.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(temporary));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task RunOnceAsync_PassBlockedOnItsLockHoldsThePauseUntilItHasFinished()
    {
        // The pass entered while capture was running and is waiting for the storage lifecycle lock when the installer
        // pauses. The pause must wait for it, so it cannot delete after the pause is acknowledged.
        using var hold = new HVO.SkyMonitor.CameraAgent.Tests.Capture.MaintenanceHoldRoot();
        using var admission = await hold.AdmissionAsync(paused: false).ConfigureAwait(false);
        var service = await CreateReconciliationServiceAsync(hold.Path, admission: admission).ConfigureAwait(false);
        var gate = StorageLifecycleLock.ForRoot(hold.Path);
        await gate.WaitAsync().ConfigureAwait(false);
        Task pass;
        Task<CaptureControlCommandResult> pause;
        try
        {
            pass = service.RunOnceAsync(CancellationToken.None).AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            Assert.IsFalse(pass.IsCompleted);
            pause = admission.PauseAsync("derived-barrier", null, "installer-lifecycle:test", null, CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
            Assert.IsFalse(pause.IsCompleted, "The pause was acknowledged while an admitted derived pass waited to delete.");
        }
        finally
        {
            gate.Release();
        }
        await pass.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Paused, (await pause.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)).State);
        var temporary = Path.Combine(hold.Path, "derived", "2026-10-16", "after-pause.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        await File.WriteAllTextAsync(temporary, "written after the pause").ConfigureAwait(false);
        await service.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(temporary));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-derived-reconciliation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<DerivedProductReconciliationService> CreateReconciliationServiceAsync(
        string root,
        IRawCaptureIngress? rawIngress = null,
        HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureAdmissionCoordinator? admission = null)
    {
        if (Directory.Exists(root))
        {
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = root
        }).Build());
        var provider = services.BuildServiceProvider();
        return new DerivedProductReconciliationService(
            options,
            provider.GetRequiredService<SqliteCaptureProcessingStore>(),
            provider.GetRequiredService<CaptureProcessingTelemetry>(),
            provider.GetRequiredService<CaptureProcessingState>(),
            provider.GetRequiredService<CaptureDistributionService>(),
            NullLogger<DerivedProductReconciliationService>.Instance,
            rawIngress,
            admission);
    }

    private sealed class FailingIngress : IRawCaptureIngress
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken)
            => ValueTask.FromException(new InvalidDataException("invalid raw evidence"));

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            HVO.SkyMonitor.AgentCore.CameraModuleConfig configuration,
            HVO.SkyMonitor.AgentCore.CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
