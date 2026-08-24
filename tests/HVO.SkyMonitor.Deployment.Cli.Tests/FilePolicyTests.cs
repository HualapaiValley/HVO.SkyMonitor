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
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
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
            Directory.Delete(root, recursive: true);
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
    }
}
