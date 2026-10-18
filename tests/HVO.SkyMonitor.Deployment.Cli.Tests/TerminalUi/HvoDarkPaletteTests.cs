using System.Globalization;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.Drawing;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.TerminalUi;

/// <summary>
/// The terminal palette is CameraAgent's HVO Dark: each <see cref="HvoDarkPalette"/> token is checked against the
/// stylesheet it names (a colour with transparency as it looks over the page background), and the README's token
/// contract against the palette, so neither can drift from the web pages without failing here.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed partial class HvoDarkPaletteTests
{
    private static readonly string Root = RepositoryRoot();

    private static readonly string Theme = File.ReadAllText(
        Path.Combine(Root, "src", "HVO.SkyMonitor.CameraAgent", "wwwroot", "css", "themes", "hvo-dark.css"));

    [TestMethod]
    [DataRow("background")]
    [DataRow("surface")]
    [DataRow("surface-raised")]
    [DataRow("border")]
    [DataRow("text")]
    [DataRow("muted")]
    [DataRow("accent")]
    [DataRow("focus")]
    [DataRow("success")]
    [DataRow("warning")]
    [DataRow("danger")]
    [DataRow("info")]
    [DataRow("success-background")]
    [DataRow("success-text")]
    [DataRow("warning-background")]
    [DataRow("warning-text")]
    [DataRow("danger-background")]
    [DataRow("danger-text")]
    [DataRow("info-background")]
    [DataRow("info-text")]
    public void EachRole_IsItsStylesheetToken(string role)
    {
        var token = HvoDarkPalette.Tokens.Single(candidate => candidate.Role == role);
        var tokens = ThemeTokens();

        Assert.IsTrue(tokens.ContainsKey(token.CssToken), $"hvo-dark.css has no {token.CssToken}");
        Assert.AreEqual(AsDrawn(tokens[token.CssToken], tokens["--hvo-body-bg"]), token.Hex, $"{token.CssToken} is {tokens[token.CssToken]} in hvo-dark.css");
    }

    [TestMethod]
    public void TheRoles_AreThePrototypes_AndEachIsNamedOnce()
    {
        var roles = HvoDarkPalette.Tokens.Select(token => token.Role).ToArray();

        CollectionAssert.AllItemsAreUnique(roles);
        foreach (var role in new[] { "surface", "border", "text", "muted", "accent", "focus", "success", "warning", "danger", "info" })
        {
            CollectionAssert.Contains(roles, role);
        }

        Assert.IsTrue(HvoDarkPalette.Tokens.All(token => HexColour().IsMatch(token.Hex)));
    }

    [TestMethod]
    public void TheReadme_DocumentsEveryTokenAsThePaletteHasIt()
    {
        var readme = File.ReadAllText(Path.Combine(Root, "src", "HVO.SkyMonitor.TerminalUi", "README.md"));
        var documented = ReadmeRow().Matches(readme)
            .Select(match => (Role: match.Groups[1].Value, Css: match.Groups[2].Value, Hex: match.Groups[3].Value, Ansi: match.Groups[4].Value))
            .ToArray();

        Assert.AreEqual(HvoDarkPalette.Tokens.Count, documented.Length, "one README row for each token");
        Assert.AreEqual(20, HvoDarkPalette.Tokens.Count, "EachRole_IsItsStylesheetToken has a row for each token");
        foreach (var token in HvoDarkPalette.Tokens)
        {
            Assert.Contains((token.Role, token.CssToken, token.Hex, token.Ansi16.ToString()), documented, $"README row for {token.Role}");
        }
    }

    [TestMethod]
    public void EveryAnsi16Fallback_IsOneOfTheSixteenColours()
        => Assert.IsTrue(HvoDarkPalette.Tokens.All(token => Enum.IsDefined(token.Ansi16)));

    [TestMethod]
    [DataRow(ColorName16.Black, 0)]
    [DataRow(ColorName16.Red, 1)]
    [DataRow(ColorName16.Green, 2)]
    [DataRow(ColorName16.Yellow, 3)]
    [DataRow(ColorName16.Blue, 4)]
    [DataRow(ColorName16.Magenta, 5)]
    [DataRow(ColorName16.Cyan, 6)]
    [DataRow(ColorName16.Gray, 7)]
    [DataRow(ColorName16.DarkGray, 8)]
    [DataRow(ColorName16.BrightRed, 9)]
    [DataRow(ColorName16.BrightGreen, 10)]
    [DataRow(ColorName16.BrightYellow, 11)]
    [DataRow(ColorName16.BrightBlue, 12)]
    [DataRow(ColorName16.BrightMagenta, 13)]
    [DataRow(ColorName16.BrightCyan, 14)]
    [DataRow(ColorName16.White, 15)]
    public void TheSixteenColours_HaveTheirStandardIndexes(ColorName16 colour, int index)
        => Assert.AreEqual(index, HvoConsoleText.Index(colour));

    private static Dictionary<string, string> ThemeTokens()
    {
        var root = RootBlock().Match(Theme);
        Assert.IsTrue(root.Success, "hvo-dark.css starts with its :root tokens");
        return TokenLine().Matches(root.Groups[1].Value).ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value.Trim());
    }

    // A colour as the page shows it: rgba() over the page background, rounded to whole channels.
    private static string AsDrawn(string value, string background)
    {
        var rgba = Rgba().Match(value);
        if (!rgba.Success)
        {
            return Normalise(value);
        }

        var under = Channels(Normalise(background));
        var alpha = double.Parse(rgba.Groups[4].Value, CultureInfo.InvariantCulture);
        var channels = Enumerable.Range(0, 3)
            .Select(i => (int)Math.Round((alpha * int.Parse(rgba.Groups[i + 1].Value, CultureInfo.InvariantCulture)) + ((1 - alpha) * under[i]), MidpointRounding.AwayFromZero));
        return "#" + string.Concat(channels.Select(channel => channel.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static int[] Channels(string hex) => [Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16)];

    private static string Normalise(string hex)
    {
        hex = new string([.. hex.Trim().Select(char.ToLowerInvariant)]);
        return hex.Length == 4 ? $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}" : hex;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root holds HVO.SkyMonitor.v9.slnx.");
    }

    [GeneratedRegex(@"^:root\[data-theme=""hvo-dark""\]\s*\{([^}]*)\}")]
    private static partial Regex RootBlock();

    [GeneratedRegex(@"(--[a-z0-9-]+)\s*:\s*([^;]+);")]
    private static partial Regex TokenLine();

    [GeneratedRegex(@"^rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([0-9.]+)\s*\)$")]
    private static partial Regex Rgba();

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"^\| ([a-z-]+) \| `(--[a-z-]+)` \| `(#[0-9a-f]{6})` \| ([A-Za-z]+) \|", RegexOptions.Multiline)]
    private static partial Regex ReadmeRow();
}
