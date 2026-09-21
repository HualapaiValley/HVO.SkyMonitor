using HVO.SkyMonitor.CameraAgent.Common.Sqlite;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Sqlite;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class SqliteConnectionConfigurationGateTests
{
    [TestMethod]
    public async Task OpenAndConfigureAsync_PreCanceledOpenDisposesConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            SqliteConnectionConfigurationGate.OpenAndConfigureAsync(
                connection,
                static (_, _) => ValueTask.CompletedTask,
                cancellation.Token).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(System.Data.ConnectionState.Closed, connection.State);
    }

    [TestMethod]
    public async Task OpenAndConfigureAsync_ConfigurationFailurePreservesIdentityAndDisposesConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        var expected = new InvalidOperationException("configuration failed");

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            SqliteConnectionConfigurationGate.OpenAndConfigureAsync(
                connection,
                (_, _) => ValueTask.FromException(expected),
                CancellationToken.None).AsTask()).ConfigureAwait(false);

        Assert.AreSame(expected, actual);
        Assert.AreEqual(System.Data.ConnectionState.Closed, connection.State);
    }

    [TestMethod]
    public async Task RunAsync_SerializesCallbacksProcessWide()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var maximumConcurrent = 0;

        var first = SqliteConnectionConfigurationGate.RunAsync(async () =>
        {
            var current = Interlocked.Increment(ref concurrent);
            maximumConcurrent = Math.Max(maximumConcurrent, current);
            firstEntered.SetResult();
            await release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref concurrent);
        }, CancellationToken.None).AsTask();
        await firstEntered.Task.ConfigureAwait(false);

        var second = SqliteConnectionConfigurationGate.RunAsync(async () =>
        {
            var current = Interlocked.Increment(ref concurrent);
            maximumConcurrent = Math.Max(maximumConcurrent, current);
            secondEntered.SetResult();
            await Task.Yield();
            Interlocked.Decrement(ref concurrent);
        }, CancellationToken.None).AsTask();

        Assert.IsFalse(secondEntered.Task.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second).ConfigureAwait(false);
        Assert.AreEqual(1, maximumConcurrent);
    }

    [TestMethod]
    public async Task RunAsync_QueuedCancellationDoesNotRunCallbackAndGateRemainsUsable()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = SqliteConnectionConfigurationGate.RunAsync(async () =>
        {
            entered.SetResult();
            await release.Task.ConfigureAwait(false);
        }, CancellationToken.None).AsTask();
        await entered.Task.ConfigureAwait(false);

        using var cancellation = new CancellationTokenSource();
        var callbackRan = false;
        var queued = SqliteConnectionConfigurationGate.RunAsync(() =>
        {
            callbackRan = true;
            return ValueTask.CompletedTask;
        }, cancellation.Token).AsTask();
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => queued).ConfigureAwait(false);
        Assert.IsFalse(callbackRan);
        release.SetResult();
        await holder.ConfigureAwait(false);

        var usable = false;
        await SqliteConnectionConfigurationGate.RunAsync(() =>
        {
            usable = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(usable);
    }

    [TestMethod]
    public async Task RunAsync_PreservesThrownExceptionIdentityAndGateRemainsUsable()
    {
        var expected = new InvalidOperationException("configuration failed");
        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            SqliteConnectionConfigurationGate.RunAsync(
                () => ValueTask.FromException(expected),
                CancellationToken.None).AsTask()).ConfigureAwait(false);

        Assert.AreSame(expected, actual);
        await SqliteConnectionConfigurationGate.RunAsync(
            () => ValueTask.CompletedTask,
            CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RunAsync_ReleasesGateBeforeOrdinaryWorkContinues()
    {
        var ordinaryWorkRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinaryWorkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(async () =>
        {
            await SqliteConnectionConfigurationGate.RunAsync(
                () => ValueTask.CompletedTask,
                CancellationToken.None).ConfigureAwait(false);
            ordinaryWorkStarted.SetResult();
            await ordinaryWorkRelease.Task.ConfigureAwait(false);
        });
        await ordinaryWorkStarted.Task.ConfigureAwait(false);

        var secondRan = false;
        await SqliteConnectionConfigurationGate.RunAsync(() =>
        {
            secondRan = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(secondRan);
        ordinaryWorkRelease.SetResult();
        await first.ConfigureAwait(false);
    }
}
