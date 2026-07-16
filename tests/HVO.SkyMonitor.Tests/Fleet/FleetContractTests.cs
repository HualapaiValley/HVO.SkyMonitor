using FluentAssertions;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.TestSupport;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.Tests.Fleet;

[TestClass]
[TestCategory("Unit")]
public sealed class FleetContractTests
{
    [TestMethod]
    public void Validate_BoundedReportRoundTripsWithStableHash()
    {
        var report = FleetStatusTestData.CreateReport(Guid.NewGuid());

        FleetContractJson.Validate(report).IsValid.Should().BeTrue();
        var payload = FleetContractJson.Serialize(report);
        payload.Length.Should().BeLessThan(FleetContractJson.MaximumPayloadBytes);
        var roundTrip = FleetContractJson.DeserializeReport(payload);
        roundTrip.Should().BeEquivalentTo(report);
        FleetContractJson.ComputeSha256(roundTrip!).Should().Be(FleetContractJson.ComputeSha256(report));
    }

    [TestMethod]
    public void Validate_RejectsUnboundedAndNonFiniteValues()
    {
        var report = FleetStatusTestData.CreateReport(Guid.NewGuid()) with
        {
            Runtime = new FleetRuntimeSummary(double.NaN, 1, null, null),
            Lanes = Enumerable.Range(0, FleetContractJson.MaximumLanes + 1)
                .Select(index => new FleetLaneSummary($"lane-{index}", false, 0, 0, 0, 0, 0, null))
                .ToArray()
        };

        var validation = FleetContractJson.Validate(report);

        validation.IsValid.Should().BeFalse();
        validation.ReasonCode.Should().Be("invalid-runtime");
    }

    [TestMethod]
    public void Validate_RejectsDuplicateTimingSegments()
    {
        var report = FleetStatusTestData.CreateReport(Guid.NewGuid());
        report = report with { Timings = [report.Timings[0], report.Timings[0]] };

        FleetContractJson.Validate(report).ReasonCode.Should().Be("invalid-timings");
    }

    [TestMethod]
    public void DeserializeEnvelope_RejectsNumericEnums()
    {
        var report = FleetStatusTestData.CreateReport(Guid.NewGuid());
        var json = Encoding.UTF8.GetString(FleetContractJson.Serialize(new FleetHeartbeatEnvelope("device", "key", report)))
            .Replace("\"overallHealth\":\"Healthy\"", "\"overallHealth\":99", StringComparison.Ordinal);

        Action act = () => FleetContractJson.DeserializeEnvelope(Encoding.UTF8.GetBytes(json));

        act.Should().Throw<JsonException>();
    }

    [TestMethod]
    public void Validate_RejectsMissingNestedContract()
    {
        var report = FleetStatusTestData.CreateReport(Guid.NewGuid()) with { Configuration = null! };

        var validation = FleetContractJson.Validate(report);

        validation.IsValid.Should().BeFalse();
        validation.ReasonCode.Should().Be("invalid-configuration");
    }

    [TestMethod]
    public void Validate_RejectsNullConfigurationHashWithoutThrowing()
    {
        var report = FleetStatusTestData.CreateReport(Guid.NewGuid());
        report = report with { Configuration = report.Configuration with { ConfigurationSha256 = null! } };

        var validation = FleetContractJson.Validate(report);

        validation.IsValid.Should().BeFalse();
        validation.ReasonCode.Should().Be("invalid-configuration");
    }
}
