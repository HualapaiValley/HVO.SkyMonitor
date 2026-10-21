namespace HVO.SkyMonitor.Deployment;

internal static class Redaction
{
    private const int MaximumDiagnosticLines = 200;

    // A credential key is any key that ends with one of these words, so the key patterns put no word boundary before it:
    // db_password, client_secret, accessToken and x-api-key all match (#1491). The boundary after it keeps tokenid ordinary.
    private const string CredentialWord = "(?:password|token|secret|api[-_]?key)\\b";

    private static readonly System.Text.RegularExpressions.Regex QuotedCredential = new(
        $"""(?i)({CredentialWord}["']?\s*[=: ]\s*)["']""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    public static string SafeDiagnostic(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "no diagnostic output";
        }

        var firstLine = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        // Decoration has no place in output that is escaped anyway, so it is removed before anything is matched (#1491).
        firstLine = WithoutControlSequences(firstLine);
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
            "(?i)([\\\"']?" + CredentialWord + "[\\\"']?\\s*[=: ]\\s*\\\")[^\\\"]*(\\\")",
            "$1<redacted>$2",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        firstLine = System.Text.RegularExpressions.Regex.Replace(
            firstLine,
            "(?i)([\\\"']?" + CredentialWord + "[\\\"']?\\s*[=: ]\\s*')[^']*(')",
            "$1<redacted>$2",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        firstLine = System.Text.RegularExpressions.Regex.Replace(
            firstLine,
            "(?i)([\\\"']?(?:authorization|" + CredentialWord + ")[\\\"']?\\s*[=:]\\s*[\\\"']?)[^\\\"',;\\s}]+",
            "$1<redacted>",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        // A key separated from its value by a space alone must be the credential word itself; a longer word stays prose.
        firstLine = System.Text.RegularExpressions.Regex.Replace(
            firstLine,
            "(?i)(\\b" + CredentialWord + "\\s+)[^ ,;]+",
            "$1<redacted>",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return firstLine.Length <= 256 ? firstLine : firstLine[..256];
    }

    /// <summary>
    /// Removes every ANSI and C1 control sequence, 7-bit and 8-bit alike: CSI with its parameters, OSC, DCS, SOS, PM and APC
    /// with their strings, every other escape, and every lone C1 control (#1491). One forward pass. A string that is never
    /// terminated runs to the end of the text, as it would on a terminal.
    /// </summary>
    internal static string WithoutControlSequences(string value)
    {
        var position = 0;
        while (position < value.Length && !IsControlIntroducer(value[position])) position++;
        if (position == value.Length) return value;
        var result = new System.Text.StringBuilder(value.Length).Append(value.AsSpan(0, position));
        while (position < value.Length)
        {
            var character = value[position];
            if (!IsControlIntroducer(character))
            {
                result.Append(character);
                position++;
                continue;
            }
            // The C1 control the sequence starts with: an 8-bit one as it is, or ESC and @ to _, its 7-bit form.
            int control;
            if (character != '\u001B') control = character;
            else if (position + 1 < value.Length && value[position + 1] is >= '@' and <= '_') control = value[++position] + 0x40;
            else
            {
                // Any other escape: ESC, its intermediates, then its final character.
                position++;
                while (position < value.Length && value[position] is >= ' ' and <= '/') position++;
                if (position < value.Length && value[position] is >= '0' and <= '~') position++;
                continue;
            }
            position++;
            if (control == 0x9B)
            {
                while (position < value.Length && value[position] is >= ' ' and <= '?') position++;
                if (position < value.Length && value[position] is >= '@' and <= '~') position++;
            }
            else if (control is 0x90 or 0x98 or 0x9D or 0x9E or 0x9F)
            {
                // The string ends at ST, 7-bit or 8-bit, or at BEL, which terminals also accept for OSC. Any other ESC
                // ends it too, and starts the next sequence.
                while (position < value.Length && value[position] is not ('\u0007' or '\u009C' or '\u001B')) position++;
                if (position < value.Length && value[position] != '\u001B') position++;
                else if (position + 1 < value.Length && value[position + 1] == '\\') position += 2;
            }
        }
        return result.ToString();
    }

    private static bool IsControlIntroducer(char character) => character is '\u001B' or (>= '\u0080' and <= '\u009F');

    /// <summary>
    /// Redacts a quoted credential value, consuming escaped/doubled quotes as content, or an unterminated value through the
    /// end of the diagnostic. The value's quotes are kept.
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
