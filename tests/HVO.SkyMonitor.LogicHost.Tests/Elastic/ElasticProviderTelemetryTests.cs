using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services.Elastic;

namespace HVO.SkyMonitor.LogicHost.Tests.Elastic;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ElasticProviderTelemetryTests
{
    [TestMethod]
    public void AllocationMetricsUseOnlyProviderAndBoundedPhaseLabels()
    {
        var observations = new List<(string Name, string[] Keys)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ElasticProviderTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            observations.Add((instrument.Name, tags.ToArray().Select(tag => tag.Key).ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            observations.Add((instrument.Name, tags.ToArray().Select(tag => tag.Key).ToArray())));
        listener.Start();
        using var telemetry = new ElasticProviderTelemetry();
        telemetry.RecordProvision("local-process", ElasticScalingPolicy.ReasonEntitlementBound);
        telemetry.RecordAllocation("local-process", ElasticProviderTelemetry.LockedPhase, new ElasticFleetAllocator.Result(
            [Guid.NewGuid()], [Guid.NewGuid()], 3, 12, TimeSpan.FromMilliseconds(2)));

        observations.Select(item => item.Name).Should().Contain([
            "skymonitor.central.elastic.allocation.jobs",
            "skymonitor.central.elastic.allocation.slots",
            "skymonitor.central.elastic.allocation.edge_visits",
            "skymonitor.central.elastic.allocation.duration"
        ]);
        var tagKeys = observations.Where(item => item.Name.Contains(".allocation.", StringComparison.Ordinal))
            .SelectMany(item => item.Keys).ToArray();
        tagKeys.Should().OnlyContain(key => key == "provider" || key == "phase");
        observations.Single(item => item.Name == "skymonitor.central.elastic.provisions").Keys
            .Should().BeEquivalentTo(new[] { "provider", "reason" });
    }

    [TestMethod]
    public void RuntimeSignalManifestPinsAllocationAndDecisionContracts()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Validation", "central-elastic-runtime-signals.json")));
        var metrics = document.RootElement.GetProperty("metrics").EnumerateArray().ToArray();
        var allocation = metrics.Where(metric => metric.GetProperty("name").GetString()!.Contains(".allocation.", StringComparison.Ordinal)).ToArray();

        allocation.Select(metric => metric.GetProperty("name").GetString()).Should().BeEquivalentTo([
            "skymonitor.central.elastic.allocation.jobs",
            "skymonitor.central.elastic.allocation.slots",
            "skymonitor.central.elastic.allocation.edge_visits",
            "skymonitor.central.elastic.allocation.duration"
        ]);
        foreach (var metric in allocation)
        {
            metric.GetProperty("labels").EnumerateObject().Select(label => label.Name)
                .Should().BeEquivalentTo(new[] { "provider", "phase" });
        }
        metrics.Single(metric => metric.GetProperty("name").GetString() == "skymonitor.central.elastic.placements_rejected")
            .GetProperty("labels").GetProperty("reason").EnumerateArray().Select(reason => reason.GetString()).Should().Contain([
                ElasticScalingPolicy.ReasonDailyLimit,
                ElasticScalingPolicy.ReasonColdStartExceedsDeadline,
                ElasticScalingPolicy.ReasonEntitlementBound,
                ElasticScalingPolicy.ReasonInstanceUndescribed,
                ElasticScalingPolicy.ReasonInstanceLimit
            ]);
        metrics.Single(metric => metric.GetProperty("name").GetString() == "skymonitor.central.elastic.provisions")
            .GetProperty("labels").GetProperty("reason").EnumerateArray().Select(reason => reason.GetString()).Should().BeEquivalentTo([
                ElasticScalingPolicy.ReasonBacklog,
                ElasticScalingPolicy.ReasonEntitlementBound,
                ElasticScalingPolicy.ReasonWarmMinimum,
                ElasticScalingPolicy.ReasonColdStartExceedsDeadline
            ]);
        foreach (var metric in allocation)
        {
            metric.GetProperty("labels").GetProperty("phase").EnumerateArray().Select(phase => phase.GetString())
                .Should().BeEquivalentTo(new[] { ElasticProviderTelemetry.SamplePhase, ElasticProviderTelemetry.LockedPhase });
        }
    }
}
