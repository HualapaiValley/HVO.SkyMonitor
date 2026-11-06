using System.Globalization;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>Completed tests, rather than console chatter, advance the qualification watchdog.</summary>
[TestClass]
public sealed class DeploymentShardProgress
{
    private static readonly object ProgressLock = new();

    [GlobalTestCleanup]
    public static void RecordCompletedTest(TestContext context)
    {
        _ = context;
        var path = Environment.GetEnvironmentVariable("DEPLOY_TEST_PROGRESS_FILE");
        if (string.IsNullOrEmpty(path)) return;
        lock (ProgressLock)
        {
            var previous = File.ReadAllText(path);
            var sequence = long.Parse(previous.AsSpan("sequence=".Length, previous.IndexOf(' ', StringComparison.Ordinal) - "sequence=".Length), CultureInfo.InvariantCulture) + 1;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, FormattableString.Invariant($"sequence={sequence} checkpoint=typed-test-completed\n"));
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }
}
