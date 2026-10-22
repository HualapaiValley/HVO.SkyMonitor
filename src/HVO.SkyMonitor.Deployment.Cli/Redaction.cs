namespace HVO.SkyMonitor.Deployment;

internal static class Redaction
{
    private const int MaximumDiagnosticLines = 200;

    // A credential key is any key that ends with one of these words, so the key patterns put no word boundary before it:
    // db_password, client_secret, accessToken and x-api-key all match (#1491). The boundary after it keeps tokenid ordinary.
    // A secret key's words may be joined by -, _ or nothing, so SecretKey and secret-access-key match but secretkeeper does not (#1509).
    private const string CredentialWord = "(?:passwords?|token|secret(?:[-_]?(?:access[-_]?)?key)?|private[-_]?key|api[-_]?key)\\b";

    // A quoted value follows its key after = or :, or after whitespace alone, which may be a tab or another space character
    // rather than a plain space, so the quoted passes hide every value the space-only pass would find quoted (#1509).
    private const string QuotedSeparator = "\\s*[=:\\s]\\s*";

    private const string KeyWords = CredentialWord + "|authorization|bearer";

    // Space-only assignments accept explicit snake/kebab compounds, but not arbitrary words ending in a credential word.
    private const string SpacedKey = "(?:\\b[a-z][a-z0-9]*(?:[_-][a-z][a-z0-9]*)*[_-]\\x9C?" + CredentialWord + "|\\b" + CredentialWord + ")";

    // Without a word boundary before the key, a compound key followed by a long run of spaces made the backtracking engine
    // try every way of splitting that run around its separator. Every pattern but the URL lookbehind is linear instead (#1491).
    private const System.Text.RegularExpressions.RegexOptions Linear =
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking;

    // Removed decoration leaves this in its place while text is matched when a key follows it, so a key the merge base found
    // after a removed string still starts a word (#1491). It is a C1 control, so it is removed with the others afterwards.
    private const char Boundary = '\u009C';

