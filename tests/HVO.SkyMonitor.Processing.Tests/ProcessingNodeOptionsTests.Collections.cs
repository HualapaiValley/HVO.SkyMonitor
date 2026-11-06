using System.Text.Json;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    private static readonly JsonSerializerOptions CollectionRuntimeJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [TestMethod]
    public void SelectionDescriptorsMatchEachConsumersBoundsAndKeepDuplicateUnknownIds()
    {
        foreach (var descriptor in new[] { ProjectedSceneOptionDescriptors.Descriptor, Scene, ProcessingNodeOptionsDescriptors.ScenePresentationLayerV12 })
        {
            var option = descriptor.Find("constellationIds")!;
            var limit = descriptor.StepAlias == "ProjectedScene" ? 128 : 256;
            Assert.AreEqual(limit, option.MaximumItems);
            Assert.AreEqual(16, option.MaximumLength);
            var source = JsonSerializer.SerializeToElement(new { constellationIds = Enumerable.Repeat("unknown-retained", limit).ToArray() });
            Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, source).IsValid);
            Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new
            { constellationIds = Enumerable.Repeat("ORI", limit + 1).ToArray() })).IsValid);
            Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"), new Dictionary<string, ProcessingOptionValue>
            { [option.Key] = ProcessingOptionValue.FromTextCollection(Enumerable.Repeat("ORI", limit + 1)) }).IsValid);
            Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("""{"constellationIds":["Ori","ORI"," X ","1234567890123456"]}""")).IsValid);
            var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}"));
            Assert.IsEmpty(defaults.Get("constellationIds").Value.AsCollection().Items);
            Assert.IsFalse(defaults.Get("constellationIds").IsExplicit);
            Assert.AreEqual("{}", ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"),
                new Dictionary<string, ProcessingOptionValue> { ["constellationIds"] = ProcessingOptionValue.FromTextCollection([]) }).Options!.Value.GetRawText());
        }
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("\"ORI\"")]
    [DataRow("{}")]
    [DataRow("[null]")]
    [DataRow("[1]")]
    [DataRow("[\"\"]")]
    [DataRow("[\" \"]")]
    [DataRow("[\"12345678901234567\"]")]
    public void SelectionIdsRejectInvalidShapesAndItemsAcrossBothConsumers(string token)
    {
        foreach (var descriptor in new[] { ProjectedSceneOptionDescriptors.Descriptor, Scene, ProcessingNodeOptionsDescriptors.ScenePresentationLayerV12 })
        {
            var source = Json("{\"constellationIds\":" + token + "}");
            Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, source).IsValid);
            Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>()).IsValid);
            Assert.IsNotEmpty(ProcessingNodeOptionsCatalog.BuiltIn.Validate(Graph(Node("selection", descriptor.StepAlias, descriptor.StepVersion, source.GetRawText()))));
        }
    }

    [TestMethod]
    public void SolarSelectionUsesDefinedEnumBoundsAndRetainsEquivalentSourceTokens()
    {
        var descriptor = ProjectedSceneOptionDescriptors.Descriptor;
        var source = Json("""{ "SolarSystemBodies": [ 4, "mArS", "4", " Mars ", "Sun, Mars", "M\u0061rs" ], "future":1e+02 }""");
        // Do not infer comma-name or numeric-string semantics: this is the same converter the runtime uses.
        var runtime = source.GetProperty("SolarSystemBodies").Deserialize<SolarSystemBody[]>(CollectionRuntimeJson)!;
        Assert.IsTrue(runtime.All(static value => Enum.IsDefined(value)));
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(decoded.IsValid, string.Join(' ', decoded.Diagnostics));
        CollectionAssert.AreEqual(runtime.Select(static value => value.ToString()).ToArray(), decoded.Get("solarSystemBodies").Value.AsCollection().Items.ToArray());
        var edits = decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value);
        Assert.AreEqual(source.GetRawText(), ProcessingNodeOptionsCodec.Encode(descriptor, source, edits).Options!.Value.GetRawText());
        var selection = decoded.Get("solarSystemBodies").Value.AsCollection().Move(0, 5).RemoveAt(1).Add("Moon");
        var changed = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        { ["solarSystemBodies"] = ProcessingOptionValue.FromCollection(ProcessingOptionKind.EnumCollection, selection) });
        Assert.IsTrue(changed.IsValid);
        Assert.AreEqual("""{ "SolarSystemBodies": ["mArS"," Mars ","Sun, Mars","M\u0061rs",4,"Moon"], "future":1e+02 }""", changed.Options!.Value.GetRawText());
        var maximum = Enum.GetValues<SolarSystemBody>().Length;
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new { solarSystemBodies = Enumerable.Repeat(4, maximum) })).IsValid);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new { solarSystemBodies = Enumerable.Repeat(4, maximum + 1) })).IsValid);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        { ["solarSystemBodies"] = ProcessingOptionValue.FromEnumCollection(["Pluto"]) }).IsValid);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[null]")]
    [DataRow("[99]")]
    [DataRow("[-1]")]
    [DataRow("[4.0]")]
    [DataRow("[true]")]
    [DataRow("[\"Pluto\"]")]
    [DataRow("[\"99\"]")]
    public void SolarSelectionRejectsInvalidEnumsAndShapes(string token)
        => Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(ProjectedSceneOptionDescriptors.Descriptor, Json("{\"solarSystemBodies\":" + token + "}")).IsValid);

    [TestMethod]
    public void TypedCollectionReplacementPreservesUnchangedSourceOccurrences()
    {
        var descriptor = ProjectedSceneOptionDescriptors.Descriptor;
        var source = Json("""{ "solarSystemBodies":[4,"mArS"], "constellationIds":["\u004fRI","retained-id"], "future":1e+02 }""");
        var changed = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["solarSystemBodies"] = ProcessingOptionValue.FromEnumCollection(["Mars", "Mars", "Sun"]),
            ["constellationIds"] = ProcessingOptionValue.FromTextCollection(["retained-id", "ORI", "CAS"])
        });
        Assert.IsTrue(changed.IsValid);
        Assert.AreEqual("""{ "solarSystemBodies":[4,"mArS","Sun"], "constellationIds":["retained-id","\u004fRI","CAS"], "future":1e+02 }""", changed.Options!.Value.GetRawText());
    }

    [TestMethod]
    public void SelectionChangesRetainEscapedUnknownIdsAndRestoreNoOpIdentity()
    {
        var descriptor = ProjectedSceneOptionDescriptors.Descriptor;
        var source = Json("""{ "ConstellationIds" : [ "\u004fRI", "unknown", "Ori" ], "future":{"x":1e+02}, "Future":null }""");
        var value = ProcessingNodeOptionsCodec.Decode(descriptor, source).Get("constellationIds").Value;
        var moved = value.AsCollection().Move(0, 2);
        Assert.AreEqual(source.GetRawText(), ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        { ["constellationIds"] = ProcessingOptionValue.FromCollection(value.Kind, moved.Move(2, 0)) }).Options!.Value.GetRawText());
        var changed = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        { ["constellationIds"] = ProcessingOptionValue.FromCollection(value.Kind, moved.RemoveAt(1).Add("CAS")) });
        Assert.AreEqual("""{ "ConstellationIds" : ["unknown","\u004fRI","CAS"], "future":{"x":1e+02}, "Future":null }""", changed.Options!.Value.GetRawText());
        Assert.AreEqual(value, ProcessingOptionValue.FromTextCollection(["ORI", "unknown", "Ori"]));
        Assert.AreEqual(value.GetHashCode(), ProcessingOptionValue.FromTextCollection(["ORI", "unknown", "Ori"]).GetHashCode());
    }
}
