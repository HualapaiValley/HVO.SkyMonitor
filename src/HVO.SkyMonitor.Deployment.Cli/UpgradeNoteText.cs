using System.Globalization;
using System.Text;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// One terminal-safety rule for release-note admission and human rendering. CRLF is normalized to LF; LF and TAB
/// retain their existing behavior. Other controls, format characters, line/paragraph separators, private-use scalars
/// and Unicode noncharacters are unsafe. Ordinary letters, punctuation, symbols and spaces in any script are retained.
/// </summary>
internal static class UpgradeNoteText
{
    public static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The first unsafe scalar's name, never the raw text, after CRLF normalization.</summary>
    public static string? FindUnsafeCodePoint(string normalizedText)
    {
        foreach (var character in normalizedText.EnumerateRunes())
        {
            if (IsUnsafe(character))
            {
                return CodePoint(character);
            }
        }
        return null;
    }

    /// <summary>Defense in depth for notes constructed without the reader: unsafe scalars are visible ASCII escapes.</summary>
    public static string EscapeForTerminal(string text)
    {
        var escaped = new StringBuilder();
        Span<char> encoded = stackalloc char[2];
        foreach (var character in NormalizeLineEndings(text).EnumerateRunes())
        {
            if (IsUnsafe(character))
            {
                escaped.Append('<').Append(CodePoint(character)).Append('>');
            }
            else
            {
                var length = character.EncodeToUtf16(encoded);
                escaped.Append(encoded[..length]);
            }
        }
        return escaped.ToString();
    }

    private static bool IsUnsafe(Rune character)
        => character.Value is not ('\n' or '\t') &&
           (Rune.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse ||
            character.Value is >= 0xFDD0 and <= 0xFDEF || (character.Value & 0xFFFF) is 0xFFFE or 0xFFFF);

    private static string CodePoint(Rune character) => string.Create(CultureInfo.InvariantCulture, $"U+{character.Value:X4}");
}
