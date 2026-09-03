using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

internal sealed class Issue246RetentionEvidenceCollector : IDisposable
{
    internal Issue246RetentionEvidenceCollector()
    {
        Http = new CountingDeleteHandler { InnerHandler = new SocketsHttpHandler() };
    }

    internal CountingDbCommandInterceptor Commands { get; } = new();

    internal CountingDbTransactionInterceptor Transactions { get; } = new();

    internal CountingDeleteHandler Http { get; }

    internal void Reset()
    {
        Commands.Reset();
        Transactions.Reset();
        Http.Reset();
    }

    internal Issue246ProtocolSnapshot Snapshot()
        => new(Commands.Count, Transactions.Snapshot(), Http.Snapshot());

    public void Dispose() => Http.Dispose();
}

internal sealed class CountingDbCommandInterceptor : DbCommandInterceptor
{
    private long count;

    internal long Count => Interlocked.Read(ref count);

    internal void Reset() => Interlocked.Exchange(ref count, 0);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref count);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref count);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Interlocked.Increment(ref count);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref count);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Interlocked.Increment(ref count);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref count);
        return ValueTask.FromResult(result);
    }
}

internal sealed class CountingDbTransactionInterceptor : DbTransactionInterceptor
{
    private readonly ConcurrentDictionary<DbTransaction, long> starts = new();
    private readonly ConcurrentQueue<double> committedDurations = new();
    private long startAttempts;
    private long successfullyStarted;
    private long committed;
    private long rolledBack;
    private long failed;

    internal void Reset()
    {
        starts.Clear();
        committedDurations.Clear();
        Interlocked.Exchange(ref startAttempts, 0);
        Interlocked.Exchange(ref successfullyStarted, 0);
        Interlocked.Exchange(ref committed, 0);
        Interlocked.Exchange(ref rolledBack, 0);
        Interlocked.Exchange(ref failed, 0);
    }

    internal Issue246TransactionSnapshot Snapshot()
        => new(
            Interlocked.Read(ref startAttempts),
            Interlocked.Read(ref successfullyStarted),
            Interlocked.Read(ref committed),
            Interlocked.Read(ref rolledBack),
            Interlocked.Read(ref failed),
            committedDurations.ToArray());

    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result)
    {
        Interlocked.Increment(ref startAttempts);
        return result;
    }

    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref startAttempts);
        return ValueTask.FromResult(result);
    }

    public override DbTransaction TransactionStarted(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result)
    {
        starts[result] = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref successfullyStarted);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        starts[result] = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref successfullyStarted);
        return ValueTask.FromResult(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        RecordDuration(transaction, committedDurations);
        Interlocked.Increment(ref committed);
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        RecordDuration(transaction, committedDurations);
        Interlocked.Increment(ref committed);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        starts.TryRemove(transaction, out _);
        Interlocked.Increment(ref rolledBack);
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        starts.TryRemove(transaction, out _);
        Interlocked.Increment(ref rolledBack);
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        starts.TryRemove(transaction, out _);
        Interlocked.Increment(ref failed);
    }

    public override Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        starts.TryRemove(transaction, out _);
        Interlocked.Increment(ref failed);
        return Task.CompletedTask;
    }

    private void RecordDuration(DbTransaction transaction, ConcurrentQueue<double> destination)
    {
        if (starts.TryRemove(transaction, out var started))
        {
            destination.Enqueue(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}

internal sealed class CountingDeleteHandler : DelegatingHandler
{
    private readonly ConcurrentQueue<double> deleteDurations = new();
    private DeleteBehavior behavior = DeleteBehavior.None;
    private long requests;
    private long deletes;
    private long requestBytes;
    private long responseBytes;
    private long unknownRequestLengths;
    private long unknownResponseLengths;

    internal void Configure(TimeSpan delay, Func<string, DeleteProbe>? probeFactory = null)
        => Volatile.Write(ref behavior, new DeleteBehavior(delay, probeFactory));

    internal void Reset()
    {
        Interlocked.Exchange(ref requests, 0);
        Interlocked.Exchange(ref deletes, 0);
        Interlocked.Exchange(ref requestBytes, 0);
        Interlocked.Exchange(ref responseBytes, 0);
        Interlocked.Exchange(ref unknownRequestLengths, 0);
        Interlocked.Exchange(ref unknownResponseLengths, 0);
        deleteDurations.Clear();
    }

    internal Issue246ObjectProtocolSnapshot Snapshot()
        => new(
            Interlocked.Read(ref requests),
            Interlocked.Read(ref deletes),
            Interlocked.Read(ref requestBytes),
            Interlocked.Read(ref responseBytes),
            Interlocked.Read(ref unknownRequestLengths),
            Interlocked.Read(ref unknownResponseLengths),
            deleteDurations.ToArray());

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requests);
        if (request.Content is not null)
        {
            RecordLength(request.Content.Headers.ContentLength, ref requestBytes, ref unknownRequestLengths);
        }
        var isDelete = request.Method == HttpMethod.Delete;
        var started = isDelete ? Stopwatch.GetTimestamp() : 0;
        if (isDelete)
        {
            Interlocked.Increment(ref deletes);
            var configured = Volatile.Read(ref behavior);
            if (configured.ProbeFactory is not null)
            {
                var probe = configured.ProbeFactory(GetObjectKey(request));
                await probe.Ready.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            if (configured.Delay > TimeSpan.Zero)
            {
                await Task.Delay(configured.Delay, cancellationToken).ConfigureAwait(false);
            }
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
        {
            RecordLength(response.Content.Headers.ContentLength, ref responseBytes, ref unknownResponseLengths);
        }
        if (isDelete)
        {
            deleteDurations.Enqueue(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        return response;
    }

    private static void RecordLength(long? length, ref long bytes, ref long unknown)
    {
        if (length.HasValue)
        {
            Interlocked.Add(ref bytes, length.Value);
        }
        else
        {
            Interlocked.Increment(ref unknown);
        }
    }

    private static string GetObjectKey(HttpRequestMessage request)
    {
        var requestUri = request.RequestUri
            ?? throw new InvalidOperationException("The MinIO request has no URI.");
        var escapedPath = requestUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        const string bucketPrefix = "skymonitor-artifacts/";
        if (!escapedPath.StartsWith(bucketPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The MinIO DELETE did not target the artifact bucket.");
        }
        return Uri.UnescapeDataString(escapedPath[bucketPrefix.Length..]);
    }

    private sealed record DeleteBehavior(TimeSpan Delay, Func<string, DeleteProbe>? ProbeFactory)
    {
        internal static DeleteBehavior None { get; } = new(TimeSpan.Zero, null);
    }
}

internal sealed record DeleteProbe(Task Ready);

internal sealed record Issue246ProtocolSnapshot(
    long EfCommands,
    Issue246TransactionSnapshot SqlTransactions,
    Issue246ObjectProtocolSnapshot ObjectStore);

internal sealed record Issue246TransactionSnapshot(
    long StartAttempts,
    long SuccessfullyStarted,
    long Committed,
    long RolledBack,
    long Failed,
    IReadOnlyList<double> CommittedDurationMilliseconds);

internal sealed record Issue246ObjectProtocolSnapshot(
    long Requests,
    long Deletes,
    long RequestEntityBytes,
    long ResponseEntityBytes,
    long UnknownRequestEntityLengths,
    long UnknownResponseEntityLengths,
    IReadOnlyList<double> DeleteDurationMilliseconds);
