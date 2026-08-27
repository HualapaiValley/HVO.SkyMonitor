using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureProcessingGraphTests
{
    private const string HistoricalAnnotationPlanSha256 = "58C88E48E07E3297224DEC1894808E8E85E063D65F9B30EE4550C43EF16B6ADA";
    private const string HistoricalWeatherPlanSha256 = "7BDBFFBDAA1D2A219494E5CF9C13A10C433E605E28C375990DA2FFD39271C57F";
    private static readonly string[] ExpectedTopologicalOrder = ["first", "middle", "last"];
    private static readonly string[] ExpectedProducerConsumerOrder = ["producer", "consumer"];
    private static readonly string[] ExpectedProducerDependency = ["producer"];

    [TestMethod]
    public void CreateGraph_PublicationPolicyIsFailClosed()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var memoryOnly = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.MemoryOnly);

        Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(CreateExplicitConfig(
            new CaptureProcessingStepConfig(
                "Product",
                "product",
                DependsOn: ["$raw"],
                Publication: new CaptureProcessingPublicationPolicy(
                    (CaptureProcessingPersistenceMode)999)))));
        Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(CreateExplicitConfig(
            new CaptureProcessingStepConfig(
                "Test",
                "infrastructure",
                DependsOn: ["$raw"],
                Publication: memoryOnly))));
    }

    [TestMethod]
    public void CreateGraph_PreservesValidatedPublicationPolicyInPlan()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var policy = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.MemoryOnly);

        var graph = factory.CreateGraph(CreateExplicitConfig(new CaptureProcessingStepConfig(
            "Product", "product", DependsOn: ["$raw"], Publication: policy)));

        Assert.AreEqual(policy, graph.Nodes.Single().Publication);
        graph.DisposeSteps();
    }

    [TestMethod]
    public void CreateGraph_ExplicitV2RejectsDurableOutputWithoutEnabledStorageOwner()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = Path.GetTempPath()
            })
            .Build());
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var durable = new CaptureProcessingStepConfig(
            "Calibration",
            "calibration",
            DependsOn: ["$raw"],
            Publication: new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal));

        var missing = Assert.ThrowsExactly<InvalidOperationException>(() =>
            factory.CreateGraph(CreateExplicitConfig(durable)));
        var disabled = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(CreateExplicitConfig(
            durable,
            new CaptureProcessingStepConfig(
                "Storage", "storage", DependsOn: ["calibration"], Enabled: false))));

        StringAssert.Contains(missing.Message, "enabled Storage step", StringComparison.Ordinal);
        StringAssert.Contains(disabled.Message, "enabled Storage step", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_PublicationHashesUseStableStringEnumTokens()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var policy = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.MemoryOnly);
        var config = CreateExplicitConfig(new CaptureProcessingStepConfig(
            "Product", "product", DependsOn: ["$raw"], Publication: policy));

        var first = factory.CreateGraph(config);
        var second = factory.CreateGraph(config);
        var policyJson = CaptureContractJson.SerializeToElement(policy);

        Assert.AreEqual("memory-only", policyJson.GetProperty("persistence").GetString());
        Assert.AreEqual(first.Nodes.Single().PlanSha256, second.Nodes.Single().PlanSha256);
        first.DisposeSteps();
        second.DisposeSteps();
    }

    [TestMethod]
    public void CreateGraph_PlanHashChangesForOutputSchemaAndOrderedMultiOutputContract()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        string Hash(Type type)
        {
            var factory = new CaptureProcessingPipelineFactory(services,
                [new("Contract", type, typeof(GraphTestOptions))],
                NullLogger<CaptureProcessingPipelineFactory>.Instance, telemetry);
            var graph = factory.CreateGraph(CreateExplicitConfig(
                new CaptureProcessingStepConfig("Contract", "contract", DependsOn: ["$raw"])));
            var hash = graph.Nodes.Single().PlanSha256;
            graph.DisposeSteps();
            return hash;
        }

        Assert.AreNotEqual(Hash(typeof(GraphSchemaV1Step)), Hash(typeof(GraphSchemaV2Step)));
        Assert.AreNotEqual(Hash(typeof(GraphMultiOutputStep)), Hash(typeof(GraphReorderedMultiOutputStep)));
    }

    [TestMethod]
    public void CreateGraph_PlanHashChangesForExactDependencyRequirementContract()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        string Hash(Type consumerType)
        {
            var factory = new CaptureProcessingPipelineFactory(services,
                [
                    new("Producer", typeof(GraphSchemaV1Step), typeof(GraphTestOptions)),
                    new("Consumer", consumerType, typeof(GraphTestOptions))
                ], NullLogger<CaptureProcessingPipelineFactory>.Instance, telemetry);
            var graph = factory.CreateGraph(CreateExplicitConfig(
                new CaptureProcessingStepConfig("Producer", "producer", DependsOn: ["$raw"]),
                new CaptureProcessingStepConfig("Consumer", "consumer", DependsOn: ["producer"])));
            var hash = graph.Nodes.Single(static node => node.Id == "consumer").PlanSha256;
            graph.DisposeSteps();
            return hash;
        }

        Assert.AreNotEqual(Hash(typeof(GraphRequiredContractStep)), Hash(typeof(GraphOptionalContractStep)));
        Assert.AreNotEqual(Hash(typeof(GraphRequiredContractStep)), Hash(typeof(GraphVariantContractStep)));
    }

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
    public void CreateGraph_RequiresEveryEnabledNodeToDeclareDependencies()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var config = CreateExplicitConfig(
            new CaptureProcessingStepConfig("Consumer", "consumer", 100),
            new CaptureProcessingStepConfig("Calibrated", "producer", 0));

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(config));
        var legacyGraph = factory.CreateGraph(CreateLegacyConfig(
            new CaptureProcessingStepConfig("Consumer", "consumer", 100),
            new CaptureProcessingStepConfig("Calibrated", "producer", 0)));

        StringAssert.Contains(exception.Message, "must declare an explicit dependency", StringComparison.Ordinal);
        CollectionAssert.AreEqual(
            ExpectedProducerConsumerOrder,
            legacyGraph.Nodes.Select(static node => node.Id).ToArray());
        CollectionAssert.AreEqual(ExpectedProducerDependency, legacyGraph.Nodes[1].Dependencies.ToArray());
        legacyGraph.DisposeSteps();
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
                "Product", "display", Options: JsonSerializer.SerializeToElement(new { variant = "display" })),
            new CaptureProcessingStepConfig(
                "Product", "calibrated", Options: JsonSerializer.SerializeToElement(new { variant = "calibrated" }))
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
                "Calibrated", "one", Options: JsonSerializer.SerializeToElement(new { variant = "one" })),
            new CaptureProcessingStepConfig(
                "Calibrated", "two", Options: JsonSerializer.SerializeToElement(new { variant = "two" })),
            new CaptureProcessingStepConfig("Consumer", "consumer", DependsOn: ["one", "two"])
        };

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(CreateConfig(steps)));

        StringAssert.Contains(exception.Message, "exactly one unambiguous compatible producer", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_ExcludesTopLevelDisabledNode()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var config = CreateExplicitConfig(
            new CaptureProcessingStepConfig(
                "Product", "disabled", DependsOn: ["$raw"], Enabled: false),
            new CaptureProcessingStepConfig("Test", "storage", DependsOn: ["$raw"]));

        var graph = factory.CreateGraph(config);

        Assert.HasCount(1, graph.Nodes);
        Assert.AreEqual("storage", graph.Nodes[0].Id);
        graph.DisposeSteps();
    }

    [TestMethod]
    public void CreateGraph_ExplicitV2UsesDeclaredRawAndProducerDependencies()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var config = CreateExplicitConfig(
            new CaptureProcessingStepConfig("Calibrated", "producer", DependsOn: ["$raw"]),
            new CaptureProcessingStepConfig("Consumer", "consumer", DependsOn: ["producer"]));

        var graph = factory.CreateGraph(config);

        CollectionAssert.AreEqual(ExpectedProducerConsumerOrder, graph.Nodes.Select(static node => node.Id).ToArray());
        Assert.IsEmpty(graph.Nodes[0].Dependencies);
        CollectionAssert.AreEqual(ExpectedProducerDependency, graph.Nodes[1].Dependencies.ToArray());
        graph.DisposeSteps();
    }

    [TestMethod]
    public void CreateGraph_ExplicitV2RejectsEnabledDependentOnDisabledNode()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var config = CreateExplicitConfig(
            new CaptureProcessingStepConfig("Calibrated", "producer", DependsOn: ["$raw"], Enabled: false),
            new CaptureProcessingStepConfig("Consumer", "consumer", DependsOn: ["producer"]));

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(config));

        StringAssert.Contains(exception.Message, "depends on disabled step 'producer'", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_ExplicitV2RejectsImplicitRootAndImplementationTypeName()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);

        var implicitRoot = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(
            CreateExplicitConfig(new CaptureProcessingStepConfig("Calibrated", "producer"))));
        var implementationName = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(
            CreateExplicitConfig(new CaptureProcessingStepConfig(
                typeof(GraphCalibratedStep).FullName!, "producer", DependsOn: ["$raw"]))));

        StringAssert.Contains(implicitRoot.Message, "must declare an explicit dependency", StringComparison.Ordinal);
        StringAssert.Contains(implementationName.Message, "not a registered stable alias", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_RejectsDynamicAliasesAndReservedRawIdentifier()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var implementationName = typeof(GraphDynamicStep).FullName!;
        var aliasException = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(
            CreateExplicitConfig(new CaptureProcessingStepConfig(
                implementationName, "producer", DependsOn: ["$raw"]))));
        var registeredImplementationException = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(
            CreateExplicitConfig(new CaptureProcessingStepConfig(
                typeof(GraphCalibratedStep).FullName!, "registered", DependsOn: ["$raw"]))));
        var reservedIdException = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(
            CreateExplicitConfig(new CaptureProcessingStepConfig("Calibrated", "$raw", DependsOn: ["$raw"]))));
        var legacyDynamic = factory.CreateGraph(CreateLegacyConfig(new CaptureProcessingStepConfig(
            implementationName, "legacy", DependsOn: [])));

        StringAssert.Contains(aliasException.Message, "not a registered stable alias", StringComparison.Ordinal);
        StringAssert.Contains(registeredImplementationException.Message, "not a registered stable alias", StringComparison.Ordinal);
        StringAssert.Contains(reservedIdException.Message, "is reserved", StringComparison.Ordinal);
        legacyDynamic.DisposeSteps();
    }

    [TestMethod]
    public void CreateGraph_ExplicitV2RejectsOptionsLevelEnabledAuthority()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var config = CreateExplicitConfig(new CaptureProcessingStepConfig(
            "Calibrated",
            "producer",
            Options: JsonSerializer.SerializeToElement(new GraphTestOptions { Enabled = false }),
            DependsOn: ["$raw"],
            Enabled: false));

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(config));

        StringAssert.Contains(exception.Message, "top-level enabled field", StringComparison.Ordinal);
    }

    [TestMethod]
    public void PreviewPlan_ExplicitV2ReturnsDesiredAndEffectiveHashesWithoutDisabledNodes()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var config = CreateExplicitConfig(
            new CaptureProcessingStepConfig("Product", "disabled", DependsOn: ["$raw"], Enabled: false),
            new CaptureProcessingStepConfig("Calibrated", "producer", DependsOn: ["$raw"]),
            new CaptureProcessingStepConfig("Consumer", "consumer", DependsOn: ["producer"]));

        var preview = factory.PreviewPlan(config);

        Assert.AreEqual(CapturePipelineSchemaVersions.ExplicitV2, preview.SchemaVersion);
        Assert.AreEqual(CapturePipelineDependencyPolicy.RejectEnabledDependent, preview.DependencyPolicy);
        Assert.HasCount(3, preview.DesiredNodes);
        Assert.HasCount(2, preview.EffectiveNodes);
        Assert.IsFalse(preview.DesiredNodes.Single(static node => node.Id == "disabled").Enabled);
        Assert.IsFalse(preview.EffectiveNodes.Any(static node => node.Id == "disabled"));
        Assert.AreEqual(64, preview.DesiredSha256.Length);
        Assert.AreEqual(64, preview.EffectiveSha256.Length);
        Assert.AreNotEqual(preview.DesiredSha256, preview.EffectiveSha256);
    }

    [TestMethod]
    public void PreviewPlan_HashesOptionsAndOrder()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = CreateFactory(services, telemetry);
        var first = CreateExplicitConfig(new CaptureProcessingStepConfig(
            "Calibrated", "producer", 10,
            JsonSerializer.SerializeToElement(new { variant = "first" }), ["$raw"]));
        var second = CreateExplicitConfig(new CaptureProcessingStepConfig(
            "Calibrated", "producer", 20,
            JsonSerializer.SerializeToElement(new { variant = "second" }), ["$raw"]));
        var defaulted = CreateExplicitConfig(new CaptureProcessingStepConfig(
            "calibrated", "defaulted", DependsOn: ["$raw"]));
        var materializedDefaults = CreateExplicitConfig(new CaptureProcessingStepConfig(
            "Calibrated", "defaulted", 0,
            JsonSerializer.SerializeToElement(new { variant = "default" }), ["$raw"]));

        var firstPreview = factory.PreviewPlan(first);
        var secondPreview = factory.PreviewPlan(second);
        var defaultedPreview = factory.PreviewPlan(defaulted);
        var materializedPreview = factory.PreviewPlan(materializedDefaults);
        Assert.AreNotEqual(firstPreview.DesiredSha256, secondPreview.DesiredSha256);
        Assert.AreNotEqual(firstPreview.EffectiveSha256, secondPreview.EffectiveSha256);
        Assert.AreNotEqual(defaultedPreview.DesiredSha256, materializedPreview.DesiredSha256);
        Assert.AreEqual(defaultedPreview.EffectiveSha256, materializedPreview.EffectiveSha256);
        Assert.AreEqual(
            CaptureContractJson.ComputeCanonicalJsonSha256(defaultedPreview.EffectiveNodes),
            CaptureContractJson.ComputeCanonicalJsonSha256(materializedPreview.EffectiveNodes));
    }

    [TestMethod]
    public void LocalFileProfileUsesCurrentExplicitPipeline()
    {
        var legacyConfiguration = CreateConfig(new CaptureProcessingStepConfig(
            "Calibrated", "producer", DependsOn: ["$raw"]));
        var configuration = legacyConfiguration with
        {
            Rig = legacyConfiguration.Rig with
            {
                ControlPolicy = new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }
            }
        };
        var schedule = new CaptureScheduleDefinition(
            "capture-schedule-v1",
            [new CaptureScheduleSetpointProfile(
                "night", TimeSpan.FromSeconds(1), 1, TimeSpan.FromSeconds(2))],
            [new CaptureWeeklyScheduleWindow(
                "monday", DayOfWeek.Monday,
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(0, 0)),
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(0, 0), DayOffset: 1),
                "night")]);

        var profile = LocalCaptureProfileDefinition.CreateForConfiguration(configuration, schedule);
        var applied = profile.ApplyTo(configuration);
        var serialized = CaptureContractJson.SerializeToElement(profile);
        var legacySchedule = new CaptureScheduleDefinition(
            "capture-schedule-v1",
            schedule.SetpointProfiles,
            [],
            LegacyAlwaysOpen: true,
            LegacySetpointProfileId: "night");
        var legacy = new LocalCaptureProfileDefinition(
            LocalCaptureProfileDefinition.LegacySchemaVersion,
            legacyConfiguration.Module,
            legacyConfiguration.Rig,
            legacyConfiguration.Pipeline.Steps,
            legacySchedule);
        var appliedLegacy = legacy.ApplyTo(configuration);
        var serializedLegacy = CaptureContractJson.SerializeToElement(legacy);

        Assert.AreEqual(LocalCaptureProfileDefinition.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.IsTrue(LocalCaptureProfileContract.Validate(profile).IsValid);
        Assert.IsFalse(LocalCaptureProfileContract.Validate(legacy).IsValid);
        Assert.IsTrue(LocalCaptureProfileContract.ValidatePersistedRevision(legacy).IsValid);
        Assert.IsTrue(serialized.TryGetProperty("dependencyPolicy", out _));
        Assert.IsFalse(serializedLegacy.TryGetProperty("dependencyPolicy", out _));
        Assert.AreEqual(
            "reject-enabled-dependent-v1",
            serialized.GetProperty("dependencyPolicy").GetString());
        Assert.AreEqual(CapturePipelineSchemaVersions.ExplicitV2, applied.Pipeline.SchemaVersion);
        Assert.AreEqual(
            CapturePipelineDependencyPolicy.RejectEnabledDependent,
            applied.Pipeline.DependencyPolicy);
        Assert.AreEqual(CapturePipelineSchemaVersions.LegacyV1, appliedLegacy.Pipeline.SchemaVersion);
        Assert.AreEqual(CapturePipelineDependencyPolicy.LegacyInference, appliedLegacy.Pipeline.DependencyPolicy);
        Assert.AreEqual(
            "2BAEFC40154CF604FD5F7E6BA355F8CBAB3E084C89960B7F97DD1D7EA4A6AB2F",
            LocalCaptureProfileContract.ComputePersistedRevisionSha256(legacy));
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var executableLegacy = legacy with
        {
            ProcessingSteps =
            [
                new CaptureProcessingStepConfig("Consumer", "consumer", 100),
                new CaptureProcessingStepConfig("Calibrated", "producer", 0)
            ]
        };
        var legacyGraph = CreateFactory(services, telemetry).CreateGraph(executableLegacy.ApplyTo(configuration));
        CollectionAssert.AreEqual(
            ExpectedProducerConsumerOrder,
            legacyGraph.Nodes.Select(static node => node.Id).ToArray());
        legacyGraph.DisposeSteps();
    }

    [TestMethod]
    public void StandardLaneCacheIdentityChangesForSchemaTransitions()
    {
        var steps = new[] { new CaptureProcessingStepConfig("Calibrated", "producer", DependsOn: ["$raw"]) };
        var valid = CreateExplicitConfig(steps);
        var unknownSchema = valid with
        {
            Pipeline = valid.Pipeline! with { SchemaVersion = "cameraagent-capture-pipeline-v3" }
        };
        Assert.AreNotEqual(
            StandardCaptureLaneHandler.ComputePipelineKey(valid),
            StandardCaptureLaneHandler.ComputePipelineKey(unknownSchema));
    }

    [TestMethod]
    public void PreviewPlan_ProductionAliasesComposeCalibratedCombinedAndQualityProducts()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = Path.GetTempPath()
            })
            .Build());
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var registrations = provider.GetServices<CaptureProcessingStepRegistration>().ToArray();
        var baseline = CreateConfig();
        var config = CreateExplicitConfig(
            new CaptureProcessingStepConfig("Calibration", "calibration", DependsOn: ["$raw"]),
            new CaptureProcessingStepConfig("CalibratedPreview", "calibrated-preview", DependsOn: ["calibration"]),
            new CaptureProcessingStepConfig("RollingCombination", "rolling", DependsOn: ["calibration"]),
            new CaptureProcessingStepConfig("CombinedPreview", "combined-preview", DependsOn: ["rolling"]),
            new CaptureProcessingStepConfig("ImageQuality", "quality", DependsOn: ["calibration"]),
            new CaptureProcessingStepConfig("CloudAssessment", "cloud", DependsOn: ["calibration"]),
            new CaptureProcessingStepConfig("Annotation", "sky-annotation", DependsOn: ["combined-preview"]),
            new CaptureProcessingStepConfig("WeatherCloudOverlay", "weather-overlay", DependsOn: ["sky-annotation", "cloud"]),
            new CaptureProcessingStepConfig(
                "Storage",
                "storage",
                Options: JsonSerializer.SerializeToElement(new
                {
                    storageRoot = "/private/archive",
                    endpoint = "https://secret-endpoint.example",
                    callbackUrl = "https://secret-url.example",
                    serviceUri = "https://secret-uri.example"
                }),
                DependsOn:
            [
                "calibration", "calibrated-preview", "rolling", "combined-preview", "quality", "cloud",
                "sky-annotation", "weather-overlay"
            ]),
            new CaptureProcessingStepConfig("Telemetry", "telemetry", DependsOn:
            [
                "calibration", "calibrated-preview", "rolling", "combined-preview", "quality", "cloud",
                "sky-annotation", "weather-overlay", "storage"
            ])) with
        {
            Rig = baseline.Rig with
            {
                Sensor = baseline.Rig.Sensor with { PixelFormat = CameraPixelFormat.Mono16 }
            }
        };

        var preview = factory.PreviewPlan(config);

        Assert.AreEqual(
            "calibrated-preview",
            preview.EffectiveNodes.Single(static node => node.Alias == "CalibratedPreview").OutputVariant);
        Assert.AreEqual(
            "combined-preview",
            preview.EffectiveNodes.Single(static node => node.Alias == "CombinedPreview").OutputVariant);
        var quality = preview.EffectiveNodes.Single(static node => node.Alias == "ImageQuality");
        Assert.AreEqual(FrameArtifactRole.Metadata, quality.OutputRole);
        Assert.AreEqual("image-quality-v1", quality.OutputVariant);
        Assert.AreEqual(BuiltInProcessingRecipes.ImageQuality, quality.RecipeName);
        Assert.AreEqual(
            FrameArtifactRole.AnnotatedPreview,
            preview.EffectiveNodes.Single(static node => node.Alias == "WeatherCloudOverlay").OutputRole);
        Assert.HasCount(10, preview.EffectiveNodes);
        Assert.IsFalse(quality.Options!.Value.TryGetProperty("enabled", out _));
        Assert.HasCount(20, registrations);
        Assert.IsNull(preview.EffectiveNodes.Single(static node => node.Alias == "Storage").OutputRole);
        Assert.IsNull(preview.EffectiveNodes.Single(static node => node.Alias == "Telemetry").OutputRole);
        Assert.AreEqual(
            "[redacted]",
            preview.EffectiveNodes.Single(static node => node.Alias == "Storage").Options!.Value
                .GetProperty("storageRoot").GetString());
        Assert.IsFalse(JsonSerializer.Serialize(preview).Contains("/private/archive", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(preview).Contains("secret-endpoint", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(preview).Contains("secret-url", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(preview).Contains("secret-uri", StringComparison.Ordinal));
        var legacyPreviewOptions = CaptureContractJson.SerializeToElement(new PreviewProcessingStepOptions());
        Assert.IsTrue(legacyPreviewOptions.GetProperty("enabled").GetBoolean());
        Assert.AreEqual("default", legacyPreviewOptions.GetProperty("outputVariant").GetString());

        using var telemetry = new CaptureProcessingTelemetry();
        var duplicate = Assert.ThrowsExactly<InvalidOperationException>(() => new CaptureProcessingPipelineFactory(
            provider,
            [
                new CaptureProcessingStepRegistration("Duplicate", typeof(GraphTestStep), typeof(GraphTestOptions)),
                new CaptureProcessingStepRegistration("duplicate", typeof(GraphProductStep), typeof(GraphTestOptions))
            ],
            NullLogger<CaptureProcessingPipelineFactory>.Instance,
            telemetry));
        StringAssert.Contains(duplicate.Message, "conflicting registrations", StringComparison.Ordinal);

        var rawStorage = Assert.ThrowsExactly<InvalidOperationException>(() => factory.PreviewPlan(
            CreateExplicitConfig(new CaptureProcessingStepConfig("Storage", "storage", DependsOn: ["$raw"]))));
        StringAssert.Contains(rawStorage.Message, "unsupported artifact dependency", StringComparison.Ordinal);

        var wrongMetadata = CreateExplicitConfig(
            new CaptureProcessingStepConfig("Calibration", "calibration", DependsOn: ["$raw"]),
            new CaptureProcessingStepConfig("RollingCombination", "rolling", DependsOn: ["calibration"]),
            new CaptureProcessingStepConfig("CombinedPreview", "combined-preview", DependsOn: ["rolling"]),
            new CaptureProcessingStepConfig("ImageQuality", "quality", DependsOn: ["calibration"]),
            new CaptureProcessingStepConfig("Annotation", "sky-annotation", DependsOn: ["combined-preview"]),
            new CaptureProcessingStepConfig("WeatherCloudOverlay", "weather-overlay", DependsOn: ["sky-annotation", "quality"])) with
        {
            Rig = config.Rig
        };
        var wrongMetadataException = Assert.ThrowsExactly<InvalidOperationException>(() => factory.PreviewPlan(wrongMetadata));
        StringAssert.Contains(wrongMetadataException.Message, "required compound inputs", StringComparison.Ordinal);

        var legacyPolicy = CreateConfig() with
        {
            Pipeline = new CapturePipelineConfig(
                [],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };
        legacyPolicy = legacyPolicy with
        {
            Pipeline = legacyPolicy.Pipeline with { DependencyPolicy = CapturePipelineDependencyPolicy.LegacyInference }
        };
        var policyException = Assert.ThrowsExactly<InvalidOperationException>(() => factory.PreviewPlan(legacyPolicy));
        StringAssert.Contains(policyException.Message, "dependency policy", StringComparison.Ordinal);
        var unknownSchema = config with
        {
            Pipeline = config.Pipeline with { SchemaVersion = "cameraagent-capture-pipeline-v3" }
        };
        var unknownSchemaException = Assert.ThrowsExactly<InvalidOperationException>(() => factory.PreviewPlan(unknownSchema));
        StringAssert.Contains(unknownSchemaException.Message, "Unsupported capture pipeline schema", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_LayeredPresentationRejectsWrongBasePreviewVariant()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = Path.GetTempPath()
            }).Build());
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var baseline = CreateConfig();
        var config = CreateExplicitConfig(
            new CaptureProcessingStepConfig("Calibration", "calibration", DependsOn: ["$raw"]),
            new CaptureProcessingStepConfig("RollingCombination", "rolling", DependsOn: ["calibration"]),
            new CaptureProcessingStepConfig("CombinedPreview", "wrong-preview", DependsOn: ["rolling"],
                Options: JsonSerializer.SerializeToElement(new { outputVariant = "not-combined-preview" })),
            new CaptureProcessingStepConfig("OverlayManifest", "manifest", DependsOn: ["wrong-preview"])) with
        {
            Rig = baseline.Rig with
            {
                Sensor = baseline.Rig.Sensor with { PixelFormat = CameraPixelFormat.Mono16 }
            }
        };

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(config));
        StringAssert.Contains(exception.Message, "required dependency inputs", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_LayeredPresentationRejectsWrongBasePreviewRecipe()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = Path.GetTempPath()
            }).Build());
        using var provider = services.BuildServiceProvider();
        var registrations = provider.GetServices<CaptureProcessingStepRegistration>().Append(
            new CaptureProcessingStepRegistration("WrongPreview", typeof(GraphProductStep), typeof(GraphTestOptions)));
        using var telemetry = new CaptureProcessingTelemetry();
        var factory = new CaptureProcessingPipelineFactory(provider, registrations,
            NullLogger<CaptureProcessingPipelineFactory>.Instance, telemetry);
        var config = CreateExplicitConfig(
            new CaptureProcessingStepConfig("WrongPreview", "wrong-preview", DependsOn: ["$raw"],
                Options: JsonSerializer.SerializeToElement(new { variant = "combined-preview" })),
            new CaptureProcessingStepConfig("OverlayManifest", "manifest", DependsOn: ["wrong-preview"]));

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(config));
        StringAssert.Contains(exception.Message, "required dependency inputs", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreateGraph_LegacyPlanCompatibilityIsAllowlistedAndOptionExact()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = Path.GetTempPath()
            }).Build());
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        var baseline = CreateConfig();
        CameraModuleConfig Config(int radius) => CreateExplicitConfig(
            new CaptureProcessingStepConfig("Calibration", "calibration", DependsOn: ["$raw"]),
            new CaptureProcessingStepConfig("RollingCombination", "rolling", DependsOn: ["calibration"]),
            new CaptureProcessingStepConfig("CombinedPreview", "combined-preview", DependsOn: ["rolling"]),
            new CaptureProcessingStepConfig("CloudAssessment", "cloud", DependsOn: ["calibration"]),
            new CaptureProcessingStepConfig("Annotation", "sky-annotation", DependsOn: ["combined-preview"],
                Options: JsonSerializer.SerializeToElement(new { markRadius = radius })),
            new CaptureProcessingStepConfig("WeatherCloudOverlay", "weather-overlay",
                DependsOn: ["sky-annotation", "cloud"])) with
        {
            Rig = baseline.Rig with
            {
                Sensor = baseline.Rig.Sensor with { PixelFormat = CameraPixelFormat.Mono16 }
            }
        };

        var first = factory.CreateGraph(Config(4));
        var changed = factory.CreateGraph(Config(5));
        try
        {
            Assert.IsNotNull(first.Nodes.Single(static node => node.Id == "cloud").LegacyPlanSha256);
            Assert.IsNotNull(first.Nodes.Single(static node => node.Id == "calibration").LegacyPlanSha256);
            Assert.IsNotNull(first.Nodes.Single(static node => node.Id == "rolling").LegacyPlanSha256);
            Assert.IsNotNull(first.Nodes.Single(static node => node.Id == "combined-preview").LegacyPlanSha256);
            Assert.HasCount(64, first.Nodes.Single(static node => node.Id == "sky-annotation").LegacyPlanSha256!);
            Assert.HasCount(64, first.Nodes.Single(static node => node.Id == "weather-overlay").LegacyPlanSha256!);
            Assert.AreEqual(HistoricalAnnotationPlanSha256,
                first.Nodes.Single(static node => node.Id == "sky-annotation").LegacyPlanSha256);
            Assert.AreEqual(HistoricalWeatherPlanSha256,
                first.Nodes.Single(static node => node.Id == "weather-overlay").LegacyPlanSha256);
            Assert.AreNotEqual(
                first.Nodes.Single(static node => node.Id == "sky-annotation").LegacyPlanSha256,
                changed.Nodes.Single(static node => node.Id == "sky-annotation").LegacyPlanSha256);
            Assert.AreEqual(
                first.Nodes.Single(static node => node.Id == "weather-overlay").LegacyPlanSha256,
                changed.Nodes.Single(static node => node.Id == "weather-overlay").LegacyPlanSha256);
        }
        finally
        {
            first.DisposeSteps();
            changed.DisposeSteps();
        }
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

    [TestMethod]
    public void CreateGraph_RepeatedMetadataRequirementsUseOneToOneAssignment()
    {
        using var telemetry = new CaptureProcessingTelemetry();
        using var services = new ServiceCollection().BuildServiceProvider();
        var factory = new CaptureProcessingPipelineFactory(
            services,
            [
                new("MetadataA", typeof(GraphMetadataAStep), typeof(GraphTestOptions)),
                new("MetadataB", typeof(GraphMetadataBStep), typeof(GraphTestOptions)),
                new("RepeatedMetadata", typeof(GraphRepeatedMetadataStep), typeof(GraphTestOptions))
            ],
            NullLogger<CaptureProcessingPipelineFactory>.Instance,
            telemetry);
        var valid = CreateExplicitConfig(
            new CaptureProcessingStepConfig("MetadataA", "a", DependsOn: ["$raw"], Options: JsonSerializer.SerializeToElement(new { variant = "a" })),
            new CaptureProcessingStepConfig("MetadataB", "b", DependsOn: ["$raw"], Options: JsonSerializer.SerializeToElement(new { variant = "b" })),
            new CaptureProcessingStepConfig("RepeatedMetadata", "consumer", DependsOn: ["a", "b"]));

        var graph = factory.CreateGraph(valid);
        try
        {
            Assert.HasCount(3, graph.Nodes);
        }
        finally
        {
            graph.DisposeSteps();
        }

        var wrong = valid with
        {
            Pipeline = valid.Pipeline! with
            {
                Steps =
                [
                    new CaptureProcessingStepConfig("MetadataA", "a", DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig("MetadataA", "a2", DependsOn: ["$raw"], Options: JsonSerializer.SerializeToElement(new { variant = "second" })),
                    new CaptureProcessingStepConfig("RepeatedMetadata", "consumer", DependsOn: ["a", "a2"])
                ]
            }
        };
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateGraph(wrong));
        StringAssert.Contains(exception.Message, "required dependency inputs", StringComparison.Ordinal);
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
            new CapturePipelineConfig(steps.Select(static step => step.DependsOn is null
                ? step with { DependsOn = ["$raw"] }
                : step).ToArray()),
            AgentId: "agent-test");

    private static CameraModuleConfig CreateExplicitConfig(params CaptureProcessingStepConfig[] steps)
    {
        var config = CreateConfig();
        return config with { Pipeline = new CapturePipelineConfig(steps) };
    }

    private static CameraModuleConfig CreateLegacyConfig(params CaptureProcessingStepConfig[] steps)
    {
        var config = CreateConfig();
        return config with
        {
            Pipeline = new CapturePipelineConfig(
                steps,
                CapturePipelineSchemaVersions.LegacyV1,
                CapturePipelineDependencyPolicy.LegacyInference)
        };
    }

}

