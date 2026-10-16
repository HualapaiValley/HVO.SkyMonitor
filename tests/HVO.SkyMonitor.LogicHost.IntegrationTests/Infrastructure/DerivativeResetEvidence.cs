using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

/// <summary>Closed projections only: SQL text, literals, names and exception messages never leave the collector.</summary>
internal static class DerivativeResetEvidence
{
    internal const int MaximumSamples = 90;
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static async Task SampleAsync(Func<CancellationToken, Task> sample, Func<bool> finished,
        TimeProvider time, CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var start = time.GetTimestamp();
        for (var index = 0; index < MaximumSamples && time.GetElapsedTime(start) < Window; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sample(cancellationToken).ConfigureAwait(false);
            if (finished())
            {
                return;
            }
            // Delay after completion: a slow sample never causes catch-up bursts.
            await (delay is null ? Task.Delay(Interval, time, cancellationToken) : delay(Interval, cancellationToken)).ConfigureAwait(false);
        }
    }

    internal static XElement ProjectPlan(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024
        });
        var source = XDocument.Load(reader);
        var nodes = source.Descendants().Where(node => node.Name.LocalName == "RelOp").Take(512);
        return new XElement("CachedEstimatedPlanProjection",
            new XAttribute("sourceSha256", Hash(xml)),
            new XAttribute("actualRuntimeStatistics", false),
            nodes.Select(node => new XElement("Operator",
                new XAttribute("depth", node.Ancestors().Count(parent => parent.Name.LocalName == "RelOp")),
                node.Attributes().Where(attribute => attribute.Name.LocalName is "NodeId" or "EstimateRows" or "EstimatedTotalSubtreeCost"
                    or "EstimateCPU" or "EstimateIO" or "AvgRowSize")
                    .Where(attribute => double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                    .Select(attribute => new XAttribute(attribute.Name.LocalName, attribute.Value)),
                new XAttribute("physicalOp", SafeOperator((string?)node.Attribute("PhysicalOp"))))));
    }

    private static string SafeOperator(string? value) => value switch
    {
        "Clustered Index Scan" or "Clustered Index Seek" or "Index Scan" or "Index Seek" or "Table Scan"
            or "Nested Loops" or "Hash Match" or "Merge Join" or "Sort" or "Filter" or "Compute Scalar"
            or "Stream Aggregate" or "Table Spool" or "Index Spool" or "Sequence" or "Assert"
            or "Clustered Index Update" or "Table Update" or "Index Update" or "Parallelism"
            or "Constant Scan" or "Concatenation" or "Top" => value,
        _ => "Other"
    };

    internal static string NumericLines(string input)
    {
        // Accept proc/cgroup numeric counters only, never arbitrary process/container output.
        return string.Join('\n', input.Split('\n').Take(256).Where(line => line.Length <= 256)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 1 && AllowedCounter(parts[0])
                && parts.Skip(1).All(part => part.All(character => char.IsAsciiDigit(character) || character is ':' or '=' or '.' or '-'
                    || character is 'k' or 'B' or 'r' or 'w' or 'b' or 'y' or 't' or 'e' or 's' or 'i' or 'o' or 'd')))
            .Select(parts => string.Join(' ', parts)));
    }

    private static bool AllowedCounter(string key) => key is "cpu" or "ctxt" or "processes" or "procs_running" or "procs_blocked"
        or "MemTotal:" or "MemAvailable:" or "SwapTotal:" or "SwapFree:" or "Dirty:" or "Writeback:"
        or "pgpgin" or "pgpgout" or "pswpin" or "pswpout" or "usage_usec" or "user_usec" or "system_usec"
        or "nr_periods" or "nr_throttled" or "throttled_usec" or "memory.current" or "memory.swap.current"
        or "low" or "high" or "max" or "oom" or "oom_kill"
        || key.Length <= 16 && key.Contains(':', StringComparison.Ordinal) && key.All(character => char.IsAsciiDigit(character) || character == ':');

    internal static object HostSnapshot() => new
    {
        utc = DateTimeOffset.UtcNow,
        counters = NumericLines(string.Join('\n', new[] { "/proc/stat", "/proc/meminfo", "/proc/vmstat" }.Select(File.ReadAllText))),
        // PID/counters identify competing processes without exposing argv, users or environment.
        processes = ReadProcesses()
    };

    private static object?[] ReadProcesses()
    {
        var processes = Process.GetProcesses();
        try { return processes.Take(128).Select(ReadProcess).Where(value => value is not null).ToArray(); }
        finally { foreach (var process in processes) { process.Dispose(); } }
    }

    private static object? ReadProcess(Process process)
    {
        using (process)
        {
            try
            {
                return new { pid = process.Id, cpuMs = process.TotalProcessorTime.TotalMilliseconds, residentBytes = process.WorkingSet64 };
            }
            catch (InvalidOperationException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }
    }
}
