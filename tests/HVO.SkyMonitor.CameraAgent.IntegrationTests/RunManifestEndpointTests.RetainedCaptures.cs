using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

public sealed partial class RunManifestEndpointTests
{
    private const string PreviewNodeId = "CalibratedPreview";

    private static CameraAgentIntegrationFixture? _siblingFixture;
    private static Task<RetainedManifestCapture>? _siblingCapture;

    [ClassCleanup]
    public static void CleanupSiblingFixture()
    {
        _siblingFixture?.Dispose();
        _siblingFixture = null;
        _siblingCapture = null;
    }

    private static async Task<(CameraAgentIntegrationFixture Fixture, RetainedManifestCapture Capture)> GetSiblingCaptureAsync()
    {
        // The standalone assembly runs serially. Each ledger row restores its only write in finally.
        var fixture = _siblingFixture ??= new CameraAgentIntegrationFixture();
        var capture = await (_siblingCapture ??= PrepareSiblingCaptureAsync(fixture)).ConfigureAwait(false);
        return (fixture, capture);
    }

    private static async Task<RetainedManifestCapture> PrepareSiblingCaptureAsync(CameraAgentIntegrationFixture fixture)
    {
        await InitializeRetainedCaptureFixtureAsync(fixture).ConfigureAwait(false);
        using var scope = fixture.CreateCameraAgentScope();
        var (_, consumer) = await PrepareRetainedCapturesAsync(fixture, scope.ServiceProvider, checkMissingPreview: true).ConfigureAwait(false);
        return consumer;
    }

    private static Task InitializeRetainedCaptureFixtureAsync(CameraAgentIntegrationFixture fixture, bool nondurableCapture = false)
        => fixture.InitializeAsync(services =>
        {
            // Keep real acquisition running and complete only the two standard lane claims the test owns.
            services.Remove(services.Single(static descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(AfterApplicationStartedService<CaptureDistributionService>)));
            // Nothing consumes the shipped ephemeral queue now. The probe forwards every capture to it unchanged.
            services.AddSingleton(static provider => new CaptureQueueProbe(provider.GetRequiredService<CaptureDistributionService>()));
            services.Replace(ServiceDescriptor.Singleton<ICaptureDistributor>(static provider => provider.GetRequiredService<CaptureQueueProbe>()));
            if (!nondurableCapture) return;
            var capture = services.Single(static descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(CameraCaptureService));
            services[services.IndexOf(capture)] = ServiceDescriptor.Singleton<IHostedService>(static provider =>
                ActivatorUtilities.CreateInstance<CameraCaptureService>(provider,
                    new NondurableCaptureIngress(provider.GetRequiredService<IRawCaptureIngress>())));
        });

