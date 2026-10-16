using System.Diagnostics.CodeAnalysis;
using System.Net;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Data;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

/// <summary>
/// Drives the documented first sign-in of a new CameraAgent (issue #1278) through the real host: the owner seeded as
/// <c>admin</c> / <c>skymonitor</c>, sign-in by user name or email, the unbypassable replacement gate, the relaxed
/// password policy, and the sign-in lockout.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured async disposal would hide the strongly typed acceptance fixture.")]
public sealed class DefaultOwnerSignInAcceptanceTests
{
    private const string DefaultUserName = "admin";
    private const string DefaultPassword = "skymonitor";
    private const string ReplacementPassword = "abcdefgh";
    private const string SessionName = "default-owner";
    private const string OperationsPath = "/api/v1/operations/summary";

    [TestMethod]
    public async Task DefaultOwnerSignsInByUserNameOrEmailAndMustReplaceTheDefault()
    {
        await using var host = await CameraAgentKestrelFixture.CreateAsync(
            requireOwnerPasswordReplacement: true,
            ownerPassword: DefaultPassword).ConfigureAwait(false);

        var (byUserName, landing, _) = await SignInAsync(host.BaseAddress, DefaultUserName, DefaultPassword).ConfigureAwait(false);
        using var ownerClient = byUserName;
        Assert.AreEqual(OwnerBootstrapGateMiddleware.ReplacementPath, landing);
        Assert.AreEqual(
            OwnerBootstrapStates.TemporaryPassword,
            await OwnerBootstrapSession.ReadBootstrapStateAsync(ownerClient, SessionName).ConfigureAwait(false));
        await AssertOperationsDeniedUntilReplacementAsync(ownerClient).ConfigureAwait(false);
        using (var home = await ownerClient.GetAsync(new Uri("/", UriKind.Relative)).ConfigureAwait(false))
        {
            Assert.AreEqual(OwnerBootstrapGateMiddleware.ReplacementPath, home.RequestMessage?.RequestUri?.AbsolutePath);
        }

        var (byEmail, emailLanding, _) = await SignInAsync(
            host.BaseAddress, CameraAgentKestrelFixture.OwnerEmail, DefaultPassword).ConfigureAwait(false);
        using (byEmail)
        {
            Assert.AreEqual(OwnerBootstrapGateMiddleware.ReplacementPath, emailLanding);
            await AssertOperationsDeniedUntilReplacementAsync(byEmail).ConfigureAwait(false);
        }

        await AssertReplacementRefusedAsync(ownerClient, DefaultPassword, "The new password must be different from the current password.")
            .ConfigureAwait(false);
        await AssertReplacementRefusedAsync(ownerClient, "SkyMonitor", "The new password cannot be the default password.")
            .ConfigureAwait(false);
        await AssertReplacementRefusedAsync(ownerClient, "abcdefg", "minimum length of 8").ConfigureAwait(false);
        Assert.AreEqual(
            OwnerBootstrapStates.TemporaryPassword,
            await OwnerBootstrapSession.ReadBootstrapStateAsync(ownerClient, SessionName).ConfigureAwait(false));

        using (var accepted = await SubmitReplacementAsync(ownerClient, ReplacementPassword).ConfigureAwait(false))
        {
            Assert.AreEqual("/", accepted.RequestMessage?.RequestUri?.AbsolutePath);
        }
        Assert.AreEqual(
            OwnerBootstrapStates.Ready,
            await OwnerBootstrapSession.ReadBootstrapStateAsync(ownerClient, SessionName).ConfigureAwait(false));
        using (var operations = await ownerClient.GetAsync(new Uri(OperationsPath, UriKind.Relative)).ConfigureAwait(false))
        {
            Assert.AreEqual(HttpStatusCode.OK, operations.StatusCode);
        }

        var (staleDefault, staleLanding, staleHtml) = await SignInAsync(
            host.BaseAddress, DefaultUserName, DefaultPassword).ConfigureAwait(false);
        using (staleDefault)
        {
            Assert.AreEqual("/Account/Login", staleLanding);
            StringAssert.Contains(staleHtml, "Error: Invalid login attempt.", StringComparison.Ordinal);
        }

        var (replaced, replacedLanding, _) = await SignInAsync(
            host.BaseAddress, DefaultUserName, ReplacementPassword).ConfigureAwait(false);
        using (replaced)
        {
            Assert.AreEqual("/", replacedLanding);
            using var operations = await replaced.GetAsync(new Uri(OperationsPath, UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, operations.StatusCode);
        }
    }

    [TestMethod]
    public async Task RepeatedFailedSignInsLockTheOwnerOut()
    {
        await using var host = await CameraAgentKestrelFixture.CreateAsync(
            requireOwnerPasswordReplacement: true,
            ownerPassword: DefaultPassword).ConfigureAwait(false);

        for (var attempt = 1; attempt < 5; attempt++)
        {
            var (client, landing, html) = await SignInAsync(host.BaseAddress, DefaultUserName, "wrong-password")
                .ConfigureAwait(false);
            using (client)
            {
                Assert.AreEqual("/Account/Login", landing);
                StringAssert.Contains(html, "Error: Invalid login attempt.", StringComparison.Ordinal);
            }
        }

        var (fifth, fifthLanding, fifthHtml) = await SignInAsync(host.BaseAddress, DefaultUserName, "wrong-password")
            .ConfigureAwait(false);
        using (fifth)
        {
            Assert.AreEqual("/Account/Login", fifthLanding);
            StringAssert.Contains(fifthHtml, "This account is locked after 5 failed sign-in attempts", StringComparison.Ordinal);
        }

        // The correct password is refused while the lockout lasts, by user name and by email alike.
        foreach (var signInName in new[] { DefaultUserName, CameraAgentKestrelFixture.OwnerEmail })
        {
            var (locked, lockedLanding, lockedHtml) = await SignInAsync(host.BaseAddress, signInName, DefaultPassword)
                .ConfigureAwait(false);
            using (locked)
            {
                Assert.AreEqual("/Account/Login", lockedLanding, signInName);
                StringAssert.Contains(lockedHtml, "Try again in 5 minutes.", StringComparison.Ordinal);
                using var status = await locked.GetAsync(
                    new Uri(OwnerBootstrapGateMiddleware.StatusPath, UriKind.Relative)).ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.Unauthorized, status.StatusCode, signInName);
            }
        }
    }

