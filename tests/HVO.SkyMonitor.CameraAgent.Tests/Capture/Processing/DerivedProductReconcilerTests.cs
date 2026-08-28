using System.Diagnostics;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
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
            await CreateReconciliationService(successRoot).RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
            await CreateReconciliationService(failurePath).RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
            await CreateReconciliationService(rawFailureRoot, new FailingIngress())
                .RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

            var activities = stopped.Where(activity => activity.OperationName == "processing-artifact.reconcile").ToArray();
            Assert.HasCount(3, activities);
            Assert.AreEqual(ActivityStatusCode.Ok, activities[0].Status);
            Assert.AreEqual(ActivityStatusCode.Error, activities[1].Status);
            Assert.AreEqual("reconciliation-failed", activities[1].StatusDescription);
            Assert.AreEqual("DirectoryNotFoundException", activities[1].GetTagItem("error.type"));
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
            using var store = CreateStore(root);
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
            using var store = CreateStore(root);
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
            using var store = CreateStore(root);

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
            using var store = CreateStore(root);
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
            using var store = CreateStore(root);
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

    [TestMethod]
    [TestCategory("Unit")]
    public async Task LegacyCursorIgnoresModernPrefixBeforePageLimit()
    {
        var root = CreateRoot();
        try
        {
            var derived = Path.Combine(root, "derived");
            Directory.CreateDirectory(derived);
            for (var index = 0; index < 20; index++)
                await File.WriteAllTextAsync(Path.Combine(derived, $"a-{index:D2}.manifest.json"), "{}").ConfigureAwait(false);
            var payload = Path.Combine(derived, "z-legacy.bin");
            var sidecar = Path.Combine(derived, "z-legacy.json");
            await File.WriteAllTextAsync(payload, "payload").ConfigureAwait(false);
            await File.WriteAllTextAsync(sidecar, "{}").ConfigureAwait(false);
            using var store = CreateStore(root);
            var reconciler = new DerivedProductReconciler(root, store,
                new DerivedProductLifecycleOptions { ReconciliationBatchSize = 16 });

            await reconciler.RunAsync(CancellationToken.None).ConfigureAwait(false);

            var orphans = await store.ReadOrphanLifecyclePageAsync(null, 64, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(orphans.Items.Any(item => item.OperationId.StartsWith("orphan:legacy-malformed:", StringComparison.Ordinal)));
            Assert.IsTrue(File.Exists(payload));
            Assert.IsTrue(File.Exists(sidecar));
            Assert.IsNull(await store.ReadFileCursorAsync("legacy", CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SqliteCaptureProcessingStore CreateStore(string root) => new(Options.Create(
        new CameraAgentHostOptions { RawIngressRoot = root }));

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-derived-reconciliation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static DerivedProductReconciliationService CreateReconciliationService(
        string root,
        IRawCaptureIngress? rawIngress = null)
    {
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
            rawIngress);
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
