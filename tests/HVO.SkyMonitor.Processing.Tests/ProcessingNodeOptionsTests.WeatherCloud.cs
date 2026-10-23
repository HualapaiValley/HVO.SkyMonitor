namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void WeatherOverlayDescriptorPreservesAbsentDefaultsCustomLabelsAndOpaqueJson()
    {
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("wEaThErClOuDoVeRlAy", "weather-cloud-overlay-v1").Descriptor!;
        Assert.AreSame(WeatherCloudOverlayOptionDescriptors.Descriptor, descriptor);
        Assert.AreEqual(ProcessingNodeOptionsResolutionStatus.UnsupportedVersion,
            ProcessingNodeOptionsCatalog.BuiltIn.Resolve("WeatherCloudOverlay", "future-v2").Status);
        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, Json("{}"));
        Assert.HasCount(6, defaults.Options);
        Assert.IsTrue(defaults.Get("enabled").Value.AsToggle());
        Assert.IsTrue(defaults.Get("drawLabels").Value.AsToggle());
        Assert.AreEqual(1, defaults.Get("lineThickness").Value.AsWholeNumber());
        Assert.AreEqual(32, defaults.Get("maximumLabelCharacters").Value.AsWholeNumber());
        Assert.AreEqual("weather-cloud-overlay-v1", defaults.Get("recipeVersion").Value.AsText());
        Assert.AreEqual("weather-cloud-overlay-v1", defaults.Get("outputVariant").Value.AsText());
        var source = Json("""{ "RecipeVersion": " custom artifact label ", "LineThickness": 1, "future": [1e+02], "gridColumns": 17 }""");
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
        Assert.IsTrue(decoded.IsValid);
        var same = ProcessingNodeOptionsCodec.Encode(descriptor, source,
            decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value));
        Assert.AreEqual(source.GetRawText(), same.Options!.Value.GetRawText());
        var changed = ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>
        {
            ["lineThickness"] = ProcessingOptionValue.FromWholeNumber(8)
        });
        Assert.AreEqual(source.GetRawText().Replace("\"LineThickness\": 1", "\"LineThickness\": 8", StringComparison.Ordinal),
            changed.Options!.Value.GetRawText());
        var restoredDefaults = ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"),
            defaults.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value));
        Assert.AreEqual("{}", restoredDefaults.Options!.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("lineThickness", "0", false)]
    [DataRow("lineThickness", "1", true)]
    [DataRow("lineThickness", "8", true)]
    [DataRow("lineThickness", "9", false)]
    [DataRow("lineThickness", "2.0", false)]
    [DataRow("lineThickness", "\"2\"", false)]
    [DataRow("maximumLabelCharacters", "-1", false)]
    [DataRow("maximumLabelCharacters", "0", true)]
    [DataRow("maximumLabelCharacters", "128", true)]
    [DataRow("maximumLabelCharacters", "129", false)]
    [DataRow("drawLabels", "false", true)]
    [DataRow("drawLabels", "null", false)]
    [DataRow("enabled", "false", true)]
    [DataRow("enabled", "0", false)]
    [DataRow("recipeVersion", "\"\"", false)]
    [DataRow("recipeVersion", "\" \"", false)]
    [DataRow("recipeVersion", "\" custom label \"", true)]
    [DataRow("recipeVersion", "null", false)]
    [DataRow("outputVariant", "\"\"", false)]
    [DataRow("outputVariant", "\" route\"", false)]
    [DataRow("outputVariant", "\"custom-route\"", true)]
    public void WeatherOverlayCodecUsesExistingBoundsAndStrictJsonTypes(string member, string token, bool valid)
    {
        var decoded = ProcessingNodeOptionsCodec.Decode(WeatherCloudOverlayOptionDescriptors.Descriptor,
            Json("{\"" + member + "\":" + token + "}"));
        Assert.AreEqual(valid, decoded.IsValid);
    }

    [TestMethod]
    public void WeatherOverlayRouteAndRecipeLabelHaveTheirOwnExistingLengthRules()
    {
        var descriptor = WeatherCloudOverlayOptionDescriptors.Descriptor;
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"recipeVersion\":\"" + new string('r', 200) + "\"}")).IsValid);
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"outputVariant\":\"" + new string('v', 128) + "\"}")).IsValid);
        Assert.AreEqual(ProcessingOptionReasonCodes.TooLong,
            ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"outputVariant\":\"" + new string('v', 129) + "\"}")).Diagnostics.Single().Code);
        Assert.AreEqual(ProcessingOptionReasonCodes.DuplicateMember,
            ProcessingNodeOptionsCodec.Decode(descriptor, Json("""{"drawLabels":true,"DrawLabels":false}""")).Diagnostics.Single().Code);
    }
}
