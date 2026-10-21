using System.Formats.Tar;
using System.Reflection;
using System.IO.Compression;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    private static readonly string[] Diagnostic1476EventProperties = ["schemaVersion", "event", "warning", "snapshot"];
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false, "\u001b[31m", "\"")]
    [DataRow(true, "\u001b[31m", "\"")]
    [DataRow(false, "\u001b[31m", "'")]
    [DataRow(true, "\u001b[31m", "'")]
    [DataRow(false, "\u009b31m", "\"")]
    [DataRow(true, "\u009b31m", "\"")]
    [DataRow(false, "\u001b[1m\u009b31m\t", "'")]
    [DataRow(true, "\u001b[1m\u009b31m\t", "'")]
    public async Task Diagnostic1476_QuotedCredentialAcrossAnsiBoundaryIsRedacted(bool json, string decoration, string quote)
    {
        using var fixture = await MigratingFixtureAsync();
        var target = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        ExecuteSql(target, FixtureDump(AutomationStore));
        SaveDefinition(target, "agent\u001b[31m\"\t\u0085", 1);
        var diagnostic = $"token={decoration}{quote}boundary-redaction-sentinel with spaces\t\u0085{quote}\u001b[0m";
        ExecuteSql(target, "UPDATE automation_definition_revisions SET definition_json='{}';" +
            "CREATE TRIGGER refuse_update BEFORE UPDATE ON automation_definitions BEGIN " +
            $"SELECT RAISE(ABORT, '{diagnostic.Replace("'", "''", StringComparison.Ordinal)}'); END;");
        var plan = CameraAgentStateCounters.PlanCarry(
            [new(AutomationStore, "automation-definition-version", "agent\u001b[31m\"\t\u0085", 2)], _ => target,
            Path.Combine(fixture.Root, "boundary-trial"));
        Assert.HasCount(1, plan.Uncarried);
        StringAssert.Contains(plan.Uncarried[0].Reason, "boundary-redaction-sentinel", StringComparison.Ordinal);
        using var error = new StringWriter();
        await CameraAgentStateSnapshot.ShowRestoreUncarriedAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), DateTimeOffset.UtcNow), plan.Uncarried, json, error);
        var warning = error.ToString();
        if (json)
        {
            using var document = JsonDocument.Parse(warning);
            Assert.AreEqual(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            CollectionAssert.AreEquivalent(Diagnostic1476EventProperties,
                document.RootElement.EnumerateObject().Select(static p => p.Name).ToArray());
            warning = document.RootElement.GetProperty("warning").GetString()!;
        }
        Console.WriteLine(JsonSerializer.Serialize(warning));
        AssertDiagnostic1476Safe(warning);
        Assert.IsFalse(warning.Contains('\u001b', StringComparison.Ordinal));
        StringAssert.Contains(warning, plan.Uncarried[0].What, StringComparison.Ordinal);
        Assert.IsFalse(warning.Contains("boundary-redaction-sentinel", StringComparison.Ordinal),
            "Redaction removed the ANSI prefix but left the quoted credential in the warning.");
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Diagnostic1476_ArchiveRefusalIsTerminalSafe(bool json)
    {
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "instance/" + HostileDiagnostic1476Name)
            {
                DataStream = new MemoryStream([1])
            };
            await tar.WriteEntryAsync(entry);
        }
        archive.Position = 0;
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => BackupArchive.ReadAsync(
            archive, null, null, CancellationToken.None));
        var message = await RenderDiagnostic1476ErrorAsync(failure.Message, json);
        Console.WriteLine(JsonSerializer.Serialize(message));
        AssertDiagnostic1476Safe(message);
        Assert.IsFalse(message.Contains('\u001b', StringComparison.Ordinal), "The archive refusal echoed a literal ESC from an untrusted entry name.");
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Diagnostic1476_SnapshotStoreIdentityIsEscaped(bool json, bool hostileStore)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        const string root = "/app/data/raw/quote\"root";
        await File.WriteAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "camera-module.json"),
            JsonSerializer.Serialize(new { storageRoot = root }));
        var stores = CameraAgentStateInventory.Stores(fixture.Paths, fixture.Manifest.ReplayProfile);
        var relative = stores.Single(static store => store.Contains("quote", StringComparison.Ordinal));
        // Configured roots reject controls at admission; also exercise the snapshot boundary directly with a hostile filename.
        if (hostileStore)
        {
            relative = $"raw/{HostileDiagnostic1476Name}/store.db";
            stores = [relative];
        }
        var target = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "not a SQLite database");
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentStateSnapshot.CreateAsync(
            fixture.Paths, fixture.InstanceId, Guid.NewGuid(), fixture.Manifest.Image, stores, CancellationToken.None));
        var message = await RenderDiagnostic1476ErrorAsync(failure.Message, json);
        Console.WriteLine(JsonSerializer.Serialize(message));
        AssertDiagnostic1476Safe(message);
        Assert.IsFalse(message.Contains(relative, StringComparison.Ordinal), "The snapshot diagnostic echoed the unescaped configured store path.");
        StringAssert.Contains(failure.Message, CameraAgentStateDiagnostics.Reason(relative), StringComparison.Ordinal);
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Diagnostic1476_RestoredCredentialWarningIsTerminalSafe(bool json)
    {
        using var fixture = await BackupFixtureAsync();
        const string key = HostileDiagnostic1476Name;
        var identity = CameraAgentLayoutIdentity(fixture);
        Execute(identity, $"UPDATE AspNetUserTokens SET Name='{key}' WHERE Name='{RevokedKey}';");
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        Execute(identity, $"DELETE FROM AspNetUserTokens WHERE Name='{key}';");
        await UninstallAsync(fixture);
        using var error = new StringWriter();
        var restored = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Restore) with { Archive = backup.Backup!.Archive, Json = json },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error);
        Assert.AreEqual("completed", restored.Outcome);
        var warning = error.ToString();
        if (json)
        {
            var events = warning.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var credentialEvent = events.Single(static document => document.RootElement.GetProperty("event").GetString() ==
                    LifecycleCredentialsRestoredEvent.Name).RootElement;
                Assert.AreEqual(1, credentialEvent.GetProperty("schemaVersion").GetInt32());
                string[] properties = ["schemaVersion", "event", "warning", "backupCreatedUtc"];
                CollectionAssert.AreEquivalent(properties, credentialEvent.EnumerateObject().Select(static property => property.Name).ToArray());
                warning = credentialEvent.GetProperty("warning").GetString()!;
            }
            finally
            {
                foreach (var document in events) document.Dispose();
            }
        }
        if (!json) warning = warning.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(static line => line.StartsWith("Warning: the backup (", StringComparison.Ordinal));
        Assert.IsFalse(ReadCredentials(fixture.Paths).Keys.Contains(key, StringComparer.Ordinal));
        Console.WriteLine(JsonSerializer.Serialize(warning));
        AssertDiagnostic1476Safe(warning);
        Assert.IsFalse(warning.Contains('\u001b', StringComparison.Ordinal), "The credential restore warning emitted a literal ESC from a database key.");
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Diagnostic1476_SnapshotCleanupDoesNotEmitLiteralEscape(bool json)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var snapshot = SeedSnapshot(fixture, Guid.NewGuid(), fixture.InstanceId);
        const string name = HostileDiagnostic1476Name;
        File.CreateSymbolicLink(Path.Combine(snapshot.Root, name), Path.Combine(snapshot.Root, "absent"));
        using var error = new StringWriter();
        var method = typeof(CameraAgentLifecycleManager).GetMethod("PruneStateSnapshotsAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        IReadOnlyList<InstallationWarning>? warnings;
        try
        {
            warnings = await (Task<IReadOnlyList<InstallationWarning>?>)method.Invoke(null,
                [fixture.Paths, fixture.Manifest, json, error])!;
        }
        finally
        {
            File.Delete(Path.Combine(snapshot.Root, name));
        }
        Assert.IsNotNull(warnings);
        Assert.HasCount(1, warnings);
        var warning = json
            ? JsonSerializer.Deserialize<InstallationWarning[]>(JsonSerializer.Serialize(warnings))![0].Message
            : error.ToString();
        Console.WriteLine(JsonSerializer.Serialize(warning));
        AssertDiagnostic1476Safe(warning);
        Assert.IsFalse(warning.Contains('\u001b', StringComparison.Ordinal), "Snapshot cleanup emitted a literal ESC from a filesystem exception.");
    }

    private const string HostileDiagnostic1476Name = "hostile\u001b[31m\"\t\u0085 token=\u001b[31m\"diagnostic-1476-sentinel with spaces\"\u001b[0m";

    private static void AssertDiagnostic1476Safe(string message)
    {
        Assert.IsFalse(message.TrimEnd('\r', '\n').Any(char.IsControl), JsonSerializer.Serialize(message));
        Assert.IsFalse(message.Contains("diagnostic-1476-sentinel", StringComparison.Ordinal), message);
    }

    private static async Task<string> RenderDiagnostic1476ErrorAsync(string message, bool json)
    {
        using var error = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(error);
            var renderer = typeof(HVO.SkyMonitor.Deployment.Program).GetMethod("WriteErrorAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            await (Task)renderer.Invoke(null, [json, "installation-failed", message, false])!;
        }
        finally
        {
            Console.SetError(previous);
        }
        if (!json) return error.ToString();
        using var document = JsonDocument.Parse(error.ToString());
        return document.RootElement.GetProperty("message").GetString()!;
    }
}
