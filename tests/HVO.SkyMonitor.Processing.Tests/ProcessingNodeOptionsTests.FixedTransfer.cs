namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    [DataRow("Preview")]
    [DataRow("CalibratedPreview")]
    [DataRow("CombinedPreview")]
    public void FixedTransferEditsOnlyNestedTokensAndPreservesDefaultAbsence(string alias)
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve(alias, PreviewOptionDescriptors.FixedVersion).Descriptor!;
        const string raw = """{ "FiXeDTransfer" : { "BLACKLevel":1e+02, "WhiteLevel":4095, "Gamma":1.20, "future":{"Gamma":9,"keep":[1e+02]} }, "recipeVersion":"old", "blackPercentile":0.25, "whitePercentile":0.99, "asinhStrength":8, "future":true }""";
        var source = Json(raw);
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(decoded.IsValid);
        Assert.AreEqual(100, decoded.Get("blackLevel").Value.AsRealNumber());
        Assert.AreEqual(raw, ProcessingNodeOptionsCodec.Encode(descriptor, source,
            decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
        var changed = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["blackLevel"] = ProcessingOptionValue.FromRealNumber(100.5),
            ["whiteLevel"] = ProcessingOptionValue.FromRealNumber(65535),
            ["gamma"] = ProcessingOptionValue.FromRealNumber(10)
        });
        Assert.IsTrue(changed.IsValid);
        Assert.AreEqual(raw.Replace("1e+02,", "100.5,", StringComparison.Ordinal).Replace(":4095", ":65535", StringComparison.Ordinal)
            .Replace(":1.20", ":10", StringComparison.Ordinal), changed.Options!.Value.GetRawText());
        const string absent = """{ "FixedTransfer": { "future":1e+02 } }""";
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, Json(absent));
        Assert.AreEqual(64, defaults.Get("blackLevel").Value.AsRealNumber());
        Assert.AreEqual(4095, defaults.Get("whiteLevel").Value.AsRealNumber());
        Assert.AreEqual(2.2, defaults.Get("gamma").Value.AsRealNumber());
        Assert.IsFalse(defaults.Get("gamma").IsExplicit);
        Assert.AreEqual(absent, ProcessingNodeOptionsCodec.Encode(descriptor, Json(absent),
            defaults.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
        var appended = ProcessingNodeOptionsCodec.Encode(descriptor, Json(absent), new Dictionary<string, ProcessingOptionValue>
        { ["gamma"] = ProcessingOptionValue.FromRealNumber(1) });
        Assert.AreEqual("""{ "FixedTransfer": { "future":1e+02,"gamma":1 } }""", appended.Options!.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("blackLevel", "-1", false)]
    [DataRow("blackLevel", "0", true)]
    [DataRow("blackLevel", "4095", false)]
    [DataRow("whiteLevel", "64", false)]
    [DataRow("whiteLevel", "65535", true)]
    [DataRow("whiteLevel", "65535.1", false)]
    [DataRow("gamma", "0.1", true)]
    [DataRow("gamma", "10", true)]
    [DataRow("gamma", "0.099", false)]
    [DataRow("gamma", "10.001", false)]
    [DataRow("gamma", "1e400", false)]
    [DataRow("gamma", "\"2.2\"", false)]
    [DataRow("gamma", "null", false)]
    [DataRow("gamma", "true", false)]
    public void FixedTransferValidatesFiniteNativeBoundsTypesAndOrdering(string member, string token, bool valid)
    {
        foreach (var descriptor in PreviewOptionDescriptors.All.Where(static item => item.StepVersion == PreviewOptionDescriptors.FixedVersion))
        {
            var result = ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"FixedTransfer\":{\"" + member + "\":" + token + "}}"));
            Assert.AreEqual(valid, result.IsValid);
            if (!valid) StringAssert.Contains(result.Diagnostics[0].Path, "FixedTransfer", StringComparison.OrdinalIgnoreCase);
        }
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"FixedTransfer\":null}")]
    [DataRow("{\"FixedTransfer\":[]}")]
    [DataRow("{\"FixedTransfer\":true}")]
    [DataRow("{\"FixedTransfer\":{},\"fixedTransfer\":{}}")]
    [DataRow("{\"FixedTransfer\":{\"Gamma\":1,\"gamma\":2}}")]
    public void FixedTransferRefusesObjectCreationReplacementAndDuplicateBindings(string raw)
    {
        var descriptor = PreviewOptionDescriptors.All[1];
        Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, Json(raw)).IsValid);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, Json(raw), new Dictionary<string, ProcessingOptionValue>
        { ["gamma"] = ProcessingOptionValue.FromRealNumber(2.2) }).IsValid);
    }

    [TestMethod]
    public void FixedTransferValidatesOrderingAfterEditsAndCanRepairInvalidScalar()
    {
        var descriptor = PreviewOptionDescriptors.All[1];
        var source = Json("""{"FixedTransfer":{"BlackLevel":64,"WhiteLevel":100,"Gamma":2.2}}""");
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        { ["blackLevel"] = ProcessingOptionValue.FromRealNumber(100) }).IsValid);
        Assert.IsTrue(ProcessingNodeOptionsCodec.Encode(descriptor, Json("""{"FixedTransfer":{"Gamma":null}}"""),
            new Dictionary<string, ProcessingOptionValue> { ["gamma"] = ProcessingOptionValue.FromRealNumber(2.2) }).IsValid);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, source,
            new Dictionary<string, ProcessingOptionValue> { ["fixedTransfer"] = ProcessingOptionValue.FromText("null") }).IsValid);
        Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(PreviewOptionDescriptors.All[0], source,
            new Dictionary<string, ProcessingOptionValue> { ["gamma"] = ProcessingOptionValue.FromRealNumber(2) }).IsValid);
    }
}
