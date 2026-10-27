namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class JoinedRecoveryFixtureTests
{
    [TestMethod]
    [Timeout(300_000)]
    public async Task JoinedRestore_AdmitsOnlyPairedSqlObjectsAndHostState()
        => await RunFixtureAsync(fixture => fixture.RunAsync());

    [TestMethod]
    [Timeout(300_000)]
    public async Task SqlFailure_AfterRestorePreservesSourceTargetBackupAndAttribution()
        => await RunFixtureAsync(fixture => fixture.RunAsync(verifySqlFailurePreservation: true));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Only sanitized phase/category/SQL numbers may cross the test boundary; retain raw resources privately for attribution.")]
    private static async Task RunFixtureAsync(Func<JoinedRecoveryFixture, Task> action)
    {
        var fixture = new JoinedRecoveryFixture();
        try { await action(fixture); }
        catch (Exception exception)
        {
            var failure = fixture.SafeFailure(exception);
            try { fixture.RetainPrivateAttribution(); }
            catch (Exception) { failure += " private-attribution-write-failed"; }
            Assert.Fail("Issue 1630 sanitized failure: " + failure);
        }
    }
}
