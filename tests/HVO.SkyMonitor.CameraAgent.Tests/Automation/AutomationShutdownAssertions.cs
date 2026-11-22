using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

/// <summary>Shared assertions for the off-sweep automation shutdown regressions (#1932, #1936, #1937).</summary>
internal static class AutomationShutdownAssertions
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    internal const string BothFailedMessage =
        "The shutdown assertions failed and cleanup also failed; the assertion failure is primary.";

    /// <summary>
    /// Runs a regression's assertions and then its cleanup, so a cleanup failure cannot replace the assertion
    /// that first went wrong (#1937). Cleanup always runs. A first failure is rethrown unchanged when cleanup
    /// succeeds. When cleanup also fails, the first failure is primary and the cleanup failure is kept beside it.
    /// A cleanup failure after passing assertions is the test's failure.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every assertion failure is retained and rethrown as the primary exception after cleanup runs.")]
    internal static async Task PreserveFirstFailureAsync(Func<Task> assertions, Func<Task> cleanup)
    {
        ArgumentNullException.ThrowIfNull(assertions);
        ArgumentNullException.ThrowIfNull(cleanup);
        Exception? first = null;
        try
        {
            await assertions().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            first = exception;
        }
        if (first is null)
        {
            await cleanup().ConfigureAwait(false);
            return;
        }
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException(BothFailedMessage, first, cleanupFailure);
        }
        ExceptionDispatchInfo.Throw(first);
    }

    /// <summary>
    /// A dispatched off-sweep adapter that registers a cancellation callback, signals that it is active and holds
    /// its reservation until <paramref name="release"/>. The callback signals <paramref name="cancellationObserved"/>
    /// and then throws <paramref name="callbackFailure"/> when one is supplied.
    /// </summary>
    internal static Func<CancellationToken, Task> ControlledAdapter(TaskCompletionSource registered,
        TaskCompletionSource cancellationObserved, TaskCompletionSource release, Exception? callbackFailure)
        => async token =>
        {
            using var registration = token.Register(() =>
            {
                cancellationObserved.SetResult();
                if (callbackFailure is not null)
                {
                    throw callbackFailure;
                }
            });
            registered.SetResult();
            await release.Task.ConfigureAwait(false);
        };

    /// <summary>
    /// Waits, within the deadline, for a stop to finish. It must succeed for an ordinary shutdown, or fault with
    /// exactly the original cancellation callback failure when one was thrown.
    /// </summary>
    internal static async Task StopReportsAsync(Task stop, Exception? callbackFailure)
    {
        if (callbackFailure is null)
        {
            await stop.WaitAsync(Deadline).ConfigureAwait(false);
            return;
        }
        var error = await Assert.ThrowsExactlyAsync<AggregateException>(() => stop.WaitAsync(Deadline)).ConfigureAwait(false);
        Assert.AreSame(callbackFailure, error.Flatten().InnerExceptions.Single());
    }
}
