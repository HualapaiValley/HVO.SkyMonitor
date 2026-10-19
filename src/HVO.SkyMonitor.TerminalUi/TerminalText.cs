using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;

namespace HVO.SkyMonitor.TerminalUi;

/// <summary>
/// Text as Terminal.Gui's renderer lays it out, which is the one measure of how wide text is: in graphemes
/// (<see cref="GraphemeHelper"/>), each made printable and measured with Terminal.Gui's own <c>GetColumns</c>, and taking
/// two cells when that is two, or one (<c>OutputBufferImpl.AddGrapheme</c>). So a CJK character, an emoji, a flag, a ZWJ
/// sequence or an emoji with a skin tone takes two cells and a letter with its combining marks one, and text is measured,
/// cut and padded here rather than by its UTF-16 length, and never inside a grapheme.
/// </summary>
public static class TerminalText
{
    /// <summary>The graphemes of <paramref name="text"/>, in order, as the renderer splits it.</summary>
    public static IReadOnlyList<string> Elements(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. GraphemeHelper.GetGraphemes(text)];
    }

    /// <summary>The cells the renderer gives <paramref name="element"/>, one grapheme: two, or one.</summary>
    public static int ElementWidth(string element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.Length == 0 ? 0 : element.MakePrintable().GetColumns() > 1 ? 2 : 1;
    }

    /// <summary>The cells <paramref name="text"/> takes.</summary>
    public static int Width(string text) => Elements(text).Sum(ElementWidth);

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="cells"/> cells, at the last whole grapheme that fits.
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
