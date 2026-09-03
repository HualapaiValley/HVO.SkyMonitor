using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// Guards persisted CameraAgent schema-6 graph revisions: the V1 JSON they hold predates <c>mediaType</c>,
/// <c>timeoutTicks</c>, and <c>missingInputOutcome</c>, so those members must stay optional on read and absent
/// from the canonical form whenever they hold their defaults.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ProcessingGraphJsonCompatibilityTests
{
    // Captured from commit 621e40d (before MediaType/TimeoutTicks/MissingInputOutcome existed) by serializing the
    // definition rebuilt in CreateLegacyTransformDefinition and computing ProcessingGraphJson.ComputeDefinitionIdentity.
    private const string LegacyTransformJson =
        "{\"name\":\"schema-six-legacy\",\"nodes\":[{\"capabilityLabels\":[],\"dependencies\":[{\"kind\":\"Artifact\"," +
        "\"producerId\":\"$raw\",\"required\":true}],\"effectiveOptions\":{\"asinhStrength\":4,\"blackPercentile\":0.5," +
        "\"jpegQuality\":80,\"outputEncoding\":\"Jpeg\",\"whitePercentile\":0.9999},\"enabled\":true," +
        "\"failurePolicy\":\"Required\",\"hostApplicability\":[\"camera-agent\"],\"id\":\"Preview\",\"inputs\":[{" +
        "\"bindingKind\":\"PrimaryArtifact\",\"bindingName\":\"input\",\"productKinds\":[\"PixelData\"],\"recipeNames\":[]," +
        "\"required\":true,\"roles\":[\"Raw\"],\"schemaVersions\":[],\"variants\":[]}],\"operationKind\":\"Transform\"," +
        "\"order\":0,\"outputs\":[{\"algorithms\":[],\"productKind\":\"PixelData\",\"recipe\":{" +
        "\"implementationVersion\":\"encoded-preview-v1\",\"name\":\"encoded-preview\",\"operationKind\":\"Transform\"," +
        "\"semanticVersion\":\"1.0.0\"},\"role\":\"Preview\",\"schemaVersion\":null,\"variant\":\"preview\"}]," +
        "\"stepAlias\":\"encoded-preview\",\"stepVersion\":\"cameraagent-v2\",\"window\":null}],\"revision\":\"1\"," +
        "\"schemaVersion\":\"hvo-processing-graph-v1\",\"sources\":[{\"id\":\"$raw\",\"outputs\":[{\"algorithms\":[]," +
        "\"productKind\":\"PixelData\",\"recipe\":null,\"role\":\"Raw\",\"schemaVersion\":null,\"variant\":\"source\"}]}]}";

    private static readonly string[] RequiredOutputContractMembers = ["role", "variant", "productKind"];

    private const string LegacyTransformDefinitionIdentity =
        "F1DE628CCF5C237BA024EDE1DB4E78E7A3323010F1FCB17C977D7C17E2C85EEE";

    private const string LegacyTransformPlanIdentity =
        "73D8EAF67B9FCB212E9560E59DF08E8E4623D3115803E9A466F23C332C672A70";

    // Same provenance as above for a windowed node; the pre-change window form had no timeout or outcome members.
    private const string LegacyWindowJson =
        "{\"name\":\"schema-six-legacy\",\"nodes\":[{\"capabilityLabels\":[],\"dependencies\":[{\"kind\":\"Artifact\"," +
        "\"producerId\":\"$raw\",\"required\":true}],\"effectiveOptions\":{\"maximumAgeMilliseconds\":null," +
        "\"maximumFrameCount\":5,\"maximumIntegrationMilliseconds\":null},\"enabled\":true,\"failurePolicy\":\"Required\"," +
        "\"hostApplicability\":[\"camera-agent\"],\"id\":\"RollingMean\",\"inputs\":[{\"bindingKind\":\"PrimaryArtifact\"," +
        "\"bindingName\":\"input\",\"productKinds\":[\"PixelData\"],\"recipeNames\":[],\"required\":true,\"roles\":[\"Raw\"]," +
        "\"schemaVersions\":[],\"variants\":[]}],\"operationKind\":\"Window\",\"order\":0,\"outputs\":[{\"algorithms\":[]," +
        "\"productKind\":\"PixelData\",\"recipe\":{\"implementationVersion\":\"linear16-arithmetic-mean-v1\"," +
        "\"name\":\"rolling-mean\",\"operationKind\":\"Window\",\"semanticVersion\":\"1.0.0\"},\"role\":\"Combined\"," +
        "\"schemaVersion\":null,\"variant\":\"rolling-mean\"}],\"stepAlias\":\"rolling-mean\",\"stepVersion\":\"cameraagent-v2\"," +
        "\"window\":{\"compatibilityLabels\":[\"rig\"],\"kind\":\"Trailing\",\"maximumInputCount\":3,\"minimumInputCount\":1," +
        "\"requiredPositions\":[0]}}],\"revision\":\"1\",\"schemaVersion\":\"hvo-processing-graph-v1\",\"sources\":[{" +
        "\"id\":\"$raw\",\"outputs\":[{\"algorithms\":[],\"productKind\":\"PixelData\",\"recipe\":null,\"role\":\"Raw\"," +
        "\"schemaVersion\":null,\"variant\":\"source\"}]}]}";

    private const string LegacyWindowDefinitionIdentity =
        "DBA8D9A8826DEF5C76127FC836C045D1ADDF2974131542FD600C18EDFDC1A619";

    [TestMethod]
    public void PreChangeSchemaSixJsonParsesAndKeepsDefinitionAndPlanIdentity()
    {
        var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(LegacyTransformJson));

        Assert.IsTrue(parsed.IsValid, string.Join(Environment.NewLine, parsed.Diagnostics));
        var definition = parsed.Definition!;
        Assert.IsNull(definition.Nodes.Single().Outputs.Single().MediaType);
        Assert.IsTrue(definition.Nodes.Single().Outputs.Single().Algorithms.IsEmpty);
        Assert.AreEqual(LegacyTransformJson, Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition)));
        Assert.AreEqual(LegacyTransformDefinitionIdentity, ProcessingGraphJson.ComputeDefinitionIdentity(definition));
        var compiled = ProcessingGraphCompiler.Compile(definition);
        Assert.IsTrue(compiled.IsValid, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.AreEqual(LegacyTransformDefinitionIdentity, compiled.Plan!.DefinitionIdentitySha256);
        Assert.AreEqual(LegacyTransformPlanIdentity, compiled.Plan.PlanIdentitySha256);
        Assert.AreEqual(
            LegacyTransformDefinitionIdentity,
            ProcessingGraphJson.ComputeDefinitionIdentity(CreateLegacyTransformDefinition()));
    }

    [TestMethod]
    public void PreChangeWindowJsonOmitsDefaultTimeoutAndOutcomeFromCanonicalForm()
    {
        var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(LegacyWindowJson));

        Assert.IsTrue(parsed.IsValid, string.Join(Environment.NewLine, parsed.Diagnostics));
        var window = parsed.Definition!.Nodes.Single().Window!;
        Assert.AreEqual(TimeSpan.FromMinutes(5).Ticks, window.TimeoutTicks);
        Assert.AreEqual(ProcessingGraphMissingInputOutcome.Skip, window.MissingInputOutcome);
        Assert.AreEqual(LegacyWindowJson, Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(parsed.Definition)));
        Assert.AreEqual(LegacyWindowDefinitionIdentity, ProcessingGraphJson.ComputeDefinitionIdentity(parsed.Definition));
    }

    [TestMethod]
    public void OutputContractAcceptsJsonWithoutAlgorithmsOrMediaTypeAndRoundTripsNewMembers()
    {
        var withoutOptionalMembers = LegacyTransformJson.Replace("\"algorithms\":[],", string.Empty, StringComparison.Ordinal);
        var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(withoutOptionalMembers));
        Assert.IsTrue(parsed.IsValid, string.Join(Environment.NewLine, parsed.Diagnostics));
        Assert.AreEqual(LegacyTransformDefinitionIdentity, ProcessingGraphJson.ComputeDefinitionIdentity(parsed.Definition!));

        var legacy = CreateLegacyTransformDefinition();
        var node = legacy.Nodes.Single();
        var extended = legacy with
        {
            Nodes = [new ProcessingGraphNodeDefinition(
                node.Id, node.StepAlias, node.StepVersion, node.OperationKind, node.Enabled, node.FailurePolicy,
                node.Order, node.EffectiveOptions, node.Dependencies, node.Inputs,
                [node.Outputs.Single() with { MediaType = "image/jpeg" }],
                node.Window, node.CapabilityLabels, node.HostApplicability)]
        };
        var extendedJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(extended));
        Assert.Contains("\"mediaType\":\"image/jpeg\"", extendedJson, StringComparison.Ordinal);
        Assert.AreNotEqual(LegacyTransformDefinitionIdentity, ProcessingGraphJson.ComputeDefinitionIdentity(extended));
        var reparsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(extendedJson));
        Assert.IsTrue(reparsed.IsValid, string.Join(Environment.NewLine, reparsed.Diagnostics));
        Assert.AreEqual("image/jpeg", reparsed.Definition!.Nodes.Single().Outputs.Single().MediaType);
        Assert.AreEqual(
            ProcessingGraphJson.ComputeDefinitionIdentity(extended),
            ProcessingGraphJson.ComputeDefinitionIdentity(reparsed.Definition));
    }

    [TestMethod]
    public void OutputContractNormalizesDefaultAlgorithmsToEmpty()
    {
        var constructed = new ProcessingGraphProductContract(
            FrameArtifactRole.Raw, "source", ProcessingProductKind.PixelData, Algorithms: default);
        Assert.IsFalse(constructed.Algorithms.IsDefault);
        Assert.IsTrue(constructed.Algorithms.IsEmpty);

        var mutated = constructed with { Algorithms = default };
        Assert.IsFalse(mutated.Algorithms.IsDefault);
        Assert.IsTrue(mutated.Algorithms.IsEmpty);
        Assert.AreEqual(constructed, mutated);

        var options = CreateWebOptions();
        var parsed = JsonSerializer.Deserialize<ProcessingGraphProductContract>(
            "{\"role\":\"Raw\",\"variant\":\"source\",\"productKind\":\"PixelData\"}", options);
        Assert.IsNotNull(parsed);
        Assert.IsFalse(parsed.Algorithms.IsDefault);
        Assert.IsTrue(parsed.Algorithms.IsEmpty);
        Assert.AreEqual(constructed, parsed);
    }

    [TestMethod]
    public void OutputContractRoundTripsAlgorithmsThroughSystemTextJsonAndExportsSchema()
    {
        var options = CreateWebOptions();
        var contract = new ProcessingGraphProductContract(
            FrameArtifactRole.Preview,
            "preview",
            ProcessingProductKind.PixelData,
            Algorithms: [new ProcessingAlgorithmIdentity("stretch", "1.0.0")],
            MediaType: "image/jpeg");

        var json = JsonSerializer.Serialize(contract, options);
        Assert.Contains("\"algorithms\":[{", json, StringComparison.Ordinal);
        var roundTripped = JsonSerializer.Deserialize<ProcessingGraphProductContract>(json, options);
        Assert.IsNotNull(roundTripped);
        CollectionAssert.AreEqual(contract.Algorithms, roundTripped.Algorithms);
        Assert.AreEqual(contract with { Algorithms = [] }, roundTripped with { Algorithms = [] });

        var missingRequiredMember = Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<ProcessingGraphProductContract>("{\"role\":\"Raw\",\"variant\":\"source\"}", options));
        Assert.Contains("productKind", missingRequiredMember.Message, StringComparison.OrdinalIgnoreCase);

        // ASP.NET Core OpenAPI builds component schemas through JsonSchemaExporter, which serializes every
        // constructor parameter default; a default struct parameter fails there with HTTP 500.
        var schema = options.GetJsonSchemaAsNode(typeof(ProcessingGraphProductContract)).AsObject();
        var properties = schema["properties"]!.AsObject();
        Assert.IsTrue(properties.ContainsKey("algorithms"));
        Assert.IsFalse(properties["algorithms"]!.AsObject().ContainsKey("default"));
        CollectionAssert.AreEquivalent(
            RequiredOutputContractMembers,
            schema["required"]!.AsArray().Select(static node => node!.GetValue<string>()).ToArray());

        var definitionSchema = ProcessingGraphJsonOptions().GetJsonSchemaAsNode(typeof(ProcessingGraphDefinition));
        Assert.IsNotNull(definitionSchema);
    }

    private static JsonSerializerOptions CreateWebOptions()
    {
        // Schema export requires an explicit resolver; the hosts use the reflection resolver as well.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    private static JsonSerializerOptions ProcessingGraphJsonOptions()
    {
        var options = CreateWebOptions();
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }

    private static ProcessingGraphDefinition CreateLegacyTransformDefinition()
    {
        _ = BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.EncodedPreview, out var recipeDefinition);
        var options = BuiltInProcessingRecipes.NormalizeOptions(
            BuiltInProcessingRecipes.EncodedPreview,
            CaptureContractJson.SerializeToElement(new EncodedPreviewOptions()));
        return new(
            ProcessingGraphSchemaVersions.V1,
            "schema-six-legacy",
            "1",
            [new ProcessingGraphSourceDefinition("$raw",
                [new ProcessingGraphProductContract(FrameArtifactRole.Raw, "source", ProcessingProductKind.PixelData)])],
            [new ProcessingGraphNodeDefinition(
                "Preview",
                BuiltInProcessingRecipes.EncodedPreview,
                "cameraagent-v2",
                ProcessingOperationKind.Transform,
                true,
                ProcessingGraphNodeFailurePolicy.Required,
                0,
                options,
                [new ProcessingGraphDependencyDefinition("$raw")],
                [new ProcessingGraphInputContract([FrameArtifactRole.Raw], [ProcessingProductKind.PixelData], [], [], [])],
                [new ProcessingGraphProductContract(
                    FrameArtifactRole.Preview, "preview", ProcessingProductKind.PixelData, recipeDefinition)],
                null,
                ImmutableArray<string>.Empty,
                [ProcessingGraphHosts.CameraAgent])]);
    }
}
