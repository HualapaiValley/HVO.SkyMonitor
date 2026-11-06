using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalCanonicalSerializationTests
{
    [TestMethod]
    public void CanonicalSerializationMatchesLegacyWriterForEveryContractFixture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital", "contract-v1");
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            AssertCanonical(document.RootElement);
        }
        var snapshot = OrbitalContractExamples.Snapshot();
        AssertCanonical(snapshot.Manifest);
        AssertCanonical(snapshot.Records);
        AssertCanonical(snapshot.Records.ToArray());
        AssertCanonical(Array.Empty<OrbitalRecordV1>());
        foreach (var record in snapshot.Records)
        {
            AssertCanonical(record);
            // Omit self identity exactly as the v1 implementation does.
            var identity = new
            {
                record.ObjectIdentity,
                record.SourceDataset,
                record.SourceRecordId,
                record.ObjectName,
                record.InternationalDesignator,
                record.SourceAssertedType,
                record.SourceAssertedStatus,
                record.EpochUtc,
                record.ValidFromUtc,
                record.ValidToUtc,
                record.Model,
                record.Elements
            };
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(LegacyCanonical(identity))), OrbitalContract.ComputeRecordIdentity(record));
        }
    }

    [TestMethod]
    public void PayloadHashMatchesLegacyBytesAcrossStreamingBufferBoundaries()
    {
        var records = Enumerable.Range(100000, 257).Select(i => OrbitalContractExamples.Record(
            i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var snapshot = OrbitalContractExamples.Snapshot(records);
        var bytes = LegacyCanonical(snapshot.Records);
        Assert.IsGreaterThan(64 * 1024, bytes.Length);
        CollectionAssert.AreEqual(bytes, snapshot.SerializePayload());
        Assert.AreEqual(bytes.Length, snapshot.Manifest.NormalizedPayloadLengthBytes);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), snapshot.Manifest.NormalizedPayloadSha256);
        var parsed = OrbitalSnapshot.Parse(snapshot.SerializeManifest(), bytes);
        Assert.IsTrue(parsed.IsValid, parsed.Reason);
    }

    [TestMethod]
    public void TypedWriterPreservesEscapingOffsetsNullsAndFloatingPointBytes()
    {
        var record = OrbitalContractExamples.Record();
        foreach (var number in new[] { -0d, 0d, double.Epsilon, -double.Epsilon, 1e-100, 1e100, double.MaxValue, double.MinValue })
        {
            var value = record with
            {
                ObjectName = "<>&\"\\ é 漢字 🛰",
                EpochUtc = record.EpochUtc.ToOffset(TimeSpan.FromHours(7)),
                ValidFromUtc = record.EpochUtc.AddTicks(1234567),
                ValidToUtc = record.EpochUtc.ToOffset(TimeSpan.FromHours(-5)),
                Elements = record.Elements with { MeanMotionDot = number, MeanMotionDdot = -0d }
            };
            AssertCanonical(value);
            AssertCanonical(new[] { value });
            AssertCanonical((object)value);
        }
        foreach (var epoch in new[] { DateTimeOffset.MinValue, DateTimeOffset.MaxValue,
            record.EpochUtc.AddTicks(1), record.EpochUtc.AddTicks(1234000), record.EpochUtc.ToOffset(TimeSpan.FromHours(14)) })
            AssertCanonical(record with { EpochUtc = epoch });
        using var json = JsonDocument.Parse("{\"z\":-0.00,\"a\":{\"z\":1e-999,\"b\":0.0},\"unicode\":\"é🛰\"}");
        AssertCanonical(new { Z = 0.00m, A = new Dictionary<string, object?> { ["z"] = json.RootElement, ["a"] = null } });
    }

    [TestMethod]
    public void CreateAndValidateKeepIdentityAndNormalizationRejectionReasons()
    {
        var record = OrbitalContractExamples.Record();
        foreach (var bad in new[]
        {
            record with { RecordSha256 = new string('D', 64) },
            record with { EpochUtc = record.EpochUtc.ToOffset(TimeSpan.FromHours(1)) },
            record with { ValidFromUtc = record.EpochUtc.ToOffset(TimeSpan.FromHours(1)) },
            record with { Elements = record.Elements with { AscendingNodeDegrees = 485 } },
            record with { Elements = record.Elements with { ArgumentOfPericenterDegrees = -315 } },
            record with { Elements = record.Elements with { MeanAnomalyDegrees = 630 } }
        })
        {
            Assert.AreEqual("record-content-identity", Assert.ThrowsExactly<ArgumentException>(() => OrbitalContract.ValidateRecord(bad)).Message);
            Assert.AreEqual("record-content-identity", Assert.ThrowsExactly<ArgumentException>(() => OrbitalContractExamples.Snapshot(bad)).Message);
        }
        var missingIdentity = record with { RecordSha256 = "" };
        Assert.AreEqual("record-identity", Assert.ThrowsExactly<ArgumentException>(() => OrbitalContractExamples.Snapshot(missingIdentity)).Message);
        var timed = OrbitalContract.CreateRecord(record with { ValidFromUtc = record.EpochUtc, ValidToUtc = record.EpochUtc.AddHours(1) });
        foreach (var bad in new[] { timed with { ValidFromUtc = timed.ValidFromUtc!.Value.ToOffset(TimeSpan.FromHours(1)) },
            timed with { ValidToUtc = timed.ValidToUtc!.Value.ToOffset(TimeSpan.FromHours(-1)) } })
            Assert.AreEqual("record-content-identity", Assert.ThrowsExactly<ArgumentException>(() => OrbitalContractExamples.Snapshot(bad)).Message);
        var zero = OrbitalContract.CreateRecord(record with { Elements = record.Elements with { MeanMotionDdot = 0d } });
        var negativeZero = zero with { Elements = zero.Elements with { MeanMotionDdot = -0d } };
        OrbitalContract.ValidateRecord(negativeZero);
        Assert.AreEqual(0L, BitConverter.DoubleToInt64Bits(OrbitalContractExamples.Snapshot(negativeZero).Records[0].Elements.MeanMotionDdot));
    }

    [TestMethod]
    public void ParseRejectsNoncanonicalPayloadEvenWhenManifestHashesItsExactBytes()
    {
        var snapshot = OrbitalContractExamples.Snapshot();
        var payload = Encoding.UTF8.GetString(snapshot.SerializePayload());
        foreach (var noncanonical in new[]
        {
            " " + payload,
            payload + "\n",
            payload.Replace("15.5", "1.55e1", StringComparison.Ordinal),
            payload.Replace("\"ephemerisType\":0", "\"ephemerisType\":-0", StringComparison.Ordinal),
            payload.Replace("Synthetic", "\\u0053ynthetic", StringComparison.Ordinal),
            payload.Replace("\"authority\":\"norad\",\"catalogNumber\":\"25544\"", "\"catalogNumber\":\"25544\",\"authority\":\"norad\"", StringComparison.Ordinal)
        })
        {
            Assert.AreNotEqual(payload, noncanonical);
            var bytes = Encoding.UTF8.GetBytes(noncanonical);
            var manifest = snapshot.Manifest with
            {
                NormalizedPayloadSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                NormalizedPayloadLengthBytes = bytes.Length
            };
            manifest = manifest with { ManifestSha256 = OrbitalSnapshot.ComputeManifestIdentity(manifest) };
            var result = OrbitalSnapshot.Parse(OrbitalContract.SerializeCanonical(manifest), bytes);
            Assert.AreEqual("payload-integrity-or-canonical-form", result.Reason);
            Assert.IsFalse(result.IsValid);
        }
    }

    private static void AssertCanonical<T>(T value)
    {
        var expected = LegacyCanonical(value);
        CollectionAssert.AreEqual(expected, OrbitalContract.SerializeCanonical(value));
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(expected)), OrbitalContract.Hash(value));
    }

    // Frozen pre-1422 algorithm: it independently covers property order, escaping, numeric spelling and zero normalization.
    private static byte[] LegacyCanonical<T>(T value)
    {
        var element = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(value));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, element);
        return stream.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Write(writer, item);
            writer.WriteEndArray();
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number == 0)
            writer.WriteNumberValue(0);
        else value.WriteTo(writer);
    }
}
