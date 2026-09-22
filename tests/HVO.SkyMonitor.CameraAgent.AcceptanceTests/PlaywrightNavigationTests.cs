using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PlaywrightNavigationTests
{
    private static readonly Uri Operations = new("http://127.0.0.1:52110/operations");

    [TestMethod]
    public void IdenticalInterruptedDestinationIsJoined()
    {
        const string message = "Navigation to \"http://127.0.0.1:52110/operations\" is interrupted by another navigation to \"http://127.0.0.1:52110/operations\"";

        Assert.IsTrue(PlaywrightNavigation.IsSameDestinationInterruption(message, Operations));
    }

    [TestMethod]
    public void DifferentReplacementDestinationIsRejected()
    {
        const string message = "Navigation to \"http://127.0.0.1:52110/operations\" is interrupted by another navigation to \"http://127.0.0.1:52110/Account/Login\"";

        Assert.IsFalse(PlaywrightNavigation.IsSameDestinationInterruption(message, Operations));
    }

    [TestMethod]
    public void UnrelatedPlaywrightFailureIsRejected()
    {
        const string message = "Target page, context or browser has been closed";

        Assert.IsFalse(PlaywrightNavigation.IsSameDestinationInterruption(message, Operations));
    }

    [TestMethod]
    [DataRow("Navigation to \"http://127.0.0.1:80/operations\" is interrupted by another navigation to \"http://127.0.0.1:52110/operations\"")]
    [DataRow("prefix Navigation to \"http://127.0.0.1:52110/operations\" is interrupted by another navigation to \"http://127.0.0.1:52110/operations\"")]
    [DataRow("Navigation to \"http://127.0.0.1:52110/operations\" is interrupted by another navigation to \"http://127.0.0.1:52110/operations\" suffix")]
    [DataRow("Navigation to \"http://127.0.0.1:52110/operations\" is interrupted by another navigation to \"http://127.0.0.1:52110/operations\"\nCall log:")]
    public void CanonicalOrWrappedVariantsAreRejected(string message)
    {
        Assert.IsFalse(PlaywrightNavigation.IsSameDestinationInterruption(message, Operations));
    }
}
