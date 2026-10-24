using System.Globalization;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void RetainedLayerDescriptorsPinVersionsAndRejectInvalidScalars()
    {
        Assert.AreEqual(PresentationLayerProducers.CloudProducerVersion, RetainedLayerOptionDescriptors.Cloud.StepVersion);
        Assert.AreEqual(PresentationLayerProducers.MetadataProducerVersion, RetainedLayerOptionDescriptors.Environment.StepVersion);
        foreach (var descriptor in new[] { RetainedLayerOptionDescriptors.Cloud, RetainedLayerOptionDescriptors.Environment })
        {
            Assert.AreSame(descriptor, ProcessingNodeOptionsCatalog.BuiltIn.Resolve(descriptor.StepAlias.ToUpperInvariant(), descriptor.StepVersion).Descriptor);
            foreach (var version in new[] { descriptor.StepVersion + "-future", descriptor.StepVersion.ToUpperInvariant() })
            {
                var resolution = ProcessingNodeOptionsCatalog.BuiltIn.Resolve(descriptor.StepAlias, version);
                Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.UnsupportedVersion, resolution.Status);
                Assert.IsNull(resolution.Descriptor);
                Assert.IsNull(resolution.Upgrade);
            }
            foreach (var option in descriptor.Options)
            {
                var valid = option.Kind == ProcessingOptionKind.Toggle ? new[] { "true", "false" }
                    : new[] { option.MinimumWholeNumber!.Value.ToString(CultureInfo.InvariantCulture), option.MaximumWholeNumber!.Value.ToString(CultureInfo.InvariantCulture) };
                var invalid = new List<string> { "null", "\"1\"", "[]", "1.0", "1e0" };
                if (option.Kind == ProcessingOptionKind.Toggle) invalid.Add("1");
                else
                {
                    invalid.Add((option.MinimumWholeNumber!.Value - 1).ToString(CultureInfo.InvariantCulture));
                    invalid.Add((option.MaximumWholeNumber!.Value + 1).ToString(CultureInfo.InvariantCulture));
                }
                foreach (var token in valid)
                    Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"" + option.Key + "\":" + token + "}")).IsValid);
                foreach (var token in invalid)
                {
                    var graph = Graph(Node("layer", descriptor.StepAlias, descriptor.StepVersion, "{\"" + option.Key + "\":" + token + "}"));
                    Assert.IsNotEmpty(ProcessingNodeOptionsCatalog.BuiltIn.Validate(graph), option.Key + "=" + token);
                }
                Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"" + option.Key + "\":null,\"" + option.Key.ToUpperInvariant() + "\":null}"))
                    .Diagnostics.Any(static error => error.Code == ProcessingOptionReasonCodes.DuplicateMember));
            }
        }
    }

    [TestMethod]
    public void RetainedLayerCodecPreservesDimensionsNullVariantsBindingsAndUnknownBytes()
    {
        const string source = """{ "WidthPixels":1936, "HeightPixels":1216, "Value":null, "EnvironmentalKinds":["AirTemperature"], "StackPreviewVariant":"retained-stack", "FactsOutputVariant":"retained-facts", "OutputVariant":"retained-layer", "MaskOutputVariant":"retained-mask", "LabelOutputVariant":"retained-label", "DrAwLabels":true, "LineThickness":1, "Scale":1, "Inset":4, "LineSpacing":2, "future":{"escaped":"\u0041","number":1e+02}, "Future":null }""";
        foreach (var descriptor in new[] { RetainedLayerOptionDescriptors.Cloud, RetainedLayerOptionDescriptors.Environment })
        {
            var edits = ProcessingNodeOptionsCodec.Decode(descriptor, Json(source)).Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value);
            Assert.AreEqual(source, ProcessingNodeOptionsCodec.Encode(descriptor, Json(source), edits).Options!.Value.GetRawText());
            Assert.AreEqual("{}", ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"), edits).Options!.Value.GetRawText());
            foreach (var key in new[] { "widthPixels", "heightPixels", "value", "environmentalKinds", "stackPreviewVariant", "factsOutputVariant", "outputVariant", "maskOutputVariant", "labelOutputVariant" })
            {
                Assert.IsNull(descriptor.Find(key));
                Assert.AreEqual(ProcessingOptionReasonCodes.UnknownOption, ProcessingNodeOptionsCodec.Encode(descriptor, Json(source),
                    new Dictionary<string, ProcessingOptionValue> { [key] = ProcessingOptionValue.FromWholeNumber(2) }).Diagnostics.Single().Code);
            }
            var keyToEdit = descriptor == RetainedLayerOptionDescriptors.Cloud ? "lineThickness" : "inset";
            edits[keyToEdit] = ProcessingOptionValue.FromWholeNumber(8);
            var expected = source.Replace(descriptor == RetainedLayerOptionDescriptors.Cloud ? "Thickness\":1" : "Inset\":4",
                descriptor == RetainedLayerOptionDescriptors.Cloud ? "Thickness\":8" : "Inset\":8", StringComparison.Ordinal);
            Assert.AreEqual(expected, ProcessingNodeOptionsCodec.Encode(descriptor, Json(source), edits).Options!.Value.GetRawText());
            var graph = Graph(Node("layer", descriptor.StepAlias, descriptor.StepVersion, source));
            foreach (var host in new[] { ProcessingGraphHosts.CameraAgent, ProcessingGraphHosts.LogicHost })
            {
                var before = ProcessingGraphCompiler.Compile(graph, new(host, []));
                var after = ProcessingGraphCompiler.Compile(RoundTrip(ProcessingNodeOptionsCatalog.BuiltIn, graph), new(host, []));
                Assert.IsTrue(before.IsValid);
                Assert.AreEqual(before.Plan!.PlanIdentitySha256, after.Plan!.PlanIdentitySha256);
            }
        }
    }
}
