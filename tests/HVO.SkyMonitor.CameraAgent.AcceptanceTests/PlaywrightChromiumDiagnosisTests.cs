using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

/// <summary>
/// Covers how <see cref="PlaywrightChromium"/> classifies a launch failure, using the failure text
/// Playwright actually produced for each cause rather than text written to match the classifier.
/// </summary>
/// <remarks>
/// The library-loader case cannot be reproduced on a host once its browser dependencies are
/// installed, and installing them is what unblocks the rest of issue #785. The message below was
/// captured from Playwright 1.62.0 on `home-dev-02` while that host still reproduced it, and is kept
/// verbatim so the classifier keeps being tested against the real thing after the cause is gone.
/// <para>
/// The third case is the one worth guarding hardest. A guard that answered "the environment is
/// unavailable" to every launch failure would be the same defect this class exists to prevent,
/// turned around: it would report an environmental cause it never established, and it would fail
/// silently towards green by skipping a genuine product or fixture break.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PlaywrightChromiumDiagnosisTests
{
    private const string ExecutablePath = "/home/example/.cache/ms-playwright/chromium-1234/chrome-linux64/chrome";

    /// <summary>Captured verbatim from a host missing the browser's shared libraries.</summary>
    private const string MissingLibraryFailure = """
        Target page, context or browser has been closed
        Browser logs:

        <launching> /home/example/.cache/ms-playwright/chromium_headless_shell-1234/chrome-headless-shell-linux64/chrome-headless-shell --headless --no-sandbox
        <launched> pid=379262
        [pid=379262][err] /home/example/.cache/ms-playwright/chromium_headless_shell-1234/chrome-headless-shell-linux64/chrome-headless-shell: error while loading shared libraries: libatk-1.0.so.0: cannot open shared object file: No such file or directory
        Call log:
          - [pid=379262] <process did exit: exitCode=127, signal=null>
        """;

    /// <summary>Captured verbatim from a launch pointed at a path holding no browser.</summary>
    private const string MissingExecutableFailure =
        "Failed to launch chromium because executable doesn't exist at /nonexistent/chrome";

    [TestMethod]
    public void MissingSharedLibrariesAreReportedAsAnUnstartableBrowserNamingTheLoaderError()
    {
        var diagnosis = PlaywrightChromium.Describe(MissingLibraryFailure, ExecutablePath);

        Assert.IsNotNull(diagnosis);
        StringAssert.Contains(diagnosis, "installed but cannot start", StringComparison.Ordinal);
        StringAssert.Contains(diagnosis, "libatk-1.0.so.0", StringComparison.Ordinal);

        // The remediation has to contradict the one the old guard printed, because reinstalling the
        // browser was the advice that could not work in exactly this case.
        StringAssert.Contains(diagnosis, "Reinstalling the browser will not fix this", StringComparison.Ordinal);

        // The loader's own line is quoted without Playwright's "[pid=NNN][err] " log prefix.
        Assert.IsFalse(diagnosis.Contains("[pid=", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AMissingExecutableIsReportedAsAnUninstalledBrowserWithTheInstallCommand()
    {
        var diagnosis = PlaywrightChromium.Describe(MissingExecutableFailure, ExecutablePath);

        Assert.IsNotNull(diagnosis);
        StringAssert.Contains(diagnosis, "is not installed", StringComparison.Ordinal);
        StringAssert.Contains(diagnosis, "scripts/test:cameraagent-ui --install-browser", StringComparison.Ordinal);

        // Naming the headless shell matters: the guard this replaced checked only the full browser,
        // so a host holding one without the other passed the check and failed the launch.
        StringAssert.Contains(diagnosis, "headless shell", StringComparison.Ordinal);
    }

    [TestMethod]
    public void AnUnrecognisedFailureIsNotClaimedAsAnEnvironmentProblem()
        => Assert.IsNull(PlaywrightChromium.Describe(
            "net::ERR_CONNECTION_REFUSED at http://127.0.0.1:5130/gallery", ExecutablePath));

    [TestMethod]
    public void AFailureCarryingNoLoaderLineStillYieldsADiagnosisRatherThanAnEmptyOne()
    {
        // Playwright reports the loader error through the browser log, which a future version could
        // withhold while still failing for the same reason. The classifier must not emit a dangling
        // sentence in that case.
        var diagnosis = PlaywrightChromium.Describe(
            "Browser closed unexpectedly: loading shared libraries", ExecutablePath);

        Assert.IsNotNull(diagnosis);
        StringAssert.EndsWith(diagnosis, ".", StringComparison.Ordinal);
    }
}
