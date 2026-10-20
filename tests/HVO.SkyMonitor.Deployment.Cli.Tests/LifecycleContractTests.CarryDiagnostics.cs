using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("screen", false)]
    [DataRow("screen", true)]
    [DataRow("trial", false)]
    [DataRow("trial", true)]
    [DataRow("apply", false)]
    [DataRow("apply", true)]
    public async Task UpgradeAsync_WholeStoreCarryFailure_NamesEveryFloorAndEscapesKeys(string phase, bool json)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 5, fleetNextSequence: 10);
        const string key = "agent'\n\u001b[31m\\\"";
        using (var connection = new SqliteConnection($"Data Source={RawIngressPath(fixture.Paths)};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO raw_capture_sequences VALUES($key, 7);";
            command.Parameters.AddWithValue("$key", key);
            await command.ExecuteNonQueryAsync();
        }
        var floors = CameraAgentStateCounters.ReadReadable(fixture.Paths.StateRoot).Floors
            .Where(static floor => floor.Store == "raw/journal/raw-ingress.db").ToArray();
        Assert.IsTrue(floors.Length >= 3);
        using var error = new StringWriter();
        CameraAgentStateCounters.CarryStep = current =>
        {
            if (current == $"{phase}:raw/journal/raw-ingress.db") throw new IOException("shared carry failure");
        };
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false) with { Json = json }, fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
                fixture.Uid, fixture.Gid, CancellationToken.None, error: error));
        }
        finally
        {
            CameraAgentStateCounters.CarryStep = static _ => { };
        }
        var warnings = json
            ? error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => { using var document = JsonDocument.Parse(line); return document.RootElement.GetProperty("warning").GetString()!; })
                .Where(static warning => warning.Contains("could not carry", StringComparison.Ordinal)).ToArray()
            : [error.ToString()];
        var warning = string.Join("\n", warnings);
        foreach (var floor in floors)
        {
            StringAssert.Contains(warning,
                $"the {floor.Counter} counter of '{floor.Store}' with key \"{JsonEncodedText.Encode(floor.Key)}\" (shared carry failure)",
                StringComparison.Ordinal);
        }
        Assert.IsFalse(warning.Contains(key, StringComparison.Ordinal), "A key injected terminal control characters.");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlanCarry_RefusedDefinitions_NameEachEscapedKey(bool json)
    {
        using var fixture = await MigratingFixtureAsync();
        var target = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        ExecuteSql(target, FixtureDump(AutomationStore));
        StateCounterFloor[] floors =
        [
            new(AutomationStore, "automation-definition-version", "first\0", 2),
            new(AutomationStore, "automation-definition-version", "second\n\0", 3)
        ];
        var plan = CameraAgentStateCounters.PlanCarry(floors, _ => target, Path.Combine(fixture.Root, "trial"));
        using var error = new StringWriter();
        await CameraAgentStateSnapshot.ShowRestoreUncarriedAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), DateTimeOffset.UtcNow), plan.Uncarried, json, error);
        var warning = error.ToString();
        if (json)
        {
            using var document = JsonDocument.Parse(warning);
            warning = document.RootElement.GetProperty("warning").GetString()!;
        }
        foreach (var floor in floors)
            StringAssert.Contains(warning, $"with key \"{JsonEncodedText.Encode(floor.Key)}\" (its key contains a NUL character)", StringComparison.Ordinal);
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PlanCarry_LargeTriggerJournal_IsBudgetedBeforeTrialAndLeavesRestoreSpace()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 5, fleetNextSequence: 10);
        var target = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, FleetStore);
        ExecuteSql(target,
            "CREATE TABLE padding(value BLOB);" +
            "WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM rows WHERE n < 1024) INSERT INTO padding SELECT zeroblob(8192) FROM rows;" +
            "CREATE TRIGGER rewrite_pages AFTER UPDATE ON fleet_status_metadata BEGIN UPDATE padding SET value = randomblob(8192); END;");
        var bytes = new FileInfo(target).Length;
        Assert.IsTrue(bytes > 8 * 1024 * 1024);
        var floors = CameraAgentStateCounters.ReadReadable(fixture.Paths.StateRoot).Floors
            .Where(static floor => floor.Store == FleetStore).Select(static floor => floor with { Value = 20 }).ToArray();
        var trial = Path.Combine(fixture.Root, "large-trial");
        long journalBytes = 0;
        CameraAgentStateCounters.CarryStep = current =>
        {
            if (current == $"trial-verify:{FleetStore}") journalBytes = new FileInfo(Path.Combine(trial, "store.db-journal")).Length;
        };
        try
        {
            var carried = CameraAgentStateCounters.PlanCarry(floors, _ => target, trial);
            Assert.IsEmpty(carried.Uncarried);
            Assert.IsTrue(journalBytes >= bytes * 9 / 10, $"The real rollback journal was only {journalBytes} bytes for {bytes} bytes of database.");
            CameraAgentStateCounters.TrialHost = new FakeHostEnvironment
            {
                Fallback = new(fixture.Root, "test", 2 * bytes + 256L * 1024 * 1024)
            };
            var started = false;
            CameraAgentStateCounters.CarryStep = current => { if (current == $"trial:{FleetStore}") started = true; };
            var refused = CameraAgentStateCounters.PlanCarry(floors, _ => target, trial);
            Assert.IsEmpty(refused.Floors, "A trial consumed the space needed for its journal and the restore.");
            Assert.IsFalse(started, "The trial started before checking its space.");
            Assert.IsFalse(Directory.Exists(trial));
            Assert.IsTrue(refused.Uncarried.All(static entry => entry.Reason.Contains("rollback journal", StringComparison.Ordinal)));
        }
        finally
        {
            CameraAgentStateCounters.CarryStep = static _ => { };
            CameraAgentStateCounters.TrialHost = LinuxHostEnvironment.Instance;
        }
    }
}
