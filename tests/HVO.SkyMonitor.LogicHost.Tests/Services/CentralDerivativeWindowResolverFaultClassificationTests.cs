using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

/// <summary>
/// The bounded cause a contained window-resolution fault is counted under. Resolution wraps a lost optimistic-
/// concurrency save in <see cref="CentralDerivativeJobStateException"/>, so the cause comes from the exception chain.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CentralDerivativeWindowResolverFaultClassificationTests
{
    [TestMethod]
    public void TheFirstRecognizedExceptionInTheChainNamesTheCause()
    {
        Assert.AreEqual("concurrency", CentralDerivativeWindowResolver.ClassifyResolutionFault(
            new DbUpdateConcurrencyException("lost")), "raw concurrency");
        Assert.AreEqual("concurrency", CentralDerivativeWindowResolver.ClassifyResolutionFault(
            new CentralDerivativeJobStateException("wrapped", new DbUpdateConcurrencyException("lost"))),
            "concurrency wrapped by resolution");
        Assert.AreEqual("database", CentralDerivativeWindowResolver.ClassifyResolutionFault(
            new DbUpdateException("save failed")), "raw database");
        Assert.AreEqual("database", CentralDerivativeWindowResolver.ClassifyResolutionFault(
            new InvalidOperationException("strategy", new DbUpdateException("save failed"))), "wrapped database");
        Assert.AreEqual("timeout", CentralDerivativeWindowResolver.ClassifyResolutionFault(
            new TimeoutException("slow")), "raw timeout");
        Assert.AreEqual("timeout", CentralDerivativeWindowResolver.ClassifyResolutionFault(
            new CentralDerivativeJobStateException("wrapped", new TimeoutException("slow"))), "wrapped timeout");
    }

    [TestMethod]
    public void AnUnrecognizedChainIsOther()
    {
        Assert.AreEqual("other", CentralDerivativeWindowResolver.ClassifyResolutionFault(
            new CentralDerivativeJobStateException("state")));
        Assert.AreEqual("other", CentralDerivativeWindowResolver.ClassifyResolutionFault(
            new InvalidOperationException("outer", new ArgumentException("inner"))));
    }
}
