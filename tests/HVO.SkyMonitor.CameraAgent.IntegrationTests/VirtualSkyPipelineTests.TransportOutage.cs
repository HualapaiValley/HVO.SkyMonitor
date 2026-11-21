#if !COMBINED_INTEGRATION_TESTS
using System.Globalization;
using System.Runtime.CompilerServices;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

public sealed partial class VirtualSkyPipelineTests
{
    private static readonly TimeSpan OutageStageTimeout = TimeSpan.FromSeconds(20);

    [TestMethod]
    public async Task CentralTransportOutageDoesNotBlockAcquisitionOrLoseLocalTransientProvenance()
    {
        var gate = new StandardLaneGate();
        using var fixture = new CameraAgentIntegrationFixture();
        try
        {
            await fixture.InitializeAsync(gate.Attach).ConfigureAwait(false);
            await VerifyTransportOutageAsync(fixture, gate).ConfigureAwait(false);
        }
        finally
        {
            // Unblock the ordinary worker before fixture disposal, including on assertion failure.
            gate.Release();
        }
    }

    private static async Task VerifyTransportOutageAsync(CameraAgentIntegrationFixture fixture, StandardLaneGate gate)
    {
        using var scope = fixture.CreateCameraAgentScope();
        var services = scope.ServiceProvider;
        var root = fixture.StorageRoot;
        var diagnostics = new VirtualSkyOutageDiagnostics(services, root) { Stage = "first standard lane lease" };
        var operations = services.GetRequiredService<IProcessingGraphOperations>();
        var products = services.GetRequiredService<SqliteCaptureProcessingStore>();
        var storage = services.GetRequiredService<IFrameStorageService>();
        var lanes = services.GetRequiredService<ICaptureLaneStore>();

        // The assembly host can already have minutes of unrelated work queued. Hold the first real lease on
        // a fresh host to make a small, known prefix that predates the transport failure.
        var firstLease = await ObserveOutageAsync<CaptureLaneHandlerContext>(
            async token => await gate.Entered.WaitAsync(token).ConfigureAwait(false), diagnostics).ConfigureAwait(false);
        Assert.IsNotNull(firstLease.Execution);
        var olderRaw = await ReadOutageManifestAsync(
            Path.ChangeExtension(firstLease.RawCapture.StoredFrame.AbsolutePath, ".json"), CancellationToken.None).ConfigureAwait(false);
        var olderExecutionId = firstLease.Execution.ExecutionId;
        var running = await operations.ReadExecutionAsync(olderExecutionId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingGraphExecutionStatus.Running, running?.Status);
        await AssertNoOutagePreviewAsync(olderRaw.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);

        diagnostics.Stage = "older pending standard work before the outage";
        var beforeOutage = await ObserveOutageAsync(async token =>
        {
            var backlog = await ReadStandardBacklogAsync(token).ConfigureAwait(false);
            // Lane PendingCount includes rows already leased, retrying or quarantined.
            return CountWaiting(backlog) > 0 ? backlog : null;
        }, diagnostics).ConfigureAwait(false);
        Assert.AreEqual(1L, beforeOutage.LeasedCount);

        var configured = services.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value;
        await services.GetRequiredService<IArtifactOutbox>().EnqueueAsync(root, olderRaw, CancellationToken.None).ConfigureAwait(false);
        var outageOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            CaptureDistribution = configured.CaptureDistribution,
            UploadBatchSize = 1,
            UploadPollIntervalSeconds = 1,
            UploadRetryInitialDelaySeconds = 1,
            UploadRetryMaximumDelaySeconds = 1
        });
        using var outageFactory = new OutageHttpClientFactory();
        var outageClient = new ArtifactUploadClient(outageFactory, outageOptions, TimeProvider.System);
        using var drain = ActivatorUtilities.CreateInstance<ArtifactOutboxDrainService>(services, outageClient, outageOptions);
        await drain.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            diagnostics.Stage = "durable transport retry for the enqueued raw";
            var firstRetry = await ObserveOutageAsync(
                token => ReadOutageRetryAsync(root, olderRaw.IdempotencyKey, token), diagnostics).ConfigureAwait(false);

            // Read the sequence watermark after the timestamp. Both fences must be passed; even a pre-checkpoint
            // exposure whose journal commit races with this read cannot be accepted.
            var checkpointUtc = DateTimeOffset.UtcNow;
            diagnostics.CheckpointUtc = checkpointUtc;
            var (watermark, checkpointIds) = ReadOutageCheckpoint(root);
            diagnostics.CheckpointCaptureIds = checkpointIds;

            // Raw commitment, listing and both previews share the original post-checkpoint deadline.
            var (raw, rawManifest, published) = await WithinOutageBudgetAsync(async token =>
            {
                diagnostics.Stage = "committed post-checkpoint raw while the older lease is held";
                var selectedRaw = await PollOutageAsync(cancellation =>
                    ReadPostCheckpointRawAsync(root, checkpointUtc, watermark, cancellation), token).ConfigureAwait(false);
                var selectedManifest = await ReadOutageManifestAsync(Path.Combine(root, selectedRaw.SidecarPath), token).ConfigureAwait(false);
                var measuredCapture = selectedManifest.Descriptor.Capture;
                Assert.AreEqual(selectedRaw.CaptureId, measuredCapture.CaptureId);
                Assert.AreEqual(selectedRaw.ArtifactId, selectedManifest.Descriptor.Artifact.ArtifactId);
                Assert.AreEqual(FrameArtifactRole.Raw, selectedManifest.Descriptor.Artifact.Role);
                Assert.IsGreaterThan(watermark, measuredCapture.CaptureSequence);
                Assert.IsGreaterThan(checkpointUtc, selectedManifest.Descriptor.Timing.ExposureStartedUtc);
                Assert.IsFalse(checkpointIds.Contains(measuredCapture.CaptureId));
                Assert.IsLessThan(checkpointUtc, olderRaw.Descriptor.Timing.ExposureStartedUtc);
                await PollOutageAsync(_ => Task.FromResult(IsOutageArtifactListed(selectedManifest) ? selectedManifest : null),
                    token).ConfigureAwait(false);

                var queued = await operations.ReadExecutionAsync(selectedRaw.ExecutionId, token).ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Pending, queued?.Status);
                await AssertNoOutagePreviewAsync(measuredCapture.CaptureId, token).ConfigureAwait(false);
                Assert.IsNull(await ReadOutagePreviewAsync(selectedRaw.ExecutionId, token).ConfigureAwait(false),
                    "A committed raw behind the held lease must not satisfy the preview predicate.");
                var backlog = await ReadStandardBacklogAsync(token).ConfigureAwait(false);
                Assert.AreEqual(1L, backlog.LeasedCount);
                Assert.IsGreaterThan(0L, CountWaiting(backlog));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"outage checkpoint={checkpointUtc:O} watermark={watermark} preOutageWaiting={CountWaiting(beforeOutage)} "
                    + $"oldestUnfinished={backlog.OldestPendingUtc:O} leased={backlog.LeasedCount} waiting={CountWaiting(backlog)} "
                    + $"firstRetry={firstRetry.Attempt}/{firstRetry.Reason}@{firstRetry.RecordedUtc:O} "
                    + $"older={olderRaw.Descriptor.Capture.CaptureSequence} measured={measuredCapture.CaptureSequence}"));

                diagnostics.Stage = "published older and exact post-checkpoint previews during transport retry";
                gate.Release();
                var selectedPreviews = await PollOutageAsync(async cancellation =>
                {
                    var measuredPreview = await ReadOutagePreviewAsync(selectedRaw.ExecutionId, cancellation).ConfigureAwait(false);
                    if (measuredPreview is null) return null;
                    var olderPreview = await ReadOutagePreviewAsync(olderExecutionId, cancellation).ConfigureAwait(false);
                    return olderPreview is null ? null : new OutagePreviews(olderPreview, measuredPreview);
                }, token).ConfigureAwait(false);
                return (selectedRaw, selectedManifest, selectedPreviews);
            }, diagnostics).ConfigureAwait(false);
            var publicationObservedUtc = DateTimeOffset.UtcNow;

            // Exercise the same consumer with a real wrong-capture preview, even though both captures use
            // the same transient scenario definition and both previews were produced during the outage.
            AssertOutagePair(olderRaw, published.Older);
            AssertOutagePair(rawManifest, published.Measured);
            await AssertOutageLineageAsync(products, raw.ArtifactId, published.Measured).ConfigureAwait(false);

            // Substitute the actual older Preview's sources while keeping the measured capture metadata.
            var wrongSources = published.Measured with
            {
                Descriptor = published.Measured.Descriptor with
                {
                    Artifact = published.Measured.Descriptor.Artifact with
                    {
                        SourceArtifactIds = published.Older.Descriptor.Artifact.SourceArtifactIds
                    }
                }
            };
            AssertOutagePair(rawManifest, wrongSources);
            var wrongLineage = await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
                AssertOutageLineageAsync(products, raw.ArtifactId, wrongSources)).ConfigureAwait(false);
            StringAssert.Contains(wrongLineage.Message, "Preview lineage must reach the measured raw", StringComparison.Ordinal);
            var mismatch = Assert.ThrowsExactly<AssertFailedException>(() => AssertOutagePair(rawManifest, published.Older));
            StringAssert.Contains(mismatch.Message, "Preview capture identity must match the raw", StringComparison.Ordinal);
            Assert.IsNotNull(published.Measured.Scene);
            var wrongProvenance = published.Measured with
            {
                Scene = published.Measured.Scene with { TransientScenario = olderRaw.Scene?.TransientScenario }
            };
            var provenanceMismatch = Assert.ThrowsExactly<AssertFailedException>(() => AssertOutagePair(rawManifest, wrongProvenance));
            StringAssert.Contains(provenanceMismatch.Message, "Transient provenance must match the raw", StringComparison.Ordinal);
            Assert.AreNotEqual(olderRaw.Descriptor.Artifact.ArtifactId, raw.ArtifactId);
            await AssertOutagePayloadAsync(root, rawManifest).ConfigureAwait(false);
            await AssertOutagePayloadAsync(root, published.Measured).ConfigureAwait(false);

            diagnostics.Stage = "continued transport retries after preview publication";
            var laterRetry = await ObserveOutageAsync(async token =>
            {
                var retry = await ReadOutageRetryAsync(root, olderRaw.IdempotencyKey, token).ConfigureAwait(false);
                return retry is { } failed && failed.Attempt > firstRetry.Attempt
                    && failed.RecordedUtc > publicationObservedUtc ? failed : null;
            }, diagnostics).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"outage publication observed={publicationObservedUtc:O} older={olderRaw.Descriptor.Capture.CaptureId:D} "
                + $"measured={raw.CaptureId:D} raw={raw.ArtifactId:D} preview={published.Measured.Descriptor.Artifact.ArtifactId:D} "
                + $"laterRetry={laterRetry.Attempt}/{laterRetry.Reason}@{laterRetry.RecordedUtc:O}"));
        }
        finally
        {
            await drain.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        AssertIngressAdmitting(services.GetRequiredService<RawIngressState>().Snapshot, "after publication",
            configured.ProjectedSceneStaging.MaximumReconciliationEntries, root);
        var processingState = services.GetRequiredService<CaptureProcessingState>();
        // Healthy includes in-flight backlog. Check the defect signals independently while acquisition continues.
        diagnostics.Stage = "processing after publication";
        await WaitUntilAsync(
            () => processingState.Snapshot is
            {
                TerminalCount: 0, ProcessingQuarantineCount: 0, MissingProductCount: 0,
                ReplayTerminalCount: 0, DurableStateUnavailable: false, ReconciliationFailed: false
            },
            OutageStageTimeout, describeTimeout: diagnostics.Describe).ConfigureAwait(false);

        async Task<CaptureLaneBacklog> ReadStandardBacklogAsync(CancellationToken token)
            => (await lanes.ReadBacklogsAsync(token).ConfigureAwait(false)).Single(static lane => lane.Lane == "standard");

        static long CountWaiting(CaptureLaneBacklog backlog)
            => backlog.PendingCount - backlog.LeasedCount - backlog.RetryCount - backlog.QuarantineCount;

        async Task<ArtifactManifestV2?> ReadOutagePreviewAsync(Guid executionId, CancellationToken token)
        {
            var detail = await operations.ReadExecutionDetailAsync(executionId, token).ConfigureAwait(false);
            if (detail?.Execution.Status != ProcessingGraphExecutionStatus.Completed) return null;
            var preview = detail.Nodes.Where(static node => node.NodeId == "Preview").SelectMany(static node => node.Outputs)
                .SingleOrDefault(static output => output.Published && output.AvailabilityState == "Available"
                    && output.Role == FrameArtifactRole.Preview && output.Variant == "default");
            if (preview is null) return null;

            var output = await products.ReadOutputByArtifactIdAsync(preview.ArtifactId, token).ConfigureAwait(false);
            Assert.IsNotNull(output);
            var manifest = await ReadOutageManifestAsync(Path.Combine(root, output.SidecarRelativePath), token).ConfigureAwait(false);
            Assert.AreEqual(preview.ArtifactId, manifest.Descriptor.Artifact.ArtifactId);
            Assert.AreEqual(detail.Execution.CaptureId, manifest.Descriptor.Capture.CaptureId);
            Assert.AreEqual(preview.PayloadSha256, manifest.Descriptor.Artifact.ChecksumSha256, ignoreCase: true);
            var sources = await products.ReadOutputSourcesAsync(output.OutputIdentitySha256,
                SqliteCaptureProcessingStore.MaximumOutputSourceCount, token).ConfigureAwait(false);
            CollectionAssert.AreEqual(sources.Select(static source => source.ArtifactId).ToArray(),
                manifest.Descriptor.Artifact.SourceArtifactIds.ToArray());
            return IsOutageArtifactListed(manifest) ? manifest : null;
        }

        bool IsOutageArtifactListed(ArtifactManifestV2 manifest)
        {
            var listed = storage.List(root, DateOnly.FromDateTime(manifest.Descriptor.Timing.ExposureStartedUtc.UtcDateTime),
                null, 10_000);
            diagnostics.LastListedFrames = listed;
            diagnostics.LastListCapReached = listed.Count >= 10_000;
            return listed.Any(frame => frame.Role == manifest.Descriptor.Artifact.Role
                && string.Equals(frame.RelativePath, manifest.RelativeArtifactPath, StringComparison.Ordinal));
        }

        async Task AssertNoOutagePreviewAsync(Guid captureId, CancellationToken token)
        {
            var retained = await products.ReadCaptureProductsAsync(captureId, null, 64, token).ConfigureAwait(false);
            Assert.IsFalse(retained.Any(static product => product.Role == FrameArtifactRole.Preview),
                "The held standard lane must have no retained preview for this capture.");
        }
    }

    private static (long Watermark, HashSet<Guid> CaptureIds) ReadOutageCheckpoint(string root)
    {
        using var connection = OpenOutageEvidence(root, "journal", "raw-ingress.db");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT capture_sequence, capture_id FROM raw_capture_assignments ORDER BY capture_sequence DESC;";
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read(), "The old lease must already have a durable capture assignment.");
        var watermark = reader.GetInt64(0);
        var ids = new HashSet<Guid>();
        do
        {
            ids.Add(reader.GetGuid(1));
        } while (reader.Read());
        return (watermark, ids);
    }

    private static async Task<OutageRaw?> ReadPostCheckpointRawAsync(
        string root, DateTimeOffset checkpointUtc, long watermark, CancellationToken token)
    {
        using var connection = OpenOutageEvidence(root, "journal", "raw-ingress.db");
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw.capture_id, raw.raw_artifact_id, raw.sidecar_relative_path, execution.execution_id
            FROM raw_captures raw
            JOIN processing_executions execution ON execution.capture_id = raw.capture_id AND execution.execution_class = 'Live'
            WHERE raw.state = 'committed' AND raw.capture_sequence > $watermark AND raw.exposure_started_unix_ms > $checkpoint
            ORDER BY raw.capture_sequence LIMIT 1;
            """;
        command.Parameters.AddWithValue("$watermark", watermark);
        command.Parameters.AddWithValue("$checkpoint", checkpointUtc.ToUnixTimeMilliseconds());
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3)) : null;
    }

    private static async Task<OutageRetry?> ReadOutageRetryAsync(string root, string key, CancellationToken token)
    {
        using var connection = OpenOutageEvidence(root, "outbox", "artifact-outbox.db");
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_count, last_reason, updated_unix_ms FROM artifact_outbox_records
            WHERE idempotency_key = $key AND status = 'retry'
              AND last_reason IN ('transport-failure', 'status-transport-failure');
            """;
        command.Parameters.AddWithValue("$key", key);
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? new(reader.GetInt32(0), reader.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))) : null;
    }

    private static SqliteConnection OpenOutageEvidence(string root, string directory, string filename)
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, directory, filename),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());

    private static async Task<ArtifactManifestV2> ReadOutageManifestAsync(string path, CancellationToken token)
    {
        var parsed = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
        Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
        Assert.IsNotNull(parsed.Document?.Manifest);
        return parsed.Document.Manifest;
    }

    private static void AssertOutagePair(ArtifactManifestV2 raw, ArtifactManifestV2 preview)
    {
        Assert.AreEqual(FrameArtifactRole.Raw, raw.Descriptor.Artifact.Role);
        Assert.AreEqual(FrameArtifactRole.Preview, preview.Descriptor.Artifact.Role);
        Assert.AreEqual(raw.Descriptor.Capture, preview.Descriptor.Capture, "Preview capture identity must match the raw.");
        Assert.AreNotEqual(raw.Descriptor.Artifact.ArtifactId, preview.Descriptor.Artifact.ArtifactId);
        Assert.AreEqual(raw.Descriptor.Timing, preview.Descriptor.Timing);
        var rawScenario = raw.Scene?.TransientScenario;
        var previewScenario = preview.Scene?.TransientScenario;
        Assert.IsNotNull(rawScenario);
        Assert.IsNotNull(previewScenario);
        Assert.IsTrue(rawScenario.IsValid());
        Assert.IsTrue(previewScenario.IsValid());
        // The validated digest binds Parameters; compare the remaining capture-specific facts as values.
        Assert.AreEqual(rawScenario with { Parameters = default }, previewScenario with { Parameters = default },
            "Transient provenance must match the raw.");
    }

    private static async Task AssertOutagePayloadAsync(string root, ArtifactManifestV2 manifest)
    {
        using var payload = File.OpenRead(Path.Combine(root, manifest.RelativeArtifactPath));
        Assert.AreEqual(manifest.Descriptor.Layout.ByteLength, payload.Length);
        var checksum = await PayloadChecksum.ComputeSha256Async(payload).ConfigureAwait(false);
        Assert.AreEqual(manifest.Descriptor.Artifact.ChecksumSha256, checksum, ignoreCase: true);
    }

    private static async Task AssertOutageLineageAsync(
        SqliteCaptureProcessingStore products, Guid measuredRawId, ArtifactManifestV2 preview)
    {
        var visited = new HashSet<Guid>();
        var pending = new Queue<Guid>(preview.Descriptor.Artifact.SourceArtifactIds);
        while (pending.TryDequeue(out var artifactId))
        {
            // This identity is already bound to the real committed raw and its retained bytes by the caller.
            if (artifactId == measuredRawId) return;
            if (!visited.Add(artifactId)) continue;

            // A rolling combination can have another raw input. It has no processing-output row.
            var output = await products.ReadOutputByArtifactIdAsync(artifactId, CancellationToken.None).ConfigureAwait(false);
            if (output is null) continue;
            var sources = await products.ReadOutputSourcesAsync(
                output.OutputIdentitySha256, SqliteCaptureProcessingStore.MaximumOutputSourceCount,
                CancellationToken.None).ConfigureAwait(false);
            foreach (var source in sources)
            {
                pending.Enqueue(source.ArtifactId);
            }
        }
        Assert.Fail("Preview lineage must reach the measured raw artifact " + measuredRawId.ToString("D"));
    }

    private static Task<T> ObserveOutageAsync<T>(
        Func<CancellationToken, Task<T?>> observe, VirtualSkyOutageDiagnostics diagnostics, [CallerLineNumber] int line = 0)
        where T : class
        => WithinOutageBudgetAsync(token => PollOutageAsync(observe, token), diagnostics, line);

    private static async Task<T> WithinOutageBudgetAsync<T>(
        Func<CancellationToken, Task<T>> run, VirtualSkyOutageDiagnostics diagnostics, [CallerLineNumber] int line = 0)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(OutageStageTimeout);
        try
        {
            var result = await run(deadline.Token).ConfigureAwait(false);
            if (started.Elapsed < OutageStageTimeout) return result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Use the same failure snapshot whether polling or a cancellable evidence read used the budget.
        }

        throw CreateWaitTimeout(started.Elapsed, OutageStageTimeout, line, diagnostics.Stage, diagnostics.Describe);
    }

    private static async Task<T> PollOutageAsync<T>(Func<CancellationToken, Task<T?>> observe, CancellationToken token)
        where T : class
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (await observe(token).ConfigureAwait(false) is { } result) return result;
            await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        }
    }

    private sealed record OutageRaw(Guid CaptureId, Guid ArtifactId, string SidecarPath, Guid ExecutionId);
    private sealed record OutageRetry(int Attempt, string Reason, DateTimeOffset RecordedUtc);
    private sealed record OutagePreviews(ArtifactManifestV2 Older, ArtifactManifestV2 Measured);

    private sealed class StandardLaneGate
    {
        private readonly TaskCompletionSource<CaptureLaneHandlerContext> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CaptureLaneHandlerContext> Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public void Attach(IServiceCollection services)
        {
            for (var i = 0; i < services.Count; i++)
            {
                var descriptor = services[i];
                if (descriptor.ServiceType != typeof(ICaptureLaneHandler) || descriptor.ImplementationFactory is not { } factory) continue;
                services[i] = ServiceDescriptor.Describe(typeof(ICaptureLaneHandler), provider =>
                {
                    var handler = (ICaptureLaneHandler)factory(provider);
                    return handler is StandardCaptureLaneHandler ? new GatedHandler(handler, this) : handler;
                }, descriptor.Lifetime);
            }
        }

        private sealed class GatedHandler(ICaptureLaneHandler inner, StandardLaneGate gate) : ICaptureLaneHandler
        {
            public string Lane => inner.Lane;

            public async ValueTask<CaptureLaneHandlerResult> HandleAsync(CaptureLaneHandlerContext context, CancellationToken token)
            {
                gate._entered.TrySetResult(context);
                await gate._release.Task.WaitAsync(token).ConfigureAwait(false);
                return await inner.HandleAsync(context, token).ConfigureAwait(false);
            }
        }
    }

    private sealed class OutageHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client = new(new OutageHttpMessageHandler())
        {
            BaseAddress = new Uri("http://central-outage.invalid/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(1)
        };

        public HttpClient CreateClient(string name) => client;

        public void Dispose() => client.Dispose();
    }

    private sealed class OutageHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Injected Central transport outage.");
    }
}
#endif