    private static async Task<(RetainedManifestCapture Reference, RetainedManifestCapture Consumer)>
        PrepareRetainedCapturesAsync(CameraAgentIntegrationFixture fixture, IServiceProvider services, bool checkMissingPreview = false)
    {
        const int historyPageSize = 10;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var operations = services.GetRequiredService<IProcessingGraphOperations>();
        var queue = services.GetRequiredService<CaptureQueueProbe>();
        var stage = "waiting for capture configuration";
        try
        {
            var configuration = await services.GetRequiredService<ICameraAgentConfigurationAccessor>()
                .WaitForConfigurationAsync(token).ConfigureAwait(false);
            var store = services.GetRequiredService<ICaptureLaneStore>();
            var handler = services.GetRequiredService<StandardCaptureLaneHandler>();
            var lane = services.GetRequiredService<CaptureLanePolicy>().Definitions.Single(definition => definition.Name == handler.Lane);

            stage = "completing the reference capture";
            var reference = await CompleteNextCaptureAsync().ConfigureAwait(false);
            stage = "completing the consumer capture";
            var consumer = await CompleteNextCaptureAsync().ConfigureAwait(false);
            Assert.AreNotEqual(reference.ExecutionId, consumer.ExecutionId);
            Assert.AreNotEqual(reference.CaptureId, consumer.CaptureId);

            stage = "waiting for ten newer pending executions";
            // The history page proves the regression setup. The retained identities come from the claims.
            while (true)
            {
                var recent = await operations.ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, historyPageSize, token).ConfigureAwait(false);
                if (recent.Count == historyPageSize && recent.All(run => run.Status == ProcessingGraphExecutionStatus.Pending
                    && run.AcceptedUtc > consumer.AcceptedUtc && run.AcceptedUtc > reference.AcceptedUtc))
                {
                    break;
                }

                ThrowIfDurablePathLost(queue.Snapshot(), stage);
                await Task.Delay(50, token).ConfigureAwait(false);
            }

            // Ten pending durable executions are ordinary work, not a lost durable path.
            var pending = queue.Snapshot();
            Assert.IsFalse(pending.DurablePathLost, pending.ToString());
            Assert.IsGreaterThanOrEqualTo(2, pending.DurableCaptures, pending.ToString());
            Assert.AreEqual(
                $"The durable capture path is intact: {pending.DurableCaptures} captures were committed durably, " +
                "0 nondurable captures are queued and no capture writer is blocked.",
                pending.ToString());

            stage = "reading the retained preview evidence by execution ID";
            var retainedConsumer = await ReadRetainedCaptureAsync(operations, consumer, token).ConfigureAwait(false);
            var retainedReference = await ReadRetainedCaptureAsync(operations, reference, token).ConfigureAwait(false);
            if (checkMissingPreview)
            {
                stage = "checking the missing preview binding";
                await CheckMissingPreviewAsync(fixture, operations, retainedReference, retainedConsumer, token).ConfigureAwait(false);
            }
            return (retainedReference, retainedConsumer);

            async Task<ProcessingGraphExecutionState> CompleteNextCaptureAsync()
            {
                CaptureLaneLease? lease;
                while ((lease = await store.ClaimAsync(lane, "retained-manifest-test", configuration, token).ConfigureAwait(false)) is null)
                {
                    ThrowIfDurablePathLost(queue.Snapshot(), stage);
                    await Task.Delay(50, token).ConfigureAwait(false);
                }

                var execution = lease.Context.Execution;
                Assert.IsNotNull(execution, "The claimed capture must retain its live execution binding.");
                var before = await operations.ReadRunManifestAsync(execution.ExecutionId, token).ConfigureAwait(false);
                Assert.IsNotNull(before);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Running, before.Execution.Status);
                Assert.IsFalse(before.Nodes.SelectMany(static node => node.Outputs).Any(),
                    "The live execution must have no derived output before its standard lane claim runs.");

                var result = await handler.HandleAsync(lease.Context, token).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                await store.CompleteAsync(lease, token).ConfigureAwait(false);

                var completed = await operations.ReadExecutionAsync(execution.ExecutionId, token).ConfigureAwait(false);
                Assert.IsNotNull(completed);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, completed.Status, completed.FailureReason);
                return completed;
            }
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            var path = queue.Snapshot();
            ThrowIfDurablePathLost(path, stage, exception);
            throw new TimeoutException($"The retained manifest fixture timed out while {stage}. {path}", exception);
        }
    }

    private static void ThrowIfDurablePathLost(CaptureQueueSnapshot path, string stage, Exception? innerException = null)
    {
        if (path.DurablePathLost)
            throw new InvalidOperationException($"The retained manifest fixture lost its durable capture path while {stage}. {path}", innerException);
    }

    private static async Task<RetainedManifestCapture> ReadRetainedCaptureAsync(
        IProcessingGraphOperations operations, ProcessingGraphExecutionState expected, CancellationToken token)
    {
        var detail = await operations.ReadRunManifestAsync(expected.ExecutionId, token).ConfigureAwait(false);
        Assert.IsNotNull(detail, $"No retained run manifest for execution {expected.ExecutionId:D}.");
        Assert.AreEqual(expected.ExecutionId, detail.Execution.ExecutionId);
        Assert.AreEqual(expected.CaptureId, detail.Execution.CaptureId);
        Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, detail.Execution.Status);
        var previews = detail.Nodes.Where(static node => node.NodeId == PreviewNodeId)
            .SelectMany(static node => node.Outputs)
            .Where(static output => output.Published && output.AvailabilityState == "Available")
            .ToArray();
        Assert.HasCount(1, previews, MissingPreviewMessage(expected.ExecutionId));
        return new(detail, previews[0]);
    }

    private static async Task CheckMissingPreviewAsync(
        CameraAgentIntegrationFixture fixture, IProcessingGraphOperations operations,
        RetainedManifestCapture reference, RetainedManifestCapture consumer, CancellationToken token)
    {
        var execution = reference.Detail.Execution;
        using var db = OpenManifestJournal(fixture);
        await db.OpenAsync(token).ConfigureAwait(false);
        using var command = db.CreateCommand();
        command.Parameters.AddWithValue("$execution", execution.ExecutionId.ToString("N"));
        command.Parameters.AddWithValue("$node", PreviewNodeId);
        command.Parameters.AddWithValue("$ordinal", reference.Preview.Ordinal);
        command.Parameters.AddWithValue("$output", reference.Preview.OutputIdentitySha256);
        command.CommandText = """
            DELETE FROM processing_execution_outputs
            WHERE execution_id=$execution AND node_id=$node AND output_ordinal=$ordinal
                AND output_identity_sha256=$output AND published_flag=1;
            """;
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync(token).ConfigureAwait(false));
        try
        {
            // The completed reference has lost its preview binding while the other completed capture still has one.
            var other = await ReadRetainedCaptureAsync(operations, consumer.Detail.Execution, token).ConfigureAwait(false);
            Assert.AreEqual(consumer.Preview.ArtifactId, other.Preview.ArtifactId);
            var missing = await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
                ReadRetainedCaptureAsync(operations, execution, token)).ConfigureAwait(false);
            StringAssert.Contains(missing.Message, MissingPreviewMessage(execution.ExecutionId), StringComparison.Ordinal);
        }
        finally
        {
            command.CommandText = """
                INSERT INTO processing_execution_outputs(execution_id, node_id, output_ordinal, output_identity_sha256, published_flag)
                VALUES ($execution, $node, $ordinal, $output, 1);
                """;
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false));
        }
        var restored = await ReadRetainedCaptureAsync(operations, execution, token).ConfigureAwait(false);
        Assert.AreEqual(reference.Preview.ArtifactId, restored.Preview.ArtifactId);
    }

    private static string MissingPreviewMessage(Guid executionId)
        => $"Execution {executionId:D} must retain a published calibrated preview.";

    private static SqliteConnection OpenManifestJournal(CameraAgentIntegrationFixture fixture)
        => new($"Data Source={Path.Combine(fixture.StorageRoot, "journal", "raw-ingress.db")};Pooling=False");

    private sealed record RetainedManifestCapture(ProcessingGraphExecutionDetail Detail, ProcessingGraphExecutionOutputState Preview);
}
