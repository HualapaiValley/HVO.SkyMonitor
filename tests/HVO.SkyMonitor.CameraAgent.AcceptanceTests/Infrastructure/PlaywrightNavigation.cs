using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

internal static partial class PlaywrightNavigation
{
    internal static async Task NavigateOrJoinAsync(IPage page, string destination)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var expected = new Uri(new Uri(page.Url, UriKind.Absolute), destination);
        try
        {
            await page.GotoAsync(destination).ConfigureAwait(false);
        }
        catch (PlaywrightException exception) when (IsSameDestinationInterruption(exception.Message, expected))
        {
            await page.WaitForURLAsync(url => string.Equals(
                url,
                expected.AbsoluteUri,
                StringComparison.Ordinal)).ConfigureAwait(false);
            await page.WaitForLoadStateAsync(LoadState.Load).ConfigureAwait(false);
        }
    }

    internal static bool IsSameDestinationInterruption(string message, Uri expected)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(expected);

        var match = InterruptedNavigation().Match(message);
        return match.Success &&
            Uri.TryCreate(match.Groups["requested"].Value, UriKind.Absolute, out var requested) &&
            Uri.TryCreate(match.Groups["replacement"].Value, UriKind.Absolute, out var replacement) &&
            requested == expected && replacement == expected;
    }

    [GeneratedRegex(
        "Navigation to \\\"(?<requested>[^\\\"]+)\\\" is interrupted by another navigation to \\\"(?<replacement>[^\\\"]+)\\\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex InterruptedNavigation();
}
