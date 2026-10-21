using System.Text.Json;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Formats untrusted state diagnostics at the output boundary, regardless of the failure's source.</summary>
internal static class CameraAgentStateDiagnostics
{
    /// <summary>Redacts before escaping so quotes and controls cannot hide credentials from the redactor.</summary>
    public static string Reason(string value)
        => JsonEncodedText.Encode(Redaction.SafeDiagnostic(value)).ToString();

    /// <summary>Database fields are formatted separately from the fixed explanatory prose around them.</summary>
    public static string Field(string? value) => string.IsNullOrEmpty(value) ? string.Empty : Reason(value);

    /// <summary>Explicitly formatted diagnostics retain their encoding; every other exception message is a raw fragment.</summary>
    public static string Failure(Exception exception)
        => exception is InstallerException { IsFormattedDiagnostic: true } ? exception.Message : Reason(exception.Message);

    /// <summary>Counter identities are already escaped by <see cref="CameraAgentStateCounters.Describe"/>.</summary>
    public static string CarryFailures(IReadOnlyList<(string What, string Reason)> failures)
        => string.Join("; ", failures.Select(static entry => $"{entry.What} ({Reason(entry.Reason)})"));
}
