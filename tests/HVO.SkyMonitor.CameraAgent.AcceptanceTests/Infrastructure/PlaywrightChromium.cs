using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

/// <summary>
/// Starts the pinned Playwright Chromium for a browser-driven acceptance case, and turns the two
/// environment causes that can stop it into an Inconclusive result that names the actual cause.
/// </summary>
/// <remarks>
/// The guard this replaces was <c>if (!File.Exists(playwright.Chromium.ExecutablePath))</c> followed
/// by <c>Assert.Inconclusive("Pinned Playwright Chromium is absent.")</c>, copied at sixteen sites.
/// It reported a conclusion it had not established, and it did so in two independent ways.
/// <para>
/// The first is what it measured. Occupying a path is not the same as being able to run. On a host
/// that has the browser downloaded but lacks the shared libraries it links against, the file exists,
/// the guard passes, and the launch dies with <c>error while loading shared libraries</c> and a child
/// exit code of 127. The suite then reports a broken test rather than an unavailable browser, and
/// prints a remediation — install the browser — that cannot help, because the browser is already
/// installed. That is issue #785's own subject one level out: an assertion that misdescribes an
/// environmental condition as a defect.
/// </para>
/// <para>
/// The second is which file it looked at, and it holds on every host regardless of libraries.
/// <c>Chromium.ExecutablePath</c> resolves to the full browser at
/// <c>chromium-&lt;build&gt;/chrome-linux64/chrome</c>, but a headless launch starts a different
/// binary, the headless shell at
/// <c>chromium_headless_shell-&lt;build&gt;/chrome-headless-shell-linux64/chrome-headless-shell</c>.
/// Every launch in this project is headless, so the guard inspected a file none of them execute. A
/// host carrying the full browser without the headless shell passed the guard and failed the launch.
/// </para>
/// <para>
/// Both faults come from testing a proxy for availability rather than availability itself, so the
/// replacement stops inspecting files and starts the browser. It cannot examine the wrong binary
/// because it examines no binary; whichever one Playwright actually spawns is the one under test.
/// </para>
/// <para>
/// The classification below is deliberately narrow, and the narrowness is the point. Only the two
/// causes this can positively identify from the failure text become Inconclusive. Anything else is
/// rethrown and stays a failure. Converting every launch exception into "the environment is
/// unavailable" would be the original defect pointing the other way — a skip claiming an
/// environmental cause it never established — and it would quietly turn a real product or fixture
/// break into a green run. If a future Playwright reworded these messages, the markers stop matching
/// and behaviour degrades to a plain failure, which is the safe direction to fail in.
/// </para>
/// <para>
/// Both marker strings were taken from this Playwright version's actual output on a host that
/// reproduces each cause, not from its documentation. A classifier built on a guessed string would
/// be the same defect a third time.
/// </para>
/// </remarks>
internal static class PlaywrightChromium
{
    /// <summary>Matches the dynamic loader's failure, reported through Playwright's browser log.</summary>
    private const string MissingSharedLibraryMarker = "loading shared libraries";

    /// <summary>Matches Playwright's own refusal when the browser has never been downloaded.</summary>
    private const string MissingExecutableMarker = "executable doesn't exist";

    /// <summary>
    /// Launches the pinned headless Chromium, or ends the case as Inconclusive naming why it could not.
    /// </summary>
    internal static async Task<IBrowser> LaunchOrInconclusiveAsync(this IPlaywright playwright)
    {
        ArgumentNullException.ThrowIfNull(playwright);

        try
        {
            return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            }).ConfigureAwait(false);
        }
        catch (PlaywrightException failure)
            when (Describe(failure.Message, playwright.Chromium.ExecutablePath) is { } diagnosis)
        {
            Assert.Inconclusive(diagnosis);
            throw;
        }
    }

    /// <summary>
    /// Establishes that the pinned headless Chromium can start, before a case spends time on fixtures
    /// it would only discard. Call this only where real setup separates the check from the launch;
    /// where the launch is the next statement, wrap that launch instead of probing ahead of it.
    /// </summary>
    internal static async Task EnsureLaunchableOrInconclusiveAsync(this IPlaywright playwright)
    {
        // Disposed explicitly rather than with `await using`, so the disposal carries its own
        // ConfigureAwait and the probe leaves nothing running behind the case that follows it.
        var probe = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        await probe.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the operator-facing diagnosis for a launch failure this can attribute to the host, or
    /// <see langword="null"/> for one it cannot, which leaves the failure to propagate untouched.
    /// </summary>
    /// <remarks>
    /// Kept internal and free of Playwright types so it can be exercised directly against the real
    /// failure text captured from each cause, which is the only way to test the branch that no longer
    /// reproduces on a host once its libraries are installed.
    /// </remarks>
    internal static string? Describe(string message, string fullBrowserExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Finding the loader's line is the test for this cause, not a step taken after passing one.
        // Splitting them would leave a branch that cannot be reached and therefore cannot be trusted.
        if (TryFindLoaderError(message, out var loaderError))
        {
            return "Pinned Playwright Chromium is installed but cannot start on this host: it is "
                + "missing shared libraries it links against. Reinstalling the browser will not fix "
                + "this; the browser system dependencies have to be installed, which needs root. "
                + $"The loader reported: \"{loaderError}\".";
        }

        if (message.Contains(MissingExecutableMarker, StringComparison.OrdinalIgnoreCase))
        {
            return "Pinned Playwright Chromium is not installed. Run "
                + "`scripts/test:cameraagent-ui --install-browser` from the repository root. "
                + $"Playwright resolves the full browser to {fullBrowserExecutablePath}, and a "
                + "headless run additionally needs the headless shell beside it.";
        }

        return null;
    }

    /// <summary>
    /// Finds the dynamic loader's own line inside Playwright's browser log, so the diagnosis can name
    /// the first library that was missing instead of restating that something was.
    /// </summary>
    private static bool TryFindLoaderError(string message, out string loaderError)
    {
        foreach (var line in message.Split('\n'))
        {
            if (!line.Contains(MissingSharedLibraryMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Playwright prefixes browser-log lines with "[pid=NNN][err] "; the loader text follows it.
            var trimmed = line.Trim();
            var prefixEnd = trimmed.LastIndexOf("] ", StringComparison.Ordinal);
            loaderError = prefixEnd < 0 ? trimmed : trimmed[(prefixEnd + 2)..];
            return true;
        }

        loaderError = string.Empty;
        return false;
    }
}
