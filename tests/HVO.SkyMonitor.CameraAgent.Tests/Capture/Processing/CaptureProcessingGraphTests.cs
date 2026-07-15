using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureProcessingGraphTests
{
    private static readonly string[] ExpectedTopologicalOrder = ["first", "middle", "last"];
    private static readonly string[] ExpectedLegacyOrder = ["producer", "consumer"];
    private static readonly string[] ExpectedLegacyDependencies = ["producer"];

    [TestMethod]
    public void CreateGraph_UsesDeterministicTopologicalOrder()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var config = CreateConfig(
            Step("last", 0, ["middle"]),
            Step("first", 100),
            Step("middle", 50, ["first"]));

        var graph = factory.CreateGraph(config);

        CollectionAssert.AreEqual(ExpectedTopologicalOrder, graph.Nodes.Select(static node => node.Id).ToArray());
        graph.DisposeSteps();
    }

    [TestMethod]
    public void CreateGraph_LegacyDependencyInferenceUsesOrderNotSerializedPosition()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var config = CreateConfig(
            new CaptureProcessingStepConfig("Consumer", "consumer", 100),
            new CaptureProcessingStepConfig("Calibrated", "producer", 0));

        var graph = factory.CreateGraph(config);

        CollectionAssert.AreEqual(ExpectedLegacyOrder, graph.Nodes.Select(static node => node.Id).ToArray());
        CollectionAssert.AreEqual(ExpectedLegacyDependencies, graph.Nodes[1].Dependencies.ToArray());
        graph.DisposeSteps();
    }

    [TestMethod]
    public void CreateGraph_RejectsMissingProducer()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            factory.CreateGraph(CreateConfig(Step("consumer", 0, ["absent"]))));

        StringAssert.Contains(exception.Message, "missing step 'absent'", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_RejectsCycleBeforeExecution()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(CreateConfig(
            Step("one", 0, ["two"]),
            Step("two", 0, ["one"]))));

        StringAssert.Contains(exception.Message, "dependency cycle", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_RejectsDuplicateLogicalOutput()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var steps = new[]
        {
            new CaptureProcessingStepConfig("Product", "one"),
            new CaptureProcessingStepConfig("Product", "two")
        };

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(CreateConfig(steps)));

        StringAssert.Contains(exception.Message, "duplicate output", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_AllowsSameRoleWithDistinctVariants()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var steps = new[]
        {
            new CaptureProcessingStepConfig(
                "Product", "display", Options: JsonSerializer.SerializeToElement(new GraphTestOptions { Variant = "display" })),
            new CaptureProcessingStepConfig(
                "Product", "calibrated", Options: JsonSerializer.SerializeToElement(new GraphTestOptions { Variant = "calibrated" }))
        };

        var graph = factory.CreateGraph(CreateConfig(steps));

        Assert.HasCount(2, graph.Nodes);
        graph.DisposeSteps();
    }

    [TestMethod]
    public void CreateGraph_RejectsIncompatibleDependencyOutput()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var steps = new[]
        {
            new CaptureProcessingStepConfig("Product", "producer"),
            new CaptureProcessingStepConfig("Consumer", "consumer", DependsOn: ["producer"])
        };

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(CreateConfig(steps)));

        StringAssert.Contains(exception.Message, "exactly one unambiguous compatible producer", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_RejectsDependencyThatProducesNoArtifact()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var steps = new[]
        {
            new CaptureProcessingStepConfig("Test", "infrastructure"),
            new CaptureProcessingStepConfig("Consumer", "consumer", DependsOn: ["infrastructure"])
        };

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(CreateConfig(steps)));

        StringAssert.Contains(exception.Message, "does not produce an artifact", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_RejectsAmbiguousCompatibleProducers()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var steps = new[]
        {
            new CaptureProcessingStepConfig(
                "Calibrated", "one", Options: JsonSerializer.SerializeToElement(new GraphTestOptions { Variant = "one" })),
            new CaptureProcessingStepConfig(
                "Calibrated", "two", Options: JsonSerializer.SerializeToElement(new GraphTestOptions { Variant = "two" })),
            new CaptureProcessingStepConfig("Consumer", "consumer", DependsOn: ["one", "two"])
        };

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(CreateConfig(steps)));

        StringAssert.Contains(exception.Message, "exactly one unambiguous compatible producer", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_ExcludesDisabledLegacyRecipeNode()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var config = CreateConfig(
            new CaptureProcessingStepConfig(
                "Product", "disabled", Options: JsonSerializer.SerializeToElement(new GraphTestOptions { Enabled = false })),
            new CaptureProcessingStepConfig("Test", "storage"));

        var graph = factory.CreateGraph(config);

        Assert.HasCount(1, graph.Nodes);
        Assert.AreEqual("storage", graph.Nodes[0].Id);
        graph.DisposeSteps();
    }

    [TestMethod]
    public async Task ConfigurationInitializer_RejectsInvalidGraphBeforePublishingConfiguration()
    {
        var config = CreateConfig(Step("invalid", 0));
        var accessor = new CameraAgentConfigurationAccessor();
        var initializer = new CameraAgentConfigurationInitializer(
            new StaticConfigurationLoader(config),
            accessor,
            new RejectingPipelineFactory(),
            NullLogger<CameraAgentConfigurationInitializer>.Instance);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            initializer.StartAsync(CancellationToken.None)).ConfigureAwait(false);

        Assert.IsFalse(accessor.IsConfigured);
    }

    private static CaptureProcessingPipelineFactory CreateFactory(
        IServiceProvider services,
        CaptureProcessingTelemetry telemetry)
        => new(
            services,
            [
                new CaptureProcessingStepRegistration("Test", typeof(GraphTestStep), typeof(GraphTestOptions)),
                new CaptureProcessingStepRegistration("Product", typeof(GraphProductStep), typeof(GraphTestOptions)),
                new CaptureProcessingStepRegistration("Calibrated", typeof(GraphCalibratedStep), typeof(GraphTestOptions)),
                new CaptureProcessingStepRegistration("Consumer", typeof(GraphConsumerStep), typeof(GraphTestOptions))
            ],
            NullLogger<CaptureProcessingPipelineFactory>.Instance,
            telemetry);

    private static CaptureProcessingStepConfig Step(string id, int order, IReadOnlyList<string>? dependencies = null)
        => new("Test", id, order, JsonSerializer.SerializeToElement(new { }), dependencies);

    private static CameraModuleConfig CreateConfig(params CaptureProcessingStepConfig[] steps)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 2, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("Test", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            steps,
            AgentId: "agent-test");

}

