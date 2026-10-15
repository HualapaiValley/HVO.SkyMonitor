using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// A Named active revision compiled by an earlier version can never run its live work. Health reports it once per
/// transition while captures keep being accepted and uploaded, and it clears without a restart when the operator
/// activates a revision this version compiled or rolls back to configured-basic.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class EdgeUpgradeActiveRevisionTests
{
    private const int SupersededEventId = 2097;
    private const int ClearedEventId = 2098;

    [TestMethod]
    [DataRow("activate")]
    [DataRow("rollback")]
    public async Task NamedRevisionCompiledByAnEarlierVersionDegradesHealthUntilReplaced(string recovery)
    {
        var root = EdgeUpgradeLaneTests.CreateRoot("revision-superseded");
        try
        {
            var clock = new EdgeUpgradeLaneTests.MutableTimeProvider(DateTimeOffset.UtcNow);
            using var provider = EdgeUpgradeLaneTests.CreateProvider(root, clock);
            var fixture = await EdgeUpgradeLaneTests.StartAsync(provider, root).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var basic = await operations.EnsureConfiguredBasicAsync(fixture.Configuration, CancellationToken.None)
                .ConfigureAwait(false);
            var stale = await CreateValidatedAsync(operations, fixture, "v9").ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(stale, basic.StateVersion, "activate-v9", "owner-test",
                null, CancellationToken.None).ConfigureAwait(false);
            // The revision's nodes were compiled under a plan this build no longer produces.
            var snapshot = await operations.ReadRevisionSnapshotAsync(stale, CancellationToken.None).ConfigureAwait(false);
            await EdgeUpgradeLaneTests.ExecuteAsync(root, """
                UPDATE processing_graph_revisions
                SET nodes_json = CAST(replace(CAST(nodes_json AS TEXT), $current, $stale) AS BLOB)
                WHERE revision_id = $revision;
                """, ("$current", snapshot.Nodes[0].PlanSha256), ("$stale", EdgeUpgradeLaneTests.SupersededPlan),
                ("$revision", stale)).ConfigureAwait(false);

            var state = new CaptureProcessingState();
            var logger = new RecordingLogger<CaptureProcessingStateRefreshService>();
            using var refresh = new CaptureProcessingStateRefreshService(
                fixture.Store, state, clock, fixture.Ingress, operations, logger);
            await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
            await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(CaptureProcessingAvailability.Degraded, state.Snapshot.Availability);
            Assert.AreEqual(stale, state.Snapshot.SupersededActiveRevisionId);
            StringAssert.Contains(state.Snapshot.Reason, "active-revision-superseded", StringComparison.Ordinal);
            var health = await new CaptureProcessingHealthCheck(state).CheckHealthAsync(new HealthCheckContext())
                .ConfigureAwait(false);
            Assert.AreEqual(HealthStatus.Degraded, health.Status);
            Assert.AreEqual(stale, health.Data["SupersededActiveRevisionId"]);
            Assert.AreEqual(1, logger.Count(LogLevel.Error, SupersededEventId), "One error per transition.");

            var first = await fixture.AcceptAsync(0).ConfigureAwait(false);
            Assert.AreEqual(RawIngressAvailability.Accepting,
                provider.GetRequiredService<RawIngressState>().Snapshot.Availability);
            var abandoned = await fixture.ProcessStandardAsync().ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Abandoned, abandoned.Acknowledged, abandoned.Handled.Reason);
            Assert.AreEqual("plan-superseded", abandoned.Handled.Reason);
            await fixture.UploadAndAssertCompletedAsync(first).ConfigureAwait(false);

            if (recovery == "activate")
            {
                var current = await CreateValidatedAsync(operations, fixture, "v10").ConfigureAwait(false);
                _ = await operations.ActivateRevisionAsync(current, active.StateVersion, "activate-v10", "owner-test",
                    null, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                _ = await operations.RollbackRevisionAsync(basic.ConfiguredBasicRevisionId, active.StateVersion,
                    "rollback-basic", "owner-test", null, CancellationToken.None).ConfigureAwait(false);
            }
            await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
            await refresh.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(state.Snapshot.SupersededActiveRevisionId);
            Assert.AreEqual(CaptureProcessingAvailability.Healthy, state.Snapshot.Availability, state.Snapshot.Reason);
            Assert.AreEqual(1, logger.Count(LogLevel.Error, SupersededEventId));
            Assert.AreEqual(1, logger.Count(LogLevel.Information, ClearedEventId));
            var second = await fixture.AcceptAsync(1).ConfigureAwait(false);
            var processed = await fixture.ProcessStandardAsync().ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Handled.Outcome, processed.Handled.Reason);
            Assert.AreEqual(second, processed.CaptureId);
        }
        finally
        {
            EdgeUpgradeLaneTests.Cleanup(root);
        }
    }

    private static async Task<string> CreateValidatedAsync(
        ProcessingGraphOperationsCoordinator operations,
        EdgeUpgradeLaneTests.UpgradeFixture fixture,
        string revision)
    {
        var created = await operations.CreateRevisionAsync("nightly", revision, fixture.Configuration.Pipeline,
            "create-" + revision, "owner-test", null, CancellationToken.None).ConfigureAwait(false);
        _ = await operations.ValidateRevisionAsync(created.RevisionId, "validate-" + revision, "owner-test", null,
            CancellationToken.None).ConfigureAwait(false);
        return created.RevisionId;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, int EventId)> _entries = [];

        internal int Count(LogLevel level, int eventId)
        {
            lock (_entries) return _entries.Count(entry => entry.Level == level && entry.EventId == eventId);
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, eventId.Id));
        }
    }
}
