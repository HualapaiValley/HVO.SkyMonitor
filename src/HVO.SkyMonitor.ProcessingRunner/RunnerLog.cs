using System.Text.Json;

namespace HVO.SkyMonitor.ProcessingRunner;

/// <summary>Structured JSON log lines on stdout/stderr; the runner has no other log sink by design.</summary>
internal sealed class RunnerLog(TextWriter output, TextWriter error, string? runnerId)
{
    private readonly Lock _gate = new();

    public static RunnerLog Console(string? runnerId) => new(System.Console.Out, System.Console.Error, runnerId);

    public void Info(string eventName, string message, Guid? jobId = null) => Write("info", eventName, message, jobId);

    public void Warning(string eventName, string message, Guid? jobId = null) => Write("warning", eventName, message, jobId);

    public void Error(string eventName, string message, Guid? jobId = null) => Write("error", eventName, message, jobId);

    private void Write(string level, string eventName, string message, Guid? jobId)
    {
        var entry = new RunnerLogEvent(
            DateTimeOffset.UtcNow, level, eventName, message, runnerId, jobId, Environment.ProcessId);
        var json = JsonSerializer.Serialize(entry, RunnerJsonContext.Default.RunnerLogEvent);
        lock (_gate)
        {
            (string.Equals(level, "error", StringComparison.Ordinal) ? error : output).WriteLine(json);
        }
    }
}
