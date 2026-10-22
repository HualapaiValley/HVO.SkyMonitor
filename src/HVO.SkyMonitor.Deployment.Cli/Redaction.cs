namespace HVO.SkyMonitor.Deployment;

internal static class Redaction
{
    private const int MaximumDiagnosticLines = 200;

    private const int MaximumDiagnosticLength = 256;

    // A shortened diagnostic must keep its tail hidden when another error embeds it beside a second reason.
    private const string TruncationMarker = "<redacted>…";

    // A credential key is any key that ends with one of these words, so the key patterns put no word boundary before it:
    // db_password, client_secret, accessToken and x-api-key all match (#1491). The boundary after it keeps tokenid ordinary.
    private const string CredentialWord = "(?:passwords?|token|secret(?:[-_]key|_access_key)?|private[-_]?key|api[-_]?key)\\b";

    // The second passes also take a secret key whose words are joined by -, _ or nothing, so SecretKey and secret-access-key
    // match but secretkeeper does not (#1509).
    private const string SecondCredentialWord = "(?:passwords?|token|secret(?:[-_]?(?:access[-_]?)?key)?|private[-_]?key|api[-_]?key)\\b";

    private const string KeyWords = CredentialWord + "|authorization|bearer";

    private const string SecondKeyWords = SecondCredentialWord + "|authorization|bearer";

    // Space-only assignments accept explicit snake/kebab compounds, but not arbitrary words ending in a credential word.
    private const string SpacedKey = "(?:\\b[a-z][a-z0-9]*(?:[_-][a-z][a-z0-9]*)*[_-]\\x9C?" + CredentialWord + "|\\b" + CredentialWord + ")";

    private const string SecondSpacedKey =
        "(?:\\b[a-z][a-z0-9]*(?:[_-][a-z][a-z0-9]*)*[_-]\\x9C?" + SecondCredentialWord + "|\\b" + SecondCredentialWord + ")";

    // The words of a configuration key may also be joined by a double underscore, as in ObjectStorage__SecretKey (#1523).
    private const string ConfigurationSpacedKey = "\\b[a-z][a-z0-9]*(?:(?:__?|-)[a-z][a-z0-9]*)*(?:__?|-)\\x9C?" + SecondCredentialWord;

    // An outer assignment and the inner keys it can consume before their value is seen (#1499).
    private const string NestedKeys =
        $"""(?:{CredentialWord}["']?\s*[=:]\s*|\bauthorization\b["']?\s*[=:]\s*|\bbearer\s+)(?:[\w\x9C-]*{CredentialWord}["']?\s*[=:]\s*|\x9C*{SpacedKey}\s+)+""";

    // The same chain with the #1509 spellings and a configuration key's double underscore, for the #1523 pass only, so
    // api_key=SecretKey and api_key=db__password consume their inner key as api_key=db_password does (PR-1528-R0 F3).
    private const string SecondNestedKeys =
        $"""(?:{SecondCredentialWord}["']?\s*[=:]\s*|\bauthorization\b["']?\s*[=:]\s*|\bbearer\s+)(?:[\w\x9C-]*{SecondCredentialWord}["']?\s*[=:]\s*|\x9C*(?:{SecondSpacedKey}|{ConfigurationSpacedKey})\s+)+""";

    // Without a word boundary before the key, a compound key followed by a long run of spaces made the backtracking engine
    // try every way of splitting that run around its separator. Every pattern but the URL lookbehind is linear instead (#1491).
    private const System.Text.RegularExpressions.RegexOptions Linear =
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking;

    // Removed decoration leaves this in its place while text is matched when a key follows it, so a key the merge base found
    // after a removed string still starts a word (#1491). It is a C1 control, so it is removed with the others afterwards.
    private const char Boundary = '\u009C';

    // The origin of a character a pass wrote, such as a marker's, rather than kept from the text redaction started from.
    private const int Written = -1;

