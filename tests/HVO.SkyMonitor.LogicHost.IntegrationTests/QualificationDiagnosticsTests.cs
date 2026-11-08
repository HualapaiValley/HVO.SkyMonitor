using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Unit")]
public sealed class QualificationDiagnosticsTests
{
    [TestMethod]
    public async Task FailedSubjectRetainsOriginalExceptionAndReportsCleanupFailure()
    {
        var original = new InvalidOperationException("original-proposal-assertion");
        var cleanup = new IOException("DROP EVENT SESSION failed; owned session remains");
        var messages = new List<string>();
        var cleanupAttempts = 0;
        var action = () => QualificationDiagnostics.RunWithCleanupAsync(
            () => Task.FromException(original),
            () => { cleanupAttempts++; return ValueTask.FromException(cleanup); }, messages.Add);
        (await action.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false)).Which.Should().BeSameAs(original);
        cleanupAttempts.Should().Be(1);
        messages.Should().ContainSingle().Which.Should().Contain(cleanup.GetType().FullName!).And.Contain(cleanup.Message).And.Contain("cleanup");
        original.Data.Values.Cast<object>().Should().Contain(cleanup);
    }

    [TestMethod]
    public async Task SuccessfulSubjectStillFailsWhenCleanupFails()
    {
        var cleanup = new IOException("DROP EVENT SESSION failed");
        var messages = new List<string>();
        var action = () => QualificationDiagnostics.RunWithCleanupAsync(
            () => Task.CompletedTask, () => ValueTask.FromException(cleanup), messages.Add);
        (await action.Should().ThrowAsync<IOException>().ConfigureAwait(false)).Which.Should().BeSameAs(cleanup);
        messages.Should().ContainSingle().Which.Should().Contain(cleanup.GetType().FullName!).And.Contain(cleanup.Message);
    }

    [TestMethod]
    public async Task SuccessfulCleanupPreservesSubjectFailureWithoutFalseCleanupReport()
    {
        var original = new InvalidOperationException("first-failure");
        var messages = new List<string>();
        var cleaned = false;
        var action = () => QualificationDiagnostics.RunWithCleanupAsync(
            () => Task.FromException(original), () => { cleaned = true; return ValueTask.CompletedTask; }, messages.Add);
        (await action.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false)).Which.Should().BeSameAs(original);
        cleaned.Should().BeTrue();
        messages.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CancelledSubjectIsPreservedWhenCleanupFails()
    {
        var original = new OperationCanceledException("subject-cancelled");
        var cleanup = new IOException("cleanup-failed");
        var action = () => QualificationDiagnostics.RunWithCleanupAsync(
            () => Task.FromException(original), () => ValueTask.FromException(cleanup), _ => { });
        (await action.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false)).Which.Should().BeSameAs(original);
        original.Data.Values.Cast<object>().Should().Contain(cleanup);
    }

    [TestMethod]
    public async Task ReportingFailureDoesNotReplaceSubjectOrCleanupEvidence()
    {
        var original = new InvalidOperationException("first-failure");
        var cleanup = new IOException("cleanup-failure");
        var reporting = new InvalidOperationException("reporting-failure");
        var action = () => QualificationDiagnostics.RunWithCleanupAsync(
            () => Task.FromException(original), () => ValueTask.FromException(cleanup), _ => throw reporting);
        (await action.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false)).Which.Should().BeSameAs(original);
        original.Data.Values.Cast<object>().Should().Contain(cleanup);
        cleanup.Data.Values.Cast<object>().Should().Contain(reporting);
    }

    [TestMethod]
    public async Task SuccessfulSubjectAndCleanupRemainSuccessful()
    {
        var calls = new List<string>();
        await QualificationDiagnostics.RunWithCleanupAsync(
            () => { calls.Add("subject"); return Task.CompletedTask; },
            () => { calls.Add("cleanup"); return ValueTask.CompletedTask; }, calls.Add).ConfigureAwait(false);
        calls.Should().Equal("subject", "cleanup");
    }
}
