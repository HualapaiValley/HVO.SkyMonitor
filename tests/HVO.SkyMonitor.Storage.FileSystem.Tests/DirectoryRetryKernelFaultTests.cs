namespace HVO.SkyMonitor.Storage.FileSystem.Tests;

[TestClass]
[TestCategory("Manual")]
public sealed class DirectoryRetryKernelFaultTests
{
    [TestMethod]
    public void AncestorDirectoryRetryWithRealFsyncFailure()
    {
        var control = RequireKernelRetryControl();
        var path = Path.Combine(Path.GetTempPath(), "hvo-fs-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var root = PhysicalRoot.Open(path);
            var nested = root.Resolve("new/child");
            ArmKernelRetryFault(control, root.Path);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var failure = Assert.ThrowsExactly<FileSystemFaultException>(() => AtomicPublisher.EnsureDirectory(root, nested));
                Assert.AreEqual(FileSystemFaultKind.Io, failure.Kind);
                Assert.IsTrue(Directory.Exists(nested));
            }
            WaitForKernelController(control + ".released");
            AtomicPublisher.EnsureDirectory(root, nested);
            Assert.IsTrue(Directory.Exists(nested));
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    private static string RequireKernelRetryControl()
    {
        var control = Environment.GetEnvironmentVariable("HVO_ISSUE587_KERNEL_RETRY_CONTROL");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(control))
            Assert.Inconclusive("Requires the scoped strace retry-fault controller.");
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "hvo-issue587-retry-"), control);
        return control;
    }

    private static void ArmKernelRetryFault(string control, string directory)
    {
        File.WriteAllText(control + ".ready", System.Text.Json.JsonSerializer.Serialize(new { pid = Environment.ProcessId, directory }));
        WaitForKernelController(control + ".attached");
    }

    private static void WaitForKernelController(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!File.Exists(path) && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.IsTrue(File.Exists(path), "The external scoped fault controller did not reach " + Path.GetExtension(path));
    }
}
