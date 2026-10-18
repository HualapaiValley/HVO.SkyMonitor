using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>The same terminal-safety policy rejects release text and visibly escapes directly constructed notes.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class UpgradeNoteTerminalSafetyTests
{
    private const string Version = "1.2.0";
    private static readonly Guid InstanceId = Guid.Parse("4f7a2c51-8a1e-4c3b-9d2e-0b6f5a1c7e90");

    [TestMethod]
    [DataRow(0x202E)]
    [DataRow(0x2066)]
    [DataRow(0x200B)]
    [DataRow(0x2028)]
    [DataRow(0xFEFF)]
    [DataRow(0xE000)]
    public void ParsedNote_UnsafeCodePointIsRefusedBeforeRenderingWithoutEchoingText(int scalar)
        => AssertRefused(scalar);

    [TestMethod]
    [DataRow(0x202E)]
    [DataRow(0x2066)]
    [DataRow(0x200B)]
    [DataRow(0x2028)]
    [DataRow(0xFEFF)]
    [DataRow(0xE000)]
    public void DirectNote_UnsafeCodePointIsEscapedVisibly(int scalar)
        => AssertEscaped(scalar);

    [TestMethod]
    [DataRow(0x000B)]
    [DataRow(0x000C)]
    [DataRow(0x000D)]
    [DataRow(0x0085)]
    [DataRow(0x2028)]
    [DataRow(0x2029)]
    public void ParsedNote_UnsafeWhitespaceOnlyTextStillNamesVersionAndCodePoint(int scalar)
        => AssertRefused(scalar, char.ConvertFromUtf32(scalar));

    [TestMethod]
    public void UnsafeCategoryBoundariesAndEveryNoncharacter_UseTheSameRuleInBothPaths()
    {
        // Every C0/C1 control other than the retained LF/TAB exceptions; every explicitly named format group;
        // supplementary format/private-use characters; and all 66 Unicode noncharacters, including plane ends.
        var scalars = Enumerable.Range(0, 0x20).Except([0x09, 0x0A])
            .Concat(Enumerable.Range(0x7F, 0x21))
            .Concat(Enumerable.Range(0x202A, 5))
            .Concat(Enumerable.Range(0x2066, 4))
            .Concat([0x00AD, 0x061C, 0x200B, 0x200C, 0x200D, 0x200E, 0x200F, 0x2060, 0xFEFF,
                0x1D173, 0xE0001, 0xE0020, 0xE007F, 0x2028, 0x2029,
                0xE000, 0xF8FF, 0xF0000, 0xFFFFD, 0x100000, 0x10FFFD])
            .Concat(Enumerable.Range(0xFDD0, 0x20))
            .Concat(Enumerable.Range(0, 17).SelectMany(static plane => new[] { (plane << 16) | 0xFFFE, (plane << 16) | 0xFFFF }));
        foreach (var scalar in scalars)
        {
            AssertRefused(scalar);
            AssertEscaped(scalar);
        }
    }

    [TestMethod]
    public void ParsedAndDirectNotes_KeepNonLatinLettersSymbolsSpacesLineFeedsAndTabs()
    {
        const string first = "Élévation — 中文 \U00020000 العربية ١٢٣ ✓ ☀️ 🚀\u00A0";
        const string second = "\tCafé: e\u0301.";
        const string input = first + "\r\n" + second;
        var expected = Header() + "  " + first + Environment.NewLine + "  " + second + Environment.NewLine;

        Assert.AreEqual(expected, RenderParsed(input));
        Assert.AreEqual(expected, RenderDirect(input));
    }

    private static void AssertRefused(int scalar)
        => AssertRefused(scalar, "Use backup-" + char.ConvertFromUtf32(scalar) + "txt.sh");

    private static void AssertRefused(int scalar, string text)
    {
        var character = char.ConvertFromUtf32(scalar);
        var exception = Assert.ThrowsExactly<InstallerException>(() => RenderParsed(text));

        Assert.AreEqual(
            $"The installer will not use release.json: upgrade note 1 ({Version}) contains unsafe terminal character U+{scalar:X4}.",
            exception.Message);
        Assert.IsFalse(exception.Message.Contains(character, StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains("backup-", StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains("txt.sh", StringComparison.Ordinal));
    }

    private static void AssertEscaped(int scalar)
    {
        var character = char.ConvertFromUtf32(scalar);
        var rendered = RenderDirect("Use backup-" + character + "txt.sh");

        Assert.AreEqual(Header() + $"  Use backup-<U+{scalar:X4}>txt.sh" + Environment.NewLine, rendered);
        Assert.IsFalse(rendered.Contains(character, StringComparison.Ordinal));
    }

    private static string RenderParsed(string text)
    {
        // The complete release.json fixture of 1.2.0, with this note as its only one.
        var root = JsonNode.Parse(ReleaseManifestTests.Fixture())!.AsObject();
        root["upgradeNotes"] = new JsonArray(new JsonObject { ["version"] = Version, ["text"] = text });
        var json = JsonSerializer.SerializeToUtf8Bytes(root);
        return UpgradeNotes.Render(UpgradeNotes.Plan("1.0.0", ReleaseManifest.Parse(json), InstanceId));
    }

    private static string RenderDirect(string text)
        => UpgradeNotes.Render(new LifecycleUpgradeNotes("1.0.0", Version, [new UpgradeNote(Version, text)]));

    private static string Header()
        => string.Join(Environment.NewLine, "Upgrading from 1.0.0 to 1.2.0.", string.Empty, "Upgrade notes for 1.2.0:", string.Empty);
}