    private static readonly System.Text.RegularExpressions.Regex UserInformation = new(
        "(?<=://)[^/@\\s]+@", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // Reason JSON-encodes a diagnostic, and another embedding may encode it again. Keep the marker through either form.
    private static readonly System.Text.RegularExpressions.Regex TruncatedDiagnostic = LinearPattern(
        """(?:<|\\+u003[Cc])redacted(?:>|\\+u003[Ee])(?:…|\\+u2026)""");

    // A CSI that is not decoration stays in the text while it is matched, so a quoted value may still follow one.
    private static readonly System.Text.RegularExpressions.Regex QuotedCredential = LinearPattern(
        $"""(?i)({CredentialWord}["']?\s*[=: ]\s*)(?:(?:\x1B\[|\x9B)[0-?]*[ -/]*[@-~]\s*)*["']""");

    private static readonly System.Text.RegularExpressions.Regex KeyWord = LinearPattern($"(?i){KeyWords}");

    // Anchored where decoration ends, so it reads only the few characters of a key there.
    private static readonly System.Text.RegularExpressions.Regex KeyStart = new(
        $"\\G(?i:{KeyWords})", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // The #1523 passes also start a word at a key only the second passes find, so SecretKey after decoration is a key.
    private static readonly System.Text.RegularExpressions.Regex SecondKeyStart = new(
        $"\\G(?i:{SecondKeyWords})", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // A key that a later pass reads as one, with what makes it an assignment: its separator, a quoted value after whitespace,
    // a space-only key's value, or a bearer token. Each quoted pass's match is one too, at the same place (PR-1528-R0 F2).
    private static readonly System.Text.RegularExpressions.Regex KeyAssignment = LinearPattern(
        $"""(?i)(?:authorization|{SecondCredentialWord})["']?\s*[=:]|(?:{SecondSpacedKey}|{ConfigurationSpacedKey})\s+[^ ,;]|{SecondCredentialWord}["']?\s*[=:\s]\s*(?:(?:\x1B\[|\x9B)[0-?]*[ -/]*[@-~]\s*)*["']|\bbearer\s+[^,;\s"]""");

    // A quoted value that ends in a key and its separator, as "password=" does, leaves that key's value after it (#1523).
    private static readonly System.Text.RegularExpressions.Regex DanglingKey = LinearPattern(
        $"""(?i)(?:{SecondKeyWords})["']?\s*[=:]\s*["']?\z""");

    // No pattern starts with an optional quote. With one, the nonbacktracking engine could skip the first of two credentials
    // when a quote came earlier in the text (#1491). A quote before a key is not part of the match, so it is kept as text.
    private static readonly System.Text.RegularExpressions.Regex AuthorizationValue = LinearPattern(
        "(?i)(\\bauthorization\\b[\\\"']?\\s*[=:]\\s*[\\\"']?)[^\\\"',;}]+");

    private static readonly System.Text.RegularExpressions.Regex BearerValue = LinearPattern("(?i)(\\bbearer\\s+)[^,;\\s\\\"]+");

    private static readonly System.Text.RegularExpressions.Regex DoubleQuotedValue = LinearPattern(
        "(?i)(" + CredentialWord + "[\\\"']?\\s*[=: ]\\s*\\\")[^\\\"]*(\\\")");

    private static readonly System.Text.RegularExpressions.Regex SingleQuotedValue = LinearPattern(
        "(?i)(" + CredentialWord + "[\\\"']?\\s*[=: ]\\s*')[^']*(')");

    private static readonly System.Text.RegularExpressions.Regex UnquotedValue = LinearPattern(
        "(?i)((?:authorization|" + CredentialWord + ")[\\\"']?\\s*[=:]\\s*[\\\"']?)[^\\\"',;\\s}]+");

    // A key separated by space alone is a bare credential word or a compound of identifier words (letter first); ordinary prose stays text.
    private static readonly System.Text.RegularExpressions.Regex SpacedValue = LinearPattern(
        "(?i)(" + SpacedKey + "\\s+)[^ ,;]+");

    // An outer assignment can consume an inner key before its value is seen. Match the complete chain first, leaving its
    // final value hidden before the ordinary passes replace any keys. A removed-decoration boundary can sit inside a key.
    // The repeated prefix and value match are nonbacktracking.
    private static readonly System.Text.RegularExpressions.Regex NestedValue = LinearPattern(
        $"""(?i)({NestedKeys})[^"',;\s\x7D]+""");

    // The same chain before a quoted value, which the first quoted pass does not find after a tab, a no-break space or a NEL
    // (#1523). Its value is hidden as a quoted value is, with the text joined to it.
    private static readonly System.Text.RegularExpressions.Regex NestedQuotedCredential = LinearPattern(
        $"""(?i)({SecondNestedKeys})["']""");

    // The second passes read the first passes' output, so they can only hide more of it (#1509). A quoted value may also follow
    // its key after whitespace alone, which may be a tab, a no-break space or a NEL rather than a plain space.
    private static readonly System.Text.RegularExpressions.Regex SecondQuotedCredential = LinearPattern(
        $"""(?i)({SecondCredentialWord}["']?\s*[=:\s]\s*)(?:(?:\x1B\[|\x9B)[0-?]*[ -/]*[@-~]\s*)*["']""");

    private static readonly System.Text.RegularExpressions.Regex SecondUnquotedValue = LinearPattern(
        "(?i)((?:authorization|" + SecondCredentialWord + ")[\\\"']?\\s*[=:]\\s*[\\\"']?)[^\\\"',;\\s}]+");

    private static readonly System.Text.RegularExpressions.Regex SecondSpacedValue = LinearPattern(
        "(?i)(" + SecondSpacedKey + "\\s+)[^ ,;]+");

    private static readonly System.Text.RegularExpressions.Regex ConfigurationSpacedValue = LinearPattern(
        "(?i)(" + ConfigurationSpacedKey + "\\s+)[^ ,;]+");

    // ESC and the C1 controls, each of which may start a control sequence.
    private static readonly System.Buffers.SearchValues<char> Introducers = System.Buffers.SearchValues.Create(
        "\u001B\u0080\u0081\u0082\u0083\u0084\u0085\u0086\u0087\u0088\u0089\u008A\u008B\u008C\u008D\u008E\u008F" +
        "\u0090\u0091\u0092\u0093\u0094\u0095\u0096\u0097\u0098\u0099\u009A\u009B\u009C\u009D\u009E\u009F");

    // What JsonEncodedText.Encode writes for each ASCII character, as CameraAgentStateDiagnostics.Reason encodes a diagnostic.
    private static readonly string[] AsciiJson =
        [.. Enumerable.Range(0, 128).Select(character => System.Text.Json.JsonEncodedText.Encode(((char)character).ToString()).ToString())];

    /// <summary>
    /// Every nonbacktracking pattern. Each must return exactly the matches its backtracking form would, which a test checks.
    /// </summary>
    internal static IReadOnlyList<System.Text.RegularExpressions.Regex> LinearPatterns { get; } =
        [TruncatedDiagnostic, QuotedCredential, KeyWord, KeyAssignment, DanglingKey, AuthorizationValue, BearerValue, DoubleQuotedValue, SingleQuotedValue,
            UnquotedValue, SpacedValue, NestedValue, NestedQuotedCredential, SecondQuotedCredential, SecondUnquotedValue, SecondSpacedValue,
            ConfigurationSpacedValue];

    public static string SafeDiagnostic(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "no diagnostic output";
        }

        var firstLine = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return Redacted(firstLine, MaximumDiagnosticLength);
    }

    /// <summary>
    /// Redacts every credential in <paramref name="text"/>, at any length. Decoration is removed before anything is matched
    /// (#1491). Every other control character is matched where it stands, as it always was, and removed only afterwards.
    /// </summary>
    internal static string Redact(string text) => Redacted(text, int.MaxValue);

    /// <summary>
    /// The redacted text, at most <paramref name="length"/> characters of it. Where the #1523 passes hide nothing the merge base
    /// showed, it is the merge base's text. Where they do, it is the merge base's text up to the first character they hide, then
    /// a marker, so no character stands anywhere but where the merge base had it (PR-1528-R0 F1). A diagnostic is often embedded
    /// in an error that is encoded, redacted and cut again, and hiding a value in the middle of a text can hide the key a later
    /// reading would have found there; a text that ends at the value leaves no later key to lose (PR-1528-R1 F4, PR-1528-R2 F5).
    /// </summary>
    private static string Redacted(string text, int length)
    {
        // Read before removing URL user information or decoration, either of which could consume an embedded marker.
        var carried = TruncatedDiagnostic.Match(text);
        var source = Tracked.Of(text);
        // A URL's user information is also found as the text stood, so punctuation inside removed decoration still counts.
        // Without a control character the two texts are the same, and without :// neither holds a URL.
        if (text.AsSpan().ContainsAny(Introducers)) source = WithoutUserInformation(source);
        // Each pass reads what the passes before it left, so a pass that hides more can make a later one hide less: a value
        // that now reaches a later key hides that key, and the key's own value is then shown. The #1523 passes therefore read
        // the text apart from the merge base's, and the merge base's output ends where they first hide more.
        var shown = Passes(source, extended: false);
        var redacted = WithHidden(shown, Passes(source, extended: true), text.Length, ShownEnd(shown.Text, length),
            carried.Success ? carried.Index : -1, length);
        return redacted.Length <= length ? redacted : redacted[..length];
    }

    /// <summary>
    /// Where the merge base's cut falls in its passes' text: after the <paramref name="length"/>th character that removing the
    /// controls leaves, or at the end.
    /// </summary>
    private static int ShownEnd(string text, int length)
    {
        if (text.Length <= length) return text.Length;
        var position = 0;
        for (var kept = 0; position < text.Length && kept < length; position++)
        {
            if (!IsControlIntroducer(text[position])) kept++;
        }
        return position;
    }

    /// <summary>
    /// The merge base's passes, or with <paramref name="extended"/> set, the same passes with the #1523 additions: a key only
    /// the second passes find starts a word after decoration, a quoted value that ends in a key hides that key's value, an
    /// inner key's quoted value is found after any whitespace, and a configuration key's words may be joined by __.
    /// </summary>
    private static Tracked Passes(Tracked text, bool extended)
    {
        text = WithoutUserInformation(WithoutDecoration(text, extended ? SecondKeyStart : KeyStart));
        // Preserve inner keys until their quoted/nested values are hidden, including after a decoration boundary (#1499).
        text = RedactQuotedCredentials(text, QuotedCredential, joined: false, extended);
        // The first quoted pass has hidden every value it finds, so this pass cannot read a key inside one as the chain's.
        if (extended) text = RedactQuotedCredentials(text, NestedQuotedCredential, joined: true, extended);
        text = Replace(text, NestedValue);
        text = Replace(text, AuthorizationValue);
        text = Replace(text, BearerValue);
        text = Replace(text, DoubleQuotedValue, closing: true);
        text = Replace(text, SingleQuotedValue, closing: true);
        text = Replace(text, UnquotedValue);
        text = RedactSpacedValues(text, SpacedValue);
        // The second passes find keys and values the first passes did not. They replace text only, and only in what the first
        // passes left, so a key they find around a value the first passes hid cannot show that value again (#1509).
        text = RedactQuotedCredentials(text, SecondQuotedCredential, joined: true, extended);
        text = Replace(text, SecondUnquotedValue);
        text = RedactSpacedValues(text, SecondSpacedValue);
        return extended ? RedactSpacedValues(text, ConfigurationSpacedValue) : text;
    }

    /// <summary>
    /// The merge base's text before <paramref name="end"/>, with the controls removed, so its output is unchanged where the
    /// extended passes hide nothing more. Otherwise it ends before the first character the extended passes hide that the merge
    /// base shows, or, if earlier, the first one the merge base's passes hide when they read its output again as an error
    /// embeds it: as it is, with its controls escaped, or JSON-encoded (PR-1528-R2 F5). A marker takes the place of the rest,
    /// and of the markers and controls just before it. The text before the end is the merge base's, so a later reading finds
    /// the same keys and values in it, and nothing after it is left to show. The truncation marker carries that boundary into
    /// a later error, even when JSON-encoded, so shortening this reason cannot bring a second reason inside the later error's cut.
    /// </summary>
    private static string WithHidden(Tracked shown, Tracked extended, int origins, int end, int carried, int length)
    {
        var first = string.Equals(shown.Text, extended.Text, StringComparison.Ordinal) ? end : FirstHidden(shown, end, extended, origins);
        var carriedEnd = shown.Text.Length;
        if (carried >= 0)
        {
            for (var position = 0; position < shown.Text.Length; position++)
            {
                if (shown.Origins[position] < carried) continue;
                carriedEnd = position;
                break;
            }
            first = Math.Min(first, carriedEnd);
        }
        if (first == end && (carried < 0 || carriedEnd > end)) return WithoutControls(shown.Text[..end]);
        var output = WithoutControls(shown, end);
        foreach (var encoding in (ReadOnlySpan<Encoding>)[Encoding.None, Encoding.Controls, Encoding.Json])
        {
            first = FirstHidden(shown, first, Passes(Encoded(output, encoding), extended: false), origins);
        }
        // Keep the whole marker inside a bounded diagnostic, so a later embedding can always recognize it.
        first = Math.Min(first, ShownEnd(shown.Text, length - TruncationMarker.Length));
        while (first > 0 && (shown.Origins[first - 1] == Written || IsControlIntroducer(shown.Text[first - 1]))) first--;
        return WithoutControls(shown.Text[..first]) + TruncationMarker;
    }

    /// <summary>
    /// The position of the first character before <paramref name="end"/> that <paramref name="text"/> shows, apart from the
    /// controls, and <paramref name="reading"/> of the same text does not, or <paramref name="end"/>.
    /// </summary>
    private static int FirstHidden(Tracked text, int end, Tracked reading, int origins)
    {
        var read = new bool[origins];
        foreach (var origin in reading.Origins)
        {
            if (origin != Written) read[origin] = true;
        }
        for (var position = 0; position < end; position++)
        {
            var origin = text.Origins[position];
            if (origin != Written && !read[origin] && !IsControlIntroducer(text.Text[position])) return position;
        }
        return end;
    }

    /// <summary>How an embedding writes a redacted text before it is read again.</summary>
    private enum Encoding
    {
        /// <summary>As it is.</summary>
        None,

        /// <summary>With each control as JSON escapes it, as <c>CameraAgentStateDiagnostics.Message</c> and <c>Text</c> write it.</summary>
        Controls,

        /// <summary>JSON-encoded, as <c>CameraAgentStateDiagnostics.Reason</c> writes it.</summary>
        Json,
    }

    /// <summary>
    /// <paramref name="text"/> written with <paramref name="encoding"/>. Each escape keeps the origin of the character it stands
    /// for, so what a later reading hides of it is known by origin.
    /// </summary>
    private static Tracked Encoded(Tracked text, Encoding encoding)
    {
        if (encoding == Encoding.None) return text;
        var value = text.Text;
        var result = new TrackedBuilder(value.Length);
        for (var position = 0; position < value.Length; position++)
        {
            var character = value[position];
            if (encoding == Encoding.Controls ? !char.IsControl(character) : character < 128 && AsciiJson[character].Length == 1)
            {
                result.Append(text, position, 1);
                continue;
            }
            // The encoder writes every other UTF-16 unit as \uXXXX, a surrogate pair as two. Writing it here cannot throw on a
            // lone surrogate, which the encoder refuses.
            var escaped = character < 128 ? AsciiJson[character] : $"\\u{(int)character:X4}";
            result.Append(escaped, text.Origins[position]);
        }
        return result.ToTracked();
    }

    /// <summary>The characters of <paramref name="text"/> before <paramref name="end"/> that removing the controls leaves.</summary>
    private static Tracked WithoutControls(Tracked text, int end)
    {
        var result = new TrackedBuilder(end);
        for (var position = 0; position < end; position++)
        {
            if (!IsControlIntroducer(text.Text[position])) result.Append(text, position, 1);
        }
        return result.ToTracked();
    }

    private static string WithoutUserInformation(string text)
        => text.Contains("://", StringComparison.Ordinal) ? UserInformation.Replace(text, "<redacted>@") : text;

    private static Tracked WithoutUserInformation(Tracked text)
    {
        if (!text.Text.Contains("://", StringComparison.Ordinal)) return text;
        var match = UserInformation.Match(text.Text);
        if (!match.Success) return text;
        var result = new TrackedBuilder(text.Text.Length);
        var shown = 0;
        while (match.Success)
        {
            // The marker replaces the user information and keeps its @.
            result.Append(text, shown, match.Index - shown).AppendRedacted();
            shown = match.Index + match.Length - 1;
            match = match.NextMatch();
        }
        return result.Append(text, shown, text.Text.Length - shown).ToTracked();
    }

    /// <summary>
    /// Replaces each match as <c>$1&lt;redacted&gt;</c> would, or with <paramref name="closing"/> set, as
    /// <c>$1&lt;redacted&gt;$2</c> would, where the second group closes the match.
    /// </summary>
    private static Tracked Replace(Tracked text, System.Text.RegularExpressions.Regex pattern, bool closing = false)
    {
        var match = pattern.Match(text.Text);
        if (!match.Success) return text;
        var result = new TrackedBuilder(text.Text.Length);
        var shown = 0;
        while (match.Success)
        {
            result.Append(text, shown, match.Index + match.Groups[1].Length - shown).AppendRedacted();
            shown = match.Index + match.Length - (closing ? match.Groups[2].Length : 0);
            match = match.NextMatch();
        }
        return result.Append(text, shown, text.Text.Length - shown).ToTracked();
    }

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
        => value.AsSpan().ContainsAny(Introducers) ? WithoutDecoration(Tracked.Of(value), KeyStart).Text : value;

    private static Tracked WithoutDecoration(Tracked source, System.Text.RegularExpressions.Regex keyStart)
    {
        var value = source.Text;
        var position = value.AsSpan().IndexOfAny(Introducers);
        if (position < 0) return source;
        var result = new TrackedBuilder(value.Length).Append(source, 0, position);
        while (position < value.Length)
        {
            var end = DecorationEnd(value, position);
            if (end > position)
            {
                position = end;
                var key = position < value.Length && char.IsLetter(value[position]) && keyStart.IsMatch(value, position);
                if (key) result.Append(Boundary);
            }
            else result.Append(source, position++, 1);
            var next = value.AsSpan(position).IndexOfAny(Introducers);
            var text = next < 0 ? value.Length - position : next;
            result.Append(source, position, text);
            position += text;
        }
        return result.ToTracked();
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
    /// end of the diagnostic. The value's quotes are kept and any control sequence before it is dropped. When
    /// <paramref name="joined"/> is set, text joined to the closing quote, including another quoted part, is the same shell
    /// word, so it is hidden with the value and dropped (#1509). With <paramref name="dangling"/> set, a value that ends in a
    /// key also hides that key's value after it (#1523).
    /// </summary>
    private static Tracked RedactQuotedCredentials(Tracked text, System.Text.RegularExpressions.Regex pattern, bool joined, bool dangling)
    {
        // Both the nonbacktracking prefix search and the forward-only value scan are linear in the input length.
        var value = text.Text;
        var match = pattern.Match(value);
        if (!match.Success) return text;
        var result = new TrackedBuilder(value.Length);
        var consumed = 0;
        while (match.Success)
        {
            result.Append(text, consumed, match.Index - consumed);
            var start = match.Index + match.Length;
            result.Append(text, match.Index, match.Groups[1].Length).Append(text, start - 1, 1).AppendRedacted();
            var closing = ClosingQuote(value, start, value[start - 1]);
            if (closing >= 0) result.Append(text, closing, 1);
            consumed = closing < 0 ? value.Length : joined ? JoinedEnd(value, closing + 1) : closing + 1;
            if (dangling && closing >= 0 && DanglingKey.IsMatch(value.AsSpan(start, closing - start)))
            {
                consumed = WithoutDanglingValue(text, closing + 1, consumed, result);
            }
            match = pattern.Match(value, consumed);
        }
        return result.Append(text, consumed, value.Length - consumed).ToTracked();
    }

    /// <summary>
    /// Hides the value a key at the end of a quoted value leaves after its closing quote: the text joined to the quote, or else
    /// the word after the whitespace (#1523). A word that holds a key a later pass reads as an assignment is left, so its own
    /// value is still found; a key word with no assignment of its own, as in mysecret, is hidden with the rest of the value
    /// (PR-1528-R0 F2). The search for one ends where the next match can start at the earliest, so no character is read twice.
    /// Returns where the consumed text ends.
    /// </summary>
    private static int WithoutDanglingValue(Tracked text, int after, int consumed, TrackedBuilder result)
    {
        var value = text.Text;
        var start = after;
        if (start == value.Length || char.IsWhiteSpace(value[start]) || value[start] is ',' or ';' or '}')
        {
            while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
        }
        // Joined text the caller hid with the value is that key's value already.
        if (start < consumed) return consumed;
        var key = KeyAssignment.Match(value, start);
        var limit = key.Success ? key.Index : value.Length;
        var end = JoinedEnd(value.AsSpan(0, limit), start);
        if (end == start || key.Success && end == limit) return consumed;
        result.Append(text, consumed, start - consumed).AppendRedacted();
        return end;
    }

    /// <summary>
    /// Replaces each space-only value as <c>$1&lt;redacted&gt;</c> would. A value that starts with a quote is also hidden through
    /// its closing quote and the text joined to it, so a tab before a quoted value cannot leave its later words shown. Each
    /// match is the one the plain replacement makes, and the hidden text is the union of the two, so no less is hidden (#1509).
    /// A match whose key is already hidden adds only its own value: each extension then starts after the text hidden so far,
    /// so no character is scanned for two of them and the scans stay linear (PR-1521-R2 F3).
    /// </summary>
    private static Tracked RedactSpacedValues(Tracked tracked, System.Text.RegularExpressions.Regex pattern)
    {
        var text = tracked.Text;
        var match = pattern.Match(text);
        if (!match.Success) return tracked;
        var result = new TrackedBuilder(text.Length);
        var shown = 0;
        var hidden = 0;
        while (match.Success)
        {
            var value = match.Index + match.Groups[1].Length;
            var end = match.Index + match.Length;
            if (match.Index >= hidden && text[value] is '"' or '\'')
            {
                var closing = ClosingQuote(text, value + 1, text[value]);
                end = Math.Max(end, closing < 0 ? text.Length : JoinedEnd(text, closing + 1));
            }
            if (match.Index >= hidden)
            {
                if (hidden > shown) result.AppendRedacted();
                result.Append(tracked, Math.Max(shown, hidden), value - Math.Max(shown, hidden));
                shown = value;
            }
            hidden = Math.Max(hidden, end);
            match = match.NextMatch();
        }
        if (hidden > shown) result.AppendRedacted();
        return result.Append(tracked, Math.Max(shown, hidden), text.Length - Math.Max(shown, hidden)).ToTracked();
    }

    /// <summary>The closing quote of a value that starts at <paramref name="position"/>, or -1 if the value is unterminated.</summary>
    private static int ClosingQuote(ReadOnlySpan<char> value, int position, char quote)
    {
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
                else return position - 1;
            }
        }
        return -1;
    }

    /// <summary>Where the text joined to a closing quote ends: at whitespace, a comma, a semicolon or a closing brace.</summary>
    private static int JoinedEnd(ReadOnlySpan<char> value, int position)
    {
        while (position < value.Length && !char.IsWhiteSpace(value[position]) && value[position] is not (',' or ';' or '}'))
        {
            var character = value[position++];
            if (character is '"' or '\'')
            {
                var closing = ClosingQuote(value, position, character);
                if (closing < 0) return value.Length;
                position = closing + 1;
            }
        }
        return position;
    }

    public static string SafeDiagnostics(string value)
    {
        var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0
            ? SafeDiagnostic(value)
            : string.Join(Environment.NewLine, lines.TakeLast(MaximumDiagnosticLines).Select(SafeDiagnostic));
    }

    /// <summary>
    /// A text the passes read, with the position each of its characters had in the text redaction started from, or
    /// <see cref="Written"/> for one a pass wrote. Each character is kept once at most, in order, so two sets of passes over
    /// the same text can be compared character by character (#1523).
    /// </summary>
    private readonly record struct Tracked(string Text, int[] Origins)
    {
        public static Tracked Of(string text)
        {
            var origins = new int[text.Length];
            for (var index = 0; index < origins.Length; index++) origins[index] = index;
            return new Tracked(text, origins);
        }
    }

    private sealed class TrackedBuilder(int capacity)
    {
        private readonly System.Text.StringBuilder text = new(capacity);
        private readonly List<int> origins = new(capacity);

        public TrackedBuilder Append(Tracked source, int start, int length)
        {
            text.Append(source.Text, start, length);
            origins.AddRange(source.Origins.AsSpan(start, length));
            return this;
        }

        public TrackedBuilder Append(char written)
        {
            text.Append(written);
            origins.Add(Written);
            return this;
        }

        public TrackedBuilder Append(string escaped, int origin)
        {
            text.Append(escaped);
            for (var index = 0; index < escaped.Length; index++) origins.Add(origin);
            return this;
        }

        public TrackedBuilder AppendRedacted()
        {
            foreach (var written in "<redacted>") Append(written);
            return this;
        }

        public Tracked ToTracked() => new(text.ToString(), [.. origins]);
    }
}
