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

    /// <summary>
    /// A database-derived name and value are redacted as the one assignment they read as, then escaped. The value is the whole
    /// field, so a name the redactor treats as a credential hides all of it, not only the first word an unquoted value ends at.
    /// Decoration in the name, which the redactor removes, does not make an ordinary name read as a credential, and the decision
    /// rests on the name alone, so a sequence the name ends inside cannot reach into the value.
    /// </summary>
    public static string Assignment(string? name, string? value)
    {
        var probe = Redaction.WithoutControlSequences(name ?? string.Empty) + "=value";
        return Reason(!string.IsNullOrEmpty(value) && Redaction.SafeDiagnostic(probe) != probe ? $"{name}=<redacted>" : $"{name}={value}");
    }

    /// <summary>Explicitly formatted diagnostics retain their encoding; every other exception message is a raw fragment.</summary>
    public static string Failure(Exception exception)
        => exception is InstallerException { IsFormattedDiagnostic: true } ? exception.Message : Reason(exception.Message);

    /// <summary>An installer failure's own message, formatted as <see cref="Failure"/> does; other exceptions' text is left out.</summary>
    public static string InstallerFailure(Exception exception) => exception is InstallerException ? Failure(exception) : string.Empty;

    /// <summary>
    /// An identity, such as a counter key, escaped with every character kept. One the redactor reads as holding a credential
    /// hides it as <see cref="Assignment"/> does, because a message the formatter composed is not scanned again (#1491).
    /// </summary>
    public static string Identity(string value)
    {
        var redacted = Redaction.Redact(value);
        return JsonEncodedText.Encode(redacted == Redaction.WithoutControlSequences(value) ? value : redacted).ToString();
    }

    /// <summary>Counter identities are already redacted and escaped by <see cref="CameraAgentStateCounters.Describe"/>.</summary>
    public static string CarryFailures(IReadOnlyList<(string What, string Reason)> failures)
        => string.Join("; ", failures.Select(static entry => $"{entry.What} ({Reason(entry.Reason)})"));
}
