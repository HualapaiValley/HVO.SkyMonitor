using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.AgentCore.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CanonicalJsonHashTests
{
    [TestMethod]
    [DataRow("null")]
    [DataRow("[true,false,null,1,-0,1.00,1e+10,9223372036854775808]")]
    [DataRow("{\"z\":1,\"name\":\"fixture\",\"nested\":{\"b\":true,\"a\":null},\"array\":[2,1]}")]
    [DataRow("{\"unicode\":\"é😀<>&\\u2028\\u2029\",\"control\":\"\\b\\f\\n\\r\\t\\u0000\",\"slash\":\"a/b\\\\c\\\"d\"}")]
    [DataRow("{\"same\":1,\"same\":2,\"A\":{},\"a\":[]}")]
    public void DirectHashRetainsLegacyCanonicalBytes(string json)
    {
        using var document = JsonDocument.Parse(json);
        // The prior public pipeline is retained as the byte-compatibility oracle, including escaping and number spelling.
        var priorBytes = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(document.RootElement));
        var priorHash = Convert.ToHexString(SHA256.HashData(priorBytes));
        Assert.AreEqual(priorHash, CaptureContractJson.ComputeCanonicalJsonSha256(document.RootElement));
    }

    [TestMethod]
    public void HashRecomputesAfterNestedCallerMutation()
    {
        var values = new List<int> { 1, 2 };
        var document = new Dictionary<string, object> { ["values"] = values };
        var first = CaptureContractJson.ComputeCanonicalJsonSha256(document);
        values[1] = 3;
        var changed = CaptureContractJson.ComputeCanonicalJsonSha256(document);
        Assert.AreNotEqual(first, changed);
        values[1] = 2;
        Assert.AreEqual(first, CaptureContractJson.ComputeCanonicalJsonSha256(document));
    }
}
