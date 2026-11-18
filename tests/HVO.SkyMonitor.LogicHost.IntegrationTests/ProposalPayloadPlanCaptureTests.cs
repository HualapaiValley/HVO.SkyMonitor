using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Unit")]
public sealed class ProposalPayloadPlanCaptureTests
{
    [TestMethod]
    public void DroppedRingBufferEventsAreReportedEvenWhenExpectedPlansArePresent()
    {
        var parse = () => ProposalPayloadPlanCapture.ParsePlans(Target("droppedCount=\"7\""), "@ids");
        parse.Should().Throw<InvalidOperationException>().WithMessage("*dropped*7*");
    }

    [TestMethod]
    public void TruncationStillFails()
    {
        var parse = () => ProposalPayloadPlanCapture.ParsePlans(Target("truncated=\"1\" droppedCount=\"0\""), "@ids");
        parse.Should().Throw<InvalidOperationException>().WithMessage("*truncated*");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("droppedCount=\"0\"")]
    public void CompleteRingBufferFiltersInternalStatements(string attributes)
    {
        ProposalPayloadPlanCapture.ParsePlans(Target(attributes), "@ids").Should().ContainSingle();
    }

    private static string Target(string attributes) => $"""
        <RingBufferTarget {attributes}>
          <event><ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
            <ParameterList><ColumnReference Column="@ids" /></ParameterList>
          </ShowPlanXML></event>
          <event><ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
            <ParameterList><ColumnReference Column="@internal" /></ParameterList>
          </ShowPlanXML></event>
        </RingBufferTarget>
        """;
}
