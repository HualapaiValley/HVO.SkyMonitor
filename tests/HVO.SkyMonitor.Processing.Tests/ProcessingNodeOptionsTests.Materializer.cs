using System.Text.Json;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void MaterializerDescriptorPinsVersionDefaultsAndExactSupportedAliases()
    {
        var descriptor = PresentationMaterializerOptionDescriptors.Descriptor;
        Assert.AreSame(descriptor, ProcessingNodeOptionsCatalog.BuiltIn.Resolve("presentationmaterializer", "typed-presentation-materialization-v1").Descriptor);
        Assert.IsNull(ProcessingNodeOptionsCatalog.BuiltIn.Resolve(descriptor.StepAlias, "future-version").Descriptor);
        var option = descriptor.Options.Single();
        Assert.AreEqual(10, option.MaximumItems);
        CollectionAssert.AreEqual(PresentationLayerKindSelection.Supported.ToArray(), option.AllowedValues.ToArray());
        Assert.IsEmpty(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}")).Get(option.Key).Value.AsCollection().Items);
        foreach (var kind in option.AllowedValues)
            Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new { enabledLayerKinds = new[] { kind } })).IsValid, kind);
    }

    [TestMethod]
    public void MaterializerSelectionPreservesAliasesOrderEscapesBindingsAndNoOpBytes()
    {
        var descriptor = PresentationMaterializerOptionDescriptors.Descriptor;
        var source = Json("""{ "EnabledLayerKinds" : [ "corner-annotations", "scene-cardinals", "cloud-m\u0061sk" ], "useRichFrameFacts":true, "basePreviewVariant":"base", "sceneAnnotationVariant":"producer", "outputVariant":"saved", "future":1e+02 }""");
        var value = ProcessingNodeOptionsCodec.Decode(descriptor, source).Get("enabledLayerKinds").Value;
        Assert.AreEqual(source.GetRawText(), ProcessingNodeOptionsCodec.Encode(descriptor, source,
            new Dictionary<string, ProcessingOptionValue> { ["enabledLayerKinds"] = value }).Options!.Value.GetRawText());
        var moved = value.AsCollection().Move(0, 2);
        var changed = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        { ["enabledLayerKinds"] = ProcessingOptionValue.FromChoiceCollection(moved.Items) });
        Assert.IsTrue(changed.IsValid);
        Assert.AreEqual("""{ "EnabledLayerKinds" : ["scene-cardinals","cloud-m\u0061sk","corner-annotations"], "useRichFrameFacts":true, "basePreviewVariant":"base", "sceneAnnotationVariant":"producer", "outputVariant":"saved", "future":1e+02 }""", changed.Options!.Value.GetRawText());
        foreach (var json in new[] { "{}", "{ \"enabledLayerKinds\" : [ ] }" })
            Assert.AreEqual(json, ProcessingNodeOptionsCodec.Encode(descriptor, Json(json), new Dictionary<string, ProcessingOptionValue>
            { ["enabledLayerKinds"] = ProcessingOptionValue.FromChoiceCollection([]) }).Options!.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("\"environment\"")]
    [DataRow("{}")]
    [DataRow("[null]")]
    [DataRow("[1]")]
    [DataRow("[\"Environment\"]")]
    [DataRow("[\" environment\"]")]
    [DataRow("[\"unknown\"]")]
    [DataRow("[\"environment\",\"environment\"]")]
    [DataRow("[\"environment\",\"corner-annotations\"]")]
    [DataRow("[\"scene-annotation\",\"star-annotations\"]")]
    [DataRow("[\"scene-cardinals\",\"cardinal-directions\"]")]
    [DataRow("[\"scene-image-circle\",\"image-circle\"]")]
    [DataRow("[\"scene-constellations\",\"constellations\"]")]
    [DataRow("[\"environment\",\"environment\",\"environment\",\"environment\",\"environment\",\"environment\",\"environment\",\"environment\",\"environment\",\"environment\",\"environment\"]")]
    public void MaterializerSelectionRejectsInvalidShapesCaseAliasesAndOverflow(string token)
    {
        var descriptor = PresentationMaterializerOptionDescriptors.Descriptor;
        var source = Json("{\"enabledLayerKinds\":" + token + "}");
        Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, source).IsValid);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>()).IsValid);
        Assert.IsNotEmpty(ProcessingNodeOptionsCatalog.BuiltIn.Validate(Graph(Node("selection", descriptor.StepAlias, descriptor.StepVersion, source.GetRawText()))));
        if (source.GetProperty("enabledLayerKinds").ValueKind == JsonValueKind.Array && source.GetProperty("enabledLayerKinds").EnumerateArray().All(static item => item.ValueKind == JsonValueKind.String))
            Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"), new Dictionary<string, ProcessingOptionValue>
            { ["enabledLayerKinds"] = ProcessingOptionValue.FromChoiceCollection(source.GetProperty("enabledLayerKinds").EnumerateArray().Select(static item => item.GetString()!)) }).IsValid);
    }
}
