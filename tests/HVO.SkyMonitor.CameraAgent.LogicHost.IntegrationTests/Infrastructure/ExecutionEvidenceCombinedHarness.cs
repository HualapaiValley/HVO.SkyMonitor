using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

/// <summary>
/// A real provisioned edge, its durable source and SQLite outbox, and the production HTTP transport. Faults apply
/// outside the receiver, so response loss means SQL has committed before the sender loses its acknowledgement.
/// </summary>
internal sealed class ExecutionEvidenceCombinedHarness : IAsyncDisposable
{
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private readonly EvidenceHttpClientFactory _clients;
    private readonly IOptions<CameraAgentHostOptions> _options;
    private readonly ExecutionEvidenceExportTelemetry _telemetry;
    private ExecutionEvidenceExportService _exporter;
    private EdgeEvidenceRow? _availabilityTemplate;

    private ExecutionEvidenceCombinedHarness(
        CameraAgentIntegrationFixture fixture,
        CameraAgentIntegrationFixture.IsolatedCameraAgent agent,
        ExecutionEvidenceOriginDescriptor descriptor,
        ExecutionEvidenceEdgeMetrics metrics)
    {
        Agent = agent;
        Descriptor = descriptor;
        Metrics = metrics;
        metrics.Attach(agent.Services.GetRequiredService<ICaptureTelemetryProvider>());
        Clock = new EvidenceClock();
        Outbox = new SqliteExecutionEvidenceOutbox(Clock);
        State = new ExecutionEvidenceExportState();
        Http = new EvidenceHttpHandler(fixture.HostFixture.Factory.Server.CreateHandler());
        _clients = new EvidenceHttpClientFactory(Http, fixture.HostFixture.Factory.Server.BaseAddress);
        var configured = agent.Services.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value;
        Assert.IsFalse(configured.ExecutionEvidenceExport.Enabled,
            "A hosted exporter must not race this manually enabled instance.");
        _options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = agent.StorageRoot,
            CentralIntegration = configured.CentralIntegration,
            ExecutionEvidenceExport = new ExecutionEvidenceExportOptions
            {
                Enabled = true,
                MaximumRequestUnits = 256, // The receiver's negotiated 64-unit limit must actually clamp this.
                MaximumRequestBytes = 16 * 1024 * 1024,
                MaximumDiscoveryBatchesPerCycle = 16,
                RetryInitialDelaySeconds = 1,
                RetryMaximumDelaySeconds = 1,
                MaximumAttempts = 1000,
                MaximumRetainedAcknowledgements = 50_000
            }
        });
        Transport = ActivatorUtilities.CreateInstance<CameraAgentExecutionEvidenceTransport>(
            agent.Services, _clients, _options, Clock);
        _telemetry = new ExecutionEvidenceExportTelemetry(State, Clock);
        _exporter = CreateExporter();
    }

    internal CameraAgentIntegrationFixture.IsolatedCameraAgent Agent { get; }
    internal string Root => Agent.StorageRoot;
    internal ExecutionEvidenceOriginDescriptor Descriptor { get; }
    internal ExecutionEvidenceEdgeMetrics Metrics { get; }
    internal EvidenceClock Clock { get; }
    internal SqliteExecutionEvidenceOutbox Outbox { get; }
    internal ExecutionEvidenceExportState State { get; }
    internal EvidenceHttpHandler Http { get; }
    internal CameraAgentExecutionEvidenceTransport Transport { get; }
    internal IExecutionEvidenceSource Source => Agent.Services.GetRequiredService<IExecutionEvidenceSource>();

    internal static async Task<ExecutionEvidenceCombinedHarness> CreateAsync(
        CameraAgentIntegrationFixture fixture, bool w1Geometry = false)
    {
        var metrics = new ExecutionEvidenceEdgeMetrics();
        CameraAgentIntegrationFixture.IsolatedCameraAgent? agent = null;
        try
        {
            agent = await fixture.StartIsolatedCameraAgentAsync(w1Geometry: w1Geometry).ConfigureAwait(false);
            var provider = agent.Services.GetRequiredService<IExecutionEvidenceOriginProvider>();
            Assert.IsInstanceOfType<LocalExecutionEvidenceOriginProvider>(provider);
            var descriptor = await provider.GetDescriptorAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreNotEqual(agent.DevicePublicId, descriptor.AgentInstanceId);
            Assert.AreNotEqual(agent.DevicePublicId, descriptor.OriginInstallationId);
            Assert.IsNull(descriptor.ObservatoryId);
            Assert.IsNull(descriptor.LogicalCameraInstallationId);
            Assert.IsNull(descriptor.InstallationPublicId);
            return new(fixture, agent, descriptor, metrics);
        }
        catch
        {
            metrics.Dispose();
            if (agent is not null) { await agent.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }

    internal async Task WaitForRealExecutionAsync(CancellationToken cancellationToken = default)
        => await WaitUntilAsync(async () =>
            (await Source.ReadTerminalExecutionsAsync(0, string.Empty, 1, cancellationToken)
                .ConfigureAwait(false)).Count != 0, "a terminal real edge execution",
            cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>Stops acquisition only after the finite source, or a completed live assertion stage, drains.</summary>
    internal async Task StopCaptureAndWaitForTerminalAsync()
    {
        await PauseCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
        var capture = Agent.Services.GetServices<IHostedService>().OfType<CameraCaptureService>().Single();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await capture.StopAsync(timeout.Token).ConfigureAwait(false);
    }

    internal async Task PauseCaptureAndWaitForTerminalAsync(CancellationToken cancellationToken = default)
    {
        await WaitForRealExecutionAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        // StopAsync cancels acquisition and may leave a just-accepted execution for restart recovery. Close normal
        // admission first, let its publication finish, and only stop the capture host after durable work is terminal.
        var admission = Agent.Services.GetRequiredService<CaptureAdmissionCoordinator>();
        var paused = await admission.PauseAsync($"issue538-pause-{Guid.NewGuid():N}", null,
            "issue538-test-owner", "finite-evidence-source", timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Paused, paused.State);
        try
        {
            await WaitUntilAsync(async () =>
                await Source.ReadOldestActiveExecutionKeyAsync(timeout.Token).ConfigureAwait(false) is null,
                "all in-flight edge executions terminal after admission pauses", cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or AssertFailedException)
        {
            await RetainFailureDiagnosticsAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "All diagnostic SQL statements are fixed literals with no user input.")]
    private async Task RetainFailureDiagnosticsAsync(Exception failure)
    {
        // This runs only after a failed terminal barrier, outside every measured performance interval. Back up the
        // live SQLite journal before host cleanup; never copy identity, provisioning or credential runtime files.
        var directory = Path.GetFullPath(Path.Combine("TestResults", "issue-538", "diagnostics",
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Agent.DevicePublicId:N}"));
        try
        {
            Directory.CreateDirectory(directory);
            var journalPath = Path.Combine(Root, "journal", "raw-ingress.db");
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = journalPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString());
            await source.OpenAsync().ConfigureAwait(false);
            var backupPath = Path.Combine(directory, "raw-ingress.db");
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString()))
            {
                await backup.OpenAsync().ConfigureAwait(false);
                source.BackupDatabase(backup);
            }

            var queries = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["executionCounts"] = "SELECT execution_class, status, COUNT(*) AS count FROM processing_executions GROUP BY execution_class, status",
                ["activeExecutions"] = """
                    SELECT execution_id, execution_class, status, capture_id, primary_artifact_id, graph_revision_id,
                           accepted_unix_ms, available_unix_ms, deadline_unix_ms, maximum_age_unix_ms,
                           started_unix_ms, completed_unix_ms, failure_reason, cancellation_requested, attempt_count
                    FROM processing_executions WHERE status IN ('Pending','Running') ORDER BY accepted_unix_ms LIMIT 256
                    """,
                ["activeNodes"] = """
                    SELECT node.* FROM processing_execution_nodes node
                    JOIN processing_executions execution USING(execution_id)
                    WHERE execution.status IN ('Pending','Running') ORDER BY execution.accepted_unix_ms, node.node_id LIMIT 512
                    """,
                ["activeAttempts"] = """
                    SELECT attempt.* FROM processing_node_attempts attempt
                    JOIN processing_executions execution USING(execution_id)
                    WHERE execution.status IN ('Pending','Running')
                    ORDER BY execution.accepted_unix_ms, attempt.node_id, attempt.attempt_number LIMIT 512
                    """,
                ["linkedRawCaptures"] = """
                    SELECT raw.* FROM raw_captures raw JOIN processing_executions execution USING(capture_id)
                    WHERE execution.status IN ('Pending','Running') ORDER BY raw.raw_capture_row_id LIMIT 256
                    """,
                ["linkedLaneWork"] = """
                    SELECT work.* FROM capture_lane_work work JOIN raw_captures raw USING(raw_capture_row_id)
                    JOIN processing_executions execution USING(capture_id)
                    WHERE execution.status IN ('Pending','Running') ORDER BY work.work_id LIMIT 512
                    """,
                ["laneCounts"] = "SELECT lane_name, state, COUNT(*) AS count FROM capture_lane_work GROUP BY lane_name, state",
                ["captureControl"] = "SELECT * FROM capture_control_state"
            };
            var rows = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (name, sql) in queries)
            {
                using var command = source.CreateCommand();
                command.CommandText = sql;
                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                var items = new List<Dictionary<string, object?>>();
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var item = new Dictionary<string, object?>(StringComparer.Ordinal);
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        var value = reader.GetValue(column);
                        item[reader.GetName(column)] = value is DBNull ? null : value;
                    }
                    items.Add(item);
                }
                rows[name] = items;
            }
            await using var backupStream = File.OpenRead(backupPath);
            var journalBackupSha256 = Convert.ToHexString(await SHA256.HashDataAsync(backupStream).ConfigureAwait(false));
            await File.WriteAllTextAsync(Path.Combine(directory, "durable-snapshot.json"),
                JsonSerializer.Serialize(new { journalPath, journalBackupSha256, rows }, DiagnosticJsonOptions))
                .ConfigureAwait(false);
            var snapshot = new
            {
                observedAtUtc = DateTimeOffset.UtcNow,
                failure = failure.ToString(),
                Root,
                journalPath,
                configuredRawIngressRoot = Agent.Services.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.RawIngressRoot,
                sourceType = Source.GetType().AssemblyQualifiedName,
                oldestActiveExecutionKey = await Source.ReadOldestActiveExecutionKeyAsync(CancellationToken.None).ConfigureAwait(false),
                hostedServices = Agent.Services.GetServices<IHostedService>().Select(service => new
                {
                    type = service.GetType().AssemblyQualifiedName,
                    executeTaskStatus = (service as BackgroundService)?.ExecuteTask?.Status.ToString(),
                    executeTaskException = (service as BackgroundService)?.ExecuteTask?.Exception?.ToString()
                }).ToArray(),
                admission = Agent.Services.GetRequiredService<CaptureAdmissionCoordinator>().Snapshot,
                fleet = Agent.Services.GetRequiredService<FleetRuntimeState>().Snapshot.Capture,
                processing = Agent.Services.GetRequiredService<CaptureProcessingState>().Snapshot,
                lanes = Agent.Services.GetRequiredService<CaptureLaneState>().Snapshot,
                ingress = Agent.Services.GetRequiredService<RawIngressState>().Snapshot,
                captureTelemetry = Agent.Services.GetRequiredService<ICaptureTelemetryProvider>().GetSnapshot(),
                journalBackupSha256,
                rows
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "snapshot.json"),
                JsonSerializer.Serialize(snapshot, DiagnosticJsonOptions)).ConfigureAwait(false);
            Console.WriteLine($"Issue538 failure diagnostics retained before isolated cleanup: {directory}");
        }
