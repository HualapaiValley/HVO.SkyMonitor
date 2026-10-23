namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class JoinedRecoveryFixtureTests
{
    [TestMethod]
    [Timeout(300_000)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Only sanitized phase/category/SQL numbers may cross the test boundary; retain raw resources privately for attribution.")]
    public async Task JoinedRestore_AdmitsOnlyPairedSqlObjectsAndHostState()
    {
        var fixture = new JoinedRecoveryFixture();
        try { await fixture.RunAsync(); }
        catch (Exception exception)
        {
            var failure = fixture.SafeFailure(exception);
            try { fixture.RetainPrivateAttribution(); }
            catch (Exception) { failure += " private-attribution-write-failed"; }
            Assert.Fail("Issue 1630 sanitized failure: " + failure);
        }
    }
}
