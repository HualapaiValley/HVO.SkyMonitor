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
/// that owner, other than the bounded owner-bootstrap endpoints, with <c>403</c> and
/// <c>X-HVO-Authorization-Reason: owner-password-change-required</c> until the temporary password
/// is replaced. A harness that only posts the login form therefore holds a session that is
/// authenticated but not authorized for operations.
/// </summary>
internal static class OwnerBootstrapSession
{
    /// <summary>
    /// Mirrors the response header <see cref="OwnerBootstrapGateMiddleware"/> stamps on a refusal.
    /// The middleware writes the name as a literal, so this is deliberately a local copy rather
    /// than a shared constant; deduplicating it would put runtime authorization code into an
    /// otherwise test-only change.
    /// </summary>
    internal const string AuthorizationReasonHeader = "X-HVO-Authorization-Reason";

    internal const string ReplacementPasswordSuffix = "Z9!";
    internal const string OperationsProbePath = "/api/v1/operations/gallery/?pageSize=1";

    /// <summary>
    /// The replacement form bounds the new password; see the <c>StringLength</c> attribute on
    /// <c>ReplaceTemporaryPassword.InputModel.NewPassword</c>.
    /// </summary>
    private const int MaximumReplacementPasswordLength = 100;

    private const int MaximumReportedBodyLength = 512;

    /// <summary>
    /// Drives an already authenticated owner session to <see cref="OwnerBootstrapStates.Ready"/>.
    /// Returns the password the session authenticates with afterwards: the replacement when one was
    /// performed, or <paramref name="temporaryPassword"/> unchanged when the owner was already
    /// ready and no replacement was needed.
    /// </summary>
    internal static async Task<string> EnsureReadyOwnerAsync(
        HttpClient client,
        string temporaryPassword,
        string sessionName)
    {
        ArgumentNullException.ThrowIfNull(client);

        var state = await ReadBootstrapStateAsync(client, sessionName).ConfigureAwait(false);
        if (string.Equals(state, OwnerBootstrapStates.Ready, StringComparison.Ordinal))
        {
            return temporaryPassword;
        }

        if (state is not (OwnerBootstrapStates.TemporaryPassword or OwnerBootstrapStates.PasswordChangeRequired))
        {
            Assert.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"The {sessionName} agent reports owner bootstrap state '{state}'; an operations session cannot be established."));
        }

        var replacement = temporaryPassword + ReplacementPasswordSuffix;
        Assert.IsLessThanOrEqualTo(
            MaximumReplacementPasswordLength,
            replacement.Length,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The generated {sessionName} replacement password is {replacement.Length} characters, which the replacement form would reject as longer than {MaximumReplacementPasswordLength}."));

        using (var page = await client.GetAsync(
            new Uri(OwnerBootstrapGateMiddleware.ReplacementPath, UriKind.Relative)).ConfigureAwait(false))
        {
            page.EnsureSuccessStatusCode();
            var html = await page.Content.ReadAsStringAsync().ConfigureAwait(false);
            var token = Regex.Match(
                html,
                "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
                RegexOptions.CultureInvariant);
            Assert.IsTrue(
                token.Success,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The {sessionName} temporary-password replacement form did not render an antiforgery token."));
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
            // A refused replacement re-renders the form rather than failing the request, so only the
            // post-redirect location proves it was accepted. The re-rendered page echoes the posted
            // passwords back into its inputs, so it must never become an assertion message: that text
            // is written to the retained TRX, which the smoke then rejects as a leaked secret.
            var refusedMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"The {sessionName} temporary-password replacement was refused; the agent re-rendered the replacement form instead of redirecting.");
            Assert.AreEqual("/", response.RequestMessage?.RequestUri?.AbsolutePath, refusedMessage);
        }

        Assert.AreEqual(
            OwnerBootstrapStates.Ready,
            await ReadBootstrapStateAsync(client, sessionName).ConfigureAwait(false),
            string.Create(
                CultureInfo.InvariantCulture,
                $"The {sessionName} owner did not reach the ready state after replacing its temporary password."));
        return replacement;
    }

    internal static async Task<string> ReadBootstrapStateAsync(HttpClient client, string sessionName)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.GetAsync(
            new Uri(OwnerBootstrapGateMiddleware.StatusPath, UriKind.Relative)).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(
            HttpStatusCode.OK,
            response.StatusCode,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The {sessionName} owner bootstrap status was not readable: {Summarize(payload)}"));
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
            $"The {sessionName} owner session is not authorized for the operations API: GET {OperationsProbePath} returned {(int)response.StatusCode} with {AuthorizationReasonHeader}={reason} and body {Summarize(body)}"));
    }

    private static string Summarize(string body)
        => body.Length <= MaximumReportedBodyLength
            ? body
            : string.Concat(body.AsSpan(0, MaximumReportedBodyLength), "... (truncated)");
}
