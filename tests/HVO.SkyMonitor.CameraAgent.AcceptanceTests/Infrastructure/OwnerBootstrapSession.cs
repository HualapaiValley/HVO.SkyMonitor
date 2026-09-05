using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Data;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

/// <summary>
/// Establishes the local owner session that the CameraAgent operations API actually requires.
/// A newly provisioned agent seeds its owner with a temporary password, and
/// <see cref="OwnerBootstrapGateMiddleware"/> refuses every authenticated <c>/api</c> request from
/// that owner with <c>403</c> and <c>X-HVO-Authorization-Reason: owner-password-change-required</c>
/// until the temporary password is replaced. A harness that only posts the login form therefore
/// holds a session that is authenticated but not authorized for operations.
/// </summary>
internal static class OwnerBootstrapSession
{
    internal const string AuthorizationReasonHeader = "X-HVO-Authorization-Reason";
    internal const string ReplacementPasswordSuffix = "Z9!";
    internal const string OperationsProbePath = "/api/v1/operations/gallery/?pageSize=1";

    /// <summary>
    /// Drives an already authenticated owner session to <see cref="OwnerBootstrapStates.Ready"/> and
    /// returns the password that session now authenticates with.
    /// </summary>
    internal static async Task<string> EnsureReadyOwnerAsync(HttpClient client, string temporaryPassword)
    {
        ArgumentNullException.ThrowIfNull(client);

        var state = await ReadBootstrapStateAsync(client).ConfigureAwait(false);
        if (string.Equals(state, OwnerBootstrapStates.Ready, StringComparison.Ordinal))
        {
            return temporaryPassword;
        }

        if (state is not (OwnerBootstrapStates.TemporaryPassword or OwnerBootstrapStates.PasswordChangeRequired))
        {
            Assert.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"The agent reports owner bootstrap state '{state}'; an operations session cannot be established."));
        }

        var replacement = temporaryPassword + ReplacementPasswordSuffix;
        using (var page = await client.GetAsync(
            new Uri(OwnerBootstrapGateMiddleware.ReplacementPath, UriKind.Relative)).ConfigureAwait(false))
        {
            page.EnsureSuccessStatusCode();
            var html = await page.Content.ReadAsStringAsync().ConfigureAwait(false);
            var token = Regex.Match(
                html,
                "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
                RegexOptions.CultureInvariant);
            Assert.IsTrue(token.Success, "The temporary-password replacement form did not render an antiforgery token.");
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
                ["Input.CurrentPassword"] = temporaryPassword,
                ["Input.NewPassword"] = replacement,
                ["Input.ConfirmPassword"] = replacement,
                ["_handler"] = "replace-temporary-password"
            });
            using var response = await client.PostAsync(
                new Uri(OwnerBootstrapGateMiddleware.ReplacementPath, UriKind.Relative), form).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            // The page re-renders itself with a validation message instead of failing the request,
            // so only the post-redirect location proves the replacement was accepted.
            Assert.AreEqual(
                "/",
                response.RequestMessage?.RequestUri?.AbsolutePath,
                await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        Assert.AreEqual(OwnerBootstrapStates.Ready, await ReadBootstrapStateAsync(client).ConfigureAwait(false));
        return replacement;
    }

    internal static async Task<string> ReadBootstrapStateAsync(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.GetAsync(
            new Uri(OwnerBootstrapGateMiddleware.StatusPath, UriKind.Relative)).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, payload);
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("state").GetString() ?? string.Empty;
    }

    /// <summary>
    /// Proves the session can read the operations API before a long poll starts, so a refused
    /// session reports the status, denial reason, and body instead of an opaque failure later.
    /// </summary>
    internal static async Task AssertOperationsAuthorizedAsync(HttpClient client, string sessionName)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.GetAsync(
            new Uri(OperationsProbePath, UriKind.Relative)).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return;
        }

        var reason = response.Headers.TryGetValues(AuthorizationReasonHeader, out var values)
            ? string.Join(",", values)
            : "<none>";
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.Fail(string.Create(
            CultureInfo.InvariantCulture,
            $"The {sessionName} owner session is not authorized for the operations API: " +
            $"GET {OperationsProbePath} returned {(int)response.StatusCode} " +
            $"with {AuthorizationReasonHeader}={reason} and body {body}"));
    }
}
