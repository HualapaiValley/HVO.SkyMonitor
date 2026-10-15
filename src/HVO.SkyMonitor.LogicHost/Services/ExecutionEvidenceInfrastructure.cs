using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class ExecutionEvidenceOptions
{
    public const string SectionName = "ExecutionEvidence";
    public const int MaximumBatchUnits = GraphExecutionEvidenceLimits.MaximumFactsPerFeedback / 4;
    // The current exporter budgets envelope bytes; NDJSON separators are additional bytes.
    public const int MaximumRequestBytes = 16 * 1024 * 1024 + MaximumBatchUnits;
    [Range(1, 8)] public int MaximumConcurrentRequests { get; set; } = 2;
    [Range(1, 30)] public int SqlTimeoutSeconds { get; set; } = 5;
    [Range(1, 120)] public int RequestTimeoutSeconds { get; set; } = 30;
    [Range(1, 16)] public int MaximumBindingsPerRegistration { get; set; } = 4;
    [Range(1, 4096)] public int MaximumOriginsPerBinding { get; set; } = 256;
    [Range(1, 1000000)] public int MaximumUnitsPerBinding { get; set; } = 100000;
    [Range(1L, 107374182400L)] public long MaximumStoredBytesPerBinding { get; set; } = 1024L * 1024 * 1024;
    [Range(1, 4096)] public int MaximumConflictsPerBinding { get; set; } = 256;
    [Range(1, 1000000)] public int MaximumObservationsPerBinding { get; set; } = 100000;
    [Range(1, 3600)] public int ReconciliationIntervalSeconds { get; set; } = 60;
}

/// <summary>Separate, small SQL pool; evidence cannot exhaust raw-ingest connections.</summary>
internal sealed class ExecutionEvidenceDatabase : IDisposable, IAsyncDisposable
{
    public ApplicationDbContext Context { get; }

    public ExecutionEvidenceDatabase(ApplicationDbContext applicationContext, IOptions<ExecutionEvidenceOptions> options)
    {
        var connection = new SqlConnectionStringBuilder(applicationContext.Database.GetConnectionString())
        {
            ApplicationName = "HVO.ExecutionEvidence",
            MaxPoolSize = options.Value.MaximumConcurrentRequests + 2,
            MinPoolSize = 0,
            ConnectTimeout = options.Value.SqlTimeoutSeconds,
            Pooling = true
        };
        Context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connection.ConnectionString, sql => sql.CommandTimeout(options.Value.SqlTimeoutSeconds))
            .Options);
    }

    public void Dispose() => Context.Dispose();
    public ValueTask DisposeAsync() => Context.DisposeAsync();
}

internal sealed class ExecutionEvidenceCapacity(IOptions<ExecutionEvidenceOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _requests = new(options.Value.MaximumConcurrentRequests);
    public bool TryEnter() => _requests.Wait(0);
    public void Exit() => _requests.Release();
    public void Dispose() => _requests.Dispose();
}

internal sealed partial class ExecutionEvidenceTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.ExecutionEvidence";
    public const string ActivitySourceName = MeterName;
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly ILogger<ExecutionEvidenceTelemetry>? _logger;
    public static Activity? StartActivity(string name) => ActivitySource.StartActivity(name);
    private readonly Counter<long> _units;
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _latency;
    private readonly Counter<long> _observations;
    public ExecutionEvidenceTelemetry(ILogger<ExecutionEvidenceTelemetry>? logger = null)
    {
        _logger = logger;
        _units = _meter.CreateCounter<long>("hvo.execution_evidence.units");
        _requests = _meter.CreateCounter<long>("hvo.execution_evidence.requests");
        _latency = _meter.CreateHistogram<double>("hvo.execution_evidence.request.duration", "ms");
        _observations = _meter.CreateCounter<long>("hvo.execution_evidence.observations");
    }
    public void Unit(string outcome)
    {
        _units.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        if (outcome == "conflict" && _logger is not null) { EvidenceConflict(_logger); }
    }
    public void Request(string outcome, TimeSpan elapsed)
    {
        if (outcome == "pressure" && _logger is not null) { EvidencePressure(_logger); }
        _requests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        _latency.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", outcome));
    }
    public void Observation(string state) => _observations.Add(1, new KeyValuePair<string, object?>("state", state));
    public void Dispose() => _meter.Dispose();
    [LoggerMessage(EventId = 2481, Level = LogLevel.Warning, Message = "Execution evidence sequence conflict was durably audited; immutable accepted evidence was retained.")]
    private static partial void EvidenceConflict(ILogger logger);
    [LoggerMessage(EventId = 2482, Level = LogLevel.Warning, Message = "Execution evidence capacity is exhausted; new evidence remains retryable and retained history was preserved.")]
    private static partial void EvidencePressure(ILogger logger);
}

internal sealed class ExecutionEvidenceDeniedException : Exception
{
    public ExecutionEvidenceDeniedException() { }
    public ExecutionEvidenceDeniedException(string message) : base(message) { }
    public ExecutionEvidenceDeniedException(string message, Exception innerException) : base(message, innerException) { }
}
internal sealed class ExecutionEvidencePressureException : Exception
{
    public ExecutionEvidencePressureException() { }
    public ExecutionEvidencePressureException(string message) : base(message) { }
    public ExecutionEvidencePressureException(string message, Exception innerException) : base(message, innerException) { }
}
internal sealed class ExecutionEvidenceInvalidException : Exception
{
    public ExecutionEvidenceInvalidException() : this(GraphExecutionEvidenceReasonCodes.InvalidBody) { }
    public ExecutionEvidenceInvalidException(string message) : base(message) { ReasonCode = message; }
    public ExecutionEvidenceInvalidException(string message, Exception innerException) : base(message, innerException) { ReasonCode = message; }
    public string ReasonCode { get; }
}

internal static class ExecutionEvidenceFraming
{
    public const string MediaType = "application/x-hvo-execution-evidence-v1";

    public static IReadOnlyList<ReadOnlyMemory<byte>> Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > ExecutionEvidenceOptions.MaximumRequestBytes || payload.Span[^1] != (byte)'\n')
        {
            throw new ExecutionEvidenceInvalidException(GraphExecutionEvidenceReasonCodes.InvalidBody);
        }
        var units = new List<ReadOnlyMemory<byte>>();
        var start = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload.Span[i] != (byte)'\n') { continue; }
            if (i == start || i - start > GraphExecutionEvidenceLimits.MaximumEnvelopeBytes ||
                units.Count == ExecutionEvidenceOptions.MaximumBatchUnits)
            {
                throw new ExecutionEvidenceInvalidException(GraphExecutionEvidenceReasonCodes.LimitExceeded);
            }
            units.Add(payload[start..i]);
            start = i + 1;
        }
        return units;
    }

    public static async Task<byte[]> ReadAsync(Stream stream, long? contentLength, int limit, CancellationToken cancellationToken)
    {
        if (contentLength > limit) { throw new ExecutionEvidenceInvalidException(GraphExecutionEvidenceReasonCodes.PayloadTooLarge); }
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > limit) { throw new ExecutionEvidenceInvalidException(GraphExecutionEvidenceReasonCodes.PayloadTooLarge); }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }
}
