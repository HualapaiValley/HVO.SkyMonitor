namespace HVO.SkyMonitor.Deployment;

internal static class Redaction
{
    private const int MaximumDiagnosticLines = 200;
    private static readonly System.Text.RegularExpressions.Regex QuotedCredential = new(
        """(?i)(\b(?:password|token|secret)\b["']?\s*[=: ]\s*)(?:(?:\x1B\[|\x9B)[0-?]*[ -/]*[@-~]\s*)*["']""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    public static string SafeDiagnostic(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "no diagnostic output";
        }

        var firstLine = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        firstLine = System.Text.RegularExpressions.Regex.Replace(
            firstLine,
            "(?<=://)[^/@\\s]+@",
            "<redacted>@",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        firstLine = System.Text.RegularExpressions.Regex.Replace(
            firstLine,
            "(?i)([\\\"']?\\bauthorization\\b[\\\"']?\\s*[=:]\\s*[\\\"']?)[^\\\"',;}]+",
            "$1<redacted>",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        firstLine = System.Text.RegularExpressions.Regex.Replace(
            firstLine,
            "(?i)(\\bbearer\\s+)[^,;\\s\\\"]+",
            "$1<redacted>",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        firstLine = RedactQuotedCredentials(firstLine);
        firstLine = System.Text.RegularExpressions.Regex.Replace(
            firstLine,
            "(?i)([\\\"']?\\b(?:password|token|secret)\\b[\\\"']?\\s*[=: ]\\s*\\\")[^\\\"]*(\\\")",
            "$1<redacted>$2",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        firstLine = System.Text.RegularExpressions.Regex.Replace(
            firstLine,
            "(?i)([\\\"']?\\b(?:password|token|secret)\\b[\\\"']?\\s*[=: ]\\s*')[^']*(')",
            "$1<redacted>$2",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        firstLine = System.Text.RegularExpressions.Regex.Replace(
            firstLine,
            "(?i)([\\\"']?(?:authorization|password|token|secret)[\\\"']?\\s*[=:]\\s*[\\\"']?)[^\\\"',;\\s}]+",
            "$1<redacted>",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        firstLine = System.Text.RegularExpressions.Regex.Replace(
            firstLine,
            "(?i)(\\b(?:password|token|secret)\\b\\s+)[^ ,;]+",
            "$1<redacted>",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return firstLine.Length <= 256 ? firstLine : firstLine[..256];
    }

    /// <summary>
    /// Redacts a quoted credential value, plain or behind ANSI decoration, consuming escaped/doubled quotes as content, or an
    /// unterminated value through the end of the diagnostic. The value's quotes are kept and its decoration is dropped.
    /// </summary>
    private static string RedactQuotedCredentials(string value)
    {
        // Both the nonbacktracking prefix search and the forward-only value scan are linear in the input length.
        var match = QuotedCredential.Match(value);
        if (!match.Success) return value;
        var result = new System.Text.StringBuilder();
        var consumed = 0;
        while (match.Success)
        {
            result.Append(value.AsSpan(consumed, match.Index - consumed));
            var quote = value[match.Index + match.Length - 1];
            result.Append(match.Groups[1].Value).Append(quote).Append("<redacted>");
            var position = match.Index + match.Length;
            while (position < value.Length)
            {
                var character = value[position++];
                if (character == '\\')
                {
                    if (position < value.Length) position++;
                }
                else if (character == quote)
                {
                    if (position < value.Length && value[position] == quote) position++;
                    else
                    {
                        result.Append(quote);
                        break;
                    }
                }
            }
            consumed = position;
            match = QuotedCredential.Match(value, consumed);
        }
        return result.Append(value.AsSpan(consumed)).ToString();
    }

    public static string SafeDiagnostics(string value)
    {
        var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0
            ? SafeDiagnostic(value)
            : string.Join(Environment.NewLine, lines.TakeLast(MaximumDiagnosticLines).Select(SafeDiagnostic));
    }
}
