using System.Diagnostics;
using System.Text.Json.Nodes;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>Replays the full retained shell comparison cycle's immutable inputs through the typed phase.</summary>
[TestClass]
[OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
[TestCategory("Manual")]
public sealed class PartialPrepareParityTests
{
    [TestMethod]
    public async Task RetainedShellCycle_MatchesTypedResultsAndDurableEvidence()
    {
        var root = Environment.GetEnvironmentVariable("HVO_PARTIAL_PREPARE_PARITY") ?? throw new InvalidOperationException("Set HVO_PARTIAL_PREPARE_PARITY to the retained comparison-input directory.");
        var results = new JsonArray();
        foreach (var input in Directory.EnumerateDirectories(root, "case-*").Order(StringComparer.Ordinal))
        {
            using var host = new DeploymentFakeHost(); var metadata = await host.ImportAsync(input);
            var options = metadata["options"]!; var inspect = metadata.Array("args").Any(n => n!.GetValue<string>() == "--dry-run");
            var request = host.Request(inspect) with { Confirmation = options["--confirm"]?.GetValue<string>() };
            var environment = metadata["env"]!; var replace = environment["FAKE_PARTIAL_PREPARE_REPLACE_PATH"]?.GetValue<string>(); var move = environment["FAKE_PARTIAL_PREPARE_REPLACE_ON_MOVE"]?.GetValue<string>();
            var fired = false; var count = 0;
            host.BeforePrimitive = (operation, args) =>
            {
                if (fired) return;
                var candidate = replace ?? move;
                if (candidate is null) return;
                if (replace is not null ? operation == "stat" && args[0] == replace && ++count == 6 : operation == "move" && Path.GetFileName(args[0]) == Path.GetFileName(move))
                {
                    fired = true; var path = host.Physical(candidate); var bytes = File.ReadAllBytes(path); File.Move(path, path + ".race-original"); File.WriteAllBytes(path, bytes); File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            };
            var expected = metadata["exitCode"]!.GetValue<int>(); var code = 0; var error = "";
            var watch = Stopwatch.StartNew();
            try
            {
                await new PartialPrepareRecovery(host, new ProcessRunner(), point => { if (environment["DEPLOY_TEST_FAILPOINT"]?.GetValue<string>() == point) throw Error("injected-failure"); }).RunAsync(request, CancellationToken.None);
            }
            catch (InstallUsageException exception) { code = 2; error = exception.Message; }
            catch (InstallerException exception) { code = 1; error = exception.Message; }
            watch.Stop();
            Assert.AreEqual(expected, code, $"{input}: {error}");
            Assert.AreEqual(2, metadata["captureVersion"]!.GetValue<int>(), "Retain complete filesystem results, not just exit codes.");
            foreach (var tree in new[] { "state", "evidence" })
            {
                var expectedRoot = input + "/" + tree + "-after";
                var actualRoot = tree == "state" ? host.StateRoot : host.EvidenceRoot;
                var expectedFiles = Directory.EnumerateFiles(expectedRoot, "partial-prepare-*.json").Order(StringComparer.Ordinal).ToArray();
                CollectionAssert.AreEqual(expectedFiles.Select(Path.GetFileName).ToArray(), Directory.EnumerateFiles(actualRoot, "partial-prepare-*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray(), input + ": phase file inventory");
                foreach (var expectedFile in expectedFiles)
                {
                    var actualFile = actualRoot + "/" + Path.GetFileName(expectedFile);
                    var before = Parse(await File.ReadAllTextAsync(expectedFile)); var after = Parse(await File.ReadAllTextAsync(actualFile));
                    if (Path.GetFileName(expectedFile).EndsWith("-commit.json", StringComparison.Ordinal))
                    {
                        var ledgerName = Path.GetFileName(expectedFile).Replace("-commit.json", "-ledger.json", StringComparison.Ordinal);
                        Assert.AreEqual(Hash(Canonical(Parse(await File.ReadAllTextAsync(input + "/state-after/" + ledgerName))) + "\n"), before.Text("ledgerSha256"));
                        Assert.AreEqual(Hash(Canonical(Parse(await File.ReadAllTextAsync(host.StateRoot + "/" + ledgerName))) + "\n"), after.Text("ledgerSha256"));
                        before.Remove("ledgerSha256"); after.Remove("ledgerSha256");
                    }
                    Normalize(before); Normalize(after); Assert.AreEqual(Canonical(before), Canonical(after), input + ": " + Path.GetFileName(expectedFile));
                }
            }
            CollectionAssert.AreEqual(Filesystem(input + "/remote-after"), Filesystem(host.Directory + "/remote"), input + ": remote file types, modes, bytes and exact paths");
            results.Add(new JsonObject { ["case"] = Path.GetFileName(input), ["exitCode"] = code, ["milliseconds"] = watch.Elapsed.TotalMilliseconds, ["peakRssBytes"] = Process.GetCurrentProcess().PeakWorkingSet64, ["replacementExercised"] = fired });
        }
        Assert.AreEqual(45, results.Count, "The full shell cycle must be retained; a partial vector set is not parity.");
        await File.WriteAllTextAsync(root + "/typed-results.json", results.ToJsonString(Options));
    }
    private static string[] Filesystem(string root)
    {
        var entries = new List<string>(); Walk(root); return entries.Order(StringComparer.Ordinal).ToArray();
        void Walk(string directory)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                var path = Path.GetRelativePath(root, entry.FullName);
                if (entry.LinkTarget is { } link) entries.Add(path + ":link:" + link);
                else if (entry is DirectoryInfo) { entries.Add(path + ":directory:" + File.GetUnixFileMode(entry.FullName)); Walk(entry.FullName); }
                else entries.Add(path + ":file:" + File.GetUnixFileMode(entry.FullName) + ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry.FullName))));
            }
        }
    }
    private static void Normalize(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "startedAt", "updatedAt", "completedAt", "intentAt" }) if (obj.ContainsKey(key)) obj[key] = "<timestamp>";
            foreach (var child in obj.Select(p => p.Value).Where(n => n is not null)) Normalize(child!);
        }
        else if (node is JsonArray arr) foreach (var child in arr) if (child is not null) Normalize(child);
    }
}
