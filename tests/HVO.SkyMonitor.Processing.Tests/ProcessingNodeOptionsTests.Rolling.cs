namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void RollingLimitsPreserveAbsentNullFractionalAndUnknownBytes()
    {
        var descriptor = RollingCombinationOptionDescriptors.Descriptor;
        foreach (var key in new[] { "maximumAgeMilliseconds", "maximumIntegrationMilliseconds" })
            foreach (var token in new string?[] { null, "null", "0.125", "1e+03", "1.7976931348623157E+308", "5e-324" })
            {
                var source = Json("{ \"OutputVariant\":\"kept\", \"Recipe\":null, \"future\":[1e+02]" +
                    (token is null ? "" : ", \"" + key.ToUpperInvariant() + "\" : " + token) + " }");
                var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
                Assert.IsTrue(decoded.IsValid);
                Assert.AreEqual(token is not null, decoded.Get(key).IsExplicit);
                Assert.AreEqual(source.GetRawText(), ProcessingNodeOptionsCodec.Encode(descriptor, source,
                    decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
                foreach (var value in new double?[] { null, 0.25, double.Epsilon, double.MaxValue })
                {
                    var result = ProcessingNodeOptionsCodec.Encode(descriptor, source,
                        new Dictionary<string, ProcessingOptionValue> { [key] = ProcessingOptionValue.FromNullableRealNumber(value) });
                    Assert.IsTrue(result.IsValid);
                    Assert.AreEqual(value, ProcessingNodeOptionsCodec.Decode(descriptor, result.Options!.Value).Get(key).Value.AsNullableRealNumber());
                    Assert.AreEqual(source.GetProperty("future").GetRawText(), result.Options.Value.GetProperty("future").GetRawText());
                    Assert.AreEqual("kept", result.Options.Value.GetProperty("OutputVariant").GetString());
                    Assert.AreEqual(System.Text.Json.JsonValueKind.Null, result.Options.Value.GetProperty("Recipe").ValueKind);
                }
            }
        foreach (var token in new[] { "0", "1", "\"Trailing\"", "\"centered\"", "\"1\"" })
        {
            var source = Json("{\"windowKind\":" + token + "}");
            var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
            Assert.IsTrue(decoded.IsValid);
            Assert.AreEqual(source.GetRawText(), ProcessingNodeOptionsCodec.Encode(descriptor, source,
                decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
        }
    }

    [TestMethod]
    public void RollingCodecRejectsInvalidLimitsDuplicateMembersAndMixedGainEdits()
    {
        var descriptor = RollingCombinationOptionDescriptors.Descriptor;
        foreach (var key in new[] { "maximumAgeMilliseconds", "maximumIntegrationMilliseconds" })
        {
            foreach (var token in new[] { "0", "-0", "-1", "1e400", "\"NaN\"", "\"1\"", "false", "[]", "{}" })
                Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"" + key + "\":" + token + "}")).IsValid);
            Assert.AreEqual(ProcessingOptionReasonCodes.DuplicateMember,
                ProcessingNodeOptionsCodec.Decode(descriptor, Json("{\"" + key + "\":null,\"" + key.ToUpperInvariant() + "\":1}")).Diagnostics.Single().Code);
        }
        foreach (var json in new[] { "{\"windowSize\":0}", "{\"windowSize\":101}", "{\"windowSize\":1.5}",
            "{\"windowKind\":2}", "{\"windowKind\":\"Other\"}", "{\"enabled\":null}",
            "{\"Recipe\":\"mixed-gain-normalized-mean\",\"normalizationPlan\":{\"keep\":true}}" })
        {
            var source = Json(json);
            Assert.IsFalse(ProcessingNodeOptionsCodec.Decode(descriptor, source).IsValid);
            Assert.IsFalse(ProcessingNodeOptionsCodec.Encode(descriptor, source,
                new Dictionary<string, ProcessingOptionValue> { ["windowSize"] = ProcessingOptionValue.FromWholeNumber(3) }).IsValid);
        }
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ProcessingOptionValue.FromNullableRealNumber(double.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ProcessingOptionValue.FromNullableRealNumber(double.PositiveInfinity));
    }

    [TestMethod]
    public void RollingDraftWindowContractMustMatchOptionsOnBothHosts()
    {
        var descriptor = RollingCombinationOptionDescriptors.Descriptor;
        foreach (var size in new[] { 1, 2, 5, 100 })
            foreach (var kind in new[] { ProcessingGraphWindowKind.Trailing, ProcessingGraphWindowKind.Centered })
            {
                var window = new ProcessingGraphWindowRequirement(kind,
                    kind == ProcessingGraphWindowKind.Centered ? size : 1, size,
                    kind == ProcessingGraphWindowKind.Centered ? [.. Enumerable.Range(-(size / 2), size)] : [], []);
                var node = Node("rolling", descriptor.StepAlias, descriptor.StepVersion,
                    "{\"windowSize\":" + size + ",\"windowKind\":\"" + kind + "\"}") with
                { OperationKind = ProcessingOperationKind.Window, Window = window };
                var graph = Graph(node);
                Assert.IsEmpty(ProcessingNodeOptionsCatalog.BuiltIn.Validate(graph));
                foreach (var host in new[] { ProcessingGraphHosts.CameraAgent, ProcessingGraphHosts.LogicHost })
                {
                    var compiled = ProcessingGraphCompiler.Compile(graph, new(host, []));
                    Assert.IsTrue(compiled.IsValid, string.Join(' ', compiled.Diagnostics));
                    Assert.AreEqual(compiled.Plan!.PlanIdentitySha256,
                        ProcessingGraphCompiler.Compile(RoundTrip(ProcessingNodeOptionsCatalog.BuiltIn, graph), new(host, [])).Plan!.PlanIdentitySha256);
                    foreach (var mismatch in new[] { window with { MaximumInputCount = size + 1 },
                        window with { Kind = (ProcessingGraphWindowKind)(1 - (int)kind) }, window with { RequiredPositions = [-999, 0] } })
                        Assert.IsFalse(ProcessingGraphCompiler.Compile(Graph(node with { Window = mismatch }), new(host, [])).IsValid);
                }
            }
    }
}