internal sealed class StaticConfigurationLoader(CameraModuleConfig config) : ICameraAgentConfigurationLoader
{
    public Task<CameraModuleConfig> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(config);
}

internal sealed class RejectingPipelineFactory : ICaptureProcessingPipelineFactory
{
    public CaptureProcessingGraph CreateGraph(CameraModuleConfig config)
        => throw new InvalidOperationException("invalid graph");

    public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config)
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

internal class GraphSchemaV1Step(
    CaptureProcessingStepMetadata metadata,
    GraphTestOptions options) : ConfigurableCaptureProcessingStep<GraphTestOptions>(metadata, options),
        ICaptureProcessingGraphStep
{
    public bool Enabled => true;
    public string RecipeName => "schema-product";
    public FrameArtifactRole OutputRole => FrameArtifactRole.Preview;
    public string OutputVariant => "preview";
    public virtual string? OutputSchemaVersion => "schema-v1";
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };
    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated through ActivatorUtilities in plan contract tests.")]
internal sealed class GraphSchemaV2Step(CaptureProcessingStepMetadata metadata, GraphTestOptions options)
    : GraphSchemaV1Step(metadata, options)
{
    public override string? OutputSchemaVersion => "schema-v2";
}

internal class GraphMultiOutputStep(
    CaptureProcessingStepMetadata metadata,
    GraphTestOptions options) : GraphSchemaV1Step(metadata, options), IMultiOutputCaptureProcessingGraphStep
{
    public virtual IReadOnlyList<CaptureProcessingOutputDescriptor> Outputs =>
    [
        new(FrameArtifactRole.Metadata, "first", "first-recipe", "first-schema"),
        new(FrameArtifactRole.Metadata, "second", "second-recipe", "second-schema")
    ];
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated through ActivatorUtilities in plan contract tests.")]
internal sealed class GraphReorderedMultiOutputStep(CaptureProcessingStepMetadata metadata, GraphTestOptions options)
    : GraphMultiOutputStep(metadata, options)
{
    public override IReadOnlyList<CaptureProcessingOutputDescriptor> Outputs =>
    [
        new(FrameArtifactRole.Metadata, "second", "second-recipe", "second-schema"),
        new(FrameArtifactRole.Metadata, "first", "first-recipe", "first-schema")
    ];
}

internal class GraphRequiredContractStep(
    CaptureProcessingStepMetadata metadata,
    GraphTestOptions options) : ConfigurableCaptureProcessingStep<GraphTestOptions>(metadata, options),
        ICaptureProcessingGraphStep, IRequiredCaptureProcessingDependencies
{
    public bool Enabled => true;
    public string RecipeName => "contract-consumer";
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => "consumer";
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview };
    public virtual IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview },
        new HashSet<string> { "schema-product" }, new HashSet<string> { "schema-v1" }, "preview")];
    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated through ActivatorUtilities in plan contract tests.")]
