using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class FilePolicyTests
{
    [TestMethod]
    public void InstallationPaths_DeriveIdentityScopedRoots()
    {
        var instanceId = Guid.Parse("6bc13a6b-4728-4ecf-99d3-c1865536a67b");

        var paths = InstallationPaths.Create("/var/lib/hvo/skymonitor", instanceId, "hyg-v42-production");

        Assert.AreEqual(
            "/var/lib/hvo/skymonitor/cameraagents/6bc13a6b-4728-4ecf-99d3-c1865536a67b",
            paths.InstanceRoot);
        Assert.AreEqual("/var/lib/hvo/skymonitor/catalogs/hyg-v42-production", paths.CatalogRoot);
    }

    [TestMethod]
    public void EnsureSafeExistingAncestors_RejectsSymbolicLink()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        try
        {
            Assert.ThrowsExactly<InstallerException>(
                () => SafeFileSystem.EnsureSafeExistingAncestors(Path.Combine(link, "child")));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ValidateOwnerFile_RejectsGroupReadableCredential()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

            Assert.ThrowsExactly<InstallerException>(() => SafeFileSystem.ValidateOwnerFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CredentialFile_ExistingGeneratedCredentialIsNotReplaced()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "temporary-password");
        try
        {
            var first = await CredentialFile.GetOrCreateAsync(null, path, CancellationToken.None);
            var original = await File.ReadAllTextAsync(first);
            var second = await CredentialFile.GetOrCreateAsync(null, path, CancellationToken.None);

            Assert.AreEqual(first, second);
            Assert.AreEqual(original, await File.ReadAllTextAsync(second));
            Assert.IsTrue(original.Length >= 32);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CredentialFile_GeneratedCredentialOmitsByteOrderMark()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "temporary-password");
        try
        {
            var generated = await CredentialFile.GetOrCreateAsync(null, path, CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(generated);

            // Shell tooling reads the credential byte-for-byte, so an encoding preamble would corrupt the secret.
            Assert.IsTrue(bytes.Length >= 3);
            CollectionAssert.AreNotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.IsTrue(bytes.All(static value => value is >= 0x21 and <= 0x7E || value == (byte)'\n'));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ProtectedFilesAndLocks_RejectLinks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "credential");
        var link = Path.Combine(root, "credential-link");
        File.WriteAllText(path, "secret");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        NativeLinux.CreateHardLink(path, link);
        var lockTarget = Path.Combine(root, "lock-target");
        var lockLink = Path.Combine(root, "operation.lock");
        File.WriteAllText(lockTarget, string.Empty);
        File.SetUnixFileMode(lockTarget, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.CreateSymbolicLink(lockLink, lockTarget);
        try
        {
            Assert.ThrowsExactly<InstallerException>(() => SafeFileSystem.ValidateOwnerFile(path));
            Assert.ThrowsExactly<InstallerException>(() => OperationLock.Acquire(lockLink));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SafeDiagnostic_RedactsCredentialsAndBoundsOutput()
    {
        var diagnostic = $"pull https://user:pass@example.test failed password=abc token:xyz {new string('x', 400)}";

        var safe = Redaction.SafeDiagnostic(diagnostic);

        Assert.IsFalse(safe.Contains("user:pass", StringComparison.Ordinal));
        Assert.IsFalse(safe.Contains("abc", StringComparison.Ordinal));
        Assert.IsFalse(safe.Contains("xyz", StringComparison.Ordinal));
        Assert.IsTrue(safe.Length <= 256);

        var lines = Enumerable.Range(0, 202)
            .Select(index => $"line-{index} token=secret-{index} {new string('x', 300)}");
        var multiline = Redaction.SafeDiagnostics(string.Join('\n', lines)).Split(Environment.NewLine);
        Assert.HasCount(200, multiline);
        StringAssert.StartsWith(multiline[0], "line-2", StringComparison.Ordinal);
        StringAssert.StartsWith(multiline[^1], "line-201", StringComparison.Ordinal);
        Assert.IsTrue(multiline.All(static line => line.Length <= 256));
        Assert.IsFalse(multiline.Any(static line => line.Contains("secret-", StringComparison.Ordinal)));

        var structured = Redaction.SafeDiagnostics(
            "{\"token\":\"json-value\"}\nAuthorization: Bearer bearer-value\npassword plain-value");
        Assert.IsFalse(structured.Contains("json-value", StringComparison.Ordinal));
        Assert.IsFalse(structured.Contains("bearer-value", StringComparison.Ordinal));
        Assert.IsFalse(structured.Contains("plain-value", StringComparison.Ordinal));

        var extended = Redaction.SafeDiagnostics(
            "Authorization: Basic basic-value\npassword \"correct horse battery staple\"\n" +
            "{\"password\":\"abc'def ghi\"}");
        Assert.IsFalse(extended.Contains("basic-value", StringComparison.Ordinal));
        Assert.IsFalse(extended.Contains("correct", StringComparison.Ordinal));
        Assert.IsFalse(extended.Contains("horse", StringComparison.Ordinal));
        Assert.IsFalse(extended.Contains("battery", StringComparison.Ordinal));
        Assert.IsFalse(extended.Contains("staple", StringComparison.Ordinal));
        Assert.IsFalse(extended.Contains("abc", StringComparison.Ordinal));
        Assert.IsFalse(extended.Contains("def", StringComparison.Ordinal));
        Assert.IsFalse(extended.Contains("ghi", StringComparison.Ordinal));
    }
}
