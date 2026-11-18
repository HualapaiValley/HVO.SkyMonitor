using System.Diagnostics;
using System.Globalization;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

/// <summary>Observe only sandbox descendants of this testhost; never assert host-wide quiescence.</summary>
internal sealed class OwnedSandboxScope
{
    private readonly HashSet<string> _scopes = new(StringComparer.Ordinal);

    internal async Task WaitForSleepAsync(Task execution)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            Capture();
            foreach (var scope in _scopes)
            {
                foreach (var pid in Processes(scope))
                {
                    try
                    {
                        // BusyBox can execute sleep as an in-process ash applet, retaining comm=sh.
                        if ((await File.ReadAllTextAsync($"/proc/{pid}/wchan").ConfigureAwait(false)).Contains("nanosleep", StringComparison.Ordinal)) return;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
            Assert.IsFalse(execution.IsCompleted, $"This test's sandbox must reach sleep before its execution completes (observed {_scopes.Count} owned scopes).");
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert.Fail("This test's sandbox did not reach sleep within the qualification deadline.");
    }

    internal void AssertDrained()
    {
        Assert.IsNotEmpty(_scopes, "The test must observe its own launch before checking cleanup.");
        foreach (var scope in _scopes)
            Assert.IsEmpty(Processes(scope), "Only this test's observed cgroup may be checked for surviving processes.");
    }

    private void Capture()
    {
        var parents = new Dictionary<int, int>();
        foreach (var path in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(path), CultureInfo.InvariantCulture, out var pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(path, "stat"));
                parents[pid] = int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1], CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        foreach (var pid in parents.Keys)
        {
            var parent = pid;
            var visited = new HashSet<int>();
            while (parent != Environment.ProcessId && visited.Add(parent) && parents.TryGetValue(parent, out var next)) parent = next;
            if (parent != Environment.ProcessId || pid == Environment.ProcessId) continue;
            try
            {
                var membership = File.ReadLines($"/proc/{pid}/cgroup").SingleOrDefault(static line => line.StartsWith("0::/", StringComparison.Ordinal));
                if (membership is not null && Path.GetFileName(membership).StartsWith("hvo-custom-script-", StringComparison.Ordinal))
                    _scopes.Add("/sys/fs/cgroup" + membership[3..]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string[] Processes(string scope)
    {
        try { return File.ReadAllLines(Path.Combine(scope, "cgroup.procs")); }
        catch (DirectoryNotFoundException) { return []; }
        catch (FileNotFoundException) { return []; }
    }
}
