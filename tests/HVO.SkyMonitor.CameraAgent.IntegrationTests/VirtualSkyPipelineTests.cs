using System.Net;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class VirtualSkyPipelineTests
{
    private static readonly string[] ExpectedProcessingSteps =
        ["Calibration", "RollingCombination", "CalibratedPreview", "Preview", "Annotation", "LocalStorage"];
    private static readonly FrameArtifactRole[] ExpectedArtifactRoles =
        [FrameArtifactRole.Raw, FrameArtifactRole.Calibrated, FrameArtifactRole.Combined, FrameArtifactRole.Preview, FrameArtifactRole.AnnotatedPreview];
    private static readonly string[] ExpectedPreviewVariants = ["calibrated-display", "default"];
    private static CameraAgentIntegrationFixture Fixture => AssemblyHooks.Fixture;

    [TestMethod]
    public async Task ConfiguredPipelinePublishesPersistsReportsAndQueuesVirtualFrame()
    {
        using var scope = Fixture.CreateCameraAgentScope();
        var services = scope.ServiceProvider;
        var telemetry = services.GetRequiredService<ICaptureTelemetryProvider>();
        var latest = services.GetRequiredService<ILatestFrameAccessor>();

        await WaitUntilAsync(() => telemetry.Latest is { FrameStored: true } &&
            latest.TryGetSnapshot(FrameArtifactRole.Raw, out _) &&
            latest.TryGetSnapshot(FrameArtifactRole.Combined, out _) &&
            latest.TryGetSnapshot(out _), TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        var sample = telemetry.Latest!;
        Assert.IsTrue(sample.FrameStored);
        CollectionAssert.AreEqual(
            ExpectedProcessingSteps,
            sample.ProcessingSteps.Select(step => step.Name).ToArray());
        Assert.IsTrue(
            sample.ProcessingSteps.All(step => step.Succeeded),
            string.Join("; ", sample.ProcessingSteps
                .Where(step => !step.Succeeded)
                .Select(step => $"{step.Name}: {step.ErrorMessage}")));

        Assert.IsTrue(latest.TryGetSnapshot(FrameArtifactRole.Raw, out var raw));
        Assert.AreEqual(CameraPixelFormat.Mono16, raw.PixelFormat);
        Assert.AreEqual("VirtualSky", raw.Metadata!.SourceId);
        Assert.IsNotNull(raw.Metadata.Scene);

        Assert.IsTrue(latest.TryGetSnapshot(FrameArtifactRole.Combined, out var combined));
        Assert.AreEqual(CameraPixelFormat.Mono16, combined.PixelFormat);
        Assert.IsTrue(latest.TryGetSnapshot(out var preview));
        Assert.AreEqual(CameraPixelFormat.Mono8, preview.PixelFormat);
        Assert.AreEqual("AnnotatedPreview", preview.Metadata!.SourceId);
        Assert.AreEqual("integration-annotation-v2", preview.RecipeVersion);
        Assert.AreEqual("integration-annotation-v2", preview.Metadata.Extra!["annotationRecipeVersion"]);

        var stored = services.GetRequiredService<IFrameStorageService>().List(
            Fixture.StorageRoot, DateOnly.FromDateTime(raw.TimestampUtc.UtcDateTime), null, 1000);
        CollectionAssert.IsSubsetOf(
            ExpectedArtifactRoles, stored.Select(item => item.Role).Distinct().ToArray());
        Assert.IsTrue(stored.All(item => File.Exists(item.AbsolutePath)));
        var previewVariants = stored
            .Where(static item => item.Role == FrameArtifactRole.Preview)
            .Select(item => CaptureContractJson.ParseManifest(File.ReadAllBytes(Path.ChangeExtension(item.AbsolutePath, ".json"))))
            .Where(static parsed => parsed.IsValid)
            .Select(static parsed => parsed.Document!.Manifest!.Descriptor.Artifact.Variant)
            .ToHashSet(StringComparer.Ordinal);
        CollectionAssert.IsSubsetOf(ExpectedPreviewVariants, previewVariants.ToArray());

        var pending = services.GetRequiredService<IArtifactOutbox>().List(Fixture.StorageRoot, 1000);
        Assert.IsNotEmpty(pending);
        Assert.IsTrue(pending.All(item => item.Role == FrameArtifactRole.Raw));
        Assert.IsTrue(pending.All(item => item.AgentId == "cameraagent-integration-test"));
        Assert.IsTrue(pending.All(item => File.Exists(Path.Combine(Fixture.StorageRoot, item.RelativeArtifactPath))));
        Assert.IsTrue(pending.Any(item => item.Scene is not null));
        Assert.IsTrue(pending.Where(item => item.Role == FrameArtifactRole.Raw).All(item => item.FrameId != item.ArtifactId));
        using (var outbox = new SqliteConnection(
            $"Data Source={Path.Combine(Fixture.StorageRoot, "outbox", "artifact-outbox.db")}"))
        {
            await outbox.OpenAsync().ConfigureAwait(false);
            using var outboxCommand = outbox.CreateCommand();
            outboxCommand.CommandText = "SELECT COUNT(*) FROM artifact_outbox_records WHERE manifest_kind = 'v2' AND status = 'pending';";
            Assert.IsGreaterThanOrEqualTo(pending.Count, Convert.ToInt32(
                await outboxCommand.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture));
            outboxCommand.CommandText = "SELECT COUNT(*) FROM artifact_outbox_records WHERE status = 'pending' AND manifest_kind != 'v2';";
            Assert.AreEqual(0, Convert.ToInt32(
                await outboxCommand.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture));
        }

        await WaitUntilAsync(HasNoUnfinishedLaneWork, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        using var journal = new SqliteConnection($"Data Source={Path.Combine(Fixture.StorageRoot, "journal", "raw-ingress.db")}");
        await journal.OpenAsync().ConfigureAwait(false);
        using var countCommand = journal.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM raw_captures WHERE state = 'committed';";
        Assert.IsGreaterThan(0L, Convert.ToInt64(
            await countCommand.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        countCommand.CommandText = "SELECT COUNT(*) FROM capture_lane_work WHERE state <> 'completed';";
        Assert.AreEqual(0L, Convert.ToInt64(
            await countCommand.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        countCommand.CommandText = "SELECT COUNT(*) FROM raw_captures WHERE retention_hold = 1;";
        Assert.AreEqual(0L, Convert.ToInt64(
            await countCommand.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));

        using var processing = new SqliteConnection($"Data Source={Path.Combine(Fixture.StorageRoot, "journal", "raw-ingress.db")}");
        await processing.OpenAsync().ConfigureAwait(false);
        using var processingCommand = processing.CreateCommand();
        processingCommand.CommandText = "SELECT COUNT(*) FROM processing_nodes WHERE status <> 'Completed';";
        Assert.AreEqual(0L, Convert.ToInt64(
            await processingCommand.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        processingCommand.CommandText = "SELECT COUNT(DISTINCT output_identity_sha256) FROM processing_outputs;";
        Assert.IsGreaterThanOrEqualTo(5L, Convert.ToInt64(
            await processingCommand.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        processingCommand.CommandText = "SELECT artifact_id FROM processing_outputs WHERE node_id = 'CalibratedPreview' ORDER BY capture_sequence DESC LIMIT 1;";
        var localOnlyPreviewId = Guid.ParseExact(
            Convert.ToString(await processingCommand.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture)!,
            "N");
        Assert.IsFalse(pending.Any(item => item.ArtifactId == localOnlyPreviewId));

        var drain = ActivatorUtilities.CreateInstance<ArtifactOutboxDrainService>(services);
        await drain.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            try
            {
                await WaitUntilAsync(HasAcknowledgedOutboxRecord, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch (AssertFailedException)
            {
                var backgroundFailure = drain.ExecuteTask?.Exception?.GetBaseException().ToString() ?? "none";
                Assert.Fail($"Two-host outbox drain did not acknowledge: {ReadOutboxState()}; background failure: {backgroundFailure}");
            }
        }
        finally
        {
            await drain.StopAsync(CancellationToken.None).ConfigureAwait(false);
            drain.Dispose();
        }
        using (var hostScope = Fixture.CreateHostScope())
        {
            var centralDb = hostScope.ServiceProvider.GetRequiredService<HVO.SkyMonitor.LogicHost.Data.ApplicationDbContext>();
            var connection = centralDb.Database.GetDbConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM CentralArtifacts;";
            Assert.IsGreaterThan(0, Convert.ToInt32(
                await command.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture));
        }

        using var client = Fixture.CreateCameraAgentClient();
        using var response = await client.GetAsync(new Uri("/api/v1.0/frames/latest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
        var healthJson = await health.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        StringAssert.Contains(healthJson, "raw-ingress", StringComparison.Ordinal);
        StringAssert.Contains(healthJson, "capture-processing", StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for the configured VirtualSky pipeline.");
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private static bool HasNoUnfinishedLaneWork()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(Fixture.StorageRoot, "journal", "raw-ingress.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM capture_lane_work WHERE state <> 'completed';";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0;
    }

    private static bool HasAcknowledgedOutboxRecord()
    {
        var path = Path.Combine(Fixture.StorageRoot, "outbox", "artifact-outbox.db");
        if (!File.Exists(path))
        {
            return false;
        }
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM artifact_outbox_records WHERE status = 'acknowledged');";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static string ReadOutboxState()
    {
        var path = Path.Combine(Fixture.StorageRoot, "outbox", "artifact-outbox.db");
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status || ':' || COALESCE(last_reason, '') FROM artifact_outbox_records ORDER BY record_id LIMIT 1;";
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "no-record";
    }
}
