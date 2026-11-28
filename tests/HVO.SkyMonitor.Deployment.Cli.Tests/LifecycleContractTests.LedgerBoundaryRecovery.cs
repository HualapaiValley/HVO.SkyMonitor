using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using static HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation.RetainedIntegrityFormat;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.AspNetCore.DataProtection;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BaselineRollbackPreservesCommittedLedgerAuthority(bool plan)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var raw = Path.Combine(fixture.Paths.StateRoot, "raw");
        var keys = Path.Combine(raw, ".location", "keys");
        Directory.CreateDirectory(keys);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keys),
            builder => builder.SetApplicationName("HVO.SkyMonitor.CameraAgent.DeploymentLocation"));
        var protector = provider.CreateProtector("CameraAgent", "DeploymentLocation", "v1");
        var retained = Path.Combine(raw, "boundary-evidence.json");
        await File.WriteAllTextAsync(retained, "{}");
        var history = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, SnapshotProtectedLocations[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        // Lifecycle clients are simulated, while snapshot bytes and the v1 ledger
        // commitments use the real SQLite layout and Data Protection authentication.
        await File.WriteAllBytesAsync(history, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(
            new JsonObject { ["schemaVersion"] = 1, ["locationId"] = "synthetic" })));
        var ledger = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, CameraAgentStateSnapshot.RetainedIntegrityStore);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId && !File.Exists(ledger)) SeedBoundaryLedger(ledger, retained, history, protector);
        };
        _ = await CameraAgentLifecycleManager.ExecuteAsync(UpgradeRequest(fixture), fixture.Runner,
            _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid,
            CancellationToken.None, error: TextWriter.Null);
        var committedHistory = await File.ReadAllBytesAsync(history);
        var committedLedger = await File.ReadAllBytesAsync(ledger);
        var committedFacts = ReadStoreFacts(fixture.Paths);
        var committedJournal = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var dockerCalls = fixture.Runner.InvocationCount;

        var rollback = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback) with { DryRun = plan }, fixture.Runner,
            _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual(plan ? "planned" : "completed", rollback.Outcome);
        Assert.AreEqual(plan ? CandidateImageId : fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        if (plan)
        {
            Assert.IsTrue(fixture.Runner.InvocationCount >= dockerCalls);
            CollectionAssert.AreEqual(committedJournal, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        }
        CollectionAssert.AreEqual(committedHistory, await File.ReadAllBytesAsync(history));
        CollectionAssert.AreEqual(committedLedger, await File.ReadAllBytesAsync(ledger));
        CollectionAssert.AreEqual(committedFacts, ReadStoreFacts(fixture.Paths));
        Assert.AreEqual("{}", await File.ReadAllTextAsync(retained));
    }

    private static void SeedBoundaryLedger(string ledger, string retained, string history, IDataProtector protector)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var digest = SHA256.HashData(File.ReadAllBytes(retained));
        var proof = new EntryProof(nonce, "F|boundary-evidence.json", Convert.ToHexString(digest), 2, 1);
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = ledger, Pooling = false }.ToString());
        database.Open();
        using var schema = database.CreateCommand();
#pragma warning disable CA2100 // Fixed schema and integer version identifiers.
        schema.CommandText = SchemaSql + FormattableString.Invariant(
            $"PRAGMA application_id = {DatabaseApplicationId}; PRAGMA user_version = {SchemaVersion};");
#pragma warning restore CA2100
        _ = schema.ExecuteNonQuery();
        using var entry = database.CreateCommand();
        entry.CommandText = "INSERT INTO entries(key,digest,kind,generation,auth) VALUES($key,$digest,2,1,$auth);";
        entry.Parameters.AddWithValue("$key", proof.Key);
        entry.Parameters.AddWithValue("$digest", digest);
        entry.Parameters.AddWithValue("$auth", protector.Protect(JsonSerializer.SerializeToUtf8Bytes(proof)));
        _ = entry.ExecuteNonQuery();
        using var metadata = database.CreateCommand();
        metadata.CommandText = "INSERT INTO metadata(id,payload) VALUES(1,$payload);";
        metadata.Parameters.AddWithValue("$payload", protector.Protect(JsonSerializer.SerializeToUtf8Bytes(
            new Metadata(nonce, 1, Convert.ToHexString(Hash(proof)), 1, true, new string('0', 128)))));
        _ = metadata.ExecuteNonQuery();
        var location = JsonNode.Parse(protector.Unprotect(File.ReadAllBytes(history)))!.AsObject();
        location["retainedIntegrityNonce"] = nonce;
        File.WriteAllBytes(history, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(location)));
    }
}
