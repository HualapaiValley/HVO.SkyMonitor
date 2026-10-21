using System.Globalization;
using System.Text.Json.Nodes;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Conformance;

/// <summary>Checks every expectation, counts asserted values and records every U3 exclusion.</summary>
internal sealed class SwpcScenarioCoverage(string file)
{
    private int executedSteps;
    private int assertedSteps;
    private int assertedKeys;
    private int skippedKeys;
    private readonly List<string> skips = [];
    internal int SkippedSteps { get; private set; }

    /// <summary>Records an entire excluded exchange and the expected values it would have checked.</summary>
    internal void SkipStep(JsonObject expected, string reason)
    {
        SkippedSteps++;
        skippedKeys += Count(expected);
        skips.Add(reason + " step");
    }

    /// <summary>Objects are subsets; notice identities and all ordered arrays compare exactly.</summary>
    internal void CompareStep(JsonObject expected, JsonObject actual, string where)
    {
        executedSteps++;
        var keys = expected.Count == 0 ? 0 : Compare(expected, actual, where, false);
        if (keys > 0) assertedSteps++;
        assertedKeys += keys;
    }

    /// <summary>Rejects missing keys, checks exact notice membership and excludes only products.ovation.</summary>
    private int Compare(JsonNode? expected, JsonNode? actual, string where, bool products)
    {
        if (expected is JsonObject obj)
        {
            Assert.IsInstanceOfType<JsonObject>(actual, where + ": expected object; observed=" + actual);
            if (where.EndsWith(".notices", StringComparison.Ordinal))
                CollectionAssert.AreEquivalent(obj.Select(p => p.Key).ToArray(), actual.AsObject().Select(p => p.Key).ToArray(), where + ": group set");
            var count = 0;
            foreach (var (key, value) in obj)
            {
                if (products && key == "ovation")
                {
                    var skipped = Count(value);
                    skippedKeys += skipped;
                    skips.Add(where + ".ovation (OVATION U3, " + skipped.ToString(CultureInfo.InvariantCulture) + " keys)");
                    continue;
                }
                Assert.IsTrue(actual.AsObject().ContainsKey(key), where + "." + key + ": missing observed key");
                count += Compare(value, actual[key], where + "." + key, key == "products");
            }
            return obj.Count == 0 ? 1 : count;
        }
        Assert.IsTrue(JsonNode.DeepEquals(expected, actual), where + ": expected=" + expected?.ToJsonString() + "; observed=" + actual?.ToJsonString());
        return Count(expected);
    }

    /// <summary>Counts scalar values and empty containers; arrays retain their full ordered-value count.</summary>
    private static int Count(JsonNode? node) => node switch
    {
        JsonObject obj => Math.Max(1, obj.Sum(p => Count(p.Value))),
        JsonArray array => Math.Max(1, array.Sum(Count)),
        _ => 1
    };

    /// <summary>Fails empty core coverage and emits one reproducible row for the handoff evidence.</summary>
    internal void Report(TestContext context, int total)
    {
        if (executedSteps > 0) Assert.IsGreaterThan(0, assertedKeys, file + ": core scenario must assert expectation keys");
        Assert.AreEqual(total, executedSteps + SkippedSteps, file);
        context.WriteLine("CONFORMANCE {0}: total={1}; executed={2}; assertedSteps={3}; assertedKeys={4}; skippedSteps={5}; skippedKeys={6}; reasons={7}",
            file, total, executedSteps, assertedSteps, assertedKeys, SkippedSteps, skippedKeys, string.Join("; ", skips));
    }
}
