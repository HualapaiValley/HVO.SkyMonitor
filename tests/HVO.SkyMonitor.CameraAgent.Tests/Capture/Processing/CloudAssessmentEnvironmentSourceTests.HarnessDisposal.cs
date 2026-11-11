// Disposal hygiene of the production-composition harness. Refs #1785
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class CloudAssessmentEnvironmentSourceTests
{
    /// <summary>
    /// Disposing the harness releases the pooled SQLite handles under its root and removes the only directory it
    /// created, also when the provider's own disposal fails.
    /// </summary>
    [TestMethod]
    // SqliteConnection pools are process-wide: a sibling's ClearAllPools would empty the control below.
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HarnessDispose_ReleasesPooledSqliteHandlesAndRemovesItsOnlyDirectory(bool providerDisposalFails)
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var harness = Harness.Create(
            "nws-only",
            providerDisposalFails
                ? static services => services.AddSingleton(static _ => new FailingDisposable())
                : null);
        var root = harness.Root;
        InvalidOperationException? expectedFailure = null;
        InvalidOperationException? disposalFailure = null;
        try
        {
            if (providerDisposalFails)
            {
                // Created first, so it is disposed last: every other service is released before disposal fails.
                expectedFailure = harness.Provider.GetRequiredService<FailingDisposable>().Failure;
            }

            // The production composition opens its pooled durable stores under the root.
            await harness.Provider.GetRequiredService<IRawCaptureIngress>()
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            // A pooled connection the test owns: disposing it returns it to the pool, which keeps its file handle.
            var probe = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "pool-probe.db"),
                Pooling = true
            };
            using (var connection = new SqliteConnection(probe.ToString()))
            {
                await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (OperatingSystem.IsLinux())
            {
                Assert.IsNotEmpty(
                    OpenHandlesUnder(root), "Control: an idle pooled connection keeps a handle under the root.");
            }
        }
        finally
        {
            try
            {
                harness.Dispose();
            }
            catch (InvalidOperationException exception)
            {
                disposalFailure = exception;
            }
        }

        // The provider's own failure still reaches the caller, and nothing else fails.
        Assert.AreSame(expectedFailure, disposalFailure, disposalFailure?.ToString());
        Assert.IsFalse(Directory.Exists(root), "The harness root is deleted.");
        if (OperatingSystem.IsLinux())
        {
            var open = OpenHandlesUnder(root);
            Assert.IsEmpty(open, $"Pooled SQLite handles survived disposal: {string.Join(", ", open)}");
        }

        // The root is the only directory the harness creates. It sits directly under the temp root, so deleting
        // it leaves no parent directory (the former shared "ci1777-cloud") behind.
        Assert.AreEqual(
            tempRoot,
            Path.GetDirectoryName(Path.GetFullPath(root)),
            "The harness root sits directly under the temp root; no parent directory is created or left behind.");

        // A repeated dispose is a no-op: it neither throws nor recreates anything.
        harness.Dispose();
        Assert.IsFalse(Directory.Exists(root));
        Record($"HARNESS-DISPOSE providerDisposalFails={providerDisposalFails} root={root} " +
            $"handleProbe={(OperatingSystem.IsLinux() ? "linux-proc-fd" : "not-available")}");
    }

    /// <summary>The open descriptors of this process that still point under <paramref name="root"/> (Linux).</summary>
    private static string[] OpenHandlesUnder(string root)
    {
        // The leaf name is unique, so the match also holds when the temp root is reached through a symlink.
        var marker = $"{Path.DirectorySeparatorChar}{Path.GetFileName(root)}{Path.DirectorySeparatorChar}";
        var open = new List<string>();
        foreach (var descriptor in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            string? target;
            try
            {
                target = new FileInfo(descriptor).LinkTarget;
            }
            catch (IOException)
            {
                // The descriptor went away while it was being inspected.
                continue;
            }

            if (target is not null && target.Contains(marker, StringComparison.Ordinal))
            {
                open.Add(target);
            }
        }

        return [.. open];
    }

    private sealed class FailingDisposable : IDisposable
    {
        internal InvalidOperationException Failure { get; } =
            new("Harness disposal probe: provider disposal failed.");

        public void Dispose() => throw Failure;
    }
}
