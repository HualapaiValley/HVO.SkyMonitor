using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

internal sealed class VirtualSkyOutageDiagnostics(IServiceProvider services, string storageRoot,
    Action<object>? snapshotReadCompleted = null)
{
    internal const int SampleLimit = 16;
    internal const int ManifestByteLimit = 256 * 1024;
    internal const int OutputLimit = 32 * 1024;
    private static readonly TimeSpan CollectionBudget = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    public string Stage { get; set; } = "initial capture";
    public DateTimeOffset? CheckpointUtc { get; set; }
    public IReadOnlySet<Guid> CheckpointCaptureIds { get; set; } = new HashSet<Guid>();
    public IReadOnlyList<StoredFrameReference>? LastListedFrames { get; set; }
    public bool? LastListCapReached { get; set; }

    internal enum ManifestState { Valid, Invalid, MissingManifest, TooLarge, Unavailable }
    internal sealed record Observation(Guid? CaptureId, long? CaptureSequence, FrameArtifactRole ListedRole,
        FrameArtifactRole? ManifestRole, DateTimeOffset? ExposureStartedUtc, bool? AtCheckpoint, bool? AfterCheckpoint,
        ManifestState Manifest, bool PayloadExists, CaptureMeteringOutcome? Metering, DateTimeOffset? ListedTimestampUtc = null);

    public string Describe()
    {
        var budget = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;
        var sections = new List<string>
        {
            "standalone outage timeout snapshot",
            Section("checkpoint", () => Json(new
            {
                Stage, CheckpointUtc, ObservedUtc = now, CheckpointCount = CheckpointCaptureIds.Count,
                CheckpointCaptureIds = CheckpointCaptureIds.Order().Take(SampleLimit).ToArray(),
                SampleLimit, ManifestByteLimit, CollectionBudgetSeconds = 2
            })),
            Section("capture loop/metering/admission", () =>
            {
                var task = services.GetServices<IHostedService>().OfType<CameraCaptureService>().SingleOrDefault()?.ExecuteTask;
                var latest = services.GetRequiredService<ILatestFrameAccessor>();
                latest.TryGetSnapshot(FrameArtifactRole.Raw, out var raw);
                latest.TryGetSnapshot(FrameArtifactRole.Preview, out var preview);
                return Json(new
                {
                    LoopTask = task?.Status, FaultType = task?.Exception?.GetBaseException().GetType().Name,
                    LatestRawUtc = raw?.TimestampUtc, LatestPreviewUtc = preview?.TimestampUtc,
                    Admission = services.GetRequiredService<CaptureAdmissionCoordinator>().Snapshot,
                    Metering = "latest durable metering outcome and times are in journal and storage observations"
                });
            }),
            Section("capture telemetry", () => ReadSnapshot(budget,
                services.GetRequiredService<CaptureTelemetrySink>(), static (telemetry, remaining) =>
                {
                    if (!telemetry.TryGetLatest(remaining, out var sample)) return "unavailable(timeout)";
                    return Json(sample is null ? null : new { sample.StartedUtc, sample.FrameStored, sample.Exposure, sample.ProcessingLatency, sample.LoopDuration });
                })),
            Section("fleet capture", () => ReadSnapshot(budget,
                services.GetRequiredService<FleetRuntimeState>(), static (state, remaining) =>
                {
                    if (!state.TrySnapshot(remaining, out var snapshot)) return "unavailable(timeout)";
                    var fleet = snapshot.Capture;
                    return Json(new { fleet.Availability, fleet.LastSucceededUtc, fleet.LastFailedUtc, fleet.LastRecoveredUtc });
                })),
            Section("ingress queues/bytes/oldest age", () =>
            {
                var ingress = services.GetRequiredService<RawIngressState>().Snapshot;
                var lanes = services.GetRequiredService<CaptureLaneState>().Snapshot;
                return Json(new
                {
                    ingress.Availability, ingress.PendingCount, ingress.PendingBytes, ingress.QuarantineCount,
                    ingress.QuarantineBytes, ingress.OldestPendingUtc, ingress.EvaluatedUtc,
                    OldestPendingSeconds = Age(now, ingress.OldestPendingUtc),
                    LaneCount = lanes.Lanes.Count, LaneSampleLimit = 8,
                    Lanes = lanes.Lanes.Take(8).Select(lane => new
                    {
                        lane.Required, lane.PendingCount, lane.PendingBytes, lane.LeasedCount, lane.RetryCount,
                        lane.QuarantineCount, lane.PressureLevel, lane.OldestPendingUtc
                    }).ToArray()
                });
            }),
            Section("durable processing/publication", () => ReadSnapshot(budget,
                services.GetRequiredService<CaptureProcessingState>(), static (state, remaining) =>
                {
                    if (!state.TrySnapshot(remaining, out var p)) return "unavailable(timeout)";
                    return Json(new
                    {
                        p.Availability, p.PendingCount, p.LeasedCount, p.RetryCount, p.TerminalCount,
                        p.ProcessingQuarantineCount, p.MissingProductCount, p.DurableStateUnavailable, p.ReconciliationFailed,
                        p.OldestPendingUtc, p.EvaluatedUtc, OldestPendingSeconds = Age(DateTimeOffset.UtcNow, p.OldestPendingUtc)
                    });
                })),
            Section("journal capture sequence/publication/metering", () => ReadJournal(budget)),
            Section("storage list/manifest filtering", () =>
            {
                // Reuse the exact acceptance-list result; never take its index lock again after a timeout.
                var listed = LastListedFrames;
                var observed = new List<Observation>();
                if (listed is not null)
                {
                    foreach (var item in listed.OrderByDescending(item => item.TimestampUtc).Take(SampleLimit))
                    {
                        if (budget.Elapsed >= CollectionBudget) break;
                        observed.Add(ReadObservation(item));
                    }
                }
                return FormatStorage(listed?.Count, observed, LastListCapReached);
            })
        };
        var result = string.Join(Environment.NewLine, sections) + $"\ncollectionElapsedMs={budget.Elapsed.TotalMilliseconds:F0}; budgetReached={budget.Elapsed >= CollectionBudget}";
        return result.Length <= OutputLimit ? result : result[..(OutputLimit - 24)] + "\n<snapshot truncated>";
    }

    private string ReadSnapshot<T>(Stopwatch budget, T state, Func<T, TimeSpan, string> read)
    {
        var remaining = CollectionBudget - budget.Elapsed;
        if (remaining <= TimeSpan.Zero) return "unavailable(timeout)";
        // The source bounds acquisition of its real gate. Collection creates no background waiter.
        return Read(() =>
        {
            try { return read(state, remaining); }
            finally { snapshotReadCompleted?.Invoke(state!); }
        });
    }

    internal static string Section(string label, Func<string> read) => label + ": " + Read(read);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Timeout diagnostics must preserve the original assertion if a snapshot provider fails; only the exception type is emitted.")]
    private static string Read(Func<string> read)
    {
        try { return read(); }
        catch (Exception error) { return "unavailable(" + error.GetType().Name + ")"; }
    }

    internal static string FormatStorage(int? listedCount, IEnumerable<Observation> observations, bool? listCapReached = null) => Json(new
    {
        ListedCount = listedCount,
        AcceptanceListCapPerDate = 10_000,
        ListCapReached = listCapReached,
        SampleLimit,
        Observations = observations.Take(SampleLimit).ToArray()
    });

    private Observation ReadObservation(StoredFrameReference item)
    {
        var payload = File.Exists(item.AbsolutePath);
        try
        {
            using var stream = File.OpenRead(Path.ChangeExtension(item.AbsolutePath, ".json"));
            if (stream.Length > ManifestByteLimit) return Empty(ManifestState.TooLarge);
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            var parsed = CaptureContractJson.ParseManifest(bytes);
            var manifest = parsed.Document?.Manifest;
            if (!parsed.IsValid || manifest is null) return Empty(parsed.IsValid ? ManifestState.MissingManifest : ManifestState.Invalid);
            var d = manifest.Descriptor;
            return new(d.Capture.CaptureId, d.Capture.CaptureSequence, item.Role, d.Artifact.Role, d.Timing.ExposureStartedUtc,
                CheckpointUtc is null ? null : CheckpointCaptureIds.Contains(d.Capture.CaptureId), CheckpointUtc is { } checkpoint ? d.Timing.ExposureStartedUtc > checkpoint : null,
                ManifestState.Valid, payload, d.CycleEvidence?.Metering?.Outcome, item.TimestampUtc);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return Empty(ManifestState.Unavailable); }
        Observation Empty(ManifestState state) => new(null, null, item.Role, null, null, null, null, state, payload, null, item.TimestampUtc);
    }

    private string ReadJournal(Stopwatch budget)
    {
        if (budget.Elapsed >= CollectionBudget) return "<collection budget reached>";
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(storageRoot, "journal", "raw-ingress.db"),
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 1,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "SELECT COALESCE(MAX(last_sequence), 0) FROM raw_capture_sequences;";
        var assignedWatermark = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = """
            SELECT c.capture_id, c.capture_sequence, c.exposure_started_unix_ms, c.durable_ingress_unix_ms,
                   c.committed_unix_ms, c.state = 'committed',
                   EXISTS(SELECT 1 FROM processing_outputs o WHERE o.capture_id = c.capture_id AND o.role = 'Preview'),
                   EXISTS(SELECT 1 FROM processing_outputs o WHERE o.capture_id = c.capture_id AND o.role = 'Preview' AND o.availability_state = 'Available'),
                   CASE WHEN length(c.manifest_json) <= $bytes THEN c.manifest_json ELSE NULL END
            FROM raw_captures c ORDER BY c.capture_sequence DESC LIMIT $take;
            """;
        command.Parameters.AddWithValue("$bytes", ManifestByteLimit);
        command.Parameters.AddWithValue("$take", SampleLimit);
        using var reader = command.ExecuteReader();
        var captures = new List<object>();
        while (budget.Elapsed < CollectionBudget && reader.Read())
        {
            var metering = reader.IsDBNull(8) ? null : CaptureContractJson.ParseManifest((byte[])reader.GetValue(8)).Document?.Manifest?.Descriptor.CycleEvidence?.Metering;
            captures.Add(new
            {
                CaptureId = Guid.Parse(reader.GetString(0)),
                CaptureSequence = reader.GetInt64(1),
                ExposureStartedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                DurableIngressUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                CommittedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                RawCommitted = reader.GetBoolean(5),
                PreviewOutputCommitted = reader.GetBoolean(6),
                PreviewOutputAvailable = reader.GetBoolean(7),
                MeteringOutcome = metering?.Outcome,
                MeteringStartedUtc = metering?.StartedUtc,
                MeteringCompletedUtc = metering?.CompletedUtc
            });
        }
        return Json(new { AssignedWatermark = assignedWatermark, SampleLimit, Captures = captures });
    }

    private static double? Age(DateTimeOffset now, DateTimeOffset? oldest) => oldest is { } value ? Math.Max(0, (now - value).TotalSeconds) : null;
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
