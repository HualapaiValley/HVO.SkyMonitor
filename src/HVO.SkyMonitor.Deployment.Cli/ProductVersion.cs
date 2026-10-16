using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Product release versions in SemVer 2.0 precedence, as the install contract and RoofController's installer order
/// them: by major, minor and patch; a pre-release (<c>1.2.0-rc.1</c>, <c>1.2.0-dev.3</c>) before its release; pre-release
/// identifiers compared numerically when both are numbers, a number before a word, words ordinally, and more identifiers
/// after fewer that match. Build metadata after <c>+</c> does not count.
/// </summary>
internal static class ProductVersion
{
    private const string Identifier = "(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)";

    private static readonly Regex Pattern = new(
        $@"^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<pre>{Identifier}(?:\.{Identifier})*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="version"/> is a SemVer 2.0 version.</summary>
    public static bool IsValid(string? version) => version is not null && Pattern.IsMatch(version);

    /// <summary>
    /// Below zero when <paramref name="left"/> is an earlier release than <paramref name="right"/>, zero when they are
    /// the same release, above zero when it is later.
    /// </summary>
    /// <exception cref="FormatException">Either value is not a SemVer 2.0 version.</exception>
    public static int Compare(string left, string right)
    {
        var (leftCore, leftPrerelease) = Parts(left);
        var (rightCore, rightPrerelease) = Parts(right);
        for (var index = 0; index < 3; index++)
        {
            var core = CompareNumbers(leftCore[index], rightCore[index]);
            if (core != 0)
            {
                return core;
            }
        }

        // A release comes after every pre-release of it.
        if (leftPrerelease.Length == 0 || rightPrerelease.Length == 0)
        {
            return Math.Sign(rightPrerelease.Length.CompareTo(leftPrerelease.Length));
        }
        for (var index = 0; index < Math.Min(leftPrerelease.Length, rightPrerelease.Length); index++)
        {
            var identifier = CompareIdentifiers(leftPrerelease[index], rightPrerelease[index]);
            if (identifier != 0)
            {
                return identifier;
            }
        }
        return Math.Sign(leftPrerelease.Length.CompareTo(rightPrerelease.Length));
    }

    /// <summary>True when <paramref name="left"/> is an earlier release than <paramref name="right"/>.</summary>
    public static bool IsOlder(string left, string right) => Compare(left, right) < 0;

    /// <summary>The release a version belongs to: <c>1.2.0</c> for <c>1.2.0-rc.1+abc</c>.</summary>
    public static string Release(string version)
    {
        var (core, _) = Parts(version);
        return string.Join('.', core);
    }

    private static (string[] Core, string[] Prerelease) Parts(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var match = Pattern.Match(version);
        if (!match.Success)
        {
            throw new FormatException($"'{version}' is not a release version (major.minor.patch, such as 1.2.0 or 1.2.0-rc.1).");
        }
        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : [];
        return ([match.Groups["major"].Value, match.Groups["minor"].Value, match.Groups["patch"].Value], prerelease);
    }

    private static int CompareIdentifiers(string left, string right)
    {
        var leftNumber = left.All(char.IsAsciiDigit);
        var rightNumber = right.All(char.IsAsciiDigit);
        return leftNumber && rightNumber ? CompareNumbers(left, right)
            : leftNumber ? -1
            : rightNumber ? 1
            : Math.Sign(string.CompareOrdinal(left, right));
    }

    // Numbers of any length without leading zeros (the pattern refuses them): the longer is larger, else by digits.
    private static int CompareNumbers(string left, string right)
        => Math.Sign(left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right));
}
