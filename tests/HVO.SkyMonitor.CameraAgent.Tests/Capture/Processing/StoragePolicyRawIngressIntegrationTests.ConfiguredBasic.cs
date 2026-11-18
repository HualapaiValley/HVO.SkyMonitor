using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class StoragePolicyRawIngressIntegrationTests
{
    [TestMethod]
    public async Task ConfiguredBasicSkippedBacklogHasDurableHealthSignalAcrossRestart()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-basic-skipped-health");
        try
        {
            var file = Profile(root);
            var clock = new EdgeUpgradeLaneTests.MutableTimeProvider(DateTimeOffset.UtcNow);
            using (var provider = ProcessingGraphOperationsTests.CreateProvider(root, timeProvider: clock))
            {
                await InitializeAsync(provider, file).ConfigureAwait(false);
                await AssertSkippedHealthAsync(provider, 0).ConfigureAwait(false);
                var receipt = (await Ingress(provider).AcceptAsync(file, Submission(1), CancellationToken.None).ConfigureAwait(false))!;
                var current = await ChangeProfileAsync(provider, file).ConfigureAwait(false);
                // A profile refresh alone is healthy; only actual abandoned durable work raises the signal.
                await AssertSkippedHealthAsync(provider, 0).ConfigureAwait(false);
                var result = await ProcessNextAsync(provider, current).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Abandoned, result.Outcome);
                Assert.AreEqual(LaneReason, result.Reason);
                AssertRawRetained(receipt);
                await AssertSkippedHealthAsync(provider, 1).ConfigureAwait(false);
                var next = await Ingress(provider).AcceptAsync(current, Submission(2), CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(next);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessNextAsync(provider, current).ConfigureAwait(false)).Outcome);
                await AssertSkippedHealthAsync(provider, 1).ConfigureAwait(false);
            }
            using var reopened = ProcessingGraphOperationsTests.CreateProvider(root, timeProvider: clock);
            await InitializeAsync(reopened, file).ConfigureAwait(false);
            await AssertSkippedHealthAsync(reopened, 1).ConfigureAwait(false);
            // Fault fixtures distinguish Storage skips (including optional-scene exhaustion) from other abandonment.
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE capture_lane_work SET failure_reason = $reason WHERE lane_name = 'standard' AND state = 'abandoned';";
                command.Parameters.AddWithValue("$reason", "processing-expired");
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
                await AssertSkippedHealthAsync(reopened, 0).ConfigureAwait(false);
                command.Parameters["$reason"].Value = "storage-policy-mismatch-scene-unavailable";
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
                await AssertSkippedHealthAsync(reopened, 1).ConfigureAwait(false);
            }
            clock.Now = clock.Now.AddDays(1);
            await AssertSkippedHealthAsync(reopened, 1).ConfigureAwait(false);
            clock.Now = clock.Now.AddMilliseconds(1);
            await AssertSkippedHealthAsync(reopened, 0).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task AssertSkippedHealthAsync(ServiceProvider provider, long expected)
    {
        var state = new CaptureProcessingState();
        using var refresh = new CaptureProcessingStateRefreshService(provider.GetRequiredService<SqliteCaptureProcessingStore>(),
            state, TimeProvider.System, Ingress(provider), Operations(provider));
        await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        var health = await new CaptureProcessingHealthCheck(state).CheckHealthAsync(new()).ConfigureAwait(false);
        Assert.AreEqual(expected, health.Data["StoragePolicySkippedCount"]);
        Assert.AreEqual(expected == 0 ? HealthStatus.Healthy : HealthStatus.Degraded, health.Status);
        Assert.IsNull(state.Snapshot.StoragePolicyMismatchActiveRevisionId);
        if (expected > 0)
        {
            StringAssert.Contains(state.Snapshot.Reason, "storage-policy-skipped", StringComparison.Ordinal);
            StringAssert.Contains(health.Description!, "Executions", StringComparison.Ordinal);
        }
    }
}
