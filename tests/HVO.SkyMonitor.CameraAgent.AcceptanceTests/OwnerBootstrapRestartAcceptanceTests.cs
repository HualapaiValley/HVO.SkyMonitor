using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Data;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured async disposal would hide the strongly typed acceptance fixture.")]
public sealed class OwnerBootstrapRestartAcceptanceTests
{
    private const string ReplacementPassword = "RestartReplacement!418";

    [TestMethod]
    public async Task InstallerStyleBootstrapAndReplacementSurviveHostRestarts()
    {
        await using var host = await CameraAgentKestrelFixture.CreateAsync(
            requireOwnerPasswordReplacement: true).ConfigureAwait(false);
        using (var seededClient = await host.CreateOwnerClientAsync().ConfigureAwait(false))
        {
            Assert.AreEqual(
                OwnerBootstrapStates.TemporaryPassword,
                await ReadBootstrapStateAsync(seededClient).ConfigureAwait(false));
        }

        await host.RestartWithoutPasswordAuthorityAsync().ConfigureAwait(false);
        using var replacingClient = await host.CreateOwnerClientAsync().ConfigureAwait(false);
        using var staleClient = await host.CreateOwnerClientAsync().ConfigureAwait(false);
        using var restartStaleClient = await host.CreateOwnerClientAsync().ConfigureAwait(false);
        Assert.AreEqual(
            OwnerBootstrapStates.PasswordChangeRequired,
            await ReadBootstrapStateAsync(replacingClient).ConfigureAwait(false));
        using (var healthClient = new HttpClient { BaseAddress = host.BaseAddress })
        using (var health = await healthClient.GetAsync(new Uri("/health", UriKind.Relative)).ConfigureAwait(false))
        {
            var healthJson = await health.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode, healthJson);
            StringAssert.Contains(healthJson, "Owner bootstrap is operational.", StringComparison.Ordinal);
            Assert.IsFalse(healthJson.Contains(OwnerBootstrapStates.PasswordChangeRequired, StringComparison.Ordinal));
            Assert.IsFalse(healthJson.Contains(OwnerBootstrapStates.TemporaryPassword, StringComparison.Ordinal));
        }
        using (var denied = await replacingClient.GetAsync(
            new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        await AssertSamePasswordRejectedAsync(replacingClient).ConfigureAwait(false);
        Assert.AreEqual(
            OwnerBootstrapStates.PasswordChangeRequired,
            await ReadBootstrapStateAsync(replacingClient).ConfigureAwait(false));
        await ReplacePasswordAsync(replacingClient).ConfigureAwait(false);
        Assert.AreEqual(
            OwnerBootstrapStates.Ready,
            await ReadBootstrapStateAsync(replacingClient).ConfigureAwait(false));
        using (var stale = await staleClient.GetAsync(
            new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, stale.StatusCode);
        }

        await host.RestartAsync().ConfigureAwait(false);
        using (var restartStale = await restartStaleClient.GetAsync(
            new Uri(host.BaseAddress, "/api/v1/operations/summary")).ConfigureAwait(false))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, restartStale.StatusCode);
        }
        using var oldCredentialClient = await LoginAsync(
            host.BaseAddress,
            CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        using (var oldCredentialStatus = await oldCredentialClient.GetAsync(
            new Uri("/api/internal/owner-bootstrap/status", UriKind.Relative)).ConfigureAwait(false))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, oldCredentialStatus.StatusCode);
        }

        using var replacementClient = await LoginAsync(host.BaseAddress, ReplacementPassword).ConfigureAwait(false);
        Assert.AreEqual(
            OwnerBootstrapStates.Ready,
            await ReadBootstrapStateAsync(replacementClient).ConfigureAwait(false));
        using var operations = await replacementClient.GetAsync(
            new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, operations.StatusCode);
    }

    private static async Task AssertSamePasswordRejectedAsync(HttpClient client)
    {
        using var response = await SubmitPasswordReplacementAsync(
            client,
            CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(
            OwnerBootstrapGateMiddleware.ReplacementPath,
            response.RequestMessage?.RequestUri?.AbsolutePath);
        StringAssert.Contains(
            content,
            "The new password must be different from the current password.",
            StringComparison.Ordinal);
    }

    private static async Task ReplacePasswordAsync(HttpClient client)
    {
        using var response = await SubmitPasswordReplacementAsync(client, ReplacementPassword).ConfigureAwait(false);
        Assert.AreEqual("/", response.RequestMessage?.RequestUri?.AbsolutePath);
    }

    private static async Task<HttpResponseMessage> SubmitPasswordReplacementAsync(
        HttpClient client,
        string newPassword)
    {
        using var page = await client.GetAsync(
            new Uri("/Account/ReplaceTemporaryPassword", UriKind.Relative)).ConfigureAwait(false);
        page.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync().ConfigureAwait(false));
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.CurrentPassword"] = CameraAgentKestrelFixture.OwnerPassword,
            ["Input.NewPassword"] = newPassword,
            ["Input.ConfirmPassword"] = newPassword,
            ["_handler"] = "replace-temporary-password"
        });
        var response = await client.PostAsync(
            new Uri("/Account/ReplaceTemporaryPassword", UriKind.Relative), form).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response;
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "HttpClient owns the handler and the caller owns the returned client.")]
    private static async Task<HttpClient> LoginAsync(Uri baseAddress, string password)
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
            var token = ExtractAntiforgeryToken(await page.Content.ReadAsStringAsync().ConfigureAwait(false));
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["Input.Email"] = CameraAgentKestrelFixture.OwnerEmail,
                ["Input.Password"] = password,
                ["Input.RememberMe"] = "false",
                ["_handler"] = "login"
            });
            using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            succeeded = true;
            return client;
        }
        finally
        {
            if (!succeeded)
            {
                client.Dispose();
            }
        }
    }

    private static async Task<string> ReadBootstrapStateAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            new Uri("/api/internal/owner-bootstrap/status", UriKind.Relative)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        return json.RootElement.GetProperty("state").GetString()
            ?? throw new InvalidDataException("Owner bootstrap status omitted its state.");
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        return match.Success
            ? WebUtility.HtmlDecode(match.Groups[1].Value)
            : throw new InvalidDataException("The antiforgery token was not rendered.");
    }
}
