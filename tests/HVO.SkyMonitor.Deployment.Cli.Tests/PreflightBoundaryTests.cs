using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
[OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
public sealed class PreflightBoundaryTests
{
    [TestMethod]
    [DoNotParallelize]
    public void MissingRemoteBash_KeepsExit127DiagnosticAcrossPipeDisposal()
    {
        using var f = new PreflightFixture(); var path = f.Host.Directory + "/ssh";
        DeploymentFakeHost.Write(path, "#!/bin/bash\nexit 127\n"); File.SetUnixFileMode(path, SafeFileSystem.OwnerDirectoryMode);
        var previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", f.Host.Directory + ":" + previous);
            var error = Assert.ThrowsExactly<InstallerException>(() => { using var files = new SshDeploymentFiles("fake@example", CancellationToken.None, readOnly: true); files.Call("facts"); });
            StringAssert.Contains(error.Message, "tool-unavailable", StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }
    [TestMethod]
    public async Task RealReadOnlyAdapter_ProbesTcpHttpAndLiteralRoots_AndHasNoMutationOperations()
    {
        using var f = new PreflightFixture(); var root = f.Host.Directory + "/literal 'quoted' runtime";
        Directory.CreateDirectory(root, SafeFileSystem.OwnerDirectoryMode);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                try { await client.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray()); } catch (IOException) { }
            }
        });
        using (var files = new SshDeploymentFiles("", CancellationToken.None, local: true, readOnly: true))
        {
            Assert.AreEqual(9, files.Call("facts").Split('\t').Length);
            Assert.AreEqual("ready", PreflightProbe.RuntimeRoot(files, root).Status);
            Assert.AreEqual("reachable", files.Call("tcp", "127.0.0.1", port));
            Assert.AreEqual("reachable", files.Call("http-reachable", "http://127.0.0.1:" + port));
            Assert.ThrowsExactly<InstallerException>(() => files.Call("mkdir", root + "/forbidden"));
        }
        await serving; Assert.IsFalse(Directory.Exists(root + "/forbidden"));
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(SshDeploymentFiles.ReadOnlyAdapter, "(?:^|\\s)(?:build|load|up|start|stop|down|rm|create|mkdir|install|touch)(?:\\s|$)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)));
    }
    [TestMethod]
    public async Task LegacyLoginEmailShape_IsRejectedByDisposableHttpTarget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(); using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
            var length = 0; string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..], System.Globalization.CultureInfo.InvariantCulture);
            var body = new char[length]; Assert.AreEqual(length, await reader.ReadBlockAsync(body)); Assert.IsTrue(new string(body).Contains("Input.Email=", StringComparison.Ordinal));
            await client.GetStream().WriteAsync("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
        });
        using var files = new SshDeploymentFiles("", CancellationToken.None, local: true);
        Assert.AreEqual("405", files.Call("http", "--silent", "--max-time", "8", "--request", "POST", "--output", "/dev/null", "--write-out", "%{http_code}", "--data-urlencode", "Input.Email=owner@example.test", "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/Account/Login"));
        await serving;
    }
    [TestMethod]
    [DataRow("preflight-secret-source-stat", "secret-source-stat-failed")]
    [DataRow("preflight-required-reference-scan", "required-reference-scan-failed")]
    public async Task LocalReadFailures_RejectBeforeTransportOrStatePublication(string boundary, string reason)
    {
        using var f = new PreflightFixture(); await f.StageAsync();
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => f.RunAsync(name => { if (name == boundary) throw Error(reason); }));
        StringAssert.Contains(failure.Message, reason, StringComparison.Ordinal); Assert.AreEqual(0, f.Opens); Assert.IsFalse(Directory.Exists(f.Host.StateRoot));
    }
    [TestMethod]
    public async Task InventoryRefusal_PrecedesSecondaryRunIdRefusal()
    {
        using var f = new PreflightFixture(); await f.StageAsync(); f.Host.Inventory["unexpected"] = true; DeploymentFakeHost.WriteJson(f.Host.InventoryPath, f.Host.Inventory);
        var request = f.Request with { RunId = new string('a', 33) };
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => new SplitHostPreflight(f.Host, new ProcessRunner()).RunAsync(request, CancellationToken.None));
        StringAssert.Contains(failure.Message, "schema-or-value-invalid", StringComparison.Ordinal); Assert.AreEqual(0, f.Opens);
    }
    [TestMethod]
    public async Task PrivateBinaryCopy_StreamsExactBytesWithLiteralPathsThroughRealPrimitiveAdapter()
    {
        using var f = new PreflightFixture(); await f.StageAsync(); var target = f.Host.Contract.Targets[1];
        var root = f.Host.Physical(target.RuntimeRoot); Directory.CreateDirectory(root + "/upload 'quoted'", SafeFileSystem.OwnerDirectoryMode);
        f.Host.UseShellPrimitives = true; f.Host.PrimitiveHandler = null;
        var source = f.Host.Directory + "/certificate"; var content = Enumerable.Range(0, 160000).Select(i => (byte)(i % 256)).ToArray(); await File.WriteAllBytesAsync(source, content); File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var state = new PartialPrepareState(f.Host.StateRoot, f.Host.EvidenceRoot, false, _ => { }, "preflight"); PreflightPrivateUploads.Initialize(state, f.Host.Contract, f.Host.StateRoot);
        new PreflightUploadTransport(f.Host, f.Host.Contract, state, f.Host.StateRoot, "smoke").Copy(source, target, target.RuntimeRoot + "/upload 'quoted'/certificate", CancellationToken.None);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(root + "/upload 'quoted'/certificate"));
    }
    [TestMethod]
    [DataRow("amd64", "amd64")]
    [DataRow("x86_64", "amd64")]
    [DataRow("arm64", "arm64")]
    [DataRow("aarch64", "arm64")]
    [DataRow("ppc64le", null)]
    public void DockerArchitecture_KeepsOnlySupportedAliases(string raw, string? normalized) => Assert.AreEqual(normalized, PreflightHostObservation.DockerArchitecture(raw));
}
