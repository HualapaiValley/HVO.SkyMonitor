using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

/// <summary>
/// Pins the pre-push shell gate against the release tool's validator.
/// <para>
/// The component-inventory rule exists twice: <c>scripts/lib/component-inventory.jq</c> runs before the
/// immutable registry push, and <c>ValidateComponentInventory</c> runs again before signing. Five consecutive
/// review rounds on this pull request found the same defect — a rule present in the validator and missing from
/// the gate — and each was corrected by hand with nothing to stop the next one. The gate is the side that runs
/// before the irreversible step, so when it is the looser of the two, a malformed inventory consumes a
/// published version that adoption can never complete. This test runs one table of documents through both sides
/// and fails when they disagree, which turns "keep the two in step" from a comment into a gate.
/// </para>
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ComponentInventoryParityTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.IsNotNull(directory, "Could not locate the repository root from the test output directory.");
        return directory!.FullName;
    }

    /// <summary>
    /// Runs a document through the shell gate exactly as the release script invokes it, and returns whether it
    /// was accepted. A missing <c>jq</c> fails the test rather than skipping it: the release script cannot run
    /// without <c>jq</c>, so a machine that cannot run this check cannot produce a release either.
    /// </summary>
    private static bool ShellGateAccepts(string documentPath, string imageId, int floor)
    {
        var root = RepositoryRoot();
        var start = new ProcessStartInfo("jq")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        foreach (var argument in new[]
                 {
                     "--exit-status", "-f", Path.Combine(root, "scripts", "lib", "component-inventory.jq"),
                     "--arg", "imageId", imageId,
                     "--argjson", "floor", floor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     documentPath
                 })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)
            ?? throw new AssertFailedException("jq is required by the release script and by this parity test.");
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static async Task<bool> ReleaseToolAcceptsAsync(ImageReleaseFixture fixture, string inventoryPath, string architecture)
    {
        var arguments = fixture.CreateArguments(Path.Combine(fixture.Root, $"parity-{Guid.NewGuid():N}"));
        arguments[Array.IndexOf(arguments, $"--component-sbom-{architecture}") + 1] = inventoryPath;
        var original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            return await ReleaseTool.Program.Main(arguments) == 0;
        }
        finally
        {
            Console.SetError(original);
        }
    }

    /// <summary>
    /// Reads the floor the release script actually passes to the gate, so the shell constant and
    /// <c>MinimumInventoryComponents</c> are bound by the boundary rows below rather than by a comment.
    /// </summary>
    private static int ShellComponentFloor()
    {
        var script = Path.Combine(RepositoryRoot(), "scripts", "release:cameraagent-image");
        var declaration = File.ReadLines(script)
            .FirstOrDefault(line => line.StartsWith("inventory_component_floor=", StringComparison.Ordinal));
        Assert.IsNotNull(declaration, "The release script must declare inventory_component_floor.");
        return int.Parse(
            declaration!["inventory_component_floor=".Length..],
            System.Globalization.CultureInfo.InvariantCulture);
    }

    [TestMethod]
    public async Task ReleaseShellGates_RejectUnsafeRegistryDataAndMatchInventoryValidator()
    {
        await AssertRegistryBlobIntegrityAsync();

        using var fixture = ImageReleaseFixture.Create();
        var floor = ShellComponentFloor();
        var imageId = fixture.ImageIdFor("amd64");
        var valid = await File.ReadAllTextAsync(fixture.ComponentInventoryFor("amd64"));

        var cases = new List<(string Name, string Json)>
        {
            ("valid", valid),
            ("missing-spdxid", Mutate(valid, node => node.Remove("SPDXID"))),
            ("wrong-spdxid", Mutate(valid, node => node["SPDXID"] = "SPDXRef-Other")),
            ("non-string-spdxversion", Mutate(valid, node => node["spdxVersion"] = 23)),
            ("wrong-spdxversion", Mutate(valid, node => node["spdxVersion"] = "SPDX-2.2")),
            ("packages-object", Mutate(valid, node => node["packages"] = new JsonObject { ["a"] = new JsonObject() })),
            ("packages-missing", Mutate(valid, node => node.Remove("packages"))),
            ("package-string", Mutate(valid, node => node["packages"]!.AsArray().Add("a-string"))),
            ("non-object-annotation", Mutate(valid, node =>
                node["packages"]!.AsArray()[0]!["annotations"]!.AsArray().Insert(0, "a-string"))),
            ("root-array", "[1,2]"),
            ("foreign-second-subject", Mutate(valid, node => node["packages"]!.AsArray().Add(new JsonObject
            {
                ["name"] = "smuggled",
                ["versionInfo"] = "1.0.0",
                ["annotations"] = new JsonArray(new JsonObject { ["comment"] = $"ImageID: {fixture.ImageIdFor("arm64")}" })
            }))),
            ("files-not-array", Mutate(valid, node => node["files"] = "not-an-array")),
            ("file-without-sha1", Mutate(valid, node => node["files"] = new JsonArray(new JsonObject
            {
                ["fileName"] = "./x",
                ["checksums"] = new JsonArray(new JsonObject
                {
                    ["algorithm"] = "SHA256",
                    ["checksumValue"] = new string('a', 64)
                })
            }))),
            ("checksums-object", Mutate(valid, node => node["files"] = new JsonArray(new JsonObject
            {
                ["fileName"] = "./x",
                ["checksums"] = new JsonObject
                {
                    ["nested"] = new JsonObject
                    {
                        ["algorithm"] = "SHA1",
                        ["checksumValue"] = new string('0', 40)
                    }
                }
            }))),
            // The floor itself: the two sides must agree at the boundary, which is what binds the shell
            // variable inventory_component_floor to MinimumInventoryComponents in the release tool.
            ("one-below-the-floor", await ComponentsAsync(fixture, imageId, floor - 1)),
            ("exactly-the-floor", await ComponentsAsync(fixture, imageId, floor))
        };

        var disagreements = new List<string>();
        foreach (var (name, json) in cases)
        {
            var path = Path.Combine(fixture.Root, $"parity-{name}.spdx.json");
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
            var shell = ShellGateAccepts(path, imageId, floor);
            var tool = await ReleaseToolAcceptsAsync(fixture, path, "amd64");
            if (shell != tool)
            {
                disagreements.Add($"{name}: shell gate {(shell ? "accepted" : "rejected")}, release tool {(tool ? "accepted" : "rejected")}");
            }
        }

        Assert.AreEqual(
            0,
            disagreements.Count,
            "The pre-push gate and the release tool must accept and reject exactly the same documents, because " +
            "the gate runs before the immutable registry push:" + Environment.NewLine +
            string.Join(Environment.NewLine, disagreements));
    }

    private static async Task<string> ComponentsAsync(ImageReleaseFixture fixture, string imageId, int components)
        => await File.ReadAllTextAsync(
            fixture.WriteComponentInventory($"parity-components-{components}.spdx.json", imageId, components));

    private static string Mutate(string json, Action<JsonObject> mutate)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        mutate(node);
        return node.ToJsonString();
    }

    private static async Task AssertRegistryBlobIntegrityAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-registry-blob-{Guid.NewGuid():N}");
        var tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(tools);
        var blob = Path.Combine(root, "blob.json");
        var destination = Path.Combine(root, "download.json");
        var curlLog = Path.Combine(root, "curl.log");
        var dockerConfig = Path.Combine(root, "docker-config");
        Directory.CreateDirectory(dockerConfig);
        var content = Encoding.UTF8.GetBytes("{\"subject\":\"known-registry-blob\"}\n");
        await File.WriteAllBytesAsync(blob, content);
        var curl = Path.Combine(tools, "curl");
        await File.WriteAllTextAsync(curl, """
            #!/usr/bin/env bash
            set -euo pipefail
            printf '%s\n' "$*" >> "$FAKE_CURL_LOG"
            destination=""
            while [[ $# -gt 0 ]]; do
                if [[ "$1" == "--output" ]]; then
                    destination="${2:-}"
                    shift 2
                else
                    shift
                fi
            done
            if [[ -n "$destination" ]]; then
                cp "$FAKE_BLOB_SOURCE" "$destination"
            fi
            """, new UTF8Encoding(false));
        File.SetUnixFileMode(curl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var mismatch = await RunRegistryBlobAsync(
                tools, blob, curlLog, dockerConfig, destination, $"sha256:{new string('0', 64)}");
            Assert.AreNotEqual(0, mismatch.ExitCode, "Digest-mismatched registry bytes must be rejected.");
            StringAssert.Contains(mismatch.Diagnostics, "returned content digest", StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(destination), "Rejected registry bytes must not remain at the destination.");

            var digest = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))}";
            var accepted = await RunRegistryBlobAsync(tools, blob, curlLog, dockerConfig, destination, digest);
            Assert.AreEqual(0, accepted.ExitCode, accepted.Diagnostics);
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(destination));
            StringAssert.Contains(
                await File.ReadAllTextAsync(curlLog),
                $"--max-filesize {64 * 1024 * 1024}",
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Diagnostics)> RunRegistryBlobAsync(
        string tools,
        string blob,
        string curlLog,
        string dockerConfig,
        string destination,
        string digest)
    {
        var root = RepositoryRoot();
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = root
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("source \"$1\"; registry_blob \"$2\" \"$3\" \"$4\"");
        start.ArgumentList.Add("registry-blob-contract");
        start.ArgumentList.Add(Path.Combine(root, "scripts", "lib", "registry-blob.sh"));
        start.ArgumentList.Add("registry.example/hvo/cameraagent");
        start.ArgumentList.Add(digest);
        start.ArgumentList.Add(destination);
        start.Environment["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["FAKE_BLOB_SOURCE"] = blob;
        start.Environment["FAKE_CURL_LOG"] = curlLog;
        start.Environment["DOCKER_CONFIG"] = dockerConfig;
        using var process = Process.Start(start)
            ?? throw new AssertFailedException("bash is required by the CameraAgent image release.");
        var diagnostics = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, diagnostics);
    }
}
