using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureProcessingTelemetryTests
{
    private static readonly string[] ExpectedInstruments =
    [
        "camera_agent.processing.graphs",
        "camera_agent.processing.nodes",
        "camera_agent.processing.outcomes",
        "camera_agent.processing.outputs",
        "camera_agent.processing.output.bytes",
        "camera_agent.processing.recovered",
        "camera_agent.processing.validation.duration",
        "camera_agent.processing.dependency_wait.duration",
        "camera_agent.processing.recipe.duration",
        "camera_agent.processing.persistence.duration",
        "camera_agent.processing.graph.duration"
    ];

    [TestMethod]
    public void RecordsBoundedGraphNodeAndOutputSignals()
    {
        var measurements = new ConcurrentQueue<(string Name, IReadOnlyList<string> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CaptureProcessingTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, GetTagNames(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, GetTagNames(tags))));
        listener.Start();
        using var telemetry = new CaptureProcessingTelemetry();
        var step = new TestStep();
        var node = new CaptureProcessingGraphNode(
            "preview", step, ["normalize"], true, "encoded-preview", FrameArtifactRole.Preview, "display");
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
            "encoded-preview", "1.0.0", "test-v1", JsonSerializer.SerializeToElement(new { mode = "packed" })));
        var product = new ProcessingProduct(
            FrameArtifactRole.Preview,
            "display",
            new string('A', 64),
            "application/x-hvo-packed-image",
            null,
            new byte[16],
            new string('B', 64),
            recipe,
            [],
            [Guid.NewGuid()],
            TimeSpan.FromSeconds(1),
            new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"));

        telemetry.RecordValidation(1, TimeSpan.FromMilliseconds(1));
        telemetry.GraphStarted();
        telemetry.RecordDependencyWait(node, TimeSpan.FromMilliseconds(2));
        telemetry.RecordNode(node, DurableProcessingNodeStatus.Completed, null, TimeSpan.FromMilliseconds(3));
        telemetry.RecordPersistence(node, product, TimeSpan.FromMilliseconds(4));
        telemetry.RecordRecovered(FrameArtifactRole.Preview, "display");
        telemetry.RecordGraph("completed", TimeSpan.FromMilliseconds(10));

        var snapshot = measurements.ToArray();
        var names = snapshot.Select(static measurement => measurement.Name).ToHashSet(StringComparer.Ordinal);
        CollectionAssert.IsSubsetOf(ExpectedInstruments, names.ToArray());
        var allowedTags = new HashSet<string>(
            ["step", "recipe", "role", "variant", "required", "outcome", "reason"],
            StringComparer.Ordinal);
        Assert.IsTrue(snapshot.SelectMany(static measurement => measurement.Tags).All(allowedTags.Contains));
    }

    private static string[] GetTagNames(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var names = new string[tags.Length];
        for (var index = 0; index < tags.Length; index++)
        {
            names[index] = tags[index].Key;
        }
        return names;
    }

    private sealed class TestStep : ICaptureProcessingStep
    {
        public string Name => "preview";
        public int Order => 0;

        public ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
