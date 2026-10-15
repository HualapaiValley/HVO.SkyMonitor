using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;

internal sealed class ExecutionEvidencePerformanceCounters : IDisposable, IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>
{
    private readonly ConcurrentBag<IDisposable> _subscriptions = [];
    private readonly IDisposable _listeners;
    private readonly string _database;
    private long _commands;
    private long _evidenceCommands;
    private long _peakRss;
    private readonly Timer _sampler;

    internal ExecutionEvidencePerformanceCounters(string connectionString)
    {
        _database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        _listeners = DiagnosticListener.AllListeners.Subscribe(this);
        _sampler = new Timer(_ => SampleRss(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
    }

    internal ResourceSnapshot Snapshot()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var io = ReadKeyValues("/proc/self/io");
        return new(process.TotalProcessorTime.TotalMilliseconds, process.WorkingSet64,
            Interlocked.Read(ref _peakRss), GC.GetTotalAllocatedBytes(precise: true),
            io.GetValueOrDefault("read_bytes"), io.GetValueOrDefault("write_bytes"),
            io.GetValueOrDefault("rchar"), io.GetValueOrDefault("wchar"),
            Interlocked.Read(ref _commands), Interlocked.Read(ref _evidenceCommands));
    }

    internal ResourceSnapshot BeginMeasurement()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        Interlocked.Exchange(ref _peakRss, process.WorkingSet64);
        return Snapshot();
    }

    private void SampleRss()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var current = process.WorkingSet64;
        var previous = Interlocked.Read(ref _peakRss);
        while (previous < current)
        {
            var observed = Interlocked.CompareExchange(ref _peakRss, current, previous);
            if (observed == previous) { break; }
            previous = observed;
        }
    }

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == "SqlClientDiagnosticListener")
        {
            _subscriptions.Add(value.Subscribe(this,
                static name => name == "Microsoft.Data.SqlClient.WriteCommandBefore"));
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Value?.GetType().GetProperty("Command")?.GetValue(value.Value) is not DbCommand command ||
            command.Connection?.Database != _database) { return; }
        Interlocked.Increment(ref _commands);
        if (new SqlConnectionStringBuilder(command.Connection.ConnectionString).ApplicationName == "HVO.ExecutionEvidence")
        {
            Interlocked.Increment(ref _evidenceCommands);
        }
    }
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose()
    {
        _sampler.Dispose();
        _listeners.Dispose();
        foreach (var subscription in _subscriptions) { subscription.Dispose(); }
    }

    internal static Dictionary<string, long> ReadKeyValues(string path)
        => File.Exists(path) ? File.ReadLines(path).Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && long.TryParse(parts[1].Trim(), out _))
            .ToDictionary(parts => parts[0], parts => long.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                StringComparer.Ordinal) : [];

    internal static string Run(string executable, string root, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments) { process.StartInfo.ArgumentList.Add(argument); }
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{executable} exceeded the evidence command deadline.");
        }
        Assert.AreEqual(0, process.ExitCode, error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult().Trim();
    }

    internal static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            root = root.Parent;
        }
        return root?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    internal static SourceEvidence Source(string root)
    {
        var head = Run("git", root, "rev-parse", "HEAD");
        var status = Run("git", root, "status", "--porcelain", "--untracked-files=all");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(Run("git", root, "diff", "HEAD", "--binary")));
        var untracked = Run("git", root, "ls-files", "--others", "--exclude-standard")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);
        foreach (var path in untracked)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData(File.ReadAllBytes(Path.Combine(root, path)));
        }
        return new(head, Run("git", root, "branch", "--show-current"), status.Length != 0,
            Convert.ToHexString(hash.GetHashAndReset()));
    }
}

internal sealed record SourceEvidence(string Sha, string Branch, bool Dirty, string DirtyFingerprintSha256);

internal sealed record ResourceSnapshot(
    double CpuMilliseconds, long RssBytes, long PeakSampledRssBytes, long AllocatedBytes,
    long ReadBytes, long WriteBytes, long ReadCharacters, long WriteCharacters, long SqlCommands, long EvidenceSqlCommands)
{
    internal object Delta(ResourceSnapshot after) => new
    {
        CpuMilliseconds = after.CpuMilliseconds - CpuMilliseconds,
        AllocatedBytes = after.AllocatedBytes - AllocatedBytes,
        RssStartBytes = RssBytes,
        RssEndBytes = after.RssBytes,
        after.PeakSampledRssBytes,
        PhysicalReadBytes = after.ReadBytes - ReadBytes,
        PhysicalWriteBytes = after.WriteBytes - WriteBytes,
        LogicalReadCharacters = after.ReadCharacters - ReadCharacters,
        LogicalWriteCharacters = after.WriteCharacters - WriteCharacters,
        SqlCommands = after.SqlCommands - SqlCommands,
        EvidenceSqlCommands = after.EvidenceSqlCommands - EvidenceSqlCommands
    };
}

internal sealed record LatencyEvidence(int Samples, double Minimum, double Median, double P95, double Maximum)
{
    internal static LatencyEvidence From(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        Assert.IsGreaterThanOrEqualTo(30, sorted.Length, "Do not infer p95 from fewer than thirty measured operations.");
        return new(sorted.Length, sorted[0], sorted[(int)Math.Ceiling(sorted.Length * .5) - 1],
            sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], sorted[^1]);
    }
}
