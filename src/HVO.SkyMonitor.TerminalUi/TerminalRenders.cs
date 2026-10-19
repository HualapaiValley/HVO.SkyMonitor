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
    public static string Folder(string fallback) => Folder(fallback, Environment.GetEnvironmentVariable);

    /// <summary>The folder the screens go to, with <paramref name="environment"/> as the process environment.</summary>
    public static string Folder(string fallback, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(environment);
        return environment(DirectoryVariable) is { Length: > 0 } configured ? configured : fallback;
    }

    /// <summary>
    /// Saves the screen <paramref name="app"/> shows as <paramref name="name"/>.ans in <see cref="Folder(string)"/>,
    /// after a full layout and draw, and returns the file's path. The name is lower-case letters, digits and hyphens.
    /// </summary>
    public static string Save(IApplication app, string name, string fallback)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.LayoutAndDraw(true);
        return SaveText(app.Driver?.ToAnsi() ?? string.Empty, name, fallback);
    }

    /// <summary>Saves <paramref name="ansi"/>, text with ANSI escapes, as <paramref name="name"/>.ans in <see cref="Folder(string)"/>.</summary>
    public static string SaveText(string ansi, string name, string fallback) => SaveText(ansi, name, fallback, Environment.GetEnvironmentVariable);

    /// <summary>
    /// Saves <paramref name="ansi"/> as <paramref name="name"/>.ans in <see cref="Folder(string, Func{string, string})"/>
    /// of <paramref name="environment"/>: a test of this class passes an environment without <c>HVO_TERMINAL_RENDERS_DIR</c>,
    /// so it never writes where a run's screenshots are.
    /// </summary>
    public static string SaveText(string ansi, string name, string fallback, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(ansi);
        ArgumentNullException.ThrowIfNull(name);
        if (!Name().IsMatch(name))
        {
            throw new ArgumentException($"A render's name is lower-case letters, digits and hyphens, not '{name}'.", nameof(name));
        }

        var folder = Folder(fallback, environment);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{name}.ans");
        File.WriteAllText(path, ansi);
        return path;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();
}