internal sealed class StaticConfigurationLoader(CameraModuleConfig config) : ICameraAgentConfigurationLoader
{
    public Task<CameraModuleConfig> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(config);
}

internal sealed class RejectingPipelineFactory : ICaptureProcessingPipelineFactory
{
    public IReadOnlyList<ICaptureProcessingStep> CreatePipeline(CameraModuleConfig config) => [];

    public CaptureProcessingGraph CreateGraph(CameraModuleConfig config)
        => throw new InvalidOperationException("invalid graph");
}

public sealed class GraphTestOptions
{
    public bool Enabled { get; init; } = true;
    public string Variant { get; init; } = "default";
}

public sealed class GraphTestStep(
    CaptureProcessingStepMetadata metadata,
    GraphTestOptions options) : ConfigurableCaptureProcessingStep<GraphTestOptions>(metadata, options)
{
    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

public sealed class GraphProductStep(
    CaptureProcessingStepMetadata metadata,
    GraphTestOptions options) : ConfigurableCaptureProcessingStep<GraphTestOptions>(metadata, options), ICaptureProcessingGraphStep
{
    public bool Enabled => Options.Enabled;
    public string RecipeName => "test-product";
    public FrameArtifactRole OutputRole => FrameArtifactRole.Preview;
    public string OutputVariant => Options.Variant;
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

public sealed class GraphCalibratedStep(
    CaptureProcessingStepMetadata metadata,
    GraphTestOptions options) : ConfigurableCaptureProcessingStep<GraphTestOptions>(metadata, options), ICaptureProcessingGraphStep
{
    public bool Enabled => Options.Enabled;
    public string RecipeName => "test-calibration";
    public FrameArtifactRole OutputRole => FrameArtifactRole.Calibrated;
    public string OutputVariant => Options.Variant;
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

public sealed class GraphConsumerStep(
    CaptureProcessingStepMetadata metadata,
    GraphTestOptions options) : ConfigurableCaptureProcessingStep<GraphTestOptions>(metadata, options), ICaptureProcessingGraphStep
{
    public bool Enabled => true;
    public string RecipeName => "test-consumer";
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => "default";
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Calibrated };

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
