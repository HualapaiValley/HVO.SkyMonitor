using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public async Task AnnotationV4UsesRgbChannelsOnRgbAndConstellationIntensityOnlyOnMono8()
    {
        foreach (var format in new[] { CameraPixelFormat.Mono8, CameraPixelFormat.Rgb24 })
        {
            var channels = format == CameraPixelFormat.Mono8 ? 1 : 3;
            var source = new ProcessingArtifact(Guid.Parse("10000000-0000-0000-0000-000000000001"), FrameArtifactRole.Preview,
                "preview", new string('0', 64), "application/x-hvo-packed-image",
                new FrameLayoutDescriptor(32, 32, 32 * channels, format, FrameByteOrder.NotApplicable, 8, 8,
                    FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 255, 32 * 32 * channels),
                new byte[32 * 32 * channels], DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1),
                new("rig-v1", "north-up", "none-v1", "full-v1", "sensor-v1", "night-v1", "pipeline-v1"));
            var annotation = new ProcessingAnnotationInput([], [new("TST", new(2, 2), new(20, 2))],
                new(1, 1), null, new string('A', 64));
            async Task<ProcessingProduct> Execute(AnnotationRecipeOptions options)
            {
                var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(new(BuiltInProcessingRecipes.Annotation,
                    JsonSerializer.SerializeToElement(options), ProcessingInputSelector.RecipeResult(source.Role, source.Variant, source.RecipeIdentitySha256),
                    [source], "annotation", annotation)).ConfigureAwait(false);
                Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
                return outcome.Products.Single();
            }
            var options = new AnnotationRecipeOptions(OutputEncoding: "Packed");
            var original = await Execute(options).ConfigureAwait(false);
            var changedRgb = await Execute(options with { ConstellationLineRed = 255, ConstellationLineGreen = 0, ConstellationLineBlue = 0 }).ConfigureAwait(false);
            Assert.AreNotEqual(original.Recipe.IdentitySha256, changedRgb.Recipe.IdentitySha256);
            if (format == CameraPixelFormat.Mono8)
                CollectionAssert.AreEqual(original.Payload.ToArray(), changedRgb.Payload.ToArray(), "RGB channels have no Mono8 effect.");
            else
                Assert.AreNotEqual(original.ChecksumSha256, changedRgb.ChecksumSha256, "RGB channels reach the renderer.");
            var changedIntensity = await Execute(options with { ConstellationLineValue = 32 }).ConfigureAwait(false);
            if (format == CameraPixelFormat.Mono8)
                Assert.AreNotEqual(original.ChecksumSha256, changedIntensity.ChecksumSha256);
            else
            {
                CollectionAssert.AreEqual(original.Payload.ToArray(), changedIntensity.Payload.ToArray());
                CollectionAssert.AreEqual(new byte[] { 77, 128, 204 }, original.Payload.Slice((2 * 32 + 8) * 3, 3).ToArray(), "Default RGB is 96/160/255 with opacity 0.8.");
            }
            var replay = await Execute(options).ConfigureAwait(false);
            Assert.AreEqual(original.OutputIdentitySha256, replay.OutputIdentitySha256);
            CollectionAssert.AreEqual(original.Payload.ToArray(), replay.Payload.ToArray());
        }
    }

    [TestMethod]
    public void AnnotationDescriptorBindsOnlyTheExactRegisteredImplementationAndEffectiveMembers()
    {
        var descriptor = AnnotationOptionDescriptors.Descriptor;
        Assert.AreEqual("projected-annotation-v4", descriptor.StepVersion);
        Assert.IsTrue(BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.Annotation, out var recipe));
        Assert.AreEqual(recipe!.ImplementationVersion, descriptor.StepVersion);
        Assert.AreSame(descriptor, ProcessingNodeOptionsCatalog.BuiltIn.Resolve("aNnOtAtIoN", descriptor.StepVersion).Descriptor);
        foreach (var version in new[] { "projected-annotation-v3", "projected-annotation-v5", "projected-scene-annotation-v3", "Projected-annotation-v4" })
        {
            var resolution = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("Annotation", version);
            Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.UnsupportedVersion, resolution.Status);
            Assert.IsNull(resolution.Descriptor);
            Assert.IsNull(resolution.Upgrade);
        }
        Assert.HasCount(21, descriptor.Options);
        foreach (var key in new[] { "constellationLineRed", "constellationLineGreen", "constellationLineBlue",
            "recipeVersion", "outputVariant", "requireProjectedSceneDependency", "expectedPositionDiagnostics", "constellationIds",
            "topLeftTokens", "topRightTokens", "bottomLeftTokens", "bottomRightTokens", "environmentalKinds", "future" })
        {
            Assert.IsNull(descriptor.Find(key), key);
            Assert.AreEqual(ProcessingOptionReasonCodes.UnknownOption, ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"),
                new Dictionary<string, ProcessingOptionValue> { [key] = ProcessingOptionValue.FromText("changed") }).Diagnostics.Single().Code, key);
        }
    }

    [TestMethod]
    public void AnnotationCodecEnforcesEveryScalarBoundTypeAndDuplicateMember()
    {
        var descriptor = AnnotationOptionDescriptors.Descriptor;
        void Check(string member, string token, bool valid)
            => Assert.AreEqual(valid, ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"" + member + "\":" + token + "}")).IsValid, member + "=" + token);
        foreach (var option in descriptor.Options)
        {
            if (option.Kind == ProcessingOptionKind.Color)
            {
                foreach (var channel in option.Members)
                {
                    foreach (var token in new[] { "0", "255" }) Check(channel, token, true);
                    foreach (var token in new[] { "-1", "256", "null", "1.0", "1e0", "\"1\"", "true" }) Check(channel, token, false);
                    Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"" + channel + "\":0,\"" + channel.ToUpperInvariant() + "\":1}")).Diagnostics.Any(static error => error.Code == ProcessingOptionReasonCodes.DuplicateMember));
                }
                continue;
            }
            var member = option.Members.Single();
            Check(member, "null", false);
            Check(member, "\"1\"", false);
            if (option.Kind == ProcessingOptionKind.Toggle)
            {
                Check(member, "true", true);
                Check(member, "false", true);
                Check(member, "1", false);
            }
            else if (option.Kind == ProcessingOptionKind.WholeNumber)
            {
                foreach (var bound in new[] { option.MinimumWholeNumber!.Value, option.MaximumWholeNumber!.Value })
                    Check(member, bound.ToString(CultureInfo.InvariantCulture), true);
                Check(member, (option.MinimumWholeNumber!.Value - 1).ToString(CultureInfo.InvariantCulture), false);
                Check(member, (option.MaximumWholeNumber!.Value + 1).ToString(CultureInfo.InvariantCulture), false);
                Check(member, "1.0", false);
                Check(member, "1e0", false);
            }
            else
            {
                foreach (var bound in new[] { option.MinimumRealNumber!.Value, option.MaximumRealNumber!.Value })
                    Check(member, bound.ToString("R", CultureInfo.InvariantCulture), true);
                Check(member, (option.MinimumRealNumber!.Value - 0.01).ToString("R", CultureInfo.InvariantCulture), false);
                Check(member, (option.MaximumRealNumber!.Value + 0.01).ToString("R", CultureInfo.InvariantCulture), false);
                Check(member, "1e999", false);
            }
            Check(member, "[]", false);
            var duplicate = Json("{\"" + member + "\":null,\"" + member.ToUpperInvariant() + "\":null}");
            Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, duplicate).Diagnostics.Any(static error => error.Code == ProcessingOptionReasonCodes.DuplicateMember));
        }
        var invalidGraph = Graph(Node("annotation", "Annotation", descriptor.StepVersion, "{\"metadataScale\":5}"));
        Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, ProcessingNodeOptionsCatalog.BuiltIn.Validate(invalidGraph).Single().Code);
    }

    [TestMethod]
    public void AnnotationCodecPreservesProtectedJsonFormattingAndUnchangedIdentities()
    {
        var descriptor = AnnotationOptionDescriptors.Descriptor;
        const string json = """{ "MaRkRadius": 6, "MarkerValue":144, "LabelScale":1, "ConstellationLineOpacity":8e-1, "RecipeVersion":"retained", "OutputVariant":"retained-output", "RequireProjectedSceneDependency":true, "ExpectedPositionDiagnostics":true, "ConstellationIds":[ "ORI" ], "ConstellationLineRed":1, "ConstellationLineGreen":2, "ConstellationLineBlue":3, "TopLeftTokens":["utc"], "TopRightTokens":[], "BottomLeftTokens":null, "BottomRightTokens":[], "EnvironmentalKinds":[], "future": { "escaped":"\u0041", "number":1e+02 }, "Future":null }""";
        var source = Json(json);
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(defaults.IsValid);
        var edits = defaults.Options.ToDictionary(static option => option.Descriptor.Key, static option => option.Value);
        Assert.AreEqual(json, ProcessingNodeOptionsCodec.Encode(descriptor, source, edits).Options!.Value.GetRawText());
        Assert.AreEqual("{}", ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"),
            ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}")).Options.ToDictionary(static option => option.Descriptor.Key, static option => option.Value)).Options!.Value.GetRawText());
        edits["markRadius"] = ProcessingOptionValue.FromWholeNumber(12);
        edits["constellationLineOpacity"] = ProcessingOptionValue.FromRealNumber(0.5);
        Assert.AreEqual(json.Replace("Radius\": 6", "Radius\": 12", StringComparison.Ordinal).Replace("8e-1", "0.5", StringComparison.Ordinal),
            ProcessingNodeOptionsCodec.Encode(descriptor, source, edits).Options!.Value.GetRawText());
        var graph = Graph(Node("annotation", "Annotation", descriptor.StepVersion, json));
        var compiled = ProcessingGraphCompiler.Compile(graph, new(ProcessingGraphHosts.CameraAgent, []));
        var roundTrip = ProcessingGraphCompiler.Compile(RoundTrip(ProcessingNodeOptionsCatalog.BuiltIn, graph), new(ProcessingGraphHosts.CameraAgent, []));
        Assert.IsTrue(compiled.IsValid);
        Assert.AreEqual(compiled.Plan!.PlanIdentitySha256, roundTrip.Plan!.PlanIdentitySha256);
        CollectionAssert.AreEqual(ProcessingGraphJson.SerializeCanonical(graph), ProcessingGraphJson.SerializeCanonical(RoundTrip(ProcessingNodeOptionsCatalog.BuiltIn, graph)));
    }
}
