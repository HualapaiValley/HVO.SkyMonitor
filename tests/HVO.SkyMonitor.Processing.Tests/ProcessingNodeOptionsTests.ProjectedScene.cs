using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void ProjectedSceneDescriptorBindsOnlyTheExistingQueryAtItsExactVersion()
    {
        var descriptor = ProjectedSceneOptionDescriptors.Descriptor;
        Assert.AreEqual("canonical-projected-scene-v1", descriptor.StepVersion);
        Assert.AreSame(descriptor, ProcessingNodeOptionsCatalog.BuiltIn.Resolve("pRoJeCtEdScEnE", descriptor.StepVersion).Descriptor);
        foreach (var version in new[] { "canonical-projected-scene-v2", "Canonical-projected-scene-v1", "projected-scene-v1" })
        {
            var resolved = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("ProjectedScene", version);
            Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.UnsupportedVersion, resolved.Status);
            Assert.IsNull(resolved.Descriptor);
            Assert.IsNull(resolved.Upgrade);
        }
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}"));
        Assert.HasCount(5, defaults.Options);
        Assert.AreEqual(6.5, defaults.Get("maximumMagnitude").Value.AsRealNumber());
        Assert.AreEqual(2000, defaults.Get("maximumResults").Value.AsWholeNumber());
        Assert.IsFalse(defaults.Get("includeConstellationEndpointStars").Value.AsToggle());
        Assert.AreEqual(ProjectedSceneJson.MaximumObjectCount, descriptor.Find("maximumResults")!.MaximumWholeNumber);
        foreach (var member in new[] { "outputVariant", "astronomyAlgorithmVersion", "curatedAnnotations", "future" })
        {
            Assert.IsNull(descriptor.Find(member));
            Assert.AreEqual(ProcessingOptionReasonCodes.UnknownOption, ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"),
                new Dictionary<string, ProcessingOptionValue> { [member] = ProcessingOptionValue.FromText("changed") }).Diagnostics.Single().Code);
        }
    }

    [TestMethod]
    [DataRow("maximumMagnitude", "-30", true)]
    [DataRow("maximumMagnitude", "30", true)]
    [DataRow("maximumMagnitude", "-30.01", false)]
    [DataRow("maximumMagnitude", "30.01", false)]
    [DataRow("maximumMagnitude", "1e999", false)]
    [DataRow("maximumMagnitude", "null", false)]
    [DataRow("maximumMagnitude", "\"6.5\"", false)]
    [DataRow("maximumMagnitude", "true", false)]
    [DataRow("maximumResults", "1", true)]
    [DataRow("maximumResults", "0", false)]
    [DataRow("maximumResults", "1.0", false)]
    [DataRow("maximumResults", "1e0", false)]
    [DataRow("maximumResults", "1.5", false)]
    [DataRow("maximumResults", "\"1\"", false)]
    [DataRow("maximumResults", "null", false)]
    [DataRow("includeConstellationEndpointStars", "false", true)]
    [DataRow("includeConstellationEndpointStars", "true", true)]
    [DataRow("includeConstellationEndpointStars", "null", false)]
    [DataRow("includeConstellationEndpointStars", "1", false)]
    [DataRow("includeConstellationEndpointStars", "\"true\"", false)]
    public void ProjectedSceneCodecEnforcesScalarTypesAndBounds(string member, string token, bool valid)
        => Assert.AreEqual(valid, ProcessingNodeOptionsCodec.Decode(ProjectedSceneOptionDescriptors.Descriptor,
            Json("{\"" + member + "\":" + token + "}")).IsValid);

    [TestMethod]
    public void ProjectedSceneCodecChecksResultCeilingDuplicatesAndGraphValidation()
    {
        var descriptor = ProjectedSceneOptionDescriptors.Descriptor;
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json(FormattableString.Invariant($"{{\"maximumResults\":{ProjectedSceneJson.MaximumObjectCount}}}"))).IsValid);
        Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, ProcessingNodeOptionsCodec.Decode(descriptor,
            Json(FormattableString.Invariant($"{{\"maximumResults\":{ProjectedSceneJson.MaximumObjectCount + 1}}}"))).Diagnostics.Single().Code);
        foreach (var member in descriptor.Options.Select(static option => option.Key))
        {
            var source = Json("{\"" + member + "\":null,\"" + member.ToUpperInvariant() + "\":null}");
            Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, source).Diagnostics.Any(static diagnostic => diagnostic.Code == ProcessingOptionReasonCodes.DuplicateMember));
            Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>()).IsValid);
        }
        var graph = Graph(Node("query", "ProjectedScene", descriptor.StepVersion, "{\"maximumResults\":0}"));
        Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, ProcessingNodeOptionsCatalog.BuiltIn.Validate(graph).Single().Code);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(", \"CuratedAnnotations\": null")]
    [DataRow(", \"CuratedAnnotations\": { \"enabled\": false, \"future\": [1e+02] }")]
    public void ProjectedSceneCodecPreservesEveryNonPanelByteAndNoOpDefaultSemantics(string curated)
    {
        var descriptor = ProjectedSceneOptionDescriptors.Descriptor;
        var source = Json("{ \"MaximumMagnitude\": 6.50, \"maximumResults\": 2000, \"includeConstellationEndpointStars\": false, \"OutputVariant\": \"retained-scene\", \"AstronomyAlgorithmVersion\": \"retained-v1\", \"ConstellationIds\": [ \"ORI\" ], \"SolarSystemBodies\": [ \"Mars\" ], \"unknown\": { \"escaped\": \"\\u0041\", \"number\": 1e+02 }" + curated + " }");
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(defaults.IsValid);
        Assert.AreEqual(source.GetRawText(), ProcessingNodeOptionsCodec.Encode(descriptor, source,
            defaults.Options.ToDictionary(static option => option.Descriptor.Key, static option => option.Value)).Options!.Value.GetRawText());
        var edits = new Dictionary<string, ProcessingOptionValue>
        {
            ["maximumMagnitude"] = ProcessingOptionValue.FromRealNumber(2),
            ["maximumResults"] = ProcessingOptionValue.FromWholeNumber(1),
            ["includeConstellationEndpointStars"] = ProcessingOptionValue.FromToggle(true)
        };
        Assert.AreEqual(source.GetRawText().Replace("6.50", "2", StringComparison.Ordinal).Replace("2000", "1", StringComparison.Ordinal)
            .Replace("Stars\": false", "Stars\": true", StringComparison.Ordinal),
            ProcessingNodeOptionsCodec.Encode(descriptor, source, edits).Options!.Value.GetRawText());
        Assert.AreEqual("{}", ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"),
            ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}")).Options.ToDictionary(static option => option.Descriptor.Key, static option => option.Value)).Options!.Value.GetRawText());
    }
}
