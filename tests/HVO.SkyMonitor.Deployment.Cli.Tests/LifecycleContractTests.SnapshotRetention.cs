using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The database snapshot cleanup policy (#1353): a snapshot of this instance nothing refers to is removed, and a snapshot
/// the manifest keeps for a rollback, one an unfinished operation names or wrote, one another instance owns, or one whose
/// manifest cannot be read, is never touched.
/// </summary>
public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SnapshotRetention_RemovesOnlySnapshotsOfThisInstanceNothingRefersTo()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var unreferenced = SeedSnapshot(fixture, Guid.NewGuid(), fixture.InstanceId);
        var rollback = SeedSnapshot(fixture, Guid.NewGuid(), fixture.InstanceId);
        var unfinishedOperation = Guid.NewGuid();
        var unfinishedOwn = SeedSnapshot(fixture, unfinishedOperation, fixture.InstanceId);
        var namedByUnfinished = SeedSnapshot(fixture, Guid.NewGuid(), fixture.InstanceId);
        var otherInstance = SeedSnapshot(fixture, Guid.NewGuid(), Guid.NewGuid());
        var unreadable = SeedSnapshot(fixture, Guid.NewGuid(), fixture.InstanceId);
        await File.WriteAllTextAsync(Path.Combine(unreadable.Root, "manifest.json"), "{not json");
        var manifest = fixture.Manifest with { PreviousStateSnapshot = rollback.Reference };
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ManifestPath, manifest, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.LifecycleStatePath,
            new LifecycleOperationState(
                DeploymentSchemaVersions.LifecycleOperation, unfinishedOperation, LifecycleOperationKind.Upgrade, fixture.InstanceId,
                new string('5', 64), LifecycleOperationPhase.StateSnapshotRecorded, InstallationStatus.Running,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, manifest.Image, CandidateImage: manifest.Image, OriginalCatalog: manifest.Catalog,
                MutationStarted: true,
                RetainedStateSnapshot: namedByUnfinished.Reference),
            DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);

        var planned = await CameraAgentStateSnapshotRetention.PlanAsync(fixture.Paths, manifest, CancellationToken.None);
        var removed = await CameraAgentStateSnapshotRetention.PruneAsync(fixture.Paths, manifest, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { unreferenced.Root }, planned.ToArray());
        CollectionAssert.AreEqual(new[] { unreferenced.Root }, removed.ToArray());
        Assert.IsFalse(Directory.Exists(unreferenced.Root));
        foreach (var kept in new[] { rollback, unfinishedOwn, namedByUnfinished, otherInstance, unreadable })
        {
            Assert.IsTrue(Directory.Exists(kept.Root), kept.Root);
        }
    }

    private static (string Root, LifecycleStateSnapshot Reference) SeedSnapshot(LifecycleFixture fixture, Guid operationId, Guid instanceId)
    {
        var snapshotId = Guid.NewGuid();
        var root = CameraAgentStateSnapshot.SnapshotRoot(fixture.Paths, operationId, snapshotId);
        Directory.CreateDirectory(root);
        var manifestPath = Path.Combine(root, "manifest.json");
        SafeFileSystem.WriteJsonAtomicAsync(
                manifestPath,
                new StateSnapshotManifest(1, snapshotId, instanceId, operationId, DateTimeOffset.UtcNow, fixture.Manifest.Image, []),
                DeploymentJsonContext.Default.StateSnapshotManifest, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (root, new LifecycleStateSnapshot(snapshotId, operationId, new string('a', 64), DateTimeOffset.UtcNow));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void RestoredCredentials_RevokeAgainOnlyWhatTheLiveStoreNoLongerHas()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-credentials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var restored = Path.Combine(root, "restored.db");
            var live = Path.Combine(root, "live.db");
            const string schema =
                "CREATE TABLE AspNetUsers(Id TEXT PRIMARY KEY, PasswordHash TEXT, SecurityStamp TEXT, ConcurrencyStamp TEXT, PasswordChangeRequired INTEGER NOT NULL);" +
                "CREATE TABLE AspNetUserTokens(UserId TEXT, LoginProvider TEXT, Name TEXT, Value TEXT, PRIMARY KEY(UserId, LoginProvider, Name));";
            Execute(restored, schema +
                "INSERT INTO AspNetUsers VALUES('owner', 'old', 's1', 'c1', 0), ('gone', 'g', 'gs', 'gc', 0);" +
                $"INSERT INTO AspNetUserTokens VALUES('owner', '{RestoredCredentials.ApiKeyProvider}', 'kept', 'v'), ('owner', '{RestoredCredentials.ApiKeyProvider}', 'revoked', 'v'), ('owner', 'Other', 'revoked', 'v');");
            Execute(live, schema +
                "INSERT INTO AspNetUsers VALUES('owner', 'new', 's2', 'c2', 0);" +
                $"INSERT INTO AspNetUserTokens VALUES('owner', '{RestoredCredentials.ApiKeyProvider}', 'kept', 'v'), ('owner', '{RestoredCredentials.ApiKeyProvider}', 'created-later', 'v');");

            var first = RestoredCredentials.Revoke(restored, live);
            var second = RestoredCredentials.Revoke(restored, live);

            Assert.AreEqual((1, 1, 1, 3), (first.ApiKeysRevoked, first.AccountsReset, first.AuthorityRevoked, first.Changes.Count));
            Assert.AreEqual((0, 0, 0, 0), (second.ApiKeysRevoked, second.AccountsReset, second.AuthorityRevoked, second.Changes.Count),
                "running it again changes nothing more");
            using var connection = new SqliteConnection($"Data Source={restored};Pooling=False");
            connection.Open();
            using var tokens = connection.CreateCommand();
            tokens.CommandText = "SELECT group_concat(LoginProvider || ':' || Name, ',') FROM (SELECT * FROM AspNetUserTokens ORDER BY LoginProvider, Name);";
            Assert.AreEqual($"{RestoredCredentials.ApiKeyProvider}:kept,Other:revoked", tokens.ExecuteScalar());
            using var owner = connection.CreateCommand();
            owner.CommandText = "SELECT PasswordHash || ',' || SecurityStamp || ',' || ConcurrencyStamp FROM AspNetUsers WHERE Id = 'owner';";
            Assert.AreEqual("new,s2,c2", owner.ExecuteScalar());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
