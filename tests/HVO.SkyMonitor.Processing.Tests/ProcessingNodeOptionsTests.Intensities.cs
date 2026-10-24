namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProcessingNodeOptionsTests
{
    [TestMethod]
    public void NullableIntensityDescriptorsPreservePresenceBytesAndHostIdentities()
    {
        foreach (var descriptor in new[] { Scene, ProcessingNodeOptionsDescriptors.ScenePresentationLayerV12, RetainedLayerOptionDescriptors.Environment })
            foreach (var option in descriptor.Options.Where(static option => option.Kind == ProcessingOptionKind.NullableWholeNumber))
            {
                Assert.IsTrue(option.IsUnsignedByte);
                Assert.AreEqual(0L, option.MinimumWholeNumber);
                Assert.AreEqual(255L, option.MaximumWholeNumber);
                Assert.IsNull(option.DefaultValue.AsNullableWholeNumber());
                Assert.AreEqual("Use default palette", option.NullActionLabel);
                foreach (var token in new string?[] { null, "null", "0", "255" })
                {
                    var member = option.Key.ToUpperInvariant();
                    var json = "{ \"future\":{\"escaped\":\"\\u0041\",\"n\":1e+02}" +
                        (token is null ? "" : ", \"" + member + "\" : " + token) + " }";
                    var source = Json(json);
                    var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, source);
                    Assert.IsTrue(decoded.IsValid);
                    Assert.AreEqual(token is not null, decoded.Get(option.Key).IsExplicit);
                    Assert.AreEqual(token is null or "null" ? null : long.Parse(token, System.Globalization.CultureInfo.InvariantCulture), decoded.Get(option.Key).Value.AsNullableWholeNumber());
                    Assert.AreEqual(json, ProcessingNodeOptionsCodec.Encode(descriptor, source,
                        decoded.Options.ToDictionary(static item => item.Descriptor.Key, static item => item.Value)).Options!.Value.GetRawText());
                    foreach (var value in new long?[] { null, 0, 255 })
                    {
                        var result = ProcessingNodeOptionsCodec.Encode(descriptor, source,
                            new Dictionary<string, ProcessingOptionValue> { [option.Key] = ProcessingOptionValue.FromNullableWholeNumber(value) });
                        Assert.IsTrue(result.IsValid);
                        Assert.AreEqual(source.GetProperty("future").GetRawText(), result.Options!.Value.GetProperty("future").GetRawText());
                        Assert.AreEqual(value, ProcessingNodeOptionsCodec.Decode(descriptor, result.Options.Value).Get(option.Key).Value.AsNullableWholeNumber());
                        var graph = Graph(Node("layer", descriptor.StepAlias, descriptor.StepVersion, result.Options.Value.GetRawText()));
                        Assert.IsEmpty(ProcessingNodeOptionsCatalog.BuiltIn.Validate(graph));
                        foreach (var host in new[] { ProcessingGraphHosts.CameraAgent, ProcessingGraphHosts.LogicHost })
                            Assert.AreEqual(ProcessingGraphCompiler.Compile(graph, new(host, [])).Plan!.PlanIdentitySha256,
                                ProcessingGraphCompiler.Compile(RoundTrip(ProcessingNodeOptionsCatalog.BuiltIn, graph), new(host, [])).Plan!.PlanIdentitySha256);
                    }
                }
            }
    }

    [TestMethod]
    public void NullableIntensitiesRejectWrongTypesBoundsAndDuplicatesAtEveryExactVersion()
    {
        foreach (var descriptor in new[] { Scene, ProcessingNodeOptionsDescriptors.ScenePresentationLayerV12, RetainedLayerOptionDescriptors.Environment })
            foreach (var option in descriptor.Options.Where(static option => option.Kind == ProcessingOptionKind.NullableWholeNumber))
            {
                foreach (var (token, reason) in new[] { ("-0", ProcessingOptionReasonCodes.OutOfRange), ("-1", ProcessingOptionReasonCodes.OutOfRange), ("256", ProcessingOptionReasonCodes.OutOfRange),
                ("9223372036854775808", ProcessingOptionReasonCodes.OutOfRange), ("1.5", ProcessingOptionReasonCodes.WrongType),
                ("1e400", ProcessingOptionReasonCodes.WrongType), ("\"NaN\"", ProcessingOptionReasonCodes.WrongType),
                ("\"255\"", ProcessingOptionReasonCodes.WrongType), ("true", ProcessingOptionReasonCodes.WrongType),
                ("[]", ProcessingOptionReasonCodes.WrongType), ("{}", ProcessingOptionReasonCodes.WrongType) })
                {
                    var source = Json("{\"" + option.Key + "\":" + token + "}");
                    Assert.AreEqual(reason, ProcessingNodeOptionsCodec.Decode(descriptor, source).Diagnostics.Single().Code);
                    Assert.AreEqual(reason, ProcessingNodeOptionsCodec.Encode(descriptor, source, new Dictionary<string, ProcessingOptionValue>()).Diagnostics.Single().Code);
                    Assert.IsNotEmpty(ProcessingNodeOptionsCatalog.BuiltIn.Validate(Graph(Node("layer", descriptor.StepAlias, descriptor.StepVersion, source.GetRawText()))));
                }
                var signedZero = Json("{ \"" + option.Key.ToUpperInvariant() + "\" : -0, \"future\":[1e+02] }");
                foreach (var repair in new long?[] { 0, null })
                {
                    var repaired = ProcessingNodeOptionsCodec.Encode(descriptor, signedZero,
                        new Dictionary<string, ProcessingOptionValue> { [option.Key] = ProcessingOptionValue.FromNullableWholeNumber(repair) });
                    Assert.IsTrue(repaired.IsValid);
                    Assert.AreEqual(signedZero.GetRawText().Replace("-0", repair is null ? "null" : "0", StringComparison.Ordinal), repaired.Options!.Value.GetRawText());
                }
                var duplicate = Json("{\"" + option.Key + "\":null,\"" + option.Key.ToUpperInvariant() + "\":0}");
                Assert.AreEqual(ProcessingOptionReasonCodes.DuplicateMember, ProcessingNodeOptionsCodec.Decode(descriptor, duplicate).Diagnostics.Single().Code);
                foreach (var value in new long[] { -1, 256 })
                    Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, ProcessingNodeOptionsCodec.Encode(descriptor, Json("{}"),
                        new Dictionary<string, ProcessingOptionValue> { [option.Key] = ProcessingOptionValue.FromNullableWholeNumber(value) }).Diagnostics.Single().Code);
            }
    }
}
