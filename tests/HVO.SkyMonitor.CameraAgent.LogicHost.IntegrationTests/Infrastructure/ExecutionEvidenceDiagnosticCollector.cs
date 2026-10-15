using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

/// <summary>Opt-in diagnostic retention only. No production artifact, ingress decision or capture is changed.</summary>
internal sealed class ExecutionEvidenceDiagnosticCollector : IDisposable
{
    internal const string EnvironmentVariable = "HVO_ISSUE_538_DIAGNOSTICS";
    internal const string TraceDirectoryEnvironmentVariable = "HVO_ISSUE_538_TRACE_DIR";
    internal const string TraceSamplingEnvironmentVariable = "DOTNET_EventPipeThreadSamplingRate";
    internal const string TraceProfile = "dotnet-sampled-thread-time";
    internal const string TraceProviders = "Microsoft-Windows-DotNETRuntime:0x1000020039:5,Microsoft-DotNETCore-SampleProfiler:0xF00000000000:4,HVO-Issue538-ExecutionEvidence-Diagnostics:0xFFFFFFFFFFFFFFFF:5";
    private const int TraceAttachSeconds = 90;
    private const int TraceDetachSeconds = 120;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int MaximumPendingFrames = 512;
    private const int MaximumSelectedFrames = 4 * (5 + 30);
    private const int MaximumRuntimeSamples = 24_000;
    private static readonly string[] StageKeys =
    [
        "stellarProjectionMilliseconds", "stellarProjectionProcessCpuMilliseconds",
        "stellarPredictionMilliseconds", "stellarPredictionProcessCpuMilliseconds",
        "stellarReferenceSceneMilliseconds", "stellarReferenceSceneProcessCpuMilliseconds",
        "stellarRenderMilliseconds", "stellarRenderProcessCpuMilliseconds"
    ];
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ProducerFrameDiagnostic> _pending = [];
    private readonly Queue<Guid> _pendingOrder = new();
    private readonly List<SelectedCaptureDiagnostic> _selected = [];
    private readonly List<RuntimeDiagnosticSample> _samples = [];
    private readonly Timer _sampler;
    private long _evictedFrames;
    private long _droppedSamples;
    private string? _traceDirectory;
    private string? _traceRunId;
    private SourceEvidence? _traceSource;
    private string? _traceRepeatCommand;
    private bool _traceProviderEnabled;
    private bool _traceAttached;
    private bool _traceDetached;
    private readonly List<string> _traceFailures = [];