#pragma warning disable CA1031 // A failed diagnostic must preserve the original acceptance failure.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Console.WriteLine($"Issue538 diagnostic capture failed ({directory}): {exception}");
        }
    }

    internal async Task ResumeCaptureAsync(CancellationToken cancellationToken)
    {
        var admission = Agent.Services.GetRequiredService<CaptureAdmissionCoordinator>();
        var resumed = await admission.ResumeAsync($"issue538-resume-{Guid.NewGuid():N}", null,
            "issue538-test-owner", "measured-live-capture", cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Running, resumed.State);
    }

    internal async Task RunCycleAsync(CancellationToken cancellationToken = default)
    {
        Clock.Advance(TimeSpan.FromSeconds(2));
        await _exporter.RunCycleOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void RestartExporter()
    {
        _exporter.Dispose();
        _exporter = CreateExporter();
    }

    internal async Task DrainAsync(int maximumCycles = 1000)
    {
        for (var cycle = 0; cycle < maximumCycles; cycle++)
        {
            await RunCycleAsync().ConfigureAwait(false);
            var backlog = await Outbox.ReadBacklogAsync(Root, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, backlog.QuarantinedCount, "Transient dependencies must never terminally reject units.");
            Assert.AreEqual(0, backlog.ProjectionRejectedEvents, "The real source must project without silent loss.");
            if (backlog.PendingCount + backlog.RetryCount == 0)
            {
                return;
            }
        }
        Assert.Fail($"Evidence did not drain in {maximumCycles} bounded cycles: {State.Snapshot}");
    }

    internal IReadOnlyList<EdgeEvidenceRow> ReadLocalRows()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Root, "evidence", "execution-evidence-outbox.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record_id, payload, status FROM execution_evidence_units ORDER BY record_id";
        using var reader = command.ExecuteReader();
        var result = new List<EdgeEvidenceRow>();
        while (reader.Read())
        {
            var bytes = (byte[])reader.GetValue(1);
            var envelope = GraphExecutionEvidenceJson.ParseEnvelope(bytes);
            Assert.IsNotNull(envelope.Value, envelope.Validation.ToString());
            result.Add(new(reader.GetInt64(0), bytes, envelope.Value, reader.GetString(2)));
        }
        return result;
    }

    /// <summary>
    /// Re-observes an artifact from a real execution. Only observation time, sequence, and evidence ID change;
    /// revision, plan, capture, artifact and lineage remain the actual source projection.
    /// </summary>
    internal async Task EnlistAvailabilityHistoryAsync(int count, string series)
    {
        var source = _availabilityTemplate ??= ReadLocalRows()
            .First(row => row.Envelope.Kind == ExecutionEvidenceBodyKind.ArtifactAvailability);
        var origin = source.Envelope.Origin;
        var cursor = await Outbox.ReadDiscoveryCursorAsync(Root, CancellationToken.None).ConfigureAwait(false);
        var units = Enumerable.Range(0, count).Select(index =>
        {
            var key = $"issue538-availability:{series}:{index}";
            return new ExecutionEvidenceEnlistmentUnit(ExecutionEvidenceBodyKind.ArtifactAvailability, key, sequence =>
            {
                var observedAt = source.Envelope.ProducedAtUtc.AddTicks(index + 1);
                var envelope = GraphExecutionEvidenceJson.Seal(source.Envelope with
                {
                    EvidenceId = LocalExecutionEvidenceOriginProvider.DeriveIdentity($"{origin.IdentitySha256}\n{key}"),
                    OriginSequence = sequence,
                    ProducedAtUtc = observedAt,
                    Availability = source.Envelope.Availability! with
                    {
                        Observations = source.Envelope.Availability!.Observations.Take(1).Select(observation =>
                            observation with { ObservedAtUtc = observedAt }).ToImmutableArray()
                    }
                });
                return new ExecutionEvidenceSealedUnit(
                    envelope.EvidenceId, GraphExecutionEvidenceJson.Serialize(envelope), envelope.PayloadSha256);
            });
        }).ToArray();
        // Keep each transaction bounded even for the W3M history.
        foreach (var batch in units.Chunk(64))
        {
            var enlisted = await Outbox.EnlistAsync(Root, origin.IdentitySha256,
                source.Envelope.Availability!.ExecutionId, batch, cursor,
                new(50_000, 2L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024,
                    GraphExecutionEvidenceLimits.MaximumEnvelopeBytes), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ExecutionEvidenceEnlistmentDisposition.Enlisted, enlisted.Disposition);
            Assert.AreEqual(batch.Length, enlisted.EnlistedCount);
        }
    }

    internal static async Task WaitUntilAsync(Func<Task<bool>> predicate, string expectation, int seconds = 60,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        while (!await predicate().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsLessThan(TimeSpan.FromSeconds(seconds), watch.Elapsed, $"Timed out waiting for {expectation}.");
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private ExecutionEvidenceExportService CreateExporter() => new(
        Outbox, Transport, Agent.Services.GetRequiredService<IExecutionEvidenceOriginProvider>(), Source,
        Agent.Services.GetRequiredService<ICameraAgentConfigurationAccessor>(), State, _telemetry,
        new ExecutionEvidenceExportWakeup(), Agent.Services.GetRequiredService<StoragePressureState>(),
        _options, Clock, NullLogger<ExecutionEvidenceExportService>.Instance);

    public async ValueTask DisposeAsync()
    {
        _exporter.Dispose();
        _telemetry.Dispose();
        _clients.Dispose();
        Outbox.Dispose();
        Metrics.Dispose();
        await Agent.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed record EdgeEvidenceRow(long Id, byte[] Bytes, ExecutionEvidenceEnvelopeV1 Envelope, string Status);

internal sealed class EvidenceClock : TimeProvider
{
    private long _offsetTicks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));
    internal void Advance(TimeSpan interval) => Interlocked.Add(ref _offsetTicks, interval.Ticks);
}

internal enum EvidenceHttpFault { None, Unavailable, LoseCommittedResponse, WrongAcknowledgementHash, StaleOriginAcknowledgement }

internal sealed record EvidenceHttpExchange(
    string Route, byte[] Request, HttpStatusCode Status, byte[] Response, double Milliseconds);

internal sealed class EvidenceHttpHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private int _inFlight;
    private int _maximumInFlight;
    internal EvidenceHttpFault Fault { get; set; }
    internal ConcurrentQueue<EvidenceHttpExchange> Exchanges { get; } = new();
    internal int MaximumInFlight => Volatile.Read(ref _maximumInFlight);
    internal Action<HttpRequestMessage>? ModifyRequest { get; set; }
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SendOverride { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _inFlight);
        InterlockedExtensions.Maximum(ref _maximumInFlight, active);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var route = request.RequestUri!.AbsolutePath;
            var payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var submit = route == "/api/device/execution-evidence";
            if (submit)
            {
                Assert.AreEqual(ExecutionEvidenceBatchCodec.MediaType, request.Content.Headers.ContentType!.MediaType);
                Assert.AreEqual((byte)'\n', payload[^1]);
                Assert.IsLessThanOrEqualTo(64, ExecutionEvidenceBatchCodec.Decode(payload, 256).Count);
            }
            ModifyRequest?.Invoke(request);
            var response = Fault == EvidenceHttpFault.Unavailable
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) }
                : SendOverride is { } send
                    ? await send(request, cancellationToken).ConfigureAwait(false)
                    : await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            Exchanges.Enqueue(new(route, payload, response.StatusCode, responseBytes,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            if (submit && response.IsSuccessStatusCode)
            {
                if (Fault == EvidenceHttpFault.LoseCommittedResponse)
                {
                    Fault = EvidenceHttpFault.None;
                    response.Dispose();
                    throw new HttpRequestException("Test fault: receiver committed, response lost before sender receives it.");
                }
                if (Fault is EvidenceHttpFault.WrongAcknowledgementHash or EvidenceHttpFault.StaleOriginAcknowledgement)
                {
                    var feedback = GraphExecutionEvidenceJson.ParseFeedback(responseBytes).Value!;
                    feedback = Fault == EvidenceHttpFault.WrongAcknowledgementHash
                        ? feedback with
                        {
                            Facts = feedback.Facts.Select(fact => fact.Kind == ExecutionEvidenceFactKind.Acknowledged
                                ? fact with { PayloadSha256 = new string('A', 64) } : fact).ToImmutableArray()
                        }
                        : feedback with { OriginIdentitySha256 = new string('B', 64) };
                    response.Content.Dispose();
                    response.Content = new ByteArrayContent(GraphExecutionEvidenceJson.Serialize(feedback));
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }
            }
            return response;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private static class InterlockedExtensions
    {
        internal static void Maximum(ref int target, int value)
        {
            var previous = Volatile.Read(ref target);
            while (previous < value)
            {
                var observed = Interlocked.CompareExchange(ref target, value, previous);
                if (observed == previous) { return; }
                previous = observed;
            }
        }
    }
}

internal sealed class EvidenceHttpClientFactory(EvidenceHttpHandler handler, Uri baseAddress) : IHttpClientFactory, IDisposable
{
    private readonly ConcurrentBag<HttpClient> _clients = [];
    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = baseAddress };
        _clients.Add(client);
        return client;
    }
    public void Dispose()
    {
        foreach (var client in _clients) { client.Dispose(); }
        handler.Dispose();
    }
}
