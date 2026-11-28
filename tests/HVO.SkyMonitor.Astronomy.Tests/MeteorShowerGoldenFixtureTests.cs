using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed partial class MeteorShowerGoldenFixtureTests
{
    private const string Manifest = "manifest.json";
    private const string Occurrences = "occurrences.json";
    private const string Observing = "observing.json";
    private const string CanonicalVectors = "jcs.json";
    private const string SnapshotVersion = "hvo-meteor-showers-v1";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string CivilFormat = "yyyy-MM-dd'T'HH:mm:sszzz";
    private static readonly string FixtureRoot = FindFixtureRoot();

    [TestMethod]
    public void ManifestCoversEveryFileAndPinsCanonicalSnapshots()
    {
        var manifest = AssertCanonicalFile(File.ReadAllBytes(Path.Combine(FixtureRoot, Manifest)), Manifest);
        Fields(manifest, "schemaVersion", "provenance", "files");
        Assert.AreEqual("hvo-meteor-shower-fixture-manifest-v1", String(manifest, "schemaVersion"));

        var provenance = manifest.GetProperty("provenance");
        Fields(provenance, "author", "authoredDate", "origin", "decisionCommit", "decisionPath", "contractPath",
            "approvalUrl", "thirdPartyDelivery", "containsThirdPartyDatasets", "redistributionGates");
        Assert.AreEqual("HVO SkyMonitor", String(provenance, "author"));
        Assert.AreEqual("2026-11-27", String(provenance, "authoredDate"));
        Assert.AreEqual("HVO-authored synthetic inputs and HVO-computed references", String(provenance, "origin"));
        Assert.AreEqual("770fb1f2017e368dd2221f859768fb8126fea4dc", String(provenance, "decisionCommit"));
        Assert.AreEqual("docs/planning/sky-layers-source-decision-1396.md", String(provenance, "decisionPath"));
        Assert.AreEqual("docs/contracts/meteor-shower-context-v1.md", String(provenance, "contractPath"));
        Assert.AreEqual("https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1396#issuecomment-7216125734",
            String(provenance, "approvalUrl"));
        Assert.AreEqual("OperatorImportOnly", String(provenance, "thirdPartyDelivery"));
        Assert.IsFalse(provenance.GetProperty("containsThirdPartyDatasets").GetBoolean());
        var gates = provenance.GetProperty("redistributionGates");
        Fields(gates, "M1", "M2");
        Assert.AreEqual("Unresolved", String(gates, "M1"));
        Assert.AreEqual("Unresolved", String(gates, "M2"));

        var entries = manifest.GetProperty("files").EnumerateObject().ToArray();
        var listed = entries.Select(entry => entry.Name).Append(Manifest).ToArray();
        var actual = Directory.GetFiles(FixtureRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(FixtureRoot, path)).ToArray();
        CollectionAssert.AreEquivalent(listed, actual);

        foreach (var entry in entries)
        {
            Fields(entry.Value, "kind", "sha256");
            var bytes = File.ReadAllBytes(Path.Combine(FixtureRoot, entry.Name));
            Assert.AreEqual(String(entry.Value, "sha256"), Hash(bytes), entry.Name);
            switch (String(entry.Value, "kind"))
            {
                case "documentation":
                    Assert.AreEqual("README.md", entry.Name);
                    Assert.StartsWith("# Meteor-shower golden fixtures v1\n", new UTF8Encoding(false, true).GetString(bytes));
                    break;
                case "corrupt":
                    Assert.AreEqual("corrupt.json", entry.Name);
                    Assert.Throws<JsonException>(() => Parse(bytes));
                    break;
                case "snapshot":
                case "rejected-snapshot":
                    Assert.IsTrue(bytes.Length <= 2 * 1024 * 1024, entry.Name);
                    var snapshot = AssertCanonicalFile(bytes, entry.Name);
                    ValidateSnapshotShape(snapshot);
                    Assert.AreEqual(Path.GetFileNameWithoutExtension(entry.Name), String(snapshot, "snapshotId"));
                    Assert.AreEqual(String(snapshot, "snapshotSha256"),
                        Hash(MeteorShowerFixtureCanonicalJson.Encode(snapshot, "snapshotSha256")), entry.Name);
                    var reason = ImportRejection(snapshot);
                    if (String(entry.Value, "kind") == "snapshot")
                    {
                        Assert.IsNull(reason, entry.Name);
                    }
                    else
                    {
                        Assert.IsNotNull(reason, entry.Name);
                    }
                    break;
                case "occurrences":
                    Assert.AreEqual(Occurrences, entry.Name);
                    Assert.AreEqual("hvo-meteor-shower-occurrence-fixtures-v1",
                        String(AssertCanonicalFile(bytes, entry.Name), "schemaVersion"));
                    break;
                case "observing":
                    Assert.AreEqual(Observing, entry.Name);
                    Assert.AreEqual("hvo-meteor-shower-observing-fixtures-v1",
                        String(AssertCanonicalFile(bytes, entry.Name), "schemaVersion"));
                    break;
                case "jcs":
                    Assert.AreEqual(CanonicalVectors, entry.Name);
                    Assert.AreEqual("hvo-meteor-shower-jcs-fixtures-v1",
                        String(AssertCanonicalFile(bytes, entry.Name), "schemaVersion"));
                    break;
                default:
                    Assert.Fail($"Unknown fixture kind for {entry.Name}.");
                    break;
            }
        }
    }

    [TestMethod]
    public void JcsVectorsMatchFrozenTextBytesAndDigests()
    {
        var fixture = Read(CanonicalVectors);
        Fields(fixture, "schemaVersion", "vectors");
        var vectors = Array(fixture, "vectors");
        Assert.HasCount(7, vectors);
        foreach (var vector in vectors)
        {
            Fields(vector, "input", "expectedCanonical", "expectedUtf8Hex", "expectedSha256");
            var input = String(vector, "input");
            var bytes = MeteorShowerFixtureCanonicalJson.Encode(Parse(Encoding.UTF8.GetBytes(input)));
            Assert.AreEqual(String(vector, "expectedCanonical"), Encoding.UTF8.GetString(bytes), input);
            Assert.AreEqual(String(vector, "expectedUtf8Hex"), Convert.ToHexStringLower(bytes), input);
            Assert.AreEqual(String(vector, "expectedSha256"), Hash(bytes), input);
        }

        foreach (var invalid in new[] { """{"a":0,"\u0061":1}""", "1e999" })
        {
            Assert.ThrowsExactly<AssertFailedException>(
                () => MeteorShowerFixtureCanonicalJson.Encode(Parse(Encoding.UTF8.GetBytes(invalid))), invalid);
        }
        foreach (var invalid in new[] { """{"\ud800":0}""", "\"\\udc00\"" })
        {
            // System.Text.Json rejects unpaired surrogate escapes upon reading their decoded names/values.
            Assert.ThrowsExactly<InvalidOperationException>(
                () => MeteorShowerFixtureCanonicalJson.Encode(Parse(Encoding.UTF8.GetBytes(invalid))), invalid);
        }
    }

    private static JsonElement AssertCanonicalFile(byte[] bytes, string name)
    {
        var value = Parse(bytes);
        CollectionAssert.AreEqual(bytes, MeteorShowerFixtureCanonicalJson.Encode(value), name);
        return value;
    }

    private static string FindFixtureRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return Path.Combine(directory.FullName, "tests", "fixtures", "meteor-showers", "v1");
            }
        }

        throw new DirectoryNotFoundException("Meteor-shower fixture tests run from a SkyMonitor source checkout.");
    }

    private static JsonElement Read(string file) => Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, file)));

    private static JsonElement Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void Fields(JsonElement obj, params string[] names) =>
        CollectionAssert.AreEquivalent(names, obj.EnumerateObject().Select(property => property.Name).ToArray());

    private static string String(JsonElement obj, string key) => obj.GetProperty(key).GetString()!;
    private static string Text(JsonElement obj, string key, int max)
    {
        var text = String(obj, key);
        Assert.IsNotNull(text, key);
        Assert.IsTrue(text.Length is > 0 && text.Length <= max, key);
        return text;
    }

    private static double Number(JsonElement obj, string key) => obj.GetProperty(key).GetDouble();
    private static int Int(JsonElement obj, string key) => obj.GetProperty(key).GetInt32();
    private static JsonElement[] Array(JsonElement obj, string key) => [.. obj.GetProperty(key).EnumerateArray()];
    private static bool IsNull(JsonElement obj, string key) => obj.GetProperty(key).ValueKind == JsonValueKind.Null;
    private static DateTimeOffset Utc(JsonElement obj, string key) =>
        DateTimeOffset.ParseExact(String(obj, key), TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static JsonElement Shower(JsonElement snapshot, string code) =>
        Array(snapshot, "showers").Single(shower => String(shower, "iauCode") == code);
}