    private static readonly System.Text.RegularExpressions.Regex UserInformation = new(
        "(?<=://)[^/@\\s]+@", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // A CSI that is not decoration stays in the text while it is matched, so a quoted value may still follow one.
    private static readonly System.Text.RegularExpressions.Regex QuotedCredential = LinearPattern(
        $"""(?i)({CredentialWord}["']?{QuotedSeparator})(?:(?:\x1B\[|\x9B)[0-?]*[ -/]*[@-~]\s*)*["']""");

    private static readonly System.Text.RegularExpressions.Regex KeyWord = LinearPattern($"(?i){KeyWords}");

    // Anchored where decoration ends, so it reads only the few characters of a key there.
    private static readonly System.Text.RegularExpressions.Regex KeyStart = new(
        $"\\G(?i:{KeyWords})", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // No pattern starts with an optional quote. With one, the nonbacktracking engine could skip the first of two credentials
    // when a quote came earlier in the text (#1491). A quote before a key is not part of the match, so it is kept as text.
    private static readonly System.Text.RegularExpressions.Regex AuthorizationValue = LinearPattern(
        "(?i)(\\bauthorization\\b[\\\"']?\\s*[=:]\\s*[\\\"']?)[^\\\"',;}]+");

    private static readonly System.Text.RegularExpressions.Regex BearerValue = LinearPattern("(?i)(\\bbearer\\s+)[^,;\\s\\\"]+");

    private static readonly System.Text.RegularExpressions.Regex DoubleQuotedValue = LinearPattern(
        "(?i)(" + CredentialWord + "[\\\"']?" + QuotedSeparator + "\\\")[^\\\"]*(\\\")");

    private static readonly System.Text.RegularExpressions.Regex SingleQuotedValue = LinearPattern(
        "(?i)(" + CredentialWord + "[\\\"']?" + QuotedSeparator + "')[^']*(')");

    private static readonly System.Text.RegularExpressions.Regex UnquotedValue = LinearPattern(
        "(?i)((?:authorization|" + CredentialWord + ")[\\\"']?\\s*[=:]\\s*[\\\"']?)[^\\\"',;\\s}]+");

    // A key separated by space alone is a bare credential word or a compound of identifier words (letter first); ordinary prose stays text.
    private static readonly System.Text.RegularExpressions.Regex SpacedValue = LinearPattern(
        "(?i)(" + SpacedKey + "\\s+)[^ ,;]+");

    // An outer assignment can consume an inner key before its value is seen. Match the complete chain first, leaving its
    // final value hidden before the ordinary passes replace any keys. A removed-decoration boundary can sit inside a key.
    // The repeated prefix and value match are nonbacktracking.
    private static readonly System.Text.RegularExpressions.Regex NestedValue = LinearPattern(
        $"""(?i)((?:{CredentialWord}["']?\s*[=:]\s*|\bauthorization\b["']?\s*[=:]\s*|\bbearer\s+)(?:[\w\x9C-]*{CredentialWord}["']?\s*[=:]\s*|\x9C*{SpacedKey}\s+)+)[^"',;\s\x7D]+""");

    // ESC and the C1 controls, each of which may start a control sequence.
    private static readonly System.Buffers.SearchValues<char> Introducers = System.Buffers.SearchValues.Create(
        "\u001B\u0080\u0081\u0082\u0083\u0084\u0085\u0086\u0087\u0088\u0089\u008A\u008B\u008C\u008D\u008E\u008F" +
        "\u0090\u0091\u0092\u0093\u0094\u0095\u0096\u0097\u0098\u0099\u009A\u009B\u009C\u009D\u009E\u009F");

    /// <summary>
    /// Every nonbacktracking pattern. Each must return exactly the matches its backtracking form would, which a test checks.
    /// </summary>
    internal static IReadOnlyList<System.Text.RegularExpressions.Regex> LinearPatterns { get; } =
        [QuotedCredential, KeyWord, AuthorizationValue, BearerValue, DoubleQuotedValue, SingleQuotedValue, UnquotedValue, SpacedValue, NestedValue];

    public static string SafeDiagnostic(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "no diagnostic output";
        }

        var firstLine = Redact(value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty);
        return firstLine.Length <= 256 ? firstLine : firstLine[..256];
    }

    /// <summary>
    /// Redacts every credential in <paramref name="text"/>, at any length. Decoration is removed before anything is matched
    /// (#1491). Every other control character is matched where it stands, as it always was, and removed only afterwards.
    /// </summary>
    internal static string Redact(string text)
    {
        // A URL's user information is also found as the text stood, so punctuation inside removed decoration still counts.
        // Without a control character the two texts are the same, and without :// neither holds a URL.
        if (text.AsSpan().ContainsAny(Introducers)) text = WithoutUserInformation(text);
        text = WithoutUserInformation(WithoutDecoration(text));
        // Preserve inner keys until their quoted/nested values are hidden, including after a decoration boundary (#1499).
        text = RedactQuotedCredentials(text);
        text = NestedValue.Replace(text, "$1<redacted>");
        text = AuthorizationValue.Replace(text, "$1<redacted>");
        text = BearerValue.Replace(text, "$1<redacted>");
        text = DoubleQuotedValue.Replace(text, "$1<redacted>$2");
        text = SingleQuotedValue.Replace(text, "$1<redacted>$2");
        text = UnquotedValue.Replace(text, "$1<redacted>");
        text = SpacedValue.Replace(text, HideSpacedValue);
        return WithoutControls(text);
    }

    /// <summary>
    /// A value the quoted passes already hid whole keeps its quotes. Anything else the space-only pass reads as a value is
    /// hidden as before, including text joined to a quoted value's closing quote (#1509).
    /// </summary>
    private static string HideSpacedValue(System.Text.RegularExpressions.Match match)
        => match.ValueSpan[match.Groups[1].Length..] is "\"<redacted>\"" or "'<redacted>'"
            ? match.Value
            : match.Groups[1].Value + "<redacted>";

    private static string WithoutUserInformation(string text)
        => text.Contains("://", StringComparison.Ordinal) ? UserInformation.Replace(text, "<redacted>@") : text;

    /// <summary>What <see cref="Redact"/> leaves of <paramref name="value"/> when it holds no credential.</summary>
    internal static string WithoutControlSequences(string value) => WithoutControls(WithoutDecoration(value));

    /// <summary>
    /// Removes decoration, the control sequences that cannot hold the first letter of a credential's key (#1491), 7-bit and
    /// 8-bit alike: SGR (CSI, its parameters, then m), a CSI the text ends inside, and an OSC, DCS, SOS, PM or APC string with
    /// its terminator, unless the string names a credential. Any other final byte may be the first letter of a key, so every
    /// other sequence is left for matching, and only its control characters are removed afterwards. Where a key follows removed
    /// decoration, a boundary control stands in for it, so the word before it does not join the key. One forward pass.
    /// </summary>
    internal static string WithoutDecoration(string value)
    {
        var position = value.AsSpan().IndexOfAny(Introducers);
        if (position < 0) return value;
        var result = new System.Text.StringBuilder(value.Length).Append(value.AsSpan(0, position));
        while (position < value.Length)
        {
            var end = DecorationEnd(value, position);
            if (end > position)
            {
                position = end;
                var key = position < value.Length && char.IsLetter(value[position]) && KeyStart.IsMatch(value, position);
                if (key) result.Append(Boundary);
            }
            else result.Append(value[position++]);
            var next = value.AsSpan(position).IndexOfAny(Introducers);
            var text = next < 0 ? value.Length - position : next;
            result.Append(value.AsSpan(position, text));
            position += text;
        }
        return result.ToString();
    }

    /// <summary>Where the decoration that starts at <paramref name="start"/> ends, or <paramref name="start"/> if none does.</summary>
    private static int DecorationEnd(string value, int start)
    {
        // The C1 control the sequence starts with: an 8-bit one as it is, or ESC and @ to _, its 7-bit form.
        var position = start + 1;
        int control = value[start];
        if (control == 0x1B)
        {
            if (position == value.Length || value[position] is < '@' or > '_') return start;
            control = value[position++] + 0x40;
        }
        if (control == 0x9B)
        {
            while (position < value.Length && value[position] is >= '0' and <= '?') position++;
            if (position < value.Length && value[position] == 'm') return position + 1;
            while (position < value.Length && value[position] is >= ' ' and <= '/') position++;
            return position == value.Length ? position : start;
        }
        if (control is not (0x90 or 0x98 or 0x9D or 0x9E or 0x9F)) return start;
        // The string ends at ST, 7-bit or 8-bit, or, for OSC, at BEL, which terminals also accept. Anything else before it
        // leaves the string unterminated. Its text includes a 7-bit introducer's letter, as the P of a DCS.
        var text = start + 1;
        while (position < value.Length && value[position] != '\u0007' && !IsControlIntroducer(value[position])) position++;
        int end;
        if (position == value.Length) return start;
        if (value[position] == '\u009C' || value[position] == '\u0007' && control == 0x9D) end = position + 1;
        else if (value[position] == '\u001B' && position + 1 < value.Length && value[position + 1] == '\\') end = position + 2;
        else return start;
        return KeyWord.IsMatch(value.AsSpan(text, position - text)) ? start : end;
    }

    private static string WithoutControls(string value)
    {
        if (!value.AsSpan().ContainsAny(Introducers)) return value;
        var result = new System.Text.StringBuilder(value.Length);
        foreach (var range in value.AsSpan().SplitAny(Introducers)) result.Append(value.AsSpan(range));
        return result.ToString();
    }

    private static bool IsControlIntroducer(char character) => character is '\u001B' or (>= '\u0080' and <= '\u009F');

    private static System.Text.RegularExpressions.Regex LinearPattern(string pattern) => new(pattern, Linear, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Redacts a quoted credential value, consuming escaped/doubled quotes as content, or an unterminated value through the
    /// end of the diagnostic. The value's quotes are kept and any control sequence before it is dropped.
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
