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
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task RollbackAcrossLedgerBoundaryRestoresCommittedUpgradeWhenOldImageFails(bool interruptRecovery, bool unexpectedProtectedEdit)
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
        var startedOld = false;
        var recoveredStarts = 0;
        byte[]? upgradedHistory = null;
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId)
            {
                startedOld = true;
                Assert.IsFalse(File.Exists(ledger), "The old image must be started on its paired pre-ledger state.");
                if (unexpectedProtectedEdit)
                    File.WriteAllBytes(history, protector.Protect("{\"locationId\":\"unexpected-edit\"}"u8.ToArray()));
                return;
            }
            if (startedOld)
            {
                Assert.IsTrue(File.Exists(ledger), "Recovery must restore S1 authority before restarting the committed image.");
                CollectionAssert.AreEqual(upgradedHistory!, File.ReadAllBytes(history));
                recoveredStarts++;
            }
            if (!File.Exists(ledger)) SeedBoundaryLedger(ledger, retained, history, protector);
        };
        _ = await CameraAgentLifecycleManager.ExecuteAsync(UpgradeRequest(fixture, assertMigration: false), fixture.Runner,
            _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid,
            CancellationToken.None, error: TextWriter.Null);
        upgradedHistory = await File.ReadAllBytesAsync(history);
        var upgradedFacts = ReadStoreFacts(fixture.Paths);
        var interrupted = false;
        var oldHook = CameraAgentStateSnapshot.StepCompleted;
        CameraAgentStateSnapshot.StepCompleted = step =>
        {
            if (!interruptRecovery || !startedOld || interrupted || step != "restore-boundary-authority") return;
            interrupted = true;
            throw new IOException("Synthetic interruption after recovery authority became durable.");
        };
        try
        {
            var rejectingOwner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => new FakeLifecycleClient(),
                _ => rejectingOwner, fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));
            Assert.IsTrue(startedOld);
            if (unexpectedProtectedEdit)
            {
                Assert.IsFalse(File.Exists(ledger), "Unknown protected edits must not authorize reconstruction of authority.");
                Assert.AreEqual(0, recoveredStarts);
                Assert.AreEqual("{}", await File.ReadAllTextAsync(retained));
                return;
            }
            if (interruptRecovery)
            {
                Assert.IsTrue(interrupted, "The first recovery must reach its durable authority boundary.");
                var resumed = await CameraAgentLifecycleManager.ExecuteAsync(
                    fixture.Request(LifecycleOperationKind.Rollback) with { Resume = true }, fixture.Runner,
                    _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
                    fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);
                Assert.AreEqual("completed", resumed.Outcome);
            }
            else
            {
                Assert.IsTrue(fixture.Runner.Running, "A failed old image must not leave the committed image stopped.");
                Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
                CollectionAssert.AreEqual(upgradedHistory, await File.ReadAllBytesAsync(history));
                CollectionAssert.AreEqual(upgradedFacts, ReadStoreFacts(fixture.Paths));
            }
            Assert.IsGreaterThan(0, recoveredStarts);
            Assert.AreEqual("{}", await File.ReadAllTextAsync(retained));
        }
        finally { CameraAgentStateSnapshot.StepCompleted = oldHook; }
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
        schema.CommandText = SchemaSql;
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
