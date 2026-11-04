using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>Audit complete immutable real-CLI comparison receipts; a partial cycle cannot move authority.</summary>
[TestClass]
[TestCategory("Manual")]
[OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
public sealed class PreflightParityTests
{
    [TestMethod]
    public void CompleteOriginalCycle_HasExactExitStateEvidenceAndRemoteParity()
    {
        var root = Environment.GetEnvironmentVariable("HVO_PREFLIGHT_PARITY") ?? throw new InvalidOperationException("Set HVO_PREFLIGHT_PARITY to the retained 69-case receipt directory.");
        var acceptance = Parse(DeploymentFakeHost.Read(root + "/accepted.json")); var attempt = acceptance.Text("attempt");
        var cases = Directory.GetDirectories(root, "case-*").Order(StringComparer.Ordinal).ToArray(); Assert.AreEqual(69, cases.Length, "The entire original assertion cycle is required.");
        foreach (var input in cases)
        {
            var receipt = input + "/" + attempt;
            var original = Parse(DeploymentFakeHost.Read(input + "/shell-result.json")); var shell = Parse(DeploymentFakeHost.Read(receipt + "/pinned-shell-result.json")); var typed = Parse(DeploymentFakeHost.Read(receipt + "/typed-result.json"));
            Assert.AreEqual(original["exitCode"]!.GetValue<int>(), shell["exitCode"]!.GetValue<int>(), input);
            Assert.AreEqual(shell["exitCode"]!.GetValue<int>(), typed["exitCode"]!.GetValue<int>(), input);
            Assert.AreEqual(acceptance.Text("binarySha256"), typed.Text("binarySha256"), input);
            foreach (var tree in new[] { "state", "evidence", "remote" })
                CollectionAssert.AreEqual(Snapshot(receipt + "/pinned-shell-" + tree + "-after"), Snapshot(receipt + "/typed-" + tree + "-after"), input + ": " + tree);
        }
    }
    private static string[] Snapshot(string root)
    {
        var result = new List<string>(); if (Directory.Exists(root)) { result.Add(".:directory:" + File.GetUnixFileMode(root)); Walk(root); }
        return result.Order(StringComparer.Ordinal).ToArray();
        void Walk(string directory)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                var path = Path.GetRelativePath(root, entry.FullName);
                if (entry.LinkTarget is { } target) result.Add(path + ":link:" + target);
                else if (entry is DirectoryInfo) { result.Add(path + ":directory:" + File.GetUnixFileMode(entry.FullName)); Walk(entry.FullName); }
                else
                {
                    var bytes = File.ReadAllBytes(entry.FullName); string value;
                    try
                    {
                        var node = JsonNode.Parse(bytes)!;
                        if (entry.Name is "manifest.json" or "preflight.json") Normalize(node);
                        value = Canonical(node);
                    }
                    catch (System.Text.Json.JsonException) { value = Convert.ToHexStringLower(SHA256.HashData(bytes)); }
                    result.Add(path + ":file:" + File.GetUnixFileMode(entry.FullName) + ":" + value);
                }
            }
        }
    }
    private static void Normalize(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "startedAt", "updatedAt", "completedAt", "timestamp" }) if (obj[key] is not null) obj[key] = "<timestamp>";
            if (obj.ContainsKey("clockOffsetSeconds")) obj["clockOffsetSeconds"] = "<clock-offset>";
            foreach (var child in obj.Select(p => p.Value).Where(n => n is not null)) Normalize(child!);
        }
        else if (node is JsonArray array) foreach (var child in array.Where(n => n is not null)) Normalize(child!);
    }
}
