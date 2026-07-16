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

    [TestMethod]
    public void Validate_RejectsEnvelopeWhoseSerializedPayloadExceedsBound()
    {
        const char escapedCharacter = '\u0080';
        var maximumName = new string(escapedCharacter, 64);
        var maximumReason = new string(escapedCharacter, 128);
        var report = FleetStatusTestData.CreateReport(Guid.NewGuid());
        report = report with
        {
            SoftwareVersion = maximumName,
            Configuration = report.Configuration with
            {
                DeclaredVersion = maximumName,
                ModuleType = maximumName,
                RigProfileVersion = maximumName
            },
            Capture = report.Capture with { Reason = maximumReason },
            Ingress = report.Ingress with { Reason = maximumReason },
            Processing = report.Processing with { Reason = maximumReason },
            ArtifactOutbox = report.ArtifactOutbox with { Reason = maximumReason },
            HeartbeatOutbox = report.HeartbeatOutbox with { Reason = maximumReason },
            Lanes = Enumerable.Range(0, FleetContractJson.MaximumLanes)
                .Select(_ => new FleetLaneSummary(maximumName, false, 0, 0, 0, 0, 0, null))
                .ToArray(),
            Storage = Enumerable.Range(0, FleetContractJson.MaximumStorageTargets)
                .Select(_ => new FleetStorageSummary(maximumName, 1, 1, false, 1, report.ObservedAtUtc, maximumReason))
                .ToArray()
        };

        for (var reasonLength = 128; reasonLength > 0; reasonLength--)
        {
            var healthReason = new string(escapedCharacter, reasonLength);
            var candidate = report with
            {
                HealthChecks = Enumerable.Range(0, FleetContractJson.MaximumHealthChecks)
                    .Select(_ => new FleetHealthCheckSummary(maximumName, FleetHealth.Healthy, healthReason))
                    .ToArray()
            };
            if (FleetContractJson.Serialize(candidate).Length <= FleetContractJson.MaximumPayloadBytes)
            {
                report = candidate;
                break;
            }
        }

        FleetContractJson.Validate(report).IsValid.Should().BeTrue();
        var envelope = new FleetHeartbeatEnvelope(new string('d', 128), new string('k', 256), report);
        FleetContractJson.Serialize(envelope).Length.Should().BeGreaterThan(FleetContractJson.MaximumPayloadBytes);

        var validation = FleetContractJson.Validate(envelope);

        validation.IsValid.Should().BeFalse();
        validation.ReasonCode.Should().Be("payload-too-large");
    }
}
