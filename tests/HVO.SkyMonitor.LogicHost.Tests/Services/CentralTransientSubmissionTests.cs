using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralTransientSubmissionTests
{
    [TestMethod]
    public void RejectedException_MessageConstructors_DefaultToInvalidContractConflict()
    {
        var exceptions = new CentralTransientSubmissionRejectedException[]
        {
            new("invalid submission"),
            new("invalid submission", new InvalidOperationException("inner"))
        };

        exceptions.Should().OnlyContain(exception =>
            exception.ReasonCode == CentralTransientSubmissionReasonCodes.InvalidContract
            && exception.Kind == CentralTransientSubmissionRejectionKind.Conflict);
    }
}
