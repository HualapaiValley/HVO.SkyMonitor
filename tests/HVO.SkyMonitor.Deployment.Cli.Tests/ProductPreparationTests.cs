using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Product-level catalog operations against a shared product root. Production roots (the default and a host-policy
/// root alike) are <c>root:root 0755</c>, so the runtime user can neither re-mode them nor create beneath them. Without
/// root, these cases reproduce that denial with a runtime-owned root the user cannot write (mode 0555): creating beneath
/// it fails exactly as it does beneath a root-owned namespace, and the simulated helper is the only party that writes.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ProductPreparationTests
{
    private const UnixFileMode SharedReadOnly =
        UnixFileMode.UserRead | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode SharedWritable = SharedReadOnly | UnixFileMode.UserWrite;

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PrepareProductAsync_FreshTestRoot_CreatesWhatCatalogOperationsWriteWithoutPrivilege()
    {
        var scratch = Scratch();
        var paths = InstallationPaths.Create(Path.Combine(scratch, "product"), Guid.Empty, HygV42.CatalogId);
        try
        {
            await PrivilegedPreparation.PrepareProductAsync(
                paths, NativeLinux.getuid(), NativeLinux.getgid(), new HelperRunner(paths, _ => Assert.Fail("Escalated without a denial.")), CancellationToken.None);

            Assert.IsTrue(Directory.Exists(paths.OperationsRoot));
            foreach (var catalog in paths.ForEveryApprovedCatalog())
            {
                Assert.IsTrue(Directory.Exists(catalog.CatalogRoot), catalog.CatalogRoot);
            }
        }
        finally
        {
            Remove(scratch);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PrepareProductAsync_SharedRootDeniedToTheRuntimeUser_IsNeverRemodedAndEscalatesThroughTheProductHelper()
    {
        if (NativeLinux.getuid() == 0) Assert.Inconclusive("The kernel denial is observable only from a non-root test user.");
        var scratch = Scratch();
        var paths = SharedLayout(scratch);
        var uid = NativeLinux.getuid();
        var gid = NativeLinux.getgid();
        try
        {
            var denied = new HelperRunner(paths, _ => { }) { Result = new ProcessResult(1, string.Empty, "sudo: sorry, user may not run this") };
            var failed = await Assert.ThrowsExactlyAsync<InstallerException>(
                () => PrivilegedPreparation.PrepareProductAsync(paths, uid, gid, denied, CancellationToken.None));
            StringAssert.Contains(failed.Message, "Privileged product-root preparation failed: sudo: sorry", StringComparison.Ordinal);
            Assert.AreEqual(SharedReadOnly, File.GetUnixFileMode(paths.ProductRoot));

            var helper = new HelperRunner(paths, SimulateProductHelper);
            await PrivilegedPreparation.PrepareProductAsync(paths, uid, gid, helper, CancellationToken.None);

            string[] expected =
            [
                "--", $"/proc/{Environment.ProcessId}/exe", "internal-prepare", "--product-root", paths.ProductRoot,
                "--uid", uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--gid", gid.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ];
            CollectionAssert.AreEqual(expected, helper.Invocations.Single().ToArray());
            Assert.AreEqual(SharedReadOnly, File.GetUnixFileMode(paths.ProductRoot));
            Assert.AreEqual(SharedReadOnly, File.GetUnixFileMode(Path.Combine(paths.ProductRoot, "catalogs")));
            Assert.IsTrue(Directory.Exists(paths.OperationsRoot));
            Assert.IsFalse(Directory.Exists(Path.Combine(paths.ProductRoot, "cameraagents")), "Product preparation created an instance namespace entry.");

            // Once prepared, the shared root needs no privilege and is still never re-moded.
            await PrivilegedPreparation.PrepareProductAsync(
                paths, uid, gid, new HelperRunner(paths, _ => Assert.Fail("Escalated for a prepared root.")), CancellationToken.None);
            Assert.AreEqual(SharedReadOnly, File.GetUnixFileMode(paths.ProductRoot));
        }
        finally
        {
            Remove(scratch);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CatalogInstall_SharedRootDeniedToTheRuntimeUser_PreparesThroughTheHelperAndInstalls()
    {
        if (NativeLinux.getuid() == 0) Assert.Inconclusive("The kernel denial is observable only from a non-root test user.");
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the catalog install contract.");
        }
        var scratch = Scratch();
        var paths = SharedLayout(scratch);
        var helper = new HelperRunner(paths, SimulateProductHelper);
        var request = new LifecycleRequest(
            LifecycleOperationKind.CatalogInstall, null, paths.ProductRoot, dryRun: false, resume: false, json: false)
        {
            AllowTestProductRoot = true,
            CatalogBundle = bundle
        };
        try
        {
            var result = await CameraAgentLifecycleManager.ExecuteAsync(
                request, helper, null, null, NativeLinux.getuid(), NativeLinux.getgid(), CancellationToken.None);

            Assert.AreEqual("completed", result.Outcome);
            Assert.AreEqual(HygV42.PackageVersion, result.Catalog!.PackageVersion);
            Assert.IsTrue(Directory.Exists(Path.Combine(paths.CatalogRoot, "versions", HygV42.PackageVersion)));
            Assert.HasCount(1, helper.Invocations);
            Assert.AreEqual(SharedReadOnly, File.GetUnixFileMode(paths.ProductRoot));
            Assert.AreEqual(SharedReadOnly, File.GetUnixFileMode(Path.Combine(paths.ProductRoot, "catalogs")));
        }
        finally
        {
            Remove(scratch);
        }
    }

    /// <summary>A runtime-owned root and catalogs namespace the runtime user cannot write, with nothing prepared.</summary>
    private static InstallationPaths SharedLayout(string scratch)
    {
        var root = Path.Combine(scratch, "product");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, SharedReadOnly);
        return InstallationPaths.Create(root, Guid.Empty, HygV42.CatalogId);
    }

    /// <summary>
    /// What the root helper's product form does: create the shared catalogs namespace and the runtime-owned operations
    /// and approved catalog roots, never an instance, and leave the shared directories as they were.
    /// </summary>
    private static void SimulateProductHelper(InstallationPaths paths)
    {
        var catalogs = Path.Combine(paths.ProductRoot, "catalogs");
        File.SetUnixFileMode(paths.ProductRoot, SharedWritable);
        try
        {
            if (!Directory.Exists(catalogs))
            {
                Directory.CreateDirectory(catalogs);
            }
            File.SetUnixFileMode(catalogs, SharedWritable);
            foreach (var directory in paths.ForEveryApprovedCatalog().Select(static catalog => catalog.CatalogRoot).Append(paths.OperationsRoot))
            {
                SafeFileSystem.CreateOwnerDirectory(directory);
            }
        }
        finally
        {
            File.SetUnixFileMode(catalogs, SharedReadOnly);
            File.SetUnixFileMode(paths.ProductRoot, SharedReadOnly);
        }
    }

    private static string Scratch()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"hvo-product-preparation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        return scratch;
    }

    private static void Remove(string scratch)
    {
        foreach (var directory in Directory.EnumerateDirectories(scratch, "*", SearchOption.AllDirectories).Prepend(scratch))
        {
            if (new DirectoryInfo(directory).LinkTarget is null)
            {
                File.SetUnixFileMode(directory, SharedWritable);
            }
        }
        SafeFileSystem.MakeTreeOwnerWritable(scratch);
        Directory.Delete(scratch, recursive: true);
    }

    /// <summary>Answers only the privileged preparation handoff, applying <paramref name="helper"/> as the root helper would.</summary>
    private sealed class HelperRunner(InstallationPaths paths, Action<InstallationPaths> helper) : IProcessRunner
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        public ProcessResult Result { get; init; } = new(0, string.Empty, string.Empty);

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Assert.AreEqual("sudo", fileName, $"Unexpected process: {fileName} {string.Join(' ', arguments)}");
            Invocations.Add(arguments);
            if (Result.ExitCode == 0)
            {
                helper(paths);
            }
            return Task.FromResult(Result);
        }
    }
}
