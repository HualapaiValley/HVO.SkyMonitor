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
/// The cases worth guarding hardest are the negative ones. A guard that answered "the environment is
/// unavailable" to every launch failure would be the same defect this class exists to prevent,
/// turned around: it would report an environmental cause it never established, and it would fail
/// silently towards green by skipping a genuine product or fixture break.
/// </para>
/// <para>
/// One of those negative cases is here because it happened, not because it was imagined. An earlier
/// version of the classifier matched the bare phrase `loading shared libraries`, and a review drove
/// a headless shell that logged that phrase in a warning and then died of something unrelated
/// through the real helper. It came back as a confident missing-library diagnosis telling the
/// operator to install system dependencies as root.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PlaywrightChromiumDiagnosisTests
{
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

    /// <summary>
    /// The failure a child produces when it logs the words and then dies of something else. Taken
    /// from the reproduction a reviewer drove through the shipped helper, not written to fit.
    /// </summary>
    private const string UnrelatedCrashMentioningTheWords = """
        Target page, context or browser has been closed
        Browser logs:

        <launching> /home/example/.cache/ms-playwright/chromium_headless_shell-1234/chrome-headless-shell-linux64/chrome-headless-shell --headless --no-sandbox
        <launched> pid=411907
        [pid=411907][err] [0909/120000.1:WARNING:dynamic_module.cc(88)] plugin finished loading shared libraries
        [pid=411907][err] [0909/120000.2:FATAL:memory.cc(38)] Out of memory. size=1048576
        Call log:
          - [pid=411907] <process did exit: exitCode=1, signal=null>
        """;

    /// <summary>Captured verbatim from a launch pointed at a path holding no browser.</summary>
    private const string MissingExecutableFailure =
        "Failed to launch chromium because executable doesn't exist at "
        + "/home/example/.cache/ms-playwright/chromium_headless_shell-1234/chrome-headless-shell-linux64/chrome-headless-shell";

    [TestMethod]
    public void MissingSharedLibrariesAreReportedAsAnUnstartableBrowserNamingTheLoaderError()
    {
        var diagnosis = PlaywrightChromium.Describe(MissingLibraryFailure);

        Assert.IsNotNull(diagnosis);
        StringAssert.Contains(diagnosis, "installed but cannot start", StringComparison.Ordinal);
        StringAssert.Contains(diagnosis, "libatk-1.0.so.0", StringComparison.Ordinal);

        // The remediation has to contradict the one the old guard printed, because reinstalling the
        // browser was the advice that could not work in exactly this case.
        StringAssert.Contains(diagnosis, "Reinstalling the browser will not fix this", StringComparison.Ordinal);

        // The quote starts after the loader's signature, so Playwright's "[pid=NNN][err] " prefix and
        // the binary path are outside it rather than stripped out of it.
        Assert.IsFalse(diagnosis.Contains("[pid=", StringComparison.Ordinal));
        Assert.IsFalse(diagnosis.Contains("chrome-headless-shell:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ALoaderLineArrivingWithoutPlaywrightsLogPrefixIsStillDiagnosed()
    {
        // A future Playwright could relay the loader's line without wrapping it. The signature is
        // what identifies the cause, so the diagnosis must not depend on the wrapper around it.
        var diagnosis = PlaywrightChromium.Describe(
            "Target page, context or browser has been closed\n"
            + "chrome-headless-shell: error while loading shared libraries: libnss3.so: "
            + "cannot open shared object file: No such file or directory\n"
            + "  - <process did exit: exitCode=127, signal=null>");

        Assert.IsNotNull(diagnosis);
        StringAssert.Contains(diagnosis, "libnss3.so", StringComparison.Ordinal);
    }

    [TestMethod]
    public void AnUnrelatedCrashThatMerelyLogsThePhraseIsNotClaimedAsAMissingLibrary()
    {
        // This is the measured false positive that the loose substring match produced: an
        // out-of-memory death reported as an environment problem, with a root-level install as the
        // remediation. Nothing about it is environmental and nothing about it should be skipped.
        Assert.IsNull(PlaywrightChromium.Describe(UnrelatedCrashMentioningTheWords));
    }

    [TestMethod]
    public void ThePhraseWithoutTheLoaderSignatureIsNotClaimedAsAMissingLibrary()
        => Assert.IsNull(PlaywrightChromium.Describe(
            "Browser closed unexpectedly: loading shared libraries\n"
            + "  - <process did exit: exitCode=127, signal=null>"));

    [TestMethod]
    public void AValidLookingTailAfterAnOrdinaryPhraseIsNotClaimedAsAMissingLibrary()
        => Assert.IsNull(PlaywrightChromium.Describe(
            "plugin finished loading shared libraries: libnss3.so: "
            + "cannot open shared object file: No such file or directory\n"
            + "  - <process did exit: exitCode=127, signal=null>"));

    [TestMethod]
    public void TheLoaderSignatureWithoutItsExitStatusIsNotClaimedAsAMissingLibrary()
        => Assert.IsNull(PlaywrightChromium.Describe(
            "Target page, context or browser has been closed\n"
            + "[pid=1][err] chrome-headless-shell: error while loading shared libraries: libnss3.so: "
            + "cannot open shared object file: No such file or directory"));

    [TestMethod]
    public void TheCompleteLoaderSignatureWithTheWrongExitStatusIsNotClaimedAsAMissingLibrary()
        => Assert.IsNull(PlaywrightChromium.Describe(
            "chrome-headless-shell: error while loading shared libraries: libnss3.so: "
            + "cannot open shared object file: No such file or directory\n"
            + "  - <process did exit: exitCode=1, signal=null>"));

    [TestMethod]
    public void ExitCodeWith127AsOnlyAPrefixIsNotClaimedAsAMissingLibrary()
        => Assert.IsNull(PlaywrightChromium.Describe(
            "chrome-headless-shell: error while loading shared libraries: libnss3.so: "
            + "cannot open shared object file: No such file or directory\n"
            + "  - <process did exit: exitCode=1270, signal=null>"));

    [TestMethod]
    public void TheSignatureAndExitStatusWithoutTheMissingFileClauseAreNotClaimedAsAMissingLibrary()
        => Assert.IsNull(PlaywrightChromium.Describe(
            "chrome-headless-shell: error while loading shared libraries: bad\n"
            + "  - <process did exit: exitCode=127, signal=null>"));

    [TestMethod]
    public void AnArbitraryMissingFileReasonIsNotClaimedAsAMissingLibrary()
        => Assert.IsNull(PlaywrightChromium.Describe(
            "chrome-headless-shell: error while loading shared libraries: libnss3.so: "
            + "cannot open shared object file: arbitrary\n"
            + "  - <process did exit: exitCode=127, signal=null>"));

    [TestMethod]
    public void AMissingExecutableIsReportedAsAnUninstalledBrowserWithTheInstallCommand()
    {
        var diagnosis = PlaywrightChromium.Describe(MissingExecutableFailure);

        Assert.IsNotNull(diagnosis);
        StringAssert.Contains(diagnosis, "is not installed", StringComparison.Ordinal);
        StringAssert.Contains(diagnosis, "scripts/test:cameraagent-ui --install-browser", StringComparison.Ordinal);

        // The path has to be the one the failure carried, which is the headless shell. The guard this
        // replaced named the full browser, and so did the first version of this replacement.
        StringAssert.Contains(
            diagnosis,
            "/chromium_headless_shell-1234/chrome-headless-shell-linux64/chrome-headless-shell",
            StringComparison.Ordinal);
        Assert.IsFalse(diagnosis.Contains("/chrome-linux64/", StringComparison.Ordinal));
        StringAssert.Contains(diagnosis, "headless shell", StringComparison.Ordinal);
    }

    [TestMethod]
    public void AnUnrecognisedFailureIsNotClaimedAsAnEnvironmentProblem()
        => Assert.IsNull(PlaywrightChromium.Describe(
            "net::ERR_CONNECTION_REFUSED at http://127.0.0.1:5130/gallery"));
}
