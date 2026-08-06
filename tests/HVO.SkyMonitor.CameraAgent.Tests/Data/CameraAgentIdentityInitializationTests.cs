using HVO.SkyMonitor.CameraAgent.Data;

namespace HVO.SkyMonitor.CameraAgent.Tests.Data;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentIdentityInitializationTests
{
    [TestMethod]
    public async Task WaitAsync_CompletesOnlyAfterIdentityInitialization()
    {
        var initialization = new CameraAgentIdentityInitialization();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = initialization.RunAsync(_ => gate.Task, CancellationToken.None);
        var waiting = initialization.WaitAsync(CancellationToken.None);

        Assert.IsFalse(waiting.IsCompleted);
        Assert.IsFalse(initialization.IsCompleted);
        gate.SetResult();
        await running.ConfigureAwait(false);
        await waiting.ConfigureAwait(false);

        Assert.IsTrue(initialization.IsCompleted);
    }

    [TestMethod]
    public async Task WaitAsync_PropagatesInitializationFailure()
    {
        var initialization = new CameraAgentIdentityInitialization();
        var failure = new InvalidOperationException("identity initialization failed");

        var runFailure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => initialization.RunAsync(_ => Task.FromException(failure), CancellationToken.None)).ConfigureAwait(false);
        var waitFailure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => initialization.WaitAsync(CancellationToken.None)).ConfigureAwait(false);

        Assert.AreSame(failure, runFailure);
        Assert.AreSame(failure, waitFailure);
        Assert.IsFalse(initialization.IsCompleted);
    }

    [TestMethod]
    public async Task WaitAsync_PropagatesCallerCancellationWithoutCompletingInitialization()
    {
        var initialization = new CameraAgentIdentityInitialization();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => initialization.WaitAsync(cancellation.Token)).ConfigureAwait(false);

        Assert.IsFalse(initialization.IsCompleted);
    }
}