internal sealed class GraphOptionalContractStep(CaptureProcessingStepMetadata metadata, GraphTestOptions options)
    : GraphRequiredContractStep(metadata, options)
{
    public override IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview },
        new HashSet<string> { "schema-product" }, new HashSet<string> { "schema-v1" }, "preview", Required: false)];
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Instantiated through ActivatorUtilities in plan contract tests.")]
internal sealed class GraphVariantContractStep(CaptureProcessingStepMetadata metadata, GraphTestOptions options)
    : GraphRequiredContractStep(metadata, options)
{
    public override IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Preview },
        new HashSet<string> { "schema-product" }, new HashSet<string> { "schema-v1" })];
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

public sealed class GraphDynamicStep(
    CaptureProcessingStepMetadata metadata,
    GraphDynamicOptions options) : ConfigurableCaptureProcessingStep<GraphDynamicOptions>(metadata, options)
{
    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

public sealed class GraphDynamicOptions
{
}

public sealed class GraphMetadataAStep(
    CaptureProcessingStepMetadata metadata,
    GraphTestOptions options) : ConfigurableCaptureProcessingStep<GraphTestOptions>(metadata, options), ICaptureProcessingGraphStep
{
    public bool Enabled => true;
    public string RecipeName => "metadata-a";
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.Variant;
    public string? OutputSchemaVersion => "schema-a";
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };
    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class GraphMetadataBStep(
    CaptureProcessingStepMetadata metadata,
    GraphTestOptions options) : ConfigurableCaptureProcessingStep<GraphTestOptions>(metadata, options), ICaptureProcessingGraphStep
{
    public bool Enabled => true;
    public string RecipeName => "metadata-b";
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.Variant;
    public string? OutputSchemaVersion => "schema-b";
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };
    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class GraphRepeatedMetadataStep(
    CaptureProcessingStepMetadata metadata,
    GraphTestOptions options) : ConfigurableCaptureProcessingStep<GraphTestOptions>(metadata, options),
        ICaptureProcessingGraphStep, IRequiredCaptureProcessingDependencies
{
    public bool Enabled => true;
    public string RecipeName => "repeated-metadata";
    public FrameArtifactRole OutputRole => FrameArtifactRole.Preview;
    public string OutputVariant => Options.Variant;
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata };
    IReadOnlyList<CaptureProcessingDependencyRequirement> IRequiredCaptureProcessingDependencies.DependencyRequirements { get; } =
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { "metadata-a" }, new HashSet<string> { "schema-a" }),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata }, new HashSet<string> { "metadata-b" }, new HashSet<string> { "schema-b" })
    ];
    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
