namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void ChoiceCollectionValidatesNonemptyDefaultsAndPreservesTheEmptyDefaultOverload()
    {
        string[] allowed = ["utc", "catalog"];
        string[] defaults = ["catalog", "utc"];
        var legacy = ProcessingOptionDescriptor.ChoiceCollection("tokens", "tokens", "Tokens", "Existing choices.", allowed, 2);
        Assert.IsEmpty(legacy.DefaultValue.AsCollection().Items);
        var preset = ProcessingOptionDescriptor.ChoiceCollection("tokens", "tokens", "Tokens", "Existing preset.", allowed, defaults, 2);
        CollectionAssert.AreEqual(defaults, preset.DefaultValue.AsCollection().Items.ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => ProcessingOptionDescriptor.ChoiceCollection(
            "tokens", "tokens", "Tokens", "Invalid preset.", allowed, ["unknown"], 2));
        Assert.ThrowsExactly<ArgumentException>(() => ProcessingOptionDescriptor.ChoiceCollection(
            "tokens", "tokens", "Tokens", "Too many defaults.", allowed, defaults, 1));
        Assert.ThrowsExactly<ArgumentException>(() => ProcessingOptionDescriptor.ChoiceCollection(
            "tokens", "tokens", "Tokens", "Repeated defaults.", allowed, ["utc", "utc"], 2, uniqueItemIdentity: static item => item));
    }

    [TestMethod]
    public void AnnotationSelectionDefaultsDescribeTheExistingCornerPresets()
    {
        var descriptor = AnnotationOptionDescriptors.Descriptor;
        foreach (var key in new[] { "constellationIds", "topLeftTokens", "topRightTokens", "bottomLeftTokens", "bottomRightTokens", "environmentalKinds" })
            Assert.IsNotNull(descriptor.Find(key), key);
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}"));
        Assert.IsTrue(defaults.IsValid);
        CollectionAssert.AreEqual(new[] { "agent-identity", "capture-sequence", "utc" }, defaults.Get("topLeftTokens").Value.AsCollection().Items.ToArray());
        CollectionAssert.AreEqual(new[] { "schedule-profile", "exposure", "cadence", "gain", "offset", "sensor-setpoint" }, defaults.Get("topRightTokens").Value.AsCollection().Items.ToArray());
        CollectionAssert.AreEqual(new[] { "environment" }, defaults.Get("bottomLeftTokens").Value.AsCollection().Items.ToArray());
        CollectionAssert.AreEqual(new[] { "catalog", "calibration", "stack", "processing-profile" }, defaults.Get("bottomRightTokens").Value.AsCollection().Items.ToArray());
        Assert.AreEqual(6, defaults.Get("environmentalKinds").Value.AsCollection().Items.Length);
        Assert.IsEmpty(defaults.Get("constellationIds").Value.AsCollection().Items);
        Assert.IsTrue(defaults.Options.All(static item => !item.IsExplicit));
    }

    [TestMethod]
    [DataRow("topLeftTokens")]
    [DataRow("topRightTokens")]
    [DataRow("bottomLeftTokens")]
    [DataRow("bottomRightTokens")]
    public void AnnotationCornerSelectionsRoundTripDefaultsAndPreserveOtherSourceMembers(string key)
    {
        var descriptor = AnnotationOptionDescriptors.Descriptor;
        Assert.IsNotNull(descriptor.Find(key));
        var source = Json("{ \"" + key + "\":[\"utc\"], \"future\":[1e+02], \"recipeVersion\":\"retained\" }");
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(decoded.IsValid);
        var unchanged = ProcessingNodeOptionsCodec.Encode(descriptor, source, decoded.Options.ToDictionary(
            static item => item.Descriptor.Key, static item => item.Value));
        Assert.AreEqual(source.GetRawText(), unchanged.Options!.Value.GetRawText());
        var edited = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        { [key] = ProcessingOptionValue.FromChoiceCollection(["capture-sequence", "utc"]) });
        Assert.IsTrue(edited.IsValid);
        CollectionAssert.AreEqual(new[] { "capture-sequence", "utc" }, ProcessingNodeOptionsCodec.Decode(descriptor, edited.Options!.Value).Get(key).Value.AsCollection().Items.ToArray());
        Assert.AreEqual("[1e+02]", edited.Options.Value.GetProperty("future").GetRawText());
        Assert.AreEqual("retained", edited.Options.Value.GetProperty("recipeVersion").GetString());
    }

    [TestMethod]
    [DataRow("{\"topLeftTokens\":null}")]
    [DataRow("{\"topRightTokens\":null}")]
    [DataRow("{\"bottomLeftTokens\":null}")]
    [DataRow("{\"bottomRightTokens\":null}")]
    [DataRow("{\"topRightTokens\":[\"undefined\"]}")]
    [DataRow("{\"bottomLeftTokens\":[\"utc\",\"utc\"]}")]
    [DataRow("{\"bottomRightTokens\":[1]}")]
    [DataRow("{\"topLeftTokens\":[\"UTC\"]}")]
    [DataRow("{\"topLeftTokens\":[\"environment\",\"utc\",\"catalog\",\"exposure\"]}")]
    [DataRow("{\"environmentalKinds\":[\"AirTemperature\",0]}")]
    [DataRow("{\"environmentalKinds\":null}")]
    [DataRow("{\"environmentalKinds\":[999]}")]
    [DataRow("{\"constellationIds\":[\"\"]}")]
    public void AnnotationSelectionValidationRejectsInvalidTypesChoicesDuplicatesAndExpandedLineBudgets(string source)
    {
        var decoded = ProcessingNodeOptionsCodec.Decode(AnnotationOptionDescriptors.Descriptor, Json(source));
        Assert.IsFalse(decoded.IsValid, source);
        Assert.IsNotEmpty(decoded.Diagnostics);
    }

    [TestMethod]
    public void AnnotationSelectionsPreserveNumericEnumsAndOrderedDuplicateConstellationIds()
    {
        var descriptor = AnnotationOptionDescriptors.Descriptor;
        Assert.IsNotNull(descriptor.Find("environmentalKinds"));
        const string raw = """{ "EnvironmentalKinds":[0,"wInDsPeEd"], "ConstellationIds":["ORI","future-id","ORI"], "TopLeftTokens":[], "future":[1e+02] }""";
        var source = Json(raw);
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(decoded.IsValid);
        CollectionAssert.AreEqual(new[] { "AirTemperature", "WindSpeed" }, decoded.Get("environmentalKinds").Value.AsCollection().Items.ToArray());
        CollectionAssert.AreEqual(new[] { "ORI", "future-id", "ORI" }, decoded.Get("constellationIds").Value.AsCollection().Items.ToArray());
        var unchanged = ProcessingNodeOptionsCodec.Encode(descriptor, source, decoded.Options.ToDictionary(
            static item => item.Descriptor.Key, static item => item.Value));
        Assert.AreEqual(raw, unchanged.Options!.Value.GetRawText());
        var empty = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        { ["environmentalKinds"] = ProcessingOptionValue.FromEnumCollection([]) });
        Assert.IsTrue(empty.IsValid);
        Assert.IsEmpty(empty.Options!.Value.GetProperty("EnvironmentalKinds").EnumerateArray().ToArray());
    }
}