    private static async Task AssertOperationsDeniedUntilReplacementAsync(HttpClient client)
    {
        using var denied = await client.GetAsync(new Uri(OperationsPath, UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.AreEqual(
            OwnerBootstrapStates.PasswordChangeRequired,
            denied.Headers.GetValues(OwnerBootstrapGateMiddleware.AuthorizationReasonHeader).Single());
    }

    private static async Task AssertReplacementRefusedAsync(HttpClient client, string newPassword, string expectedMessage)
    {
        using var response = await SubmitReplacementAsync(client, newPassword).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(OwnerBootstrapGateMiddleware.ReplacementPath, response.RequestMessage?.RequestUri?.AbsolutePath);
        Assert.IsTrue(
            html.Contains(expectedMessage, StringComparison.Ordinal),
            $"The refused replacement did not report '{expectedMessage}'.");
    }

    private static async Task<HttpResponseMessage> SubmitReplacementAsync(HttpClient client, string newPassword)
    {
        using var page = await client.GetAsync(
            new Uri(OwnerBootstrapGateMiddleware.ReplacementPath, UriKind.Relative)).ConfigureAwait(false);
        page.EnsureSuccessStatusCode();
        var token = OwnerBootstrapSession.ExtractAntiforgeryToken(
            await page.Content.ReadAsStringAsync().ConfigureAwait(false),
            $"{SessionName} temporary-password replacement form");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.CurrentPassword"] = DefaultPassword,
            ["Input.NewPassword"] = newPassword,
            ["Input.ConfirmPassword"] = newPassword,
            ["_handler"] = "replace-temporary-password"
        });
        var response = await client.PostAsync(
            new Uri(OwnerBootstrapGateMiddleware.ReplacementPath, UriKind.Relative), form).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <summary>
    /// Posts the sign-in form and follows its redirect, returning the session, the path it landed on, and that page.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "HttpClient owns the handler and the caller owns the returned client.")]
    private static async Task<(HttpClient Client, string? LandingPath, string Html)> SignInAsync(
        Uri baseAddress,
        string signInName,
        string password)
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer(),
            CheckCertificateRevocationList = true
        })
        {
            BaseAddress = baseAddress
        };
        var succeeded = false;
        try
        {
            using var page = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
            page.EnsureSuccessStatusCode();
            var token = OwnerBootstrapSession.ExtractAntiforgeryToken(
                await page.Content.ReadAsStringAsync().ConfigureAwait(false),
                $"{SessionName} login form");
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Input.Email"] = signInName,
                ["Input.Password"] = password,
                ["Input.RememberMe"] = "false",
                ["_handler"] = "login"
            });
            using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            succeeded = true;
            return (client, response.RequestMessage?.RequestUri?.AbsolutePath, html);
        }
        finally
        {
            if (!succeeded)
            {
                client.Dispose();
            }
        }
    }
}
