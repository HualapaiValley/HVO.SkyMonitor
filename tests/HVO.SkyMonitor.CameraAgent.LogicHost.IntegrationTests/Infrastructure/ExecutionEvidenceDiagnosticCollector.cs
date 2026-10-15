using System.Diagnostics;
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

    internal static bool IsEnabled => string.Equals(
        Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);

    internal ExecutionEvidenceDiagnosticCollector()
    {
        SampleRuntime();
        _sampler = new Timer(_ => SampleRuntime(), null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
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
