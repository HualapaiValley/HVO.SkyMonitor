using System.Text.Json;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void EnvironmentSelectionDistinguishesAbsentDefaultEmptyAndEverySupportedKind()
    {
        var descriptor = RetainedLayerOptionDescriptors.Environment;
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}"));
        string[] expected = ["AirTemperature", "RelativeHumidity", "AtmosphericPressure", "WindSpeed", "RainState", "CloudCover"];
        CollectionAssert.AreEqual(expected, defaults.Get("environmentalKinds").Value.AsCollection().Items.ToArray());
        Assert.IsFalse(defaults.Get("environmentalKinds").IsExplicit);
        Assert.IsEmpty(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"environmentalKinds\":[]}")).Get("environmentalKinds").Value.AsCollection().Items);
        foreach (var kind in Enum.GetValues<EnvironmentalObservationKind>())
        {
            foreach (var token in new[] { JsonSerializer.Serialize(kind), JsonSerializer.Serialize(kind.ToString()), JsonSerializer.Serialize(kind.ToString().ToUpperInvariant()) })
            {
                var source = Json("{\"environmentalKinds\":[" + token + "]}");
                var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
                Assert.IsTrue(decoded.IsValid);
                Assert.AreEqual(kind.ToString(), decoded.Get("environmentalKinds").Value.AsCollection().Items.Single());
                Assert.AreEqual(source.GetRawText(), ProcessingNodeOptionsCodec.Encode(descriptor, source,
                    decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
            }
        }
        var empty = ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"), new Dictionary<string, ProcessingOptionValue>
        { ["environmentalKinds"] = ProcessingOptionValue.FromEnumCollection([]) });
        Assert.IsEmpty(ProcessingNodeOptionsCodec.Decode(descriptor, empty.Options!.Value).Get("environmentalKinds").Value.AsCollection().Items);
        Assert.IsTrue(empty.Options!.Value.TryGetProperty("environmentalKinds", out _));
        var repeatedSource = JsonSerializer.SerializeToElement(new { environmentalKinds = Enumerable.Repeat("AirTemperature", 32) });
        var repeated = ProcessingNodeOptionsCodec.Decode(descriptor, repeatedSource);
        Assert.IsTrue(repeated.IsValid, "The consumer has no occurrence limit; effective kinds are deduplicated.");
        Assert.AreEqual(repeatedSource.GetRawText(), ProcessingNodeOptionsCodec.Encode(descriptor, repeatedSource,
            repeated.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
        Assert.ThrowsExactly<ArgumentException>(() => ProcessingOptionDescriptor.EnumCollection<EnvironmentalObservationKind>(
            "kinds", "kinds", "Kinds", "Kinds to show.", [(EnvironmentalObservationKind)999]));
    }

    [TestMethod]
    public void EnvironmentSelectionPreservesSourceSpellingsDuplicatesOrderAndHostNeutralIdentity()
    {
        var descriptor = RetainedLayerOptionDescriptors.Environment;
        const string json = """{ "EnvironmentalKinds" : [ 0, "aIrTemperature", "0", " WindSpeed ", "Cloud\u0043over" ], "WidthPixels":1936, "future":{"n":1e+02}, "Future":null }""";
        var source = Json(json);
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(decoded.IsValid);
        var selection = decoded.Get("environmentalKinds").Value.AsCollection();
        Assert.AreEqual(json, ProcessingNodeOptionsCodec.Encode(descriptor, source,
            decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
        var changed = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        { ["environmentalKinds"] = ProcessingOptionValue.FromCollection(ProcessingOptionKind.EnumCollection, selection.Move(4, 0).RemoveAt(3)) });
        Assert.AreEqual("""{ "EnvironmentalKinds" : ["Cloud\u0043over",0,"aIrTemperature"," WindSpeed "], "WidthPixels":1936, "future":{"n":1e+02}, "Future":null }""", changed.Options!.Value.GetRawText());
        var graph = Graph(Node("environment", descriptor.StepAlias, descriptor.StepVersion, json));
        var plans = new[] { ProcessingGraphHosts.CameraAgent, ProcessingGraphHosts.LogicHost }.Select(host =>
        {
            var before = ProcessingGraphCompiler.Compile(graph, new(host, []));
            var after = ProcessingGraphCompiler.Compile(RoundTrip(ProcessingNodeOptionsCatalog.BuiltIn, graph), new(host, []));
            Assert.IsTrue(before.IsValid);
            Assert.AreEqual(before.Plan!.PlanIdentitySha256, after.Plan!.PlanIdentitySha256);
            return before.Plan.Nodes.Single().Definition.EffectiveOptions.GetRawText();
        }).ToArray();
        Assert.AreEqual(plans[0], plans[1]);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[null]")]
    [DataRow("[999]")]
    [DataRow("[-1]")]
    [DataRow("[0.0]")]
    [DataRow("[true]")]
    [DataRow("[\"Unknown\"]")]
    [DataRow("[\"999\"]")]
    public void EnvironmentSelectionRejectsInvalidSourceWithoutRepair(string token)
    {
        var descriptor = RetainedLayerOptionDescriptors.Environment;
        var source = Json("{\"environmentalKinds\":" + token + ",\"future\":1e+02}");
        Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, source).IsValid);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {}).IsValid);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"), new Dictionary<string, ProcessingOptionValue>
        { ["environmentalKinds"] = ProcessingOptionValue.FromEnumCollection(["Unknown"]) }).IsValid);
        Assert.AreEqual("{\"environmentalKinds\":" + token + ",\"future\":1e+02}", source.GetRawText());
    }
}
