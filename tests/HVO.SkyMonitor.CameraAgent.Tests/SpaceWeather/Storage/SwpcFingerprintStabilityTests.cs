using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;

/// <summary>
/// Idempotent replay compares new requests with fingerprints that earlier binaries already stored,
/// so the canonical fingerprint bytes are a durable format. These values were produced by the
/// JsonDocument/MemoryStream canonicalization that shipped before #1489.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SwpcFingerprintStabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Pinned =
    [
        "ack:9a8b7c6d5e4f43218fedcba987654321=1C81BA0C5DFBDA4A0AA669299F8070685C9F06CC73F306E95104F5A224978D7B",
        "completion:0b9e8d7c6f5a4b3c8d2e1f0a9b8c7d6e=0BDB76BBD6873F791DC159F80CA2440EB496802A54EF6B70FD8B3C52FD6BA8A8",
        "completion:6a1f0c3e2b7d4e599c841d2e3f405162=DDC7D94B6B3CD34FD7726F0A8B06D016992DB51A9E365F40863C6AB9B6FFD1E4",
        "halt:3c4d5e6f70814293a4b5c6d7e8f90a1b=AD19F006F5119A2EE13C002472932C53C2910466C31B9663B8C27AD0A6FC044D",
        "start:0b9e8d7c6f5a4b3c8d2e1f0a9b8c7d6e=1CA81A29468710A92A6C6A40611FFFF160756E9A0FA64FFE95E6D1F1E267EA2C",
        "start:6a1f0c3e2b7d4e599c841d2e3f405162=D695D2EBB4191872F56341B0E9553674C9D9CC6CE1CAE9E7D084921BFE672F90"
    ];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swpc-" + Guid.NewGuid().ToString("N"));
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public async Task StoredFingerprintsMatchTheValuesEarlierBinariesWrote()
    {
        using var store = new SqliteSwpcStore(SwpcStoreTests.Settings(_root), NullLogger<SqliteSwpcStore>.Instance);
        var message = SwpcStoreTests.Message(detail: "<b> & 'quoted' + \\\"escaped\\\" café ☃");
        var snapshot = SwpcStoreTests.Alerts(message + new string(' ', 65536 - message.Length));
        var initial = await store.ReadAsync(Now, CancellationToken.None).ConfigureAwait(false);
        var updated = new SwpcAttemptStart(new Guid("6a1f0c3e-2b7d-4e59-9c84-1d2e3f405162"), initial.Revision, SwpcProduct.Alerts,
            "fingerprint-golden", SwpcAttemptTrigger.Manual, Now, new("W/\"prior\"", null), SwpcStoreTests.Empty with { NextAttemptNotBeforeUtc = Now.AddMinutes(2) });
        var begun = await store.BeginAttemptAsync(updated, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Applied, begun.Status);
        var completion = new SwpcAttemptCompletion(updated.AttemptId, begun.View.Revision, Now, Now.AddSeconds(1), Now,
            SwpcFetchOutcome.Updated, 200, "updated", snapshot.DecodedByteCount, SwpcValidatorOutcome.NotSent, snapshot);
        var completed = await store.CompleteAttemptAsync(completion, Project, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Applied, completed.Status);
        Assert.AreEqual(SwpcWriteStatus.Replayed, (await store.CompleteAttemptAsync(completion with { ExpectedRevision = 0 }, Project,
            CancellationToken.None).ConfigureAwait(false)).Status, "A byte-identical completion replays against its stored fingerprint.");

        var failed = new SwpcAttemptStart(new Guid("0b9e8d7c-6f5a-4b3c-8d2e-1f0a9b8c7d6e"), completed.View.Revision, SwpcProduct.Scales,
            "fingerprint-golden", SwpcAttemptTrigger.Scheduled, Now.AddMinutes(1), new(null, null), SwpcStoreTests.Empty);
        begun = await store.BeginAttemptAsync(failed, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Applied, begun.Status);
        completed = await store.CompleteAttemptAsync(new(failed.AttemptId, begun.View.Revision, Now.AddMinutes(1), Now.AddMinutes(1).AddSeconds(2),
            null, SwpcFetchOutcome.TransientFailure, 503, "http-503", 0, SwpcValidatorOutcome.NotSent, null), Project, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Applied, completed.Status);

        var halt = new SwpcHaltCommand(new Guid("3c4d5e6f-7081-4293-a4b5-c6d7e8f90a1b"), completed.View.Revision, Now.AddMinutes(2),
            new(SwpcProduct.KpForecast, new("operator-halt", Now.AddMinutes(2))), new Dictionary<SwpcProduct, SwpcFetchCheckpoint>
            {
                [SwpcProduct.ThreeDayForecast] = SwpcStoreTests.Empty with { InBackoff = true, ConsecutiveRejected = 2 },
                [SwpcProduct.Alerts] = SwpcStoreTests.Empty with { RetryNotBeforeUtc = Now.AddMinutes(5) }
            }.ToImmutableDictionary());
        var halted = await store.ChangeHaltAsync(halt, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Applied, halted.Status);
        var group = Assert.ContainsSingle(halted.View.Notices.Groups).Identity;
        var acknowledged = await store.AcknowledgeAsync(new(new Guid("9a8b7c6d-5e4f-4321-8fed-cba987654321"), halted.View.Revision,
            Now.AddMinutes(3), group, new("night-operator", Now.AddMinutes(3))), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(SwpcWriteStatus.Applied, acknowledged.Status);

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "space-weather.db"), Pooling = false }.ToString());
        await db.OpenAsync().ConfigureAwait(false);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT 'start:'||id||'='||start_fingerprint FROM swpc_attempts
            UNION ALL SELECT 'completion:'||id||'='||completion_fingerprint FROM swpc_attempts
            UNION ALL SELECT kind||':'||id||'='||fingerprint FROM swpc_receipts ORDER BY 1;
            """;
        var stored = new List<string>();
        using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
            while (await reader.ReadAsync().ConfigureAwait(false)) stored.Add(reader.GetString(0));
        CollectionAssert.AreEqual(Pinned, stored, string.Join(Environment.NewLine, stored));
    }

    [TestMethod]
    public void DeferredByteBodiesHashTheSameBytesAsFullCanonicalization()
    {
        byte[] every = [.. Enumerable.Range(0, 600).Select(i => (byte)(i % 256))];
        var snapshot = SwpcStoreTests.Alerts(SwpcStoreTests.Message());
        foreach (ImmutableArray<byte> body in new[] { [.. every], [], [0], [255], ImmutableArray<byte>.Empty, snapshot.DecodedBody })
        {
            // A string shaped like a placeholder without this process's marker stays an ordinary string.
            var completion = new SwpcAttemptCompletion(new Guid("6a1f0c3e-2b7d-4e59-9c84-1d2e3f405162"), 0, Now, Now.AddSeconds(1), Now,
                SwpcFetchOutcome.Updated, 200, "hvo-deferred-bytes-0", body.Length, SwpcValidatorOutcome.NotSent, snapshot with { DecodedBody = body });
            Assert.AreEqual(Reference(completion), SqliteSwpcStore.Fingerprint(completion));
            var pair = new { Second = snapshot with { DecodedBody = [.. every.Reverse()] }, First = completion, Raw = body };
            Assert.AreEqual(Reference(pair), SqliteSwpcStore.Fingerprint(pair), "Several bodies expand in place, in member order.");
        }
    }

    // The canonicalization every earlier binary used: parse the serializer's text and rewrite every value.
    private static string Reference<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) Canonical(document.RootElement, writer);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
    private static void Canonical(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Canonical(property.Value, writer); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Canonical(item, writer); writer.WriteEndArray(); }
        else element.WriteTo(writer);
    }
    private static SwpcCommitEffects Project(SwpcCommitContext context) => new(SwpcStoreTests.Empty, null, null, []);
}
