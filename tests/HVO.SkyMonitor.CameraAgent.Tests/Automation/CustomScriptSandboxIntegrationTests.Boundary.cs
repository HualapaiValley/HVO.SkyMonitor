using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

public sealed partial class CustomScriptSandboxIntegrationTests
{
    [TestMethod]
    public async Task InheritedHostFileAndDirectoryDescriptorsCannotCrossTheLaunchBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-script-fd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "directory"));
        var marker = Path.Combine(root, "marker");
        const string original = "synthetic-fd-boundary\n";
        await File.WriteAllTextAsync(marker, original).ConfigureAwait(false);
        try
        {
            using var file = File.OpenHandle(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            // F_DUPFD deliberately clears CLOEXEC. Use fds above the script's RLIMIT_NOFILE too.
            using var inheritedFile = Duplicate(file, 512);
            using var directory = new SafeFileHandle((IntPtr)OpenPath(Path.Combine(root, "directory"), 0), true);
            Assert.IsFalse(directory.IsInvalid);
            using var inheritedDirectory = Duplicate(directory, 1024);
            var fileFd = inheritedFile.DangerousGetHandle().ToInt32().ToString(CultureInfo.InvariantCulture);
            var directoryFd = inheritedDirectory.DangerousGetHandle().ToInt32().ToString(CultureInfo.InvariantCulture);
            Assert.AreEqual(original, await File.ReadAllTextAsync($"/proc/self/fd/{fileFd}").ConfigureAwait(false));
            // Use native open: .NET normalizes /fd/N/../marker before the kernel can traverse N.
            using (var traversal = new SafeFileHandle((IntPtr)OpenPath($"/proc/self/fd/{directoryFd}/../marker", 0), true))
            {
                Assert.IsFalse(traversal.IsInvalid, "Positive control: the host directory fd can traverse outside its directory.");
                var bytes = new byte[128];
                var read = await RandomAccess.ReadAsync(traversal, bytes, 0).ConfigureAwait(false);
                Assert.AreEqual(original, System.Text.Encoding.UTF8.GetString(bytes, 0, read));
            }
            Assert.AreEqual(0, Flock(inheritedFile.DangerousGetHandle().ToInt32(), 6), "Positive control: inherit a locked fixture fd too (LOCK_EX|LOCK_NB).");
            var script = CustomScriptTaskAdapterTests.Script("""
                [ ! -e /proc/self/fd/$1 ] || exit 81
                [ ! -e /proc/self/fd/$2 ] || exit 82
                if ( read -r value < /proc/self/fd/$1 ) 2>/dev/null; then exit 83; fi
                if ( printf overwritten > /proc/self/fd/$1 ) 2>/dev/null; then exit 84; fi
                if ( eval "printf overwritten >&$1" ) 2>/dev/null; then exit 85; fi
                if ( read -r value < /proc/self/fd/$2/../marker ) 2>/dev/null; then exit 86; fi
                if ( printf overwritten > /proc/self/fd/$2/../marker ) 2>/dev/null; then exit 87; fi
                printf ok
                """);
            script.Arguments = [new(CustomScriptArgumentKind.WholeNumber, fileFd), new(CustomScriptArgumentKind.WholeNumber, directoryFd)];
            var result = await LinuxCustomScriptSandbox.ExecuteAsync(script, default).ConfigureAwait(false);
            Assert.AreEqual("completed", result.Status);
            Assert.AreEqual(0, result.ExitCode, "Neither a host file fd nor a directory fd may be visible or usable.");
            Assert.AreEqual(0, Flock(inheritedFile.DangerousGetHandle().ToInt32(), 8), "Release only the fixture's lock before .NET reopens it for verification.");
            Assert.AreEqual(original, await File.ReadAllTextAsync(marker).ConfigureAwait(false));
            Assert.AreEqual(original, await File.ReadAllTextAsync($"/proc/self/fd/{fileFd}").ConfigureAwait(false));
            Assert.AreEqual(0, Fcntl(inheritedFile.DangerousGetHandle().ToInt32(), 1, 0), "Cleanup must not change the parent's fd table/flags.");
            Assert.AreEqual(2L, result.StdoutBytes, "The script must execute every descriptor assertion before its completion marker.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("background-wait", "sleep 0.05 & child=$!; wait \"$child\"; printf ok")]
    [DataRow("glob", "printf x > one; printf x > two; set -- /work/*; [ \"$#\" = 2 ] && [ \"$1\" = /work/one ] && [ \"$2\" = /work/two ] || exit 81; printf ok")]
    [DataRow("readable", "[ -r /dev/null ] || exit 82; printf ok")]
    [DataRow("touch", "touch /work/created && [ -f /work/created ] || exit 83; touch /work/created || exit 84; printf ok")]
    public async Task BasicShellFeaturesCompleteWithinTheirResourceLimits(string feature, string content)
    {
        var script = CustomScriptTaskAdapterTests.Script(content);
        script.TimeoutSeconds = 3;
        var result = await LinuxCustomScriptSandbox.ExecuteAsync(script, default).ConfigureAwait(false);
        Assert.AreEqual("completed", result.Status, feature);
        Assert.AreEqual(0, result.ExitCode, feature);
        Assert.AreEqual(2L, result.StdoutBytes, "The feature must complete its own assertions, not silently skip them.");
        Assert.AreEqual(0L, result.StderrBytes, feature);
    }

    [TestMethod]
    public async Task ScopeMetadataAndJournalDoNotContainScriptContentOrArguments()
    {
        var contentMarker = "content-marker-" + Guid.NewGuid().ToString("N");
        var argumentMarker = "argument-marker-" + Guid.NewGuid().ToString("N");
        var script = CustomScriptTaskAdapterTests.Script(": " + contentMarker + "; printf 'ready\\n'; exec sleep 30");
        script.Arguments = [new(CustomScriptArgumentKind.Text, argumentMarker)];
        using var process = new Process { StartInfo = LinuxCustomScriptSandbox.BuildStartInfo(script) };
        var unit = process.StartInfo.ArgumentList.Single(static value => value.StartsWith("--unit=", StringComparison.Ordinal))[7..] + ".scope";
        Assert.IsTrue(process.Start());
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(CustomScriptSyscallPolicy.Create()).ConfigureAwait(false);
            process.StandardInput.Close();
            Assert.AreEqual("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
            var metadata = await ReadScopeToolAsync("/usr/bin/systemctl", "--user", "show", unit).ConfigureAwait(false);
            StringAssert.Contains(metadata, "Description=" + LinuxCustomScriptSandbox.ScopeDescription, StringComparison.Ordinal);
            StringAssert.Contains(metadata, "Id=" + unit, StringComparison.Ordinal);
            var journal = await ReadScopeToolAsync("/usr/bin/journalctl", "--user", "--user-unit=" + unit,
                "--no-pager", "--output=json", "--lines=64").ConfigureAwait(false);
            StringAssert.Contains(journal, LinuxCustomScriptSandbox.ScopeDescription, StringComparison.Ordinal,
                "The journal assertion must observe this scope's actual lifecycle message.");
            foreach (var text in new[] { metadata, journal, unit })
            {
                Assert.IsFalse(text.Contains(contentMarker, StringComparison.Ordinal));
                Assert.IsFalse(text.Contains(argumentMarker, StringComparison.Ordinal));
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
        }
        var finalJournal = await ReadScopeToolAsync("/usr/bin/journalctl", "--user", "--user-unit=" + unit,
            "--no-pager", "--output=json", "--lines=64").ConfigureAwait(false);
        Assert.IsFalse(finalJournal.Contains(contentMarker, StringComparison.Ordinal));
        Assert.IsFalse(finalJournal.Contains(argumentMarker, StringComparison.Ordinal));
    }

    private static SafeFileHandle Duplicate(SafeFileHandle handle, int minimum)
    {
        var fd = Fcntl(handle.DangerousGetHandle().ToInt32(), 0, minimum); // F_DUPFD
        Assert.IsGreaterThanOrEqualTo(minimum, fd);
        var duplicate = new SafeFileHandle((IntPtr)fd, true);
        Assert.AreEqual(0, Fcntl(fd, 1, 0), "F_GETFD positive control: this host fd must NOT have FD_CLOEXEC.");
        return duplicate;
    }

    private static async Task<string> ReadScopeToolAsync(string tool, params string[] arguments)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.Environment.Clear();
        info.Environment["XDG_RUNTIME_DIR"] = LinuxCustomScriptSandbox.BuildStartInfo(CustomScriptTaskAdapterTests.Script()).Environment["XDG_RUNTIME_DIR"];
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await stderr.ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, "The scoped metadata/journal observation must execute.");
        return await stdout.ConfigureAwait(false);
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int OpenPath(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Fcntl(int fd, int command, int value);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Flock(int fd, int operation);
}
