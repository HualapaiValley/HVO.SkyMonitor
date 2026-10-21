namespace HVO.SkyMonitor.Deployment;

internal static class Redaction
{
    private const int MaximumDiagnosticLines = 200;
    private static readonly System.Text.RegularExpressions.Regex DecoratedQuotedCredential = new(
        """(?i)(\b(?:password|token|secret)\b["']?\s*[=: ]\s*)(?:(?:\x1B\[|\x9B)[0-?]*[ -/]*[@-~]\s*)+["']""",
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
        firstLine = RedactDecoratedQuotedCredentials(firstLine);
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

    /// <summary>Consumes escaped/doubled quotes as content, or an unterminated value through the end of the diagnostic.</summary>
    private static string RedactDecoratedQuotedCredentials(string value)
    {
        // Both the nonbacktracking prefix search and the forward-only value scan are linear in the input length.
        var match = DecoratedQuotedCredential.Match(value);
        if (!match.Success) return value;
        var result = new System.Text.StringBuilder();
        var consumed = 0;
        while (match.Success)
        {
            result.Append(value.AsSpan(consumed, match.Index - consumed));
            result.Append(match.Groups[1].Value).Append("<redacted>");
            var quote = value[match.Index + match.Length - 1];
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
                    else break;
                }
            }
            consumed = position;
            match = DecoratedQuotedCredential.Match(value, consumed);
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
