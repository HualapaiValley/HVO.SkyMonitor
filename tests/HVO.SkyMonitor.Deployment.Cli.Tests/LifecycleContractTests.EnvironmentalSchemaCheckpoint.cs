using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DataRow(false, false, 1)]
    [DataRow(true, false, 1)]
    [DataRow(false, true, 1)]
    [DataRow(true, true, 0)]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SchemaThreeEnvironmentalUpgradePlansTheCheckpointBeforeCandidateStartup(bool revertBoundary, bool alignRawBoundary, int expectedSnapshots)
    {
        var previous = CameraAgentSnapshotRollbackExceptionTests.ReadSchemaThreeImageLabels();
        var candidate = CameraAgentSnapshotRollbackExceptionTests.ReadDockerfileLabels();
        const string boundary = "io.hvo.skymonitor.minimum-compatible-revision";
        if (revertBoundary) candidate[boundary] = previous[boundary];
        if (alignRawBoundary)
        {
            // Isolate the environmental boundary without the independent raw schema-16 checkpoint.
            candidate["io.hvo.skymonitor.raw-ingress-schema"] = previous["io.hvo.skymonitor.raw-ingress-schema"];
            candidate["io.hvo.skymonitor.raw-ingress-migration"] = previous["io.hvo.skymonitor.raw-ingress-migration"];
            var contracts = JsonNode.Parse(candidate["io.hvo.skymonitor.state-contracts"])!;
            contracts["rawIngress"] = JsonNode.Parse(previous["io.hvo.skymonitor.state-contracts"])!["rawIngress"]!.DeepClone();
            candidate["io.hvo.skymonitor.state-contracts"] = contracts.ToJsonString();
        }
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, installedImage: image =>
            CameraAgentImageLabels.Identity(image.Source, image.ImmutableReference, image.ImageId, image.Architecture, null,
                JsonSerializer.SerializeToElement(previous)) with
            { SourceRevision = "5f5d253dd38308b8e62be9a1e32194dd675d0b60" });
        foreach (var label in candidate) fixture.Runner.CandidateLabels[label.Key] = label.Value;
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        var catalogManifest = Path.Combine(fixture.Paths.CatalogRoot, "current", "manifest.json");
        await File.WriteAllTextAsync(catalogManifest, "{\"manifestVersion\":3,\"catalog\":{\"id\":\"" + fixture.Manifest.Catalog.CatalogId + "\"}}");
        var database = Path.Combine(fixture.Paths.StateRoot, "raw", ".environment", "environmental-observation-outbox.db");
        SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(database)!);
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        using (var create = connection.CreateCommand())
        {
#pragma warning disable CA2100 // Frozen repository-owned SQL fixture, not user input.
            create.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "previous-release",
                "raw__.environment__environmental-observation-outbox.db.sql"));
            await create.ExecuteNonQueryAsync();
#pragma warning restore CA2100
        }
        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false) with { DryRun = true }, fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("planned", result.Outcome);
        Assert.IsNotNull(result.Plan);
        var snapshots = result.Plan.Items.Count(item => item.Kind == "snapshot" && item.Action == DeploymentPlanAction.Create);
        Console.WriteLine($"Schema-3 environmental upgrade: planned checkpoints={snapshots}; required={snapshots > 0}.");
        Assert.AreEqual(expectedSnapshots, snapshots);
        // The aligned pair proves the environmental boundary alone selects the checkpoint.
        // The ordinary pair keeps the independent raw schema-16 checkpoint even when that boundary is reverted.
        Assert.AreEqual(alignRawBoundary,
            previous["io.hvo.skymonitor.raw-ingress-schema"] == candidate["io.hvo.skymonitor.raw-ingress-schema"]);
        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(3L, await read.ExecuteScalarAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath), "Planning must not start a candidate or mutate its journal.");
    }
}
