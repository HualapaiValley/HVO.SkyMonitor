using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// #1532: the option descriptors leave the shipped pipelines' graph revisions and product identities unchanged, their
/// defaults equal today's rendering, and they accept and reject what CameraAgent's step binder does.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ProcessingNodeOptionsShippedPipelineTests
{
    private const string W6 = "cameraagent.standalone-w6.json";
    private static readonly ObservatoryLocation Location = new(35.5599378, -113.9119818, 520, "America/Phoenix");

    [TestMethod]
    [DataRow(W6)]
    [DataRow("cameraagent.standalone-w6-mono8.json")]
    public async Task ShippedPipelinesValidateAndRoundTripWithUnchangedIdentities(string fileName)
    {
        var plan = SharedPlan(await LoadAsync(fileName).ConfigureAwait(false));
        var canonical = Encoding.UTF8.GetBytes(plan.CanonicalDefinition.GetRawText());
        var definition = ProcessingGraphJson.Parse(canonical).Definition!;
        var catalog = ProcessingNodeOptionsCatalog.BuiltIn;

        Assert.IsEmpty(catalog.Validate(definition));
        foreach (var reassign in new[] { false, true })
        {
            var roundTripped = definition with
            {
                Nodes = [.. definition.Nodes.Select(node => RoundTrip(catalog, node, reassign))]
            };

            CollectionAssert.AreEqual(canonical, ProcessingGraphJson.SerializeCanonical(roundTripped));
            var compiled = ProcessingGraphCompiler.Compile(roundTripped, new(ProcessingGraphHosts.CameraAgent, []));
            Assert.IsTrue(compiled.IsValid);
            Assert.AreEqual(plan.DefinitionIdentitySha256, compiled.Plan!.DefinitionIdentitySha256);
            Assert.AreEqual(plan.PlanIdentitySha256, compiled.Plan.PlanIdentitySha256);
            CollectionAssert.AreEqual(
                plan.Nodes.Select(static node => (node.Definition.Id, node.IdentitySha256)).ToArray(),
                compiled.Plan.Nodes.Select(static node => (node.Definition.Id, node.IdentitySha256)).ToArray());
        }
    }

    [TestMethod]
    public async Task TheW6SceneNodeCarriesTheDescriptorDefaultsThatTodaysStepRenders()
    {
        var plan = SharedPlan(await LoadAsync(W6).ConfigureAwait(false));
        var scene = plan.Nodes.Single(static node => node.Definition.Id == "scene-presentation").Definition;
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve(scene.StepAlias, scene.StepVersion).Descriptor!;
        var stepDefaults = new ScenePresentationLayerProcessingStepOptions();

        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, scene.EffectiveOptions);

        Assert.AreSame(ProcessingNodeOptionsDescriptors.ScenePresentationLayer, descriptor);
        var expectedColor = new PresentationColor(
            stepDefaults.ConstellationLineRed, stepDefaults.ConstellationLineGreen, stepDefaults.ConstellationLineBlue);
        Assert.AreEqual(expectedColor, descriptor.Find("constellationLineColor")!.DefaultValue.AsColor());
        Assert.AreEqual(
            stepDefaults.ConstellationLineThickness,
            descriptor.Find("constellationLineThickness")!.DefaultValue.AsWholeNumber());
        // Explicit v2 materializes the bound options, so the shipped node states today's values explicitly.
        Assert.IsTrue(decoded.Get("constellationLineColor").IsExplicit);
        Assert.AreEqual(expectedColor, decoded.Get("constellationLineColor").Value.AsColor());
        Assert.IsTrue(decoded.Get("constellationLineThickness").IsExplicit);
        Assert.AreEqual(2, decoded.Get("constellationLineThickness").Value.AsWholeNumber());
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new { }));
        var runtimeDefaults = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(stepDefaults));
        Assert.IsTrue(defaults.IsValid);
        Assert.IsTrue(runtimeDefaults.IsValid);
        foreach (var option in defaults.Options)
            Assert.AreEqual(runtimeDefaults.Get(option.Descriptor.Key).Value, option.Value, option.Descriptor.Key);
    }

    [TestMethod]
    [DataRow("constellationLineRed", "0")]
    [DataRow("constellationLineRed", "255")]
    [DataRow("constellationLineRed", "-0")]
    [DataRow("constellationLineGreen", "0")]
    [DataRow("constellationLineGreen", "-0")]
    [DataRow("constellationLineGreen", "-1")]
    [DataRow("constellationLineGreen", "256")]
    [DataRow("constellationLineBlue", "0")]
    [DataRow("constellationLineBlue", "-0")]
    [DataRow("constellationLineBlue", "1.5")]
    [DataRow("constellationLineBlue", "1e2")]
    [DataRow("constellationLineBlue", "null")]
    [DataRow("constellationLineThickness", "1")]
    [DataRow("constellationLineThickness", "8")]
    [DataRow("constellationLineThickness", "0")]
    [DataRow("constellationLineThickness", "-0")]
    [DataRow("constellationLineThickness", "9")]
    [DataRow("constellationLineThickness", "2.5")]
    [DataRow("constellationLineThickness", "2.0")]
    [DataRow("constellationLineThickness", "true")]
    [DataRow("constellationLineThickness", "99999999999999999999")]
    [DataRow("drawMarkers", "false")]
    [DataRow("drawLabels", "false")]
    [DataRow("drawConstellationLines", "false")]
    [DataRow("drawImageCircle", "false")]
    [DataRow("drawCardinalDirections", "false")]
    [DataRow("drawMarkers", "null")]
    [DataRow("markerRadius", "0")]
    [DataRow("markerRadius", "32")]
    [DataRow("markerRadius", "-1")]
    [DataRow("markerRadius", "33")]
    [DataRow("labelScale", "1")]
    [DataRow("labelScale", "8")]
    [DataRow("labelScale", "0")]
    [DataRow("labelScale", "9")]
    [DataRow("maximumLabelCharacters", "0")]
    [DataRow("maximumLabelCharacters", "64")]
    [DataRow("maximumLabelCharacters", "-1")]
    [DataRow("maximumLabelCharacters", "65")]
    [DataRow("maximumLabelMagnitude", "-30")]
    [DataRow("maximumLabelMagnitude", "30")]
    [DataRow("maximumLabelMagnitude", "2.25")]
    [DataRow("maximumLabelMagnitude", "-31")]
    [DataRow("maximumLabelMagnitude", "31")]
    [DataRow("maximumLabelMagnitude", "null")]
    [DataRow("cardinalScale", "1")]
    [DataRow("cardinalScale", "8")]
    [DataRow("cardinalScale", "0")]
    [DataRow("cardinalScale", "9")]
    public async Task TheDescriptorAndTheStepBinderAgreeOnWhatTheyAccept(string member, string token)
    {
        var edited = WithSceneMember(await LoadAsync(W6).ConfigureAwait(false), member, token);

        var descriptorAccepts = ProcessingNodeOptionsCodec.Decode(
            ProcessingNodeOptionsDescriptors.ScenePresentationLayer, SceneOptions(edited)).IsValid;

        Assert.AreEqual(BinderAccepts(edited), descriptorAccepts, $"{member}={token}");
    }

    [TestMethod]
    [DataRow("-30.01")]
    [DataRow("30.01")]
    public async Task MagnitudeEditorUsesDeclaredBoundsWithoutTheLegacyIntegerRangeRounding(string token)
    {
        var edited = WithSceneMember(await LoadAsync(W6).ConfigureAwait(false), "maximumLabelMagnitude", token);
        // The legacy [Range(-30, 30)] converts to integer before comparing. The editor enforces the declared
        // finite real interval; it does not change that runtime binder or rewrite retained JSON.
        Assert.IsTrue(BinderAccepts(edited));
        Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, ProcessingNodeOptionsCodec.Decode(
            ProcessingNodeOptionsDescriptors.ScenePresentationLayer, SceneOptions(edited)).Diagnostics.Single().Code);
    }

    // The codec is stricter than the binder on quoted numbers and duplicate members. It reads an element rather than
    // re-parsing its text, so it does not see comments or trailing commas that the binder rejects; the graph and
    // configuration parsers admit neither, so no parsed node carries them.
    [TestMethod]
    [DataRow("quoted-number", true, ProcessingOptionReasonCodes.WrongType)]
    [DataRow("duplicate-member", true, ProcessingOptionReasonCodes.DuplicateMember)]
    [DataRow("case-variant-duplicate-member", true, ProcessingOptionReasonCodes.DuplicateMember)]
    [DataRow("comment", false, null)]
    [DataRow("trailing-comma", false, null)]
    public async Task TheDeclaredDifferencesFromTheBinderAreQuotedNumbersDuplicateMembersAndCommentSyntax(
        string difference,
        bool binderAccepts,
        string? codecReason)
    {
        var configuration = await LoadAsync(W6).ConfigureAwait(false);
        var raw = SceneOptions(configuration).GetRawText().TrimEnd();
        var edited = difference switch
        {
            "quoted-number" => WithSceneMember(configuration, "constellationLineThickness", "\"3\""),
            "duplicate-member" => WithSceneOptions(
                configuration, "{\"constellationLineRed\":1,\"constellationLineRed\":2," + raw[1..]),
            "case-variant-duplicate-member" => WithSceneOptions(
                configuration, "{\"constellationLineRed\":1,\"ConstellationLineRed\":2," + raw[1..]),
            "comment" => WithSceneOptions(configuration, "{/*before*/" + raw[1..]),
            "trailing-comma" => WithSceneOptions(configuration, raw[..^1] + ",}"),
            _ => throw new ArgumentOutOfRangeException(nameof(difference), difference, null)
        };

        var decoded = ProcessingNodeOptionsCodec.Decode(
            ProcessingNodeOptionsDescriptors.ScenePresentationLayer, SceneOptions(edited));

        Assert.AreEqual(binderAccepts, BinderAccepts(edited));
        Assert.AreEqual(!binderAccepts, decoded.IsValid);
        Assert.AreEqual(codecReason, decoded.Diagnostics.SingleOrDefault()?.Code);
    }

    private static CameraModuleConfig WithSceneMember(CameraModuleConfig configuration, string member, string token)
    {
        var options = JsonNode.Parse(SceneOptions(configuration).GetRawText())!.AsObject();
        options[member] = JsonNode.Parse(token);
        return WithSceneOptions(configuration, JsonSerializer.SerializeToElement(options));
    }

    private static CameraModuleConfig WithSceneOptions(CameraModuleConfig configuration, string json)
    {
        using var document = JsonDocument.Parse(
            json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return WithSceneOptions(configuration, document.RootElement.Clone());
    }

    private static CameraModuleConfig WithSceneOptions(CameraModuleConfig configuration, JsonElement options)
    {
        var steps = configuration.Pipeline!.Steps
            .Select(step => step.Id == "scene-presentation" ? step with { Options = options } : step)
            .ToArray();
        return configuration with { Pipeline = configuration.Pipeline with { Steps = steps } };
    }

    private static JsonElement SceneOptions(CameraModuleConfig configuration)
        => configuration.Pipeline!.Steps.Single(static step => step.Id == "scene-presentation").Options!.Value;

    private static ProcessingGraphNodeDefinition RoundTrip(
        ProcessingNodeOptionsCatalog catalog,
        ProcessingGraphNodeDefinition node,
        bool reassign)
    {
        if (catalog.Resolve(node.StepAlias, node.StepVersion).Descriptor is not { } descriptor)
        {
            return node;
        }
        // Re-assigning every decoded value is the editor's "save without changes"; it must not move a byte either.
        var edits = reassign
            ? ProcessingNodeOptionsCodec.Decode(descriptor, node.EffectiveOptions).Options
                .ToDictionary(static option => option.Descriptor.Key, static option => option.Value, StringComparer.Ordinal)
            : new Dictionary<string, ProcessingOptionValue>(StringComparer.Ordinal);
        var options = ProcessingNodeOptionsCodec.Encode(descriptor, node.EffectiveOptions, edits).Options!.Value;
        return new(
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
    }

    [TestMethod]
    [DataRow("ScenePresentationLayer", "markerValue")]
    [DataRow("ScenePresentationLayer", "labelValue")]
    [DataRow("ScenePresentationLayer", "imageCircleValue")]
    [DataRow("ScenePresentationLayer", "cardinalValue")]
    [DataRow("EnvironmentPresentationLayer", "value")]
    public async Task NullableIntensitySignedZeroIsRejectedAndExplicitZeroRepairsProducerBinding(string alias, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var baseline = await LoadAsync(W6).ConfigureAwait(false);
        var originalOptions = baseline.Pipeline!.Steps.Single(step => string.Equals(step.Type, alias, StringComparison.OrdinalIgnoreCase)).Options!.Value.GetRawText();
        var source = JsonSerializer.Deserialize<JsonElement>(originalOptions[..^1] + ", \"" + key.ToUpperInvariant() + "\" : -0, \"future\":[1e+02] }");
        CameraModuleConfig Configuration(JsonElement options) => baseline with
        {
            Pipeline = baseline.Pipeline! with
            {
                Steps = baseline.Pipeline.Steps.Select(step => string.Equals(step.Type, alias, StringComparison.OrdinalIgnoreCase)
                    ? step with { Options = options } : step).ToArray()
            }
        };
        using var provider = CreateProvider();
        var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
        Assert.ThrowsExactly<JsonException>(() => factory.CreateGraph(Configuration(source)));
        var descriptor = alias == "ScenePresentationLayer" ? ProcessingNodeOptionsDescriptors.ScenePresentationLayer : RetainedLayerOptionDescriptors.Environment;
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsFalse(decoded.IsValid, "The shared editor must reject the token rejected by the real byte binder.");
        Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, decoded.Diagnostics.Single().Code);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>()).IsValid);
        var repaired = ProcessingNodeOptionsCodec.Encode(descriptor, source,
            new Dictionary<string, ProcessingOptionValue> { [key] = ProcessingOptionValue.FromNullableWholeNumber(0) });
        Assert.IsTrue(repaired.IsValid);
        Assert.AreEqual(source.GetRawText().Replace("-0", "0", StringComparison.Ordinal), repaired.Options!.Value.GetRawText());
        factory.CreateGraph(Configuration(repaired.Options.Value)).DisposeSteps();
    }

    private static bool BinderAccepts(CameraModuleConfig configuration)
    {
        using var provider = CreateProvider();
        try
        {
            provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(configuration).DisposeSteps();
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ValidationException or InvalidOperationException)
        {
            return false;
        }
    }

    private static ProcessingGraphExecutionPlan SharedPlan(CameraModuleConfig configuration)
    {
        using var provider = CreateProvider();
        var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(configuration);
        try
        {
            return graph.SharedPlan!;
        }
        finally
        {
            graph.DisposeSteps();
        }
    }

    private static async Task<CameraModuleConfig> LoadAsync(string fileName)
    {
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, fileName),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            Observatory = Location
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = Path.GetTempPath()
            })
            .Build());
        return services.BuildServiceProvider();
    }
}
