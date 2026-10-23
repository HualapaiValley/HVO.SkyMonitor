using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>Audits complete retained real-CLI/shell comparison receipts, including every durable and remote result.</summary>
[TestClass]
[OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
[TestCategory("Manual")]
public sealed class ExistingDownParityTests
{
    [TestMethod]
    public async Task RetainedCompleteCycle_HasIdenticalExitStateEvidenceAndRemoteArtifacts()
    {
        var root = Environment.GetEnvironmentVariable("HVO_EXISTING_DOWN_PARITY") ?? throw new InvalidOperationException("Set HVO_EXISTING_DOWN_PARITY to the retained comparison-input directory.");
        var cases = Directory.GetDirectories(root, "case-*").Order(StringComparer.Ordinal).ToArray();
        Assert.AreEqual(56, cases.Length, "A partial cycle is not comparison evidence.");
        foreach (var input in cases)
        {
            var metadata = Parse(await File.ReadAllTextAsync(input + "/case.json")); var shell = Parse(await File.ReadAllTextAsync(input + "/shell-result.json"));
            Assert.AreEqual(metadata["exitCode"]!.GetValue<int>(), shell["exitCode"]!.GetValue<int>(), input);
            var typedReceipt = Parse(await File.ReadAllTextAsync(input + "/typed-result.json")); Assert.AreEqual(metadata["exitCode"]!.GetValue<int>(), typedReceipt["exitCode"]!.GetValue<int>(), input);
            var prefix = Directory.Exists(input + "/auth-state-after") ? "auth-" : "";
            if (prefix.Length > 0) Assert.AreEqual(metadata["exitCode"]!.GetValue<int>(), Parse(await File.ReadAllTextAsync(input + "/auth-result.json"))["exitCode"]!.GetValue<int>());
            foreach (var tree in new[] { "state", "evidence", "remote" })
            {
                var typed = Snapshot(input + "/" + prefix + tree + "-after", tree == "state" || tree == "evidence");
                var original = Snapshot(input + "/shell-" + tree + "-after", tree == "state" || tree == "evidence");
                CollectionAssert.AreEqual(typed, original, input + ": " + tree);
            }
        }
    }
    private static string[] Snapshot(string root, bool normalizePhase)
    {
        var result = new List<string>(); Walk(root); return result.Order(StringComparer.Ordinal).ToArray();
        void Walk(string directory)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(root, entry.FullName);
                if (entry.LinkTarget is { } link) result.Add(relative + ":link:" + link);
                else if (entry is DirectoryInfo) { result.Add(relative + ":directory:" + File.GetUnixFileMode(entry.FullName)); Walk(entry.FullName); }
                else
                {
                    var bytes = File.ReadAllBytes(entry.FullName); string content;
                    if (entry.Extension == ".json" && bytes.Length > 0)
                    {
                        var json = JsonNode.Parse(bytes)!;
                        if (normalizePhase && entry.Name.StartsWith("down", StringComparison.Ordinal) && entry.Name.EndsWith("-commit.json", StringComparison.Ordinal))
                        {
                            if (File.Exists(root + "/down-ledger.json"))
                            {
                                var ledger = Parse(File.ReadAllText(root + "/down-ledger.json"));
                                if (json["generation"]!.GetValue<int>() == ledger["publicationGeneration"]!.GetValue<int>())
                                {
                                    Assert.AreEqual(Hash(Canonical(ledger) + "\n"), json.Text("ledgerSha256")); json.AsObject().Remove("ledgerSha256");
                                }
                            }
                        }
                        if (normalizePhase && entry.Name is "down-ledger.json" or "down-manifest.json" or "down.json") Normalize(json);
                        content = Canonical(json);
                    }
                    else content = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    result.Add(relative + ":file:" + File.GetUnixFileMode(entry.FullName) + ":" + content);
                }
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
        else if (node is JsonArray array) foreach (var child in array.Where(n => n is not null)) Normalize(child!);
    }
}
