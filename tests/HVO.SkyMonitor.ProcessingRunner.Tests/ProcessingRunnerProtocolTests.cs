using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.ProcessingRunner.Tests;

[TestClass]
public sealed class ProcessingRunnerProtocolTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void RunnerIdsAndLabelsFollowTheContract()
    {
        Assert.IsTrue(ProcessingRunnerProtocol.IsValidRunnerId("lab-x64-01"));
        Assert.IsFalse(ProcessingRunnerProtocol.IsValidRunnerId("-leading"));
        Assert.IsFalse(ProcessingRunnerProtocol.IsValidRunnerId(new string('a', 129)));
        Assert.IsTrue(ProcessingRunnerProtocol.IsValidLabel("gpu:none"));
        Assert.IsFalse(ProcessingRunnerProtocol.IsValidLabel("Upper"));
    }
}
