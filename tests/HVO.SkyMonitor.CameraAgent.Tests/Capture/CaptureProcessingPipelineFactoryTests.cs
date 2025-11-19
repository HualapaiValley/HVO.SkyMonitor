using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
public sealed class CaptureProcessingPipelineFactoryTests
{
    [TestMethod]
    public void CreatePipeline_UsesConfiguredSteps()
    {
        var serviceProvider = BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var stepOptions = JsonSerializer.SerializeToElement(new { Value = 42 });
        var pipelineConfig = new CapturePipelineConfig(new[]
        {
            new CaptureProcessingStepConfig("Test", "Custom", 5, stepOptions)
        });
        var config = TestCameraModuleConfigFactory.Create(pipeline: pipelineConfig);

        var steps = factory.CreatePipeline(config);

        Assert.AreEqual(1, steps.Count);
        var step = Assert.IsInstanceOfType<TestProcessingStep>(steps[0]);
        Assert.AreEqual("Custom", step.Name);
        Assert.AreEqual(5, step.Order);
        Assert.AreEqual(42, step.ConfiguredOptions.Value);
    }

    [TestMethod]
    public void CreatePipeline_WhenConfigMissing_UsesRegisteredDefaults()
    {
        var serviceProvider = BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var config = TestCameraModuleConfigFactory.Create();

        var steps = factory.CreatePipeline(config);

        Assert.AreEqual(1, steps.Count);
        var step = Assert.IsInstanceOfType<TestProcessingStep>(steps[0]);
        Assert.AreEqual("Test", step.Name);
        Assert.AreEqual(10, step.Order);
        Assert.AreEqual(7, step.ConfiguredOptions.Value);
    }

    [TestMethod]
    public void CreatePipeline_DuplicateIdentifiers_Throws()
    {
        var serviceProvider = BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var pipelineConfig = new CapturePipelineConfig(new[]
        {
            new CaptureProcessingStepConfig("Test", "Duplicate", null, null),
            new CaptureProcessingStepConfig("Test", "Duplicate", 20, null)
        });
        var config = TestCameraModuleConfigFactory.Create(pipeline: pipelineConfig);

        try
        {
            factory.CreatePipeline(config);
            Assert.Fail("Expected InvalidOperationException for duplicate identifiers.");
        }
        catch (InvalidOperationException)
        {
            // Expected path
        }
    }

    [TestMethod]
    public void CreatePipeline_InvalidOptions_ThrowsValidation()
    {
        var serviceProvider = BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var invalidOptions = JsonSerializer.SerializeToElement(new { Value = 200 });
        var pipelineConfig = new CapturePipelineConfig(new[]
        {
            new CaptureProcessingStepConfig("Test", null, null, invalidOptions)
        });
        var config = TestCameraModuleConfigFactory.Create(pipeline: pipelineConfig);

        try
        {
            factory.CreatePipeline(config);
            Assert.Fail("Expected ValidationException for invalid options.");
        }
        catch (ValidationException)
        {
            // Expected path
        }
    }

    [TestMethod]
    public void CreatePipeline_ResolvesStepsByTypeName()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<ReflectionProcessingStep>();
        services.AddSingleton(new CaptureProcessingStepRegistration("Test", typeof(TestProcessingStep), typeof(TestStepOptions), 10));
        services.AddSingleton<ICaptureProcessingPipelineFactory, CaptureProcessingPipelineFactory>();
        var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var pipelineConfig = new CapturePipelineConfig(new[]
        {
            new CaptureProcessingStepConfig(typeof(ReflectionProcessingStep).FullName!, "Dynamic", 42, JsonSerializer.SerializeToElement(new { Mode = "Mirror" }))
        });
        var config = TestCameraModuleConfigFactory.Create(pipeline: pipelineConfig);

        var steps = factory.CreatePipeline(config);

        Assert.AreEqual(1, steps.Count);
        var step = Assert.IsInstanceOfType<ReflectionProcessingStep>(steps[0]);
        Assert.AreEqual("Dynamic", step.Name);
        Assert.AreEqual("Mirror", step.ConfiguredOptions.Mode);
        Assert.AreEqual(42, step.Order);
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<TestProcessingStep>();
        services.AddSingleton(new CaptureProcessingStepRegistration("Test", typeof(TestProcessingStep), typeof(TestStepOptions), 10));
        services.AddSingleton<ICaptureProcessingPipelineFactory, CaptureProcessingPipelineFactory>();
        return services.BuildServiceProvider();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated via dependency injection during tests.")]
    private sealed class TestProcessingStep : ConfigurableCaptureProcessingStep<TestStepOptions>
    {
        public TestProcessingStep(CaptureProcessingStepMetadata metadata, TestStepOptions options)
            : base(metadata, options)
        {
        }

        public TestStepOptions ConfiguredOptions => base.Options;

        public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Constructed via JSON binding during tests.")]
    private sealed class TestStepOptions
    {
        [System.ComponentModel.DataAnnotations.Range(0, 100)]
        public int Value { get; init; } = 7;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated indirectly for testing reflection-based resolution.")]
    private sealed class ReflectionProcessingStep : ConfigurableCaptureProcessingStep<ReflectionStepOptions>
    {
        public ReflectionProcessingStep(CaptureProcessingStepMetadata metadata, ReflectionStepOptions options)
            : base(metadata, options)
        {
        }

        public ReflectionStepOptions ConfiguredOptions => base.Options;

        public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Constructed via JSON binding during tests.")]
    private sealed class ReflectionStepOptions
    {
        [System.ComponentModel.DataAnnotations.Required(AllowEmptyStrings = false)]
        public string Mode { get; init; } = "Passthrough";
    }

}