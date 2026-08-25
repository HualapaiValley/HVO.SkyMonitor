using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class OperatorUiTelemetryTests
{
    [TestMethod]
    public void PublishesBoundedReadMutationAndActivitySignals()
    {
        var measurements = new List<(string Name, string[] Tags)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OperatorUiTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, GetTagNames(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, GetTagNames(tags))));
        meterListener.Start();
        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OperatorUiTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(activityListener);
        using var telemetry = new OperatorUiTelemetry();

        using (OperatorUiTelemetry.StartRead("public-home"))
        {
            telemetry.RecordRead("public-home", "visitor", "success", 4, 128, TimeSpan.FromMilliseconds(2));
        }
        using (OperatorUiTelemetry.StartMutation("central-job"))
        {
            telemetry.RecordMutation("central-job", "applied", "manager", TimeSpan.FromMilliseconds(3));
        }
        using (OperatorUiTelemetry.StartRawDownload())
        {
        }

        measurements.Select(item => item.Name).Should().Contain([
            "hvo.operator_ui.reads",
            "hvo.operator_ui.read.duration",
            "hvo.operator_ui.read.rows",
            "hvo.operator_ui.response.bytes",
            "hvo.operator_ui.mutations",
            "hvo.operator_ui.mutation.duration"]);
        measurements.SelectMany(item => item.Tags).Should().OnlyContain(tag =>
            new[] { "operation", "audience", "outcome", "role" }.Contains(tag, StringComparer.Ordinal));
        activities.Select(activity => activity.DisplayName).Should().BeEquivalentTo([
            "operator-ui.read", "operator-ui.mutate", "operator-ui.raw-download"]);
        activities.SelectMany(activity => activity.TagObjects).Should().OnlyContain(tag =>
            tag.Key == "operator_ui.operation");
    }

    [TestMethod]
    public void RuntimeManifestDeclaresBoundedSignalsAndPrivacyExclusions()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Validation",
            "logichost-ui-runtime-signals.json")));
        var root = document.RootElement;
        root.GetProperty("schema").GetString().Should().Be("hvo-runtime-signal-manifest-v1");
        root.GetProperty("issue").GetInt32().Should().Be(107);
        root.GetProperty("logs").EnumerateArray().Select(item => item.GetProperty("eventId").GetInt32())
            .Should().Equal(Enumerable.Range(7500, 9));
        var metrics = root.GetProperty("metrics").EnumerateArray().ToArray();
        metrics.Select(item => item.GetProperty("name").GetString()).Should().OnlyHaveUniqueItems();
        foreach (var label in metrics.SelectMany(item => item.GetProperty("labels").EnumerateObject()))
        {
            label.Value.EnumerateArray().Select(value => value.GetString()).Should()
                .NotContainNulls().And.OnlyHaveUniqueItems();
        }
        root.GetProperty("health").GetProperty("check").GetString().Should().Be("/health");
        root.GetProperty("collection").GetProperty("artifacts").GetArrayLength().Should().Be(9);
        root.GetProperty("privacy").GetProperty("forbidden").EnumerateArray()
            .Select(value => value.GetString()).Should().Contain([
                "exact coordinates", "storage reference", "download token", "connection string"]);
    }

    private static string[] GetTagNames(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var names = new string[tags.Length];
        for (var index = 0; index < tags.Length; index++) names[index] = tags[index].Key;
        return names;
    }
}
