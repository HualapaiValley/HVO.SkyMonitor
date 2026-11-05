using System.Collections.Concurrent;
using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Unit")]
public sealed class SqlWaitEvidenceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ObserverDisconnectionPreservesSubsequentSubjectFailure(bool canceled)
    {
        using var connection = new SqlConnection();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new ConcurrentQueue<string>();
        var original = new InvalidOperationException("subject-fault");
        using var cancellation = new CancellationTokenSource();
        async Task Operation()
        {
            started.TrySetResult();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (canceled)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw original;
        }
        async Task Observe(CancellationToken token)
        {
            await started.Task.WaitAsync(token).ConfigureAwait(false);
            try { await SqlWaitEvidence.SampleAsync(connection, output.Enqueue, token).ConfigureAwait(false); }
            finally { disconnected.TrySetResult(); }
        }
        var action = () => SqlWaitEvidence.WhileObservedAsync(Operation, Observe, output.Enqueue);
        if (canceled)
        {
            var failure = await action.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
            failure.Which.CancellationToken.Should().Be(cancellation.Token);
        }
        else
        {
            var failure = await action.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
            failure.Which.Should().BeSameAs(original);
        }
        output.Should().Contain("SQL observer disconnected; sampling stopped");
    }

    [TestMethod]
    public async Task UnexpectedSamplerFaultCannotReplaceSubjectFaultOrDiscloseItsMessage()
    {
        var original = new InvalidOperationException("subject-fault");
        var output = new ConcurrentQueue<string>();
        var action = () => SqlWaitEvidence.WhileObservedAsync(() => Task.FromException(original),
            _ => Task.FromException(new IOException("private-observer-details")), output.Enqueue);
        var failure = await action.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        failure.Which.Should().BeSameAs(original);
        output.Should().Contain("SQL observer stopped unexpectedly: type=IOException");
        output.Should().OnlyContain(text => !text.Contains("private-observer-details", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SamplerFaultDoesNotTurnSubjectSuccessIntoFailure()
    {
        await SqlWaitEvidence.WhileObservedAsync(() => Task.CompletedTask,
            _ => Task.FromException(new IOException("observer-fault")), _ => { }).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DiagnosticWriterFaultCannotReplaceSubjectFault()
    {
        var original = new InvalidOperationException("subject-fault");
        var action = () => SqlWaitEvidence.WhileObservedAsync(() => Task.FromException(original),
            _ => Task.FromException(new IOException("observer-fault")), _ => throw new ObjectDisposedException("writer"));
        var failure = await action.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        failure.Which.Should().BeSameAs(original);
    }
    [TestMethod]
    public async Task FailedConnectionAttemptsAreRetriedAtMostOncePerSample()
    {
        var attempts = 0;
        await SqlWaitEvidence.SampleAsync(null, _ => { }, CancellationToken.None,
            _ => { attempts++; return Task.FromResult<SqlConnection?>(null); }, TimeSpan.Zero).ConfigureAwait(false);
        attempts.Should().Be(3);
    }

    [TestMethod]
    public async Task CancellationStopsConnectionRetries()
    {
        using var stop = new CancellationTokenSource();
        var attempts = 0;
        await SqlWaitEvidence.SampleAsync(null, _ => { }, stop.Token,
            async _ =>
            {
                attempts++;
                await stop.CancelAsync().ConfigureAwait(false);
                return null;
            }, TimeSpan.Zero).ConfigureAwait(false);
        attempts.Should().Be(1);
    }

}
