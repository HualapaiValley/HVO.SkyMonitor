using System.Collections.Concurrent;
using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Unit")]
public sealed class SqlWaitEvidenceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ObserverOpenFailurePreservesSubsequentSubjectFailure(bool canceled)
    {
        var output = new ConcurrentQueue<string>();
        var original = new InvalidOperationException("subject-fault");
        using var cancellation = new CancellationTokenSource();
        async Task Operation()
        {
            await SqlDiagnosticObservation.ReadQueryAsync(string.Empty, "requests", 1, output.Enqueue, CancellationToken.None)
                .ConfigureAwait(false);
        }
        async Task Subject()
        {
            await Task.Yield();
            if (canceled)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw original;
        }
        var action = () => SqlDiagnosticObservation.WhileObservedAsync(Subject, _ => Operation(), output.Enqueue);
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
        output.Should().Contain("SQL observer stopped unexpectedly: type=InvalidOperationException");
    }

    [TestMethod]
    public async Task UnexpectedSamplerFaultCannotReplaceSubjectFaultOrDiscloseItsMessage()
    {
        var original = new InvalidOperationException("subject-fault");
        var output = new ConcurrentQueue<string>();
        var action = () => SqlDiagnosticObservation.WhileObservedAsync(() => Task.FromException(original),
            _ => Task.FromException(new IOException("private-observer-details")), output.Enqueue);
        var failure = await action.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        failure.Which.Should().BeSameAs(original);
        output.Should().Contain("SQL observer stopped unexpectedly: type=IOException");
        output.Should().OnlyContain(text => !text.Contains("private-observer-details", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SamplerFaultDoesNotTurnSubjectSuccessIntoFailure()
    {
        await SqlDiagnosticObservation.WhileObservedAsync(() => Task.CompletedTask,
            _ => Task.FromException(new IOException("observer-fault")), _ => { }).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DiagnosticWriterFaultCannotReplaceSubjectFault()
    {
        var original = new InvalidOperationException("subject-fault");
        var action = () => SqlDiagnosticObservation.WhileObservedAsync(() => Task.FromException(original),
            _ => Task.FromException(new IOException("observer-fault")), _ => throw new ObjectDisposedException("writer"));
        var failure = await action.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        failure.Which.Should().BeSameAs(original);
    }
    [TestMethod]
    public async Task FailedObservationDoesNotSuppressIndependentSourcesOrLaterCycles()
    {
        var calls = new ConcurrentBag<(string Name, int Sample)>();
        var output = new ConcurrentQueue<string>();
        await SqlDiagnosticObservation.SampleAsync((name, sample, _) =>
        {
            calls.Add((name, sample));
            return name == "requests" ? Task.FromException(new IOException("private-query-details")) : Task.CompletedTask;
        }, output.Enqueue, CancellationToken.None, maximumSamples: 3, interval: TimeSpan.Zero).ConfigureAwait(false);
        calls.Should().HaveCount(15);
        foreach (var name in SqlDiagnosticObservation.Queries.Keys)
            calls.Where(call => call.Name == name).Select(call => call.Sample).Order().Should().Equal(1, 2, 3);
        output.Should().Contain(line => line.Contains("source=requests sample=3 type=IOException", StringComparison.Ordinal));
        output.Should().OnlyContain(line => !line.Contains("private-query-details", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CancellationStopsObservationCycles()
    {
        using var stop = new CancellationTokenSource();
        var calls = 0;
        await SqlDiagnosticObservation.SampleAsync(async (_, _, token) =>
        {
            Interlocked.Increment(ref calls);
            await stop.CancelAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }, _ => { }, stop.Token, maximumSamples: 3, interval: TimeSpan.Zero).ConfigureAwait(false);
        calls.Should().Be(5);
    }
}
