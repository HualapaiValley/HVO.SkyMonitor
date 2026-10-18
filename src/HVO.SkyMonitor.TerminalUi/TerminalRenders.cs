using System.Text.RegularExpressions;
using Terminal.Gui.App;

namespace HVO.SkyMonitor.TerminalUi;

/// <summary>
/// Saves terminal screens as ANSI text (<c>NAME.ans</c>), the source of the documentation's screenshots: CI draws each as
/// an SVG with <c>tests/cli/ansi-to-svg.py</c>, and <c>scripts/docs:terminal-screenshots</c> copies them into
/// <c>docs/images/terminal/</c>. A test draws a screen on Terminal.Gui's ANSI driver in memory and saves it here; the
/// installer's wizard tests name theirs <c>install-*</c>, and <c>hvo-sky ui</c>'s tests add theirs beside them.
/// </summary>
public static partial class TerminalRenders
{
    /// <summary>Where the screens go; unset, the caller's fallback (a test's results folder).</summary>
    public const string DirectoryVariable = "HVO_TERMINAL_RENDERS_DIR";

    /// <summary>The folder the screens go to: <c>HVO_TERMINAL_RENDERS_DIR</c>, or <paramref name="fallback"/>.</summary>
    public static string Folder(string fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } configured ? configured : fallback;
    }

    /// <summary>
    /// Saves the screen <paramref name="app"/> shows as <paramref name="name"/>.ans in <see cref="Folder"/>, after a
    /// full layout and draw, and returns the file's path. The name is lower-case letters, digits and hyphens.
    /// </summary>
    public static string Save(IApplication app, string name, string fallback)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.LayoutAndDraw(true);
        return SaveText(app.Driver?.ToAnsi() ?? string.Empty, name, fallback);
    }

    /// <summary>Saves <paramref name="ansi"/>, text with ANSI escapes, as <paramref name="name"/>.ans in <see cref="Folder"/>.</summary>
    public static string SaveText(string ansi, string name, string fallback)
    {
        ArgumentNullException.ThrowIfNull(ansi);
        ArgumentNullException.ThrowIfNull(name);
        if (!Name().IsMatch(name))
        {
            throw new ArgumentException($"A render's name is lower-case letters, digits and hyphens, not '{name}'.", nameof(name));
        }

        var folder = Folder(fallback);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{name}.ans");
        File.WriteAllText(path, ansi);
        return path;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();
}
