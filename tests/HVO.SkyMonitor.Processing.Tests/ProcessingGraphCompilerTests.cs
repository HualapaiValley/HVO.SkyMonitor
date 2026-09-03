using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Repository test naming convention.")]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Single-use expected test values are clearer inline.")]
public sealed class ProcessingGraphCompilerTests
{
    private static readonly ProcessingGraphProductContract Raw = new(
        FrameArtifactRole.Raw,
        "source",
        ProcessingProductKind.PixelData);

    [TestMethod]
    public void Compile_ProducesDeterministicLinearBranchAndJoinPlan()
    {
        var graph = Graph(
            Node("materialize", 40, [Dependency("preview"), Dependency("facts")],
                [Input(FrameArtifactRole.Preview, name: "preview"), Input(FrameArtifactRole.Metadata, name: "facts")],
                [Output(FrameArtifactRole.AnnotatedPreview, "composed")]),
            Node("facts", 20, [Dependency("normalized")],
                [Input(FrameArtifactRole.Calibrated)],
                [Output(FrameArtifactRole.Metadata, "facts", ProcessingProductKind.Metadata, "facts-v1")]),
            Node("preview", 30, [Dependency("normalized")],
                [Input(FrameArtifactRole.Calibrated)],
                [Output(FrameArtifactRole.Preview, "display")]),
            Node("normalized", 10, [Dependency("$raw")],
                [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Calibrated, "linear")]));

        var first = ProcessingGraphCompiler.Compile(graph);
        var second = ProcessingGraphCompiler.Compile(graph);

