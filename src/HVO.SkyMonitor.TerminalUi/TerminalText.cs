using System.Globalization;
using System.Text;
using Wcwidth;

namespace HVO.SkyMonitor.TerminalUi;

/// <summary>
/// Text as a terminal lays it out: in text elements (a letter with its combining marks, a surrogate pair, an emoji
/// sequence; <see cref="StringInfo"/>), each as wide as its first scalar in display cells, as <c>Wcwidth</c> measures it
/// (the table Terminal.Gui depends on). A CJK or supplementary-plane character takes two cells and a combining mark
/// none, so text is measured, cut and padded here rather than by its UTF-16 length, and never inside an element.
/// </summary>
public static class TerminalText
{
    /// <summary>The text elements of <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<string> Elements(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var elements = new List<string>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        return elements;
    }

    /// <summary>The cells <paramref name="element"/>, one text element, takes: its first scalar's width; a control none.</summary>
    public static int ElementWidth(string element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.Length == 0 ? 0 : Math.Max(0, UnicodeCalculator.GetWidth(Rune.GetRuneAt(element, 0)));
    }

    /// <summary>The cells <paramref name="text"/> takes.</summary>
    public static int Width(string text) => Elements(text).Sum(ElementWidth);

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="cells"/> cells, at the last whole text element that fits.
    /// </summary>
    public static string Clip(string text, int cells)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (cells <= 0)
        {
            return string.Empty;
        }

        var clipped = new StringBuilder(text.Length);
        var used = 0;
        foreach (var element in Elements(text))
        {
            var width = ElementWidth(element);
            if (used + width > cells)
            {
                break;
            }

            clipped.Append(element);
            used += width;
        }

        return clipped.ToString();
    }

    /// <summary><paramref name="text"/> cut, then padded with spaces, to exactly <paramref name="cells"/> cells.</summary>
    public static string Pad(string text, int cells)
    {
        var clipped = Clip(text, cells);
        return clipped + new string(' ', Math.Max(0, cells - Width(clipped)));
    }
}
