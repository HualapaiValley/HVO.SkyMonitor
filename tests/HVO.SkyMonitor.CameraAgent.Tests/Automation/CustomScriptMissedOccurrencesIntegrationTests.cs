using HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class CustomScriptMissedOccurrencesIntegrationTests
{
    [TestMethod]
    public async Task FiveElapsedScriptPeriodsProduceOneExecutionOneMissedRowAndNoReplayAfterRestart()
    {
        Assert.IsTrue(LinuxCustomScriptSandbox.Probe(), "Required sandbox qualification is unavailable.");
        var script = CustomScriptTaskAdapterTests.Script("exit 17");
        var adapter = CustomScriptTaskAdapterTests.Adapter(script);
        await LocalAutomationScriptMissedOccurrencesTests.AssertMissedAndRestartAsync(script.Target, adapter.ExecuteAsync)
            .ConfigureAwait(false);
    }
}