    internal static bool IsTraceRequested => !string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable(TraceDirectoryEnvironmentVariable));

    internal static string DiagnosticRepeatCommand(string command)
    {
        static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        var prefix = EnvironmentVariable + "=1 ";
        if (IsTraceRequested)
        {
            prefix += TraceDirectoryEnvironmentVariable + "=" + Quote(Environment.GetEnvironmentVariable(TraceDirectoryEnvironmentVariable)!) + " " +
                TraceSamplingEnvironmentVariable + "=0xA ";
        }
        return prefix + command;
    }

    internal static bool IsEnabled => string.Equals(
        Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);

    internal ExecutionEvidenceDiagnosticCollector()
    {
        SampleRuntime();
        _sampler = new Timer(_ => SampleRuntime(), null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
    }

    internal async Task AttachTraceAsync(SourceEvidence source, string repeatCommand)
    {
        if (!IsTraceRequested) { return; }
        var directory = Environment.GetEnvironmentVariable(TraceDirectoryEnvironmentVariable)!;
        Assert.IsTrue(IsEnabled, "EventPipe attach requires explicit diagnostic mode.");
        Assert.IsTrue(Path.IsPathFullyQualified(directory) && Directory.Exists(directory), "The external runner must create a fresh absolute handshake directory.");
        Assert.AreEqual("0xA", Environment.GetEnvironmentVariable(TraceSamplingEnvironmentVariable), "Request ten milliseconds using CoreCLR hexadecimal config encoding; observed spacing is reported separately.");
        Assert.IsFalse(new[] { "pid", "enabled", "attached", "detached", "failed" }.Any(name => File.Exists(Path.Combine(directory, name + ".json"))),
            "Never reuse an EventPipe handshake directory.");
        _traceDirectory = directory;
        _traceRunId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _traceSource = source;
        _traceRepeatCommand = repeatCommand;
        await WriteTraceNoteAsync("pid", new
        {
            Profile = TraceProfile,
            Providers = TraceProviders,
            BufferMegabytes = 128,
            MaximumTraceSeconds = 1200,
            MaximumTraceBytes = 1_073_741_824,
            AttachSeconds = TraceAttachSeconds,
            DetachSeconds = TraceDetachSeconds,
            RequestedThreadSamplingMilliseconds = 10,
            StoppingProvider = TraceEventSource.ProviderName,
            StoppingEvent = "MeasurementComplete"
        }).ConfigureAwait(false);
        try
        {
            await WaitForTraceMarkerAsync("attached", TimeSpan.FromSeconds(TraceAttachSeconds)).ConfigureAwait(false);
            Assert.IsTrue(_traceProviderEnabled, "The diagnostic EventSource must be enabled before releasing capture setup.");
            _traceAttached = true;
            TraceEventSource.Log.Provenance(_traceRunId, source.Sha, source.DirtyFingerprintSha256, repeatCommand);
            TraceEventSource.Log.ClockSync(_traceRunId, DateTimeOffset.UtcNow.UtcTicks, Stopwatch.GetTimestamp(), Stopwatch.Frequency, "attached");
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _traceFailures.Add("attach:" + exception);
            await WriteTraceNoteAsync("attach-failure", new { Failure = exception.ToString() }).ConfigureAwait(false);
            throw;
        }
    }

    // Cleanup must retain the original workload assertion, including when attachment or a phase failed.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Diagnostic detach records failure without replacing the original workload exception.")]
    internal async Task<bool> DetachTraceAsync(bool workloadCompleted)
    {
        if (_traceDirectory is null) { return true; }
        try
        {
            if (!TraceEventSource.Log.IsEnabled()) { throw new IOException("The diagnostic provider is no longer enabled; trace coverage ended early or never attached."); }
            await WriteTraceNoteAsync("completion-requested", new { WorkloadCompleted = workloadCompleted }).ConfigureAwait(false);
            TraceEventSource.Log.ClockSync(_traceRunId!, DateTimeOffset.UtcNow.UtcTicks, Stopwatch.GetTimestamp(), Stopwatch.Frequency, "completion");
            TraceEventSource.Log.MeasurementComplete(_traceRunId!, workloadCompleted ? "Completed" : "Failed");
            await WaitForTraceMarkerAsync("detached", TimeSpan.FromSeconds(TraceDetachSeconds)).ConfigureAwait(false);
            _traceDetached = true;
            return _traceFailures.Count == 0;
        }
        catch (Exception exception)
        {
            _traceFailures.Add("detach:" + exception);
            try { await WriteTraceNoteAsync("detach-failure", new { Failure = exception.ToString() }).ConfigureAwait(false); }
            catch (Exception persistenceFailure) { _traceFailures.Add("detach-failure-persistence:" + persistenceFailure); }
            return false;
        }
    }

    private async Task WaitForTraceMarkerAsync(string marker, TimeSpan limit)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < limit)
        {
            if (marker == "attached" && !_traceProviderEnabled && TraceEventSource.Log.IsEnabled())
            {
                _traceProviderEnabled = true;
                await WriteTraceNoteAsync("enabled", new { ProviderEnabled = true }).ConfigureAwait(false);
            }
            if (File.Exists(Path.Combine(_traceDirectory!, "failed.json")))
            {
                using var failed = await ReadTraceNoteAsync("failed").ConfigureAwait(false);
                throw new IOException("The external trace runner failed: " + failed.RootElement.GetProperty("details"));
            }
            if (File.Exists(Path.Combine(_traceDirectory!, marker + ".json")))
            {
                using var note = await ReadTraceNoteAsync(marker).ConfigureAwait(false);
                if (!note.RootElement.GetProperty("details").GetProperty("success").GetBoolean())
                {
                    throw new IOException("The external collector reported an unsuccessful " + marker + ": " + note.RootElement.GetProperty("details"));
                }
                return;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        throw new TimeoutException($"EventPipe {marker} handshake exceeded {limit.TotalSeconds} seconds.");
    }

    private async Task<JsonDocument> ReadTraceNoteAsync(string name)
    {
        var path = Path.Combine(_traceDirectory!, name + ".json");
        if (new FileInfo(path).Length > 65_536) { throw new IOException("Oversize trace handshake marker."); }
        var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.GetProperty("runId").GetString() == _traceRunId && root.GetProperty("testHostPid").GetInt32() == Environment.ProcessId &&
            root.GetProperty("source").GetProperty("sha").GetString() == _traceSource!.Sha &&
            root.GetProperty("source").GetProperty("dirty").GetBoolean() == _traceSource.Dirty &&
            root.GetProperty("source").GetProperty("dirtyFingerprintSha256").GetString() == _traceSource.DirtyFingerprintSha256 &&
            root.GetProperty("repeatCommand").GetString() == _traceRepeatCommand &&
            root.GetProperty("mode").GetString() == "DiagnosticOnly-NotAcceptance") { return document; }
        document.Dispose();
        throw new IOException("Trace marker nonce/PID/source/command does not match this diagnostic invocation.");
    }

    private async Task WriteTraceNoteAsync(string name, object details)
    {
        var path = Path.Combine(_traceDirectory!, name + ".json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = "hvo-issue538-eventpipe-handshake-v1",
            Mode = "DiagnosticOnly-NotAcceptance",
            DiagnosticModeEnabled = true,
            Source = _traceSource,
            RepeatCommand = _traceRepeatCommand,
            TestHostPid = Environment.ProcessId,
            RunId = _traceRunId,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Details = details
        }, Json);
        await using (var stream = new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(bytes).ConfigureAwait(false);
        }
        File.Move(path + ".tmp", path);
    }

    internal void ConfigureIsolatedHost(IServiceCollection services)
    {
        // All concrete RawCaptureIngress registrations and its other interfaces keep the original singleton.
        services.RemoveAll<IRawCaptureIngress>();
        services.AddSingleton<IRawCaptureIngress>(provider =>
            new ObservedIngress(provider.GetRequiredService<RawCaptureIngress>(), this));
    }

    private void Observe(CaptureLoopSubmission submission, RawCaptureReceipt receipt,
        DateTimeOffset enteredUtc, DateTimeOffset completedUtc)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        var extra = submission.Result.Frame?.Metadata.Extra;
        foreach (var key in StageKeys)
        {
            if (extra is not null && extra.TryGetValue(key, out var value) &&
                value.Length <= 128 && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                double.IsFinite(number))
            {
                values.Add(key, value); // Exact producer strings, not reconstructed or inferred timings.
            }
            else
            {
                missing.Add(key);
            }
        }
        var descriptor = receipt.Manifest.Descriptor;
        var row = new ProducerFrameDiagnostic(descriptor.Capture.CaptureId, descriptor.Capture.CaptureSequence,
            submission.CaptureStartedUtc, descriptor.CycleEvidence?.ModuleCallStartedUtc, enteredUtc, completedUtc,
            submission.Result.ProcessingLatency.TotalMilliseconds, receipt.CommittedManifestSha256,
            descriptor.Artifact.ChecksumSha256, values,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values))), missing);
        lock (_gate)
        {
            if (_pending.ContainsKey(row.CaptureId)) { return; }
            while (_pending.Count >= MaximumPendingFrames && _pendingOrder.TryDequeue(out var oldest))
            {
                if (_pending.Remove(oldest)) { _evictedFrames++; }
            }
            _pending.Add(row.CaptureId, row);
            _pendingOrder.Enqueue(row.CaptureId);
        }
        if (_traceAttached)
        {
            TraceEventSource.Log.CaptureCommitted(_traceRunId!, row.CaptureId.ToString(), row.CaptureSequence,
                row.ModuleStartedUtc.UtcTicks, row.IngressCompletedUtc.UtcTicks, row.PayloadSha256);
        }
    }

    internal void RetainCapture(string phase, string role, int ordinal, RawUploadEvidence raw)
    {
        lock (_gate)
        {
            if (_selected.Count >= MaximumSelectedFrames || _selected.Any(item => item.CaptureId == raw.CaptureId))
            {
                throw new InvalidOperationException("Diagnostic selection must remain the declared distinct 4 x (5 + 30) captures.");
            }
            _pending.Remove(raw.CaptureId, out var producer);
            if (producer is not null)
            {
                Assert.AreEqual(raw.CaptureSequence, producer.CaptureSequence);
                Assert.AreEqual(raw.Sha256, producer.PayloadSha256, ignoreCase: true);
            }
            _selected.Add(new(phase, role, ordinal, raw.CaptureId, raw.CaptureSequence, raw.Sha256, producer));
        }
        if (_traceAttached) { TraceEventSource.Log.CaptureSelected(_traceRunId!, phase, role, ordinal, raw.CaptureId.ToString(), raw.CaptureSequence); }
    }

    private void SampleRuntime()
    {
        lock (_gate)
        {
            if (_samples.Count >= MaximumRuntimeSamples) { _droppedSamples++; return; }
            var missing = new List<string>();
            T? Read<T>(string name, Func<T> read) where T : struct
            {
                try { return read(); }
                catch (NotSupportedException exception)
                {
                    missing.Add(name + ":" + exception.GetType().Name);
                    return null;
                }
            }
            _samples.Add(new(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(),
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                Read("gc-total-pause-ms", () => GC.GetTotalPauseDuration().TotalMilliseconds),
                GC.GetTotalAllocatedBytes(precise: false),
                Read("jit-compilation-ms", () => JitInfo.GetCompilationTime(currentThread: false).TotalMilliseconds),
                Read("jit-methods", () => JitInfo.GetCompiledMethodCount(currentThread: false)),
                Read("jit-il-bytes", () => JitInfo.GetCompiledILBytes(currentThread: false)), missing));
        }
    }

    internal object Snapshot(string repeatCommand)
    {
        SampleRuntime();
        lock (_gate)
        {
            var samples = _samples.ToArray();
            return new
            {
                Mode = "DiagnosticOnly-NotAcceptance",
                DiagnosticModeEnabled = true,
                EnvironmentVariable,
                EnvironmentValue = "1",
                RepeatCommand = repeatCommand,
                SelectedDiagnosticMode = IsTraceRequested ? "EventPipeAndProducerCounters" : "ProducerCountersOnly",
                EventPipe = new
                {
                    Requested = IsTraceRequested,
                    Directory = _traceDirectory,
                    RunId = _traceRunId,
                    ProviderEnabled = _traceProviderEnabled,
                    Attached = _traceAttached,
                    Detached = _traceDetached,
                    Failures = _traceFailures.ToArray(),
                    Profile = TraceProfile,
                    Providers = TraceProviders,
                    RequestedThreadSamplingMilliseconds = 10,
                    StopwatchFrequency = Stopwatch.Frequency,
                    AttachSeconds = TraceAttachSeconds,
                    DetachSeconds = TraceDetachSeconds,
                    Attribution = "Managed thread-time stack samples are not on-CPU kernel samples. Method/tier/GC visibility and loss require the finalized trace reader report; provider enablement alone proves neither."
                },
                ProducerSource = "VirtualSkyCameraModule.CaptureCoreAsync FrameMetadata.Extra; copied at real IRawCaptureIngress.AcceptAsync boundary, bound to its returned committed manifest.",
                RetentionGap = "CaptureLaneEnvelopeSerializer removes Frame; FrameReconstructor creates metadata without Extra; FileSystemFrameStorageService indexes Metadata=null. No existing durable stage-metadata path was found.",
                Bounds = new { MaximumPendingFrames, MaximumSelectedFrames, MaximumRuntimeSamples, RuntimeSamplePeriodMilliseconds = 50 },
                EvictedUnselectedFrames = _evictedFrames,
                DroppedRuntimeSamples = _droppedSamples,
                SelectedCaptureCount = _selected.Count,
                SelectionCounts = _selected.GroupBy(item => new { item.Phase, item.Role }).Select(group => new
                {
                    group.Key.Phase,
                    group.Key.Role,
                    Count = group.Count()
                }).ToArray(),
                SelectedCaptures = _selected.Select(item =>
                {
                    var producer = item.Producer;
                    var before = producer is null ? -1 : Array.FindLastIndex(samples,
                        sample => sample.ObservedAtUtc <= producer.ModuleStartedUtc);
                    var after = producer is null ? -1 : Array.FindIndex(samples,
                        sample => sample.ObservedAtUtc >= producer.IngressCompletedUtc);
                    return new
                    {
                        Selection = item,
                        RuntimeWindowBeforeIndex = before,
                        RuntimeWindowAfterIndex = after,
                        MissingDiagnostics = new[]
                        {
                            producer is null ? "producer-frame-metadata-not-retained" : null,
                            before < 0 ? "runtime-sample-before-module-start-unavailable" : null,
                            after < 0 ? "runtime-sample-after-ingress-unavailable" : null
                        }.Where(value => value is not null).ToArray()
                    };
                }).ToArray(),
                RuntimeSamples = samples,
                Limitations = new[]
                {
                    "GC collection counts, total pause duration, allocation and JIT totals are process-wide cumulative counters, sampled with UTC and monotonic timestamps. Both hosts and the harness contribute.",
                    "Sample indices bracket the capture interval; they do not prove which capture, method or host caused compilation or collection. Exact GC pause intervals, method names and compilation tiers are unavailable.",
                    "Zero means no observed counter increase, not proof of absence. Unsupported APIs, missing producer keys and buffer limits are reported explicitly.",
                    "The ingress wrapper copies only eight existing numeric metadata strings after the production commit. It retains no image buffers and never changes the returned receipt or source frame.",
                    "Sampling, dictionary retention and serialization add overhead. This mode cannot satisfy performance acceptance. No captures, preconditioning, warmups, deadlines or thresholds are added."
                }
            };
        }
    }

    public void Dispose() => _sampler.Dispose();

    [EventSource(Name = TraceEventSource.ProviderName)]
    private sealed class TraceEventSource : EventSource
    {
        internal const string ProviderName = "HVO-Issue538-ExecutionEvidence-Diagnostics";
        internal static readonly TraceEventSource Log = new();

        [Event(1, Level = EventLevel.Informational)]
        public void MeasurementComplete(string runId, string outcome) => WriteEvent(1, runId, outcome);

        [Event(2, Level = EventLevel.Informational)]
        public void ClockSync(string runId, long utcTicks, long monotonicTicks, long frequency, string stage)
            => WriteEvent(2, runId, utcTicks, monotonicTicks, frequency, stage);

        [Event(3, Level = EventLevel.Informational)]
        public void CaptureCommitted(string runId, string captureId, long sequence, long moduleUtcTicks, long ingressUtcTicks, string payloadSha256)
            => WriteEvent(3, runId, captureId, sequence, moduleUtcTicks, ingressUtcTicks, payloadSha256);

        [Event(4, Level = EventLevel.Informational)]
        public void CaptureSelected(string runId, string phase, string role, int ordinal, string captureId, long sequence)
            => WriteEvent(4, runId, phase, role, ordinal, captureId, sequence);

        [Event(5, Level = EventLevel.Informational)]
        public void Provenance(string runId, string sourceSha, string dirtyFingerprintSha256, string repeatCommand)
            => WriteEvent(5, runId, sourceSha, dirtyFingerprintSha256, repeatCommand);
    }

    private sealed class ObservedIngress(RawCaptureIngress inner, ExecutionEvidenceDiagnosticCollector owner)
        : IRawCaptureIngress, IRawIngressRecoveryControl
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);

        public async ValueTask<RawCaptureReceipt?> AcceptAsync(CameraModuleConfig configuration,
            CaptureLoopSubmission submission, CancellationToken cancellationToken)
        {
            var enteredUtc = DateTimeOffset.UtcNow;
            var receipt = await inner.AcceptAsync(configuration, submission, cancellationToken).ConfigureAwait(false);
            if (receipt is not null) { owner.Observe(submission, receipt, enteredUtc, DateTimeOffset.UtcNow); }
            return receipt;
        }

        ValueTask<RawCapturePublicationState> IRawCaptureIngress.GetPublicationStateAsync(
            CameraModuleConfig configuration, CaptureLoopSubmission submission, CancellationToken cancellationToken)
            => ((IRawCaptureIngress)inner).GetPublicationStateAsync(configuration, submission, cancellationToken);

        ValueTask IRawCaptureIngress.BindRecoveredLiveExecutionsAsync(
            CameraModuleConfig configuration, CancellationToken cancellationToken)
            => ((IRawCaptureIngress)inner).BindRecoveredLiveExecutionsAsync(configuration, cancellationToken);

        public void InvalidateEvidence() => inner.InvalidateEvidence();
    }
}

internal sealed record ProducerFrameDiagnostic(Guid CaptureId, long CaptureSequence, DateTimeOffset ModuleStartedUtc,
    DateTimeOffset? PersistedModuleStartedUtc, DateTimeOffset IngressEnteredUtc, DateTimeOffset IngressCompletedUtc,
    double ModuleMilliseconds, string CommittedManifestSha256, string PayloadSha256,
    IReadOnlyDictionary<string, string> StageMetadata, string StageMetadataSubsetSha256, IReadOnlyList<string> MissingStageMetadata);

internal sealed record SelectedCaptureDiagnostic(string Phase, string Role, int Ordinal, Guid CaptureId,
    long CaptureSequence, string PayloadSha256, ProducerFrameDiagnostic? Producer);

internal sealed record RuntimeDiagnosticSample(DateTimeOffset ObservedAtUtc, long StopwatchTimestamp,
    int Generation0Collections, int Generation1Collections, int Generation2Collections, double? TotalGcPauseMilliseconds,
    long TotalAllocatedBytes, double? TotalJitCompilationMilliseconds, long? TotalJitCompiledMethods,
    long? TotalJitCompiledIlBytes, IReadOnlyList<string> MissingDiagnostics);
