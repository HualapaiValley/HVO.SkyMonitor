using System.Text.Json;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Formats untrusted state diagnostics at the output boundary, regardless of the failure's source.</summary>
internal static class CameraAgentStateDiagnostics
{
    /// <summary>Redacts before escaping so quotes and controls cannot hide credentials from the redactor.</summary>
    public static string Reason(string value)
        => JsonEncodedText.Encode(Redaction.SafeDiagnostic(value)).ToString();

    /// <summary>Counter identities are already escaped by <see cref="CameraAgentStateCounters.Describe"/>.</summary>
    public static string CarryFailures(IReadOnlyList<(string What, string Reason)> failures)
        => string.Join("; ", failures.Select(static entry => $"{entry.What} ({Reason(entry.Reason)})"));
}