        Assert.IsTrue(first.IsValid, Format(first));
        Assert.IsTrue(second.IsValid, Format(second));
        CollectionAssert.AreEqual(
            new[] { "normalized", "facts", "preview", "materialize" },
            first.Plan!.Nodes.Select(static node => node.Definition.Id).ToArray());
        Assert.AreEqual(first.Plan.DefinitionIdentitySha256, second.Plan!.DefinitionIdentitySha256);
        Assert.AreEqual(first.Plan.PlanIdentitySha256, second.Plan.PlanIdentitySha256);
        Assert.HasCount(2, first.Plan.Nodes[^1].InputBindings);
    }

    [TestMethod]
    public void Compile_UsesOrderThenOrdinalIdentifierForEachReadySet()
    {
        var graph = Graph(
            Node("last", 0, [Dependency("middle")], [Input(FrameArtifactRole.Combined)],
                [Output(FrameArtifactRole.Preview, "last")]),
            Node("first", 100, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Calibrated, "first")]),
            Node("middle", 50, [Dependency("first")], [Input(FrameArtifactRole.Calibrated)],
                [Output(FrameArtifactRole.Combined, "middle")]));

        var result = ProcessingGraphCompiler.Compile(graph);

        Assert.IsTrue(result.IsValid, Format(result));
        CollectionAssert.AreEqual(
            new[] { "first", "middle", "last" },
            result.Plan!.Nodes.Select(static node => node.Definition.Id).ToArray());
    }

    [TestMethod]
    public void Compile_AllowsFanOutAndExplicitlyDisambiguatedFanIn()
    {
        var graph = Graph(
            Node("source", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [
                    Output(FrameArtifactRole.Metadata, "left", ProcessingProductKind.Metadata, "side-v1"),
                    Output(FrameArtifactRole.Metadata, "right", ProcessingProductKind.Metadata, "side-v1")
                ]),
            Node("left-consumer", 10, [Dependency("source")],
                [Input(FrameArtifactRole.Metadata, variants: ["left"])],
                [Output(FrameArtifactRole.Preview, "left")]),
            Node("right-consumer", 10, [Dependency("source")],
                [Input(FrameArtifactRole.Metadata, variants: ["right"])],
                [Output(FrameArtifactRole.Preview, "right")]),
            Node("join", 20, [Dependency("left-consumer"), Dependency("right-consumer")],
                [
                    Input(FrameArtifactRole.Preview, variants: ["left"], name: "left"),
                    Input(FrameArtifactRole.Preview, variants: ["right"], name: "right")
                ],
                [Output(FrameArtifactRole.AnnotatedPreview, "joined")]));

        var result = ProcessingGraphCompiler.Compile(graph);

        Assert.IsTrue(result.IsValid, Format(result));
        Assert.HasCount(2, result.Plan!.Nodes.Single(static node => node.Definition.Id == "join").InputBindings);
    }

    [TestMethod]
    public void Compile_PreservesRequiredAndOptionalDependencyPolicy()
    {
        var graph = Graph(
            Node("preview", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Preview, "display")]),
            Node("facts", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Metadata, "facts", ProcessingProductKind.Metadata, "facts-v1")]),
            Node("consumer", 10, [Dependency("preview"), Dependency("facts", required: false)],
                [
                    Input(FrameArtifactRole.Preview, variants: ["display"], name: "display"),
                    Input(FrameArtifactRole.Metadata, variants: ["facts"], required: false, name: "facts")
                ],
                [Output(FrameArtifactRole.AnnotatedPreview, "composed")]));

        var result = ProcessingGraphCompiler.Compile(graph);

        Assert.IsTrue(result.IsValid, Format(result));
        var consumer = result.Plan!.Nodes.Single(static node => node.Definition.Id == "consumer");
        Assert.IsFalse(consumer.Definition.Dependencies[1].Required);
        Assert.IsFalse(consumer.InputBindings[1].Required);
    }

    [TestMethod]
    public void Compile_ExcludesPolicyContradictionsFromInputAmbiguity()
    {
        var graph = Graph(
            Node("required-producer", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Metadata, "required", ProcessingProductKind.Metadata)]),
            Node("optional-producer", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Metadata, "optional", ProcessingProductKind.Metadata)]),
            Node("consumer", 10,
                [Dependency("required-producer"), Dependency("optional-producer", required: false)],
                [
                    Input(FrameArtifactRole.Metadata, name: "required"),
                    Input(FrameArtifactRole.Metadata, required: false, name: "optional")
                ],
                [Output(FrameArtifactRole.AnnotatedPreview, "composed")]));

        var result = ProcessingGraphCompiler.Compile(graph);

        Assert.IsTrue(result.IsValid, Format(result));
        var bindings = result.Plan!.Nodes.Single(static node => node.Definition.Id == "consumer").InputBindings;
        Assert.AreEqual("required-producer", bindings[0].ProducerId);
        Assert.AreEqual("optional-producer", bindings[1].ProducerId);
    }

    [TestMethod]
    public void Compile_RejectsRequiredInputBoundToOptionalProducer()
    {
        var graph = Graph(
            Node("preview", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Preview, "display")]),
            Node("consumer", 10, [Dependency("preview", required: false)],
                [Input(FrameArtifactRole.Preview)],
                [Output(FrameArtifactRole.AnnotatedPreview, "composed")]));

        var result = ProcessingGraphCompiler.Compile(graph);

        Assert.IsFalse(result.IsValid);
        var diagnostic = Assert.ContainsSingle(result.Diagnostics);
        Assert.AreEqual(ProcessingGraphReasonCodes.InvalidNode, diagnostic.Code);
        StringAssert.Contains(diagnostic.Message, "required input", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Compile_RejectsAmbiguousFanInAndIncompatibleTypedProducts()
    {
        var ambiguous = Graph(
            Node("one", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Metadata, "one", ProcessingProductKind.Metadata, "facts-v1")]),
            Node("two", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Metadata, "two", ProcessingProductKind.Metadata, "facts-v1")]),
            Node("join", 10, [Dependency("one"), Dependency("two")],
                [Input(FrameArtifactRole.Metadata, name: "one"), Input(FrameArtifactRole.Metadata, name: "two")],
                [Output(FrameArtifactRole.Preview, "joined")]));
        var incompatible = Graph(
            Node("facts", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Metadata, "facts", ProcessingProductKind.Metadata, "facts-v1")]),
            Node("consumer", 10, [Dependency("facts")],
                [Input(FrameArtifactRole.Metadata, kinds: [ProcessingProductKind.PixelData])],
                [Output(FrameArtifactRole.Preview, "display")]));

        var ambiguousResult = ProcessingGraphCompiler.Compile(ambiguous);
        var incompatibleResult = ProcessingGraphCompiler.Compile(incompatible);

        Assert.IsFalse(ambiguousResult.IsValid);
        Assert.ContainsSingle(ambiguousResult.Diagnostics.Where(
            static diagnostic => diagnostic.Code == ProcessingGraphReasonCodes.AmbiguousInput));
        Assert.IsFalse(incompatibleResult.IsValid);
        Assert.ContainsSingle(incompatibleResult.Diagnostics.Where(
            static diagnostic => diagnostic.Code == ProcessingGraphReasonCodes.IncompatibleInput));
    }

    [TestMethod]
    public void Compile_ReturnsDiagnosticsWhenInvalidProducerHasDownstreamConsumer()
    {
        var graph = Graph(
            Node("invalid", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Metadata)],
                [Output(FrameArtifactRole.Preview, "invalid")]),
            Node("downstream", 10, [Dependency("invalid")], [Input(FrameArtifactRole.Preview)],
                [Output(FrameArtifactRole.AnnotatedPreview, "downstream")]));

        var result = ProcessingGraphCompiler.Compile(graph);

        Assert.IsFalse(result.IsValid);
        Assert.ContainsSingle(result.Diagnostics.Where(
            static diagnostic => diagnostic.Code == ProcessingGraphReasonCodes.IncompatibleInput));
    }

    [TestMethod]
    public void Compile_ValidatesMissingDisabledDuplicateAndCyclicProducersDeterministically()
    {
        var graph = Graph(
            Node("disabled", 0, [], [], [Output(FrameArtifactRole.Metadata, "disabled")], enabled: false),
            Node("missing", 0, [Dependency("absent")], [Input(FrameArtifactRole.Metadata)],
                [Output(FrameArtifactRole.Preview, "missing")]),
            Node("disabled-consumer", 0, [Dependency("disabled")], [Input(FrameArtifactRole.Metadata)],
                [Output(FrameArtifactRole.Preview, "disabled-consumer")]),
            Node("cycle-a", 0, [Dependency("cycle-b", ProcessingGraphDependencyKind.Ordering)], [],
                [Output(FrameArtifactRole.Metadata, "cycle-a")]),
            Node("cycle-b", 0, [Dependency("cycle-a", ProcessingGraphDependencyKind.Ordering)], [],
                [Output(FrameArtifactRole.Metadata, "cycle-b")]),
            Node("duplicate-output", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Raw, "source")]));

        var first = ProcessingGraphCompiler.Compile(graph);
        var second = ProcessingGraphCompiler.Compile(graph);

        Assert.IsFalse(first.IsValid);
        CollectionAssert.AreEqual(first.Diagnostics.ToArray(), second.Diagnostics.ToArray());
        CollectionAssert.IsSubsetOf(
            new[]
            {
                ProcessingGraphReasonCodes.MissingProducer,
                ProcessingGraphReasonCodes.DisabledProducer,
                ProcessingGraphReasonCodes.DuplicateOutput
            },
            first.Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct().ToArray());
    }

    [TestMethod]
    public void Compile_RejectsCyclesAfterStructuralValidation()
    {
        var graph = Graph(
            Node("one", 0, [Dependency("two", ProcessingGraphDependencyKind.Ordering)], [],
                [Output(FrameArtifactRole.Metadata, "one")]),
            Node("two", 0, [Dependency("one", ProcessingGraphDependencyKind.Ordering)], [],
                [Output(FrameArtifactRole.Metadata, "two")]));

        var result = ProcessingGraphCompiler.Compile(graph);

        Assert.IsFalse(result.IsValid);
        var diagnostic = Assert.ContainsSingle(result.Diagnostics);
        Assert.AreEqual(ProcessingGraphReasonCodes.Cycle, diagnostic.Code);
        StringAssert.Contains(diagnostic.Message, "one, two", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Compile_ValidatesTrailingAndCenteredWindowShapes()
    {
        var trailing = new ProcessingGraphWindowRequirement(
            ProcessingGraphWindowKind.Trailing, 1, 5, [], ["rig", "sensor"]);
        var centered = new ProcessingGraphWindowRequirement(
            ProcessingGraphWindowKind.Centered, 5, 5, [-2, -1, 0, 1, 2], ["rig", "orientation"]);
        var valid = Graph(
            Node("trailing", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Combined, "trailing", operationKind: ProcessingOperationKind.Window)], window: trailing,
                operationKind: ProcessingOperationKind.Window),
            Node("centered", 10, [Dependency("trailing")], [Input(FrameArtifactRole.Combined)],
                [Output(FrameArtifactRole.Metadata, "centered", ProcessingProductKind.Metadata, "assessment-v1",
                    ProcessingOperationKind.Window)],
                window: centered, operationKind: ProcessingOperationKind.Window));
        var invalid = Graph(Node("invalid", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
            [Output(FrameArtifactRole.Combined, "invalid", operationKind: ProcessingOperationKind.Window)],
            window: centered with { MinimumInputCount = 3, RequiredPositions = [-1, 1] },
            operationKind: ProcessingOperationKind.Window));

        Assert.IsTrue(ProcessingGraphCompiler.Compile(valid).IsValid);
        Assert.ContainsSingle(ProcessingGraphCompiler.Compile(invalid).Diagnostics.Where(
            static diagnostic => diagnostic.Code == ProcessingGraphReasonCodes.InvalidWindow));
    }

    [TestMethod]
    public void Compile_RejectsOutputRecipeOperationKindThatDiffersFromOwningNode()
    {
        var graph = Graph(Node("analyzer", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
            [Output(FrameArtifactRole.Metadata, "facts", ProcessingProductKind.Metadata, operationKind:
                ProcessingOperationKind.Transform)], operationKind: ProcessingOperationKind.Analyzer));

        var result = ProcessingGraphCompiler.Compile(graph);

        Assert.IsFalse(result.IsValid);
        var diagnostic = Assert.ContainsSingle(result.Diagnostics.Where(item =>
            item.Path == "nodes[0].outputs[0].recipe.operationKind"));
        Assert.AreEqual(ProcessingGraphReasonCodes.InvalidNode, diagnostic.Code);
    }

    [TestMethod]
    public void ProductContractCanonicalJsonEscapesNonAsciiText()
    {
        var contract = Output(FrameArtifactRole.Preview, "pr\u00e9view");

        var json = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(contract)).GetRawText();

        Assert.IsTrue(json.All(static character => character <= 0x7f));
        StringAssert.Contains(json, "\\u00E9", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Compile_ValidatesHostApplicabilityAndCapabilitiesWithoutInfrastructure()
    {
        var graph = Graph(Node("gpu", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
            [Output(FrameArtifactRole.Preview, "gpu")],
            capabilities: ["gpu"], hosts: [ProcessingGraphHosts.LogicHost]));

        var unavailable = ProcessingGraphCompiler.Compile(graph, new(
            ProcessingGraphHosts.CameraAgent, []));
        var available = ProcessingGraphCompiler.Compile(graph, new(
            ProcessingGraphHosts.LogicHost, ["gpu"]));

        Assert.IsFalse(unavailable.IsValid);
        CollectionAssert.AreEquivalent(
            new[] { ProcessingGraphReasonCodes.HostInapplicable, ProcessingGraphReasonCodes.MissingCapability },
            unavailable.Diagnostics.Select(static diagnostic => diagnostic.Code).ToArray());
        Assert.IsTrue(available.IsValid, Format(available));
    }

    [TestMethod]
    public void Compile_DefinitionIdentityIncludesDisabledRevisionWhilePlanIdentityCoversExecutionOnly()
    {
        var baseline = Graph(Node("preview", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
            [Output(FrameArtifactRole.Preview, "display")], options: Json("""{"quality":90,"mode":"fast"}""")));
        var reorderedOptions = baseline with
        {
            Nodes = [WithOptions(baseline.Nodes[0], Json("""{"mode":"fast","quality":90}"""))]
        };
        var revisionOnly = baseline with { Revision = "2" };
        var disabledOnly = baseline with
        {
            Nodes = [.. baseline.Nodes, Node("future", 100, [], [], [], enabled: false)]
        };
        var semanticChange = baseline with
        {
            Nodes = [WithOptions(baseline.Nodes[0], Json("""{"quality":91,"mode":"fast"}"""))]
        };

        var baselinePlan = ProcessingGraphCompiler.Compile(baseline).Plan!;
        var reorderedPlan = ProcessingGraphCompiler.Compile(reorderedOptions).Plan!;
        var revisionPlan = ProcessingGraphCompiler.Compile(revisionOnly).Plan!;
        var disabledPlan = ProcessingGraphCompiler.Compile(disabledOnly).Plan!;
        var changedPlan = ProcessingGraphCompiler.Compile(semanticChange).Plan!;

        Assert.AreEqual(baselinePlan.DefinitionIdentitySha256, reorderedPlan.DefinitionIdentitySha256);
        Assert.AreEqual(baselinePlan.PlanIdentitySha256, reorderedPlan.PlanIdentitySha256);
        Assert.AreNotEqual(baselinePlan.DefinitionIdentitySha256, revisionPlan.DefinitionIdentitySha256);
        Assert.AreEqual(baselinePlan.PlanIdentitySha256, revisionPlan.PlanIdentitySha256);
        Assert.AreNotEqual(baselinePlan.DefinitionIdentitySha256, disabledPlan.DefinitionIdentitySha256);
        Assert.AreEqual(baselinePlan.PlanIdentitySha256, disabledPlan.PlanIdentitySha256);
        Assert.AreNotEqual(baselinePlan.PlanIdentitySha256, changedPlan.PlanIdentitySha256);
        Assert.AreNotEqual(
            baselinePlan.Nodes[0].IdentitySha256,
            changedPlan.Nodes[0].IdentitySha256);
    }

    [TestMethod]
    public void GraphJson_CanonicalRoundTripPreservesIdentityAndTypedLayerProducts()
    {
        var graph = Graph(Node("layer", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
            [new(
                FrameArtifactRole.Metadata,
                "presentation-layer",
                ProcessingProductKind.Metadata,
                new("presentation-layer", "2.0.0", "svg-v2", ProcessingOperationKind.Transform),
                PresentationLayerV1.CurrentSchemaVersion,
                MediaType: PresentationLayerPayloadJson.MediaType)]));

        var json = ProcessingGraphJson.SerializeCanonical(graph);
        var parsed = ProcessingGraphJson.Parse(json);
        var original = ProcessingGraphCompiler.Compile(graph);
        var roundTrip = ProcessingGraphCompiler.Compile(parsed.Definition!);

        Assert.IsTrue(parsed.IsValid, string.Join(Environment.NewLine, parsed.Diagnostics));
        Assert.IsTrue(roundTrip.IsValid, Format(roundTrip));
        Assert.AreEqual(original.Plan!.DefinitionIdentitySha256, roundTrip.Plan!.DefinitionIdentitySha256);
        Assert.AreEqual(original.Plan.PlanIdentitySha256, roundTrip.Plan.PlanIdentitySha256);
        Assert.AreEqual(
            PresentationLayerV1.CurrentSchemaVersion,
            roundTrip.Plan.Nodes[0].Definition.Outputs[0].SchemaVersion);
        Assert.AreEqual(
            PresentationLayerPayloadJson.MediaType,
            roundTrip.Plan.Nodes[0].Definition.Outputs[0].MediaType);
    }

    [TestMethod]
    public void GraphJson_OldV1WindowDefaultsToExplicitCompatibleSemantics()
    {
        var explicitGraph = Graph(Node(
            "window",
            0,
            [Dependency("$raw")],
            [Input(FrameArtifactRole.Raw)],
            [Output(FrameArtifactRole.Combined, "window", operationKind: ProcessingOperationKind.Window)],
            window: new ProcessingGraphWindowRequirement(
                ProcessingGraphWindowKind.Centered,
                1,
                3,
                [0],
                ["rig"]),
            operationKind: ProcessingOperationKind.Window));
        var oldV1Json = System.Text.Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(explicitGraph))
            .Replace(",\"missingInputOutcome\":\"Skip\"", string.Empty, StringComparison.Ordinal)
            .Replace(",\"timeoutTicks\":3000000000", string.Empty, StringComparison.Ordinal);

        var parsed = ProcessingGraphJson.Parse(System.Text.Encoding.UTF8.GetBytes(oldV1Json));
        var oldPlan = ProcessingGraphCompiler.Compile(parsed.Definition!);
        var explicitPlan = ProcessingGraphCompiler.Compile(explicitGraph);

        Assert.IsTrue(parsed.IsValid, string.Join(Environment.NewLine, parsed.Diagnostics));
        Assert.AreEqual(TimeSpan.FromMinutes(5).Ticks, parsed.Definition!.Nodes[0].Window!.TimeoutTicks);
        Assert.AreEqual(ProcessingGraphMissingInputOutcome.Skip, parsed.Definition.Nodes[0].Window!.MissingInputOutcome);
        Assert.IsTrue(oldPlan.IsValid, Format(oldPlan));
        Assert.AreEqual(explicitPlan.Plan!.DefinitionIdentitySha256, oldPlan.Plan!.DefinitionIdentitySha256);
        Assert.AreEqual(explicitPlan.Plan.PlanIdentitySha256, oldPlan.Plan.PlanIdentitySha256);
        var canonical = System.Text.Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(parsed.Definition));
        Assert.DoesNotContain("\"timeoutTicks\"", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("\"missingInputOutcome\"", canonical, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Compile_WindowPolicyChangesDefinitionPlanAndNodeIdentities()
    {
        var baseline = Graph(Node(
            "window",
            0,
            [Dependency("$raw")],
            [Input(FrameArtifactRole.Raw)],
            [Output(FrameArtifactRole.Combined, "window", operationKind: ProcessingOperationKind.Window)],
            window: new ProcessingGraphWindowRequirement(
                ProcessingGraphWindowKind.Trailing,
                1,
                3,
                [],
                ["rig"]),
            operationKind: ProcessingOperationKind.Window));
        var changed = baseline with
        {
            Nodes =
            [
                WithWindow(baseline.Nodes[0], baseline.Nodes[0].Window! with
                {
                    TimeoutTicks = TimeSpan.FromMinutes(10).Ticks,
                    MissingInputOutcome = ProcessingGraphMissingInputOutcome.Fail
                })
            ]
        };

        var baselinePlan = ProcessingGraphCompiler.Compile(baseline).Plan!;
        var changedPlan = ProcessingGraphCompiler.Compile(changed).Plan!;

        Assert.AreNotEqual(baselinePlan.DefinitionIdentitySha256, changedPlan.DefinitionIdentitySha256);
        Assert.AreNotEqual(baselinePlan.PlanIdentitySha256, changedPlan.PlanIdentitySha256);
        Assert.AreNotEqual(baselinePlan.Nodes[0].IdentitySha256, changedPlan.Nodes[0].IdentitySha256);
    }

    [TestMethod]
    public void GraphJson_RejectsUnknownFieldsAndCompilerEnforcesBounds()
    {
        var invalidJson = ProcessingGraphJson.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = ProcessingGraphSchemaVersions.Current,
            name = "invalid",
            revision = "1",
            sources = Array.Empty<object>(),
            nodes = Array.Empty<object>(),
            unexpected = true
        }));
        var oversized = Graph(Node("oversized", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
            [Output(FrameArtifactRole.Preview, "display")],
            options: Json($$"""{"value":"{{new string('x', ProcessingGraphCompiler.MaximumOptionsBytes)}}"}""")));

        Assert.IsFalse(invalidJson.IsValid);
        Assert.AreEqual(ProcessingGraphReasonCodes.InvalidJson, invalidJson.Diagnostics[0].Code);
        Assert.ContainsSingle(ProcessingGraphCompiler.Compile(oversized).Diagnostics.Where(
            static diagnostic => diagnostic.Code == ProcessingGraphReasonCodes.LimitExceeded));
    }

    [TestMethod]
    public void GraphJson_RejectsDuplicatePropertiesAndCompilerDiagnosesNullMembers()
    {
        var duplicate = ProcessingGraphJson.Parse(System.Text.Encoding.UTF8.GetBytes($$"""
            {"schemaVersion":"{{ProcessingGraphSchemaVersions.Current}}","name":"one","name":"two","revision":"1","sources":[],"nodes":[]}
            """));
        var nullNode = ProcessingGraphJson.Parse(System.Text.Encoding.UTF8.GetBytes($$"""
            {"schemaVersion":"{{ProcessingGraphSchemaVersions.Current}}","name":"null-node","revision":"1","sources":[],"nodes":[null]}
            """));
        var canonical = System.Text.Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(Graph(
            Node("required-scalars", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
                [Output(FrameArtifactRole.Preview, "display")]))));
        var missingEnabled = ProcessingGraphJson.Parse(System.Text.Encoding.UTF8.GetBytes(
            canonical.Replace("\"enabled\":true,", string.Empty, StringComparison.Ordinal)));
        var nullNodeId = ProcessingGraphJson.Parse(System.Text.Encoding.UTF8.GetBytes(
            canonical.Replace("\"id\":\"required-scalars\"", "\"id\":null", StringComparison.Ordinal)));

        Assert.IsFalse(duplicate.IsValid);
        Assert.AreEqual(ProcessingGraphReasonCodes.InvalidJson, duplicate.Diagnostics[0].Code);
        Assert.IsFalse(missingEnabled.IsValid);
        Assert.AreEqual(ProcessingGraphReasonCodes.InvalidJson, missingEnabled.Diagnostics[0].Code);
        Assert.IsTrue(nullNodeId.IsValid);
        Assert.ContainsSingle(ProcessingGraphCompiler.Compile(nullNodeId.Definition!).Diagnostics.Where(
            static diagnostic => diagnostic.Code == ProcessingGraphReasonCodes.InvalidIdentity));
        Assert.IsTrue(nullNode.IsValid);
        var compiled = ProcessingGraphCompiler.Compile(nullNode.Definition!);
        Assert.IsFalse(compiled.IsValid);
        Assert.AreEqual(ProcessingGraphReasonCodes.InvalidNode, compiled.Diagnostics[0].Code);
    }

    [TestMethod]
    public void Compile_RejectsUndefinedFailurePolicyAndOversizedCanonicalDocument()
    {
        var invalidPolicy = Graph(Node("invalid-policy", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
            [Output(FrameArtifactRole.Preview, "display")],
            failurePolicy: (ProcessingGraphNodeFailurePolicy)999));
        var largeOptions = Json($$"""{"value":"{{new string('x', ProcessingGraphCompiler.MaximumOptionsBytes - 1024)}}"}""");
        var oversizedDocument = Graph(Enumerable.Range(0, 40)
            .Select(index => Node($"disabled-{index}", index, [], [], [], enabled: false, options: largeOptions))
            .ToArray());

        Assert.ContainsSingle(ProcessingGraphCompiler.Compile(invalidPolicy).Diagnostics.Where(
            static diagnostic => diagnostic.Path == "nodes[0].failurePolicy"));
        Assert.ContainsSingle(ProcessingGraphCompiler.Compile(oversizedDocument).Diagnostics.Where(
            static diagnostic => diagnostic.Code == ProcessingGraphReasonCodes.LimitExceeded && diagnostic.Path == "$"));
        Assert.ThrowsExactly<ArgumentException>(() => ProcessingGraphJson.SerializeCanonical(oversizedDocument));
    }

    [TestMethod]
    public void Compile_BoundsNestedContractsAndReturnsCanonicalSourceOrder()
    {
        var tooManyOutputs = Enumerable.Range(0, ProcessingGraphCompiler.MaximumContractsPerNode + 1)
            .Select(index => Output(FrameArtifactRole.Metadata, $"output-{index}"))
            .ToImmutableArray();
        var bounded = new ProcessingGraphDefinition(
            ProcessingGraphSchemaVersions.Current,
            "bounded",
            "1",
            [new("source", tooManyOutputs)],
            []);
        var sourceA = new ProcessingGraphSourceDefinition(
            "a",
            [new(FrameArtifactRole.Raw, "a", ProcessingProductKind.PixelData)]);
        var sourceB = new ProcessingGraphSourceDefinition(
            "b",
            [new(FrameArtifactRole.Raw, "b", ProcessingProductKind.PixelData)]);
        var first = new ProcessingGraphDefinition(
            ProcessingGraphSchemaVersions.Current, "ordered", "1", [sourceB, sourceA], []);
        var second = first with { Sources = [sourceA, sourceB] };

        Assert.ContainsSingle(ProcessingGraphCompiler.Compile(bounded).Diagnostics.Where(
            static diagnostic => diagnostic.Code == ProcessingGraphReasonCodes.LimitExceeded));
        var firstPlan = ProcessingGraphCompiler.Compile(first).Plan!;
        var secondPlan = ProcessingGraphCompiler.Compile(second).Plan!;
        Assert.AreEqual(firstPlan.PlanIdentitySha256, secondPlan.PlanIdentitySha256);
        CollectionAssert.AreEqual(new[] { "a", "b" }, firstPlan.Sources.Select(static source => source.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "a", "b" }, secondPlan.Sources.Select(static source => source.Id).ToArray());
    }

    [TestMethod]
    public void Compile_DiagnosesEveryMalformedDefinitionShapeAndTopology()
    {
        static ProcessingGraphCompilationResult Compile(ProcessingGraphDefinition definition)
        {
            var result = ProcessingGraphCompiler.Compile(definition);
            Assert.IsFalse(result.IsValid);
            Assert.IsNotEmpty(result.Diagnostics);
            return result;
        }

        var baseline = Graph(Node(
            "preview",
            0,
            [Dependency("$raw")],
            [Input(FrameArtifactRole.Raw)],
            [Output(FrameArtifactRole.Preview, "display")]));
        Compile(baseline with { SchemaVersion = "unsupported" });
        Compile(baseline with { Name = null! });
        Compile(baseline with { Revision = " " });
        Compile(baseline with { Sources = default });
        Compile(baseline with { Nodes = default });
        Compile(baseline with
        {
            Sources = ImmutableArray.CreateRange(new ProcessingGraphSourceDefinition[] { null! })
        });
        Compile(baseline with
        {
            Sources = [baseline.Sources[0] with { Outputs = default }]
        });
        Compile(baseline with
        {
            Sources = Enumerable.Range(0, ProcessingGraphCompiler.MaximumSources + 1)
                .Select(index => new ProcessingGraphSourceDefinition(
                    $"source-{index}", [Output(FrameArtifactRole.Raw, $"raw-{index}")]))
                .ToImmutableArray()
        });
        Compile(new ProcessingGraphDefinition(
            ProcessingGraphSchemaVersions.Current,
            "too-many-nodes",
            "1",
            [],
            Enumerable.Range(0, ProcessingGraphCompiler.MaximumNodes + 1)
                .Select(index => Node($"node-{index}", index, [], [], [], enabled: false))
                .ToImmutableArray()));

        var node = baseline.Nodes[0];
        ProcessingGraphNodeDefinition Rebuild(
            string? id = null,
            ProcessingOperationKind? operationKind = null,
            JsonElement? options = null,
            ImmutableArray<ProcessingGraphDependencyDefinition>? dependencies = null,
            ImmutableArray<ProcessingGraphInputContract>? inputs = null,
            ImmutableArray<ProcessingGraphProductContract>? outputs = null,
            ProcessingGraphWindowRequirement? window = null,
            ImmutableArray<string>? capabilities = null,
            ImmutableArray<string>? hosts = null)
            => new(
                id ?? node.Id,
                node.StepAlias,
                node.StepVersion,
                operationKind ?? node.OperationKind,
                node.Enabled,
                node.FailurePolicy,
                node.Order,
                options ?? node.EffectiveOptions,
                dependencies.GetValueOrDefault(node.Dependencies),
                inputs.GetValueOrDefault(node.Inputs),
                outputs.GetValueOrDefault(node.Outputs),
                window,
                capabilities.GetValueOrDefault(node.CapabilityLabels),
                hosts.GetValueOrDefault(node.HostApplicability));

        Compile(baseline with
        {
            Nodes = [Rebuild(dependencies: default(ImmutableArray<ProcessingGraphDependencyDefinition>))]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(inputs: default(ImmutableArray<ProcessingGraphInputContract>))]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(outputs: default(ImmutableArray<ProcessingGraphProductContract>))]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(capabilities: default(ImmutableArray<string>))]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(hosts: default(ImmutableArray<string>))]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(inputs: ImmutableArray.CreateRange(
                new ProcessingGraphInputContract[] { null! }))]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(inputs: [new ProcessingGraphInputContract(default, [], [], [], [])])]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(window: new ProcessingGraphWindowRequirement(
                ProcessingGraphWindowKind.Centered, 1, 1, default, ["rig"]))]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(operationKind: (ProcessingOperationKind)int.MaxValue)]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(options: JsonSerializer.SerializeToElement("not-an-object"))]
        });
        Compile(baseline with
        {
            Sources = [baseline.Sources[0], baseline.Sources[0]]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(id: "$raw")]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(dependencies: [Dependency("$raw"), Dependency("$raw")])]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(dependencies: [Dependency(node.Id)])]
        });
        Compile(baseline with
        {
            Nodes = [Rebuild(dependencies: [Dependency("missing")])]
        });
        var disabled = Node("disabled", 0, [Dependency("$raw")], [Input(FrameArtifactRole.Raw)],
            [Output(FrameArtifactRole.Preview, "disabled")], enabled: false);
        Compile(Graph(
            disabled,
            Node("consumer", 1, [Dependency(disabled.Id)], [Input(FrameArtifactRole.Preview)],
                [Output(FrameArtifactRole.Metadata, "consumer")])));
    }

    private static ProcessingGraphDefinition Graph(params ProcessingGraphNodeDefinition[] nodes) => new(
        ProcessingGraphSchemaVersions.Current,
        "test-graph",
        "1",
        [new("$raw", [Raw])],
        [.. nodes]);

    private static ProcessingGraphNodeDefinition Node(
        string id,
        int order,
        ImmutableArray<ProcessingGraphDependencyDefinition> dependencies,
        ImmutableArray<ProcessingGraphInputContract> inputs,
        ImmutableArray<ProcessingGraphProductContract> outputs,
        bool enabled = true,
        JsonElement? options = null,
        ProcessingGraphWindowRequirement? window = null,
        ImmutableArray<string> capabilities = default,
        ImmutableArray<string> hosts = default,
        ProcessingOperationKind operationKind = ProcessingOperationKind.Transform,
        ProcessingGraphNodeFailurePolicy failurePolicy = ProcessingGraphNodeFailurePolicy.Required) => new(
            id,
            id,
            "test-v1",
            operationKind,
            enabled,
            failurePolicy,
            order,
            options ?? Json("{}"),
            dependencies,
            inputs,
            outputs,
            window,
            capabilities.IsDefault ? [] : capabilities,
            hosts.IsDefault ? [] : hosts);

    private static ProcessingGraphNodeDefinition WithOptions(
        ProcessingGraphNodeDefinition node,
        JsonElement options) => new(
            node.Id,
            node.StepAlias,
            node.StepVersion,
            node.OperationKind,
            node.Enabled,
            node.FailurePolicy,
            node.Order,
            options,
            node.Dependencies,
            node.Inputs,
            node.Outputs,
            node.Window,
            node.CapabilityLabels,
            node.HostApplicability);

    private static ProcessingGraphNodeDefinition WithWindow(
        ProcessingGraphNodeDefinition node,
        ProcessingGraphWindowRequirement window) => new(
            node.Id,
            node.StepAlias,
            node.StepVersion,
            node.OperationKind,
            node.Enabled,
            node.FailurePolicy,
            node.Order,
            node.EffectiveOptions,
            node.Dependencies,
            node.Inputs,
            node.Outputs,
            window,
            node.CapabilityLabels,
            node.HostApplicability);

    private static ProcessingGraphDependencyDefinition Dependency(
        string producerId,
        ProcessingGraphDependencyKind kind = ProcessingGraphDependencyKind.Artifact,
        bool required = true) => new(producerId, kind, required);

    private static ProcessingGraphInputContract Input(
        FrameArtifactRole role,
        ImmutableArray<ProcessingProductKind> kinds = default,
        ImmutableArray<string> variants = default,
        ImmutableArray<string> recipes = default,
        ImmutableArray<string> schemas = default,
        bool required = true,
        string name = "input",
        ProcessingGraphInputBindingKind bindingKind = ProcessingGraphInputBindingKind.PrimaryArtifact) => new(
            [role],
            kinds.IsDefault ? [] : kinds,
            variants.IsDefault ? [] : variants,
            recipes.IsDefault ? [] : recipes,
            schemas.IsDefault ? [] : schemas,
            required,
            name,
            bindingKind);

    private static ProcessingGraphProductContract Output(
        FrameArtifactRole role,
        string variant,
        ProcessingProductKind kind = ProcessingProductKind.PixelData,
        string? schema = null,
        ProcessingOperationKind operationKind = ProcessingOperationKind.Transform) => new(
            role,
            variant,
            kind,
            new($"recipe-{variant}", "1.0.0", "test-v1", operationKind),
            schema);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string Format(ProcessingGraphCompilationResult result) => string.Join(
        Environment.NewLine,
        result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}:{diagnostic.Path}:{diagnostic.Message}"));
}
