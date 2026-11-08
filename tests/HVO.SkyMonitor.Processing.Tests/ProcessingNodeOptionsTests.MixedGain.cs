using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    [DataRow("0", "Trailing")]
    [DataRow("1", "Centered")]
    [DataRow("\"centered\"", "Centered")]
    [DataRow("\"1\"", "Centered")]
    public void MixedGainPanelBindsExistingWindowAndPreservesRecipeAndRetainedBindings(string token, string expected)
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("RollingCombination", "mixed-gain-normalized-mean-v1").Descriptor;
        Assert.IsNotNull(descriptor);
        var source = Json("{ \"Recipe\":\"mixed-gain-normalized-mean\", \"WindowKind\":" + token +
            ", \"WindowSize\":7, \"OutputVariant\":\"retained\", \"normalizationPlan\":{\"future\":1e+02} }");
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(decoded.IsValid, string.Join(' ', decoded.Diagnostics));
        Assert.AreEqual(expected, decoded.Get("windowKind").Value.AsText());
        Assert.AreEqual(source.GetRawText(), ProcessingNodeOptionsCodec.Encode(descriptor, source,
            decoded.Options.ToDictionary(item => item.Descriptor.Key, item => item.Value)).Options!.Value.GetRawText());
        var edited = ProcessingNodeOptionsCodec.Encode(descriptor, source,
            new Dictionary<string, ProcessingOptionValue> { ["maximumAgeMilliseconds"] = ProcessingOptionValue.FromNullableRealNumber(12.5) });
        Assert.IsTrue(edited.IsValid);
        Assert.AreEqual(source.GetProperty("Recipe").GetRawText(), edited.Options!.Value.GetProperty("Recipe").GetRawText());
        Assert.AreEqual(source.GetProperty("normalizationPlan").GetRawText(), edited.Options.Value.GetProperty("normalizationPlan").GetRawText());
        Assert.AreEqual(12.5, ProcessingNodeOptionsCodec.Decode(descriptor, edited.Options.Value).Get("maximumAgeMilliseconds").Value.AsNullableRealNumber());
    }

    [TestMethod]
    [DataRow("\"windowSize\":0")]
    [DataRow("\"windowSize\":101")]
    [DataRow("\"windowKind\":\"Centered\",\"windowSize\":65")]
    [DataRow("\"windowKind\":2")]
    [DataRow("\"maximumAgeMilliseconds\":0")]
    [DataRow("\"maximumIntegrationMilliseconds\":-1")]
    [DataRow("\"enabled\":null")]
    [DataRow("\"windowSize\":5,\"WINDOWSIZE\":7")]
    public void MixedGainPanelRejectsInvalidWindowSettings(string members)
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("RollingCombination", "mixed-gain-normalized-mean-v1").Descriptor;
        Assert.IsNotNull(descriptor);
        var source = Json("{\"recipe\":\"mixed-gain-normalized-mean\"," + members + "}");
        Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, source).IsValid);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>()).IsValid);
    }

    [TestMethod]
    public void MixedGainPanelCannotChangeRecipeOrAcceptDifferentRecipeIdentity()
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("RollingCombination", "mixed-gain-normalized-mean-v1").Descriptor;
        Assert.IsNotNull(descriptor);
        foreach (var source in new[] { "{}", "{\"recipe\":null}", "{\"recipe\":\"other\"}",
            "{\"recipe\":\"mixed-gain-normalized-mean\",\"RECIPE\":null}" })
            Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, Json(source)).IsValid);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, Json("{\"recipe\":\"mixed-gain-normalized-mean\"}"),
            new Dictionary<string, ProcessingOptionValue> { ["recipe"] = ProcessingOptionValue.FromText("other") }).IsValid);
        Assert.IsNull(ProcessingNodeOptionsCatalog.BuiltIn.Resolve("RollingCombination", "mixed-gain-normalized-mean-future").Descriptor);
    }

    [TestMethod]
    public void MixedGainPanelWindowContractMustMatchOptionsOnBothHosts()
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("RollingCombination", "mixed-gain-normalized-mean-v1").Descriptor;
        Assert.IsNotNull(descriptor);
        foreach (var kind in new[] { ProcessingGraphWindowKind.Trailing, ProcessingGraphWindowKind.Centered })
        {
            var window = new ProcessingGraphWindowRequirement(kind, kind == ProcessingGraphWindowKind.Centered ? 4 : 1, 4,
                kind == ProcessingGraphWindowKind.Centered ? [-2, -1, 0, 1] : [], ["rig"]);
            var template = Node("mixed", descriptor.StepAlias, descriptor.StepVersion,
                "{\"recipe\":\"mixed-gain-normalized-mean\",\"windowSize\":4,\"windowKind\":\"" + kind + "\"}");
            ProcessingGraphNodeDefinition WithWindow(ProcessingGraphWindowRequirement requirement) => new(
                template.Id, template.StepAlias, template.StepVersion, ProcessingOperationKind.Window,
                template.Enabled, template.FailurePolicy, template.Order, template.EffectiveOptions,
                template.Dependencies, template.Inputs,
                [new(FrameArtifactRole.Combined, "mixed", ProcessingProductKind.PixelData,
                    new(BuiltInProcessingRecipes.MixedGainNormalizedMean, "1.0.0", descriptor.StepVersion, ProcessingOperationKind.Window))],
                requirement, template.CapabilityLabels, template.HostApplicability);
            foreach (var host in new[] { ProcessingGraphHosts.CameraAgent, ProcessingGraphHosts.LogicHost })
            {
                Assert.IsTrue(ProcessingGraphCompiler.Compile(Graph(WithWindow(window)), new(host, [])).IsValid);
                foreach (var mismatch in new[] { window with { MaximumInputCount = 5 }, window with { RequiredPositions = [-99, 0] } })
                    Assert.IsFalse(ProcessingGraphCompiler.Compile(Graph(WithWindow(mismatch)), new(host, [])).IsValid);
            }
        }
    }
}
