namespace HVO.SkyMonitor.Deployment;

internal static class Redaction
{
    private const int MaximumDiagnosticLines = 200;

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

    public static string SafeDiagnostics(string value)
    {
        var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0
            ? SafeDiagnostic(value)
            : string.Join(Environment.NewLine, lines.TakeLast(MaximumDiagnosticLines).Select(SafeDiagnostic));
    }
}
