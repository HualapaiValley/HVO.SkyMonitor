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
            string.Equals(match.Groups["requested"].Value, expected.AbsoluteUri, StringComparison.Ordinal) &&
            string.Equals(match.Groups["replacement"].Value, expected.AbsoluteUri, StringComparison.Ordinal);
    }

    [GeneratedRegex(
        "\\ANavigation to \\\"(?<requested>[^\\\"\\r\\n]+)\\\" is interrupted by another navigation to \\\"(?<replacement>[^\\\"\\r\\n]+)\\\"\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex InterruptedNavigation();
}
