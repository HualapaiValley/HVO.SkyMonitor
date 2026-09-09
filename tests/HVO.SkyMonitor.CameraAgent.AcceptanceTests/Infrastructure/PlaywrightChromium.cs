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
/// because it examines no binary; whichever one Playwright actually spawns is the one under test,
/// and every path it names is taken from the failure Playwright reported rather than resolved
/// independently and hoped to match.
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
/// That narrowness was claimed before it was true. The first version of this file matched the bare
/// phrase <c>loading shared libraries</c> anywhere in any line, and a review measured the
/// consequence live: a headless shell that logged
/// <c>plugin finished loading shared libraries</c> and then died of something else entirely was
/// reported to the operator as a missing-library environment failure, with a remediation directing a
/// root-level dependency install. An ordinary sentence can contain those words. The dynamic loader's
/// own signature cannot be produced by anything but the loader, so the test is that signature plus
/// the exit status the loader produces, and the diagnosis quotes what follows the signature.
/// </para>
/// <para>
/// Both marker strings were taken from this Playwright version's actual output on a host that
/// reproduces each cause, not from its documentation. A classifier built on a guessed string would
/// be the same defect a third time.
/// </para>
/// </remarks>
internal static class PlaywrightChromium
{
    /// <summary>
    /// The dynamic loader's own signature, relayed verbatim through Playwright's browser log. The
    /// trailing space is part of the match: the library name begins immediately after it.
    /// </summary>
    private const string LoaderFailureMarker = "error while loading shared libraries: ";

    /// <summary>
    /// The child exit status a loader failure produces, as Playwright reports it in the call log.
    /// Required alongside the signature so that a child which merely logged the words, and died of
    /// something else, cannot be presented as an environment problem.
    /// </summary>
    private const string LoaderExitStatusMarker = "<process did exit: exitCode=127";

    /// <summary>Matches Playwright's own refusal when the browser has never been downloaded.</summary>
    private const string MissingExecutableMarker = "executable doesn't exist at ";

    /// <summary>
    /// The ceiling on a single launch. Playwright's default did not fire against a child that
    /// started and then held the debugging pipe open without ever becoming usable: the launch was
    /// measured blocking past one hundred seconds with no exception, so the classifier below was
    /// never reached and the run neither failed nor went Inconclusive. It stalled. A cold headless
    /// launch completes in a few seconds, so a minute is generous while still bounding the stall.
    /// <para>
    /// A timeout is a plain failure by design. It cannot reach the missing-library branch, because a
    /// child that has not exited carries no exit status for <see cref="LoaderExitStatusMarker"/> to
    /// find — which is why the classifier had to be tightened before this timeout was added, and not
    /// after. Adding it first would have opened a route from a hung launch, whose message may carry
    /// the browser log, into an environmental diagnosis.
    /// </para>
    /// </summary>
    private const float LaunchTimeoutMilliseconds = 60_000;

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
                Headless = true,
                Timeout = LaunchTimeoutMilliseconds
            }).ConfigureAwait(false);
        }
        catch (PlaywrightException failure) when (Describe(failure.Message) is { } diagnosis)
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
    /// reproduces on a host once its libraries are installed. It takes no path argument: every path
    /// it names comes out of the message, because the whole subject here is naming the binary that
    /// actually failed rather than one resolved separately and assumed to be the same.
    /// </remarks>
    internal static string? Describe(string message)
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

        if (TryFindMissingExecutablePath(message, out var missingExecutablePath))
        {
            return "Pinned Playwright Chromium is not installed. Run "
                + "`scripts/test:cameraagent-ui --install-browser` from the repository root. "
                + $"Playwright looked for the headless shell at {missingExecutablePath} and found "
                + "nothing there. Note that the headless shell and the full browser are separate "
                + "downloads in sibling directories under the browsers root, and every launch in "
                + "this project is headless.";
        }

        return null;
    }

    /// <summary>
    /// Finds the dynamic loader's own line inside Playwright's browser log, so the diagnosis can name
    /// the first library that was missing instead of restating that something was.
    /// </summary>
    private static bool TryFindLoaderError(string message, out string loaderError)
    {
        loaderError = string.Empty;

        // The signature alone is not sufficient, because it is relayed inside a log the child
        // controls and a child can be made to print anything. Requiring the loader's exit status too
        // means both halves of the evidence have to be present before this is called an environment
        // failure. Neither half is inferred from the other.
        if (!message.Contains(LoaderExitStatusMarker, StringComparison.Ordinal))
        {
            return false;
        }

        // Split on '\n' alone; the Trim below is what absorbs the '\r' of a CRLF line ending, so the
        // two are one mechanism and removing the Trim for tidiness would silently break the split.
        foreach (var line in message.Split('\n'))
        {
            var signatureStart = line.IndexOf(LoaderFailureMarker, StringComparison.Ordinal);
            if (signatureStart < 0)
            {
                continue;
            }

            // Everything after the signature is the loader's own account: the library it could not
            // find, and why. Taking the quote from here rather than from the start of the line means
            // Playwright's "[pid=NNN][err] " prefix and the binary path are never inside it to begin
            // with, so there is no prefix left to strip and no strip left to get wrong.
            loaderError = line[(signatureStart + LoaderFailureMarker.Length)..].Trim();

            // A signature with nothing after it would produce a diagnosis quoting an empty string.
            // Report nothing rather than something empty, and let the launch fail plainly.
            return loaderError.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// Takes the path Playwright says it could not find out of the failure itself, so the diagnosis
    /// names the binary that was actually looked for.
    /// </summary>
    private static bool TryFindMissingExecutablePath(string message, out string missingExecutablePath)
    {
        missingExecutablePath = string.Empty;

        var pathStart = message.IndexOf(MissingExecutableMarker, StringComparison.OrdinalIgnoreCase);
        if (pathStart < 0)
        {
            return false;
        }

        // Playwright puts the path at the end of its own sentence, so the rest of that line is the
        // path. Bounded at the line so a multi-line message cannot drag unrelated text into it.
        var pathText = message[(pathStart + MissingExecutableMarker.Length)..];
        var lineEnd = pathText.IndexOf('\n', StringComparison.Ordinal);
        missingExecutablePath = (lineEnd < 0 ? pathText : pathText[..lineEnd]).Trim();
        return missingExecutablePath.Length > 0;
    }
}
