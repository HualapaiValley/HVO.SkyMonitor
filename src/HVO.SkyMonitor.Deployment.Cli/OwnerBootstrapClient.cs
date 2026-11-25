using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal interface IOwnerBootstrapClient
{
    /// <summary>
    /// Waits for successful application health and the catalog check for <paramref name="expectedCatalog"/>.
    /// With <paramref name="allowQuarantinedReadiness"/>, an otherwise ready host whose only unhealthy checks are a
    /// processing-lane quarantine is ready too.
    /// </summary>
    Task WaitForHealthAsync(
        ApprovedCatalogContract expectedCatalog,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        bool allowQuarantinedReadiness = false);
    Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken);
    Task<string> ReadInstallationStateAsync(string verificationToken, CancellationToken cancellationToken);
    // Pre-mutation callers must compare the current running installation, not only read its owner state.
    Task<string> ReadInstallationStateAsync(
        string verificationToken, InstallationVerificationExpectation expectation, CancellationToken cancellationToken)
        => VerifyInstallationAsync(verificationToken, expectation, cancellationToken);
    Task<string> VerifyInstallationAsync(
        string verificationToken,
        InstallationVerificationExpectation expectation,
        CancellationToken cancellationToken);

    Task VerifyFleetDisabledAsync(CancellationToken cancellationToken)
        => throw new InstallerException("The running instance did not prove that Fleet heartbeat is disabled.");
}

internal sealed record InstallationVerificationExpectation(
    string AgentId,
    string OwnerEmail,
    string OwnerBootstrapState,
    string ConfigurationSha256,
    string RigProfileSha256,
    string ScheduleSha256,
    string DeploymentLocationId,
    long DeploymentLocationVersion,
    string DeploymentLocationSha256,
    HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile ReplayProfile,
    HVO.SkyMonitor.Deployment.Contracts.CatalogInstallationIdentity Catalog,
    bool AllowCompletedPasswordReplacement = false,
    CatalogInstallationIdentity? DeepSkyCatalog = null);

internal sealed class OwnerBootstrapClient(
    Uri baseAddress,
    HttpMessageHandler? handler,
    TimeSpan? protectedReadTimeout,
    TimeProvider timeProvider) : IOwnerBootstrapClient
{
    internal static readonly TimeSpan ProtectedReadTimeout = TimeSpan.FromSeconds(120);
    internal const string VerificationStateHeader = "X-HVO-Installation-Verification";
    internal const string ScrubPendingState = "scrub-pending";

    private readonly TimeSpan _protectedReadTimeout = protectedReadTimeout is null
        ? ProtectedReadTimeout
        : protectedReadTimeout >= TimeSpan.FromMilliseconds(1) && protectedReadTimeout <= ProtectedReadTimeout
            ? protectedReadTimeout.Value
            : throw new ArgumentOutOfRangeException(nameof(protectedReadTimeout));

    private static readonly Regex AntiforgeryToken = new(
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    // The native startup acceptance test also activates this constructor via reflection.
    public OwnerBootstrapClient(
        Uri baseAddress,
        HttpMessageHandler? handler = null,
        TimeSpan? protectedReadTimeout = null)
        : this(baseAddress, handler, protectedReadTimeout, TimeProvider.System)
    {
    }

    public async Task VerifyFleetDisabledAsync(CancellationToken cancellationToken)
    {
        using var ownedHandler = handler is null ? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false } : null;
        using var client = new HttpClient(handler ?? ownedHandler!, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            MaxResponseContentBufferSize = 1024 * 1024,
            Timeout = TimeSpan.FromSeconds(5)
        };
        try
        {
            using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes);
                if (ReportsFleetDisabled(document.RootElement)) return;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException ||
                                          exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InstallerException("Leave-paused could not read the running instance's Fleet heartbeat state.", exception);
        }
        throw new InstallerException("Leave-paused requires the running instance to report that Fleet heartbeat is disabled.");
    }

    internal static bool ReportsFleetDisabled(JsonElement document)
        => document.ValueKind == JsonValueKind.Object && document.TryGetProperty("checks", out var checks) &&
           checks.ValueKind == JsonValueKind.Array &&
           checks.EnumerateArray().Where(static item => HasString(item, "name", "fleet-heartbeat")).ToArray() is [var fleet] &&
           HasString(fleet, "status", "Healthy") &&
           HasString(fleet, "description", "Fleet heartbeat is disabled/not configured.");

    private static bool HasString(JsonElement element, string name, string value)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var field) &&
           field.ValueKind == JsonValueKind.String && field.GetString() == value;

    public async Task WaitForHealthAsync(
        ApprovedCatalogContract expectedCatalog,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        bool allowQuarantinedReadiness = false)
    {
        ArgumentNullException.ThrowIfNull(expectedCatalog);
        cancellationToken.ThrowIfCancellationRequested();
        var budget = timeout ?? TimeSpan.FromMinutes(3);
        using var deadline = new CancellationTokenSource(budget, timeProvider);
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var probeToken = probeCancellation.Token;
        using var ownedHandler = handler is null
            ? new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                CheckCertificateRevocationList = true
            }
            : null;
        using var client = new HttpClient(handler ?? ownedHandler!, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            MaxResponseContentBufferSize = 1024 * 1024,
            Timeout = TimeSpan.FromSeconds(5)
        };
        string? latest = null;
        try
        {
            while (true)
            {
                probeToken.ThrowIfCancellationRequested();
                try
                {
                    using var alive = await client.GetAsync(new Uri("/alive", UriKind.Relative), probeToken).ConfigureAwait(false);
                    using var health = await client.GetAsync(new Uri("/health", UriKind.Relative), probeToken).ConfigureAwait(false);
                    latest = await health.Content.ReadAsStringAsync(probeToken).ConfigureAwait(false);
                    probeToken.ThrowIfCancellationRequested();
                    if (alive.IsSuccessStatusCode && health.IsSuccessStatusCode)
                    {
                        using var document = JsonDocument.Parse(latest);
                        if (document.RootElement.GetProperty("status").GetString() is "Healthy" or "Degraded" &&
                            QuarantinedReadiness.CatalogMatches(document.RootElement, expectedCatalog))
                        {
                            probeToken.ThrowIfCancellationRequested();
                            return;
                        }
                        latest = "CameraAgent readiness report or catalog identity does not match the instance.";
                    }
                    else if (allowQuarantinedReadiness && alive.IsSuccessStatusCode &&
                             health.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        using var document = JsonDocument.Parse(latest);
                        if (QuarantinedReadiness.TryAccept(document.RootElement, expectedCatalog, out var reason))
                        {
                            probeToken.ThrowIfCancellationRequested();
                            return;
                        }
                        latest = reason;
                    }
                }
                catch (OperationCanceledException) when (!probeToken.IsCancellationRequested)
                {
                    latest = "A CameraAgent health probe timed out.";
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    latest = exception.Message;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, probeToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || deadline.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InstallerException(
                $"CameraAgent health did not become ready within {budget.TotalSeconds:g} seconds: {Redaction.SafeDiagnostic(latest ?? "No complete health report was received.")}");
        }
    }

    public async Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken)
    {
        using var client = await LoginAsync(ownerEmail, password, cancellationToken).ConfigureAwait(false);
        using var status = await client.GetAsync(
            new Uri("/api/internal/owner-bootstrap/status", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        if (status.StatusCode != HttpStatusCode.OK)
        {
            throw new InstallerException("The owner bootstrap credential did not authenticate.");
        }
        using var json = JsonDocument.Parse(await status.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        return json.RootElement.GetProperty("state").GetString()
            ?? throw new InstallerException("Owner bootstrap status omitted its state.");
    }

    public async Task<string> VerifyInstallationAsync(
        string verificationToken,
        InstallationVerificationExpectation expectation,
        CancellationToken cancellationToken)
    {
        using var json = await ReadInstallationVerificationAsync(verificationToken, cancellationToken).ConfigureAwait(false);
        var value = json.RootElement;
        var ownerBootstrapState = value.GetProperty("ownerBootstrapState").GetString();
        var replayProfileMatches = value.TryGetProperty("replayProfile", out var replayProfile)
            ? replayProfile.GetString() == expectation.ReplayProfile.ToString()
            : expectation.ReplayProfile == HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess;
        var matches = value.GetProperty("agentId").GetString() == expectation.AgentId &&
                      value.GetProperty("ownerEmail").GetString() == expectation.OwnerEmail &&
                      IsAllowedOwnerBootstrapState(
                          expectation.OwnerBootstrapState,
                          ownerBootstrapState,
                          expectation.AllowCompletedPasswordReplacement) &&
                      Lower(value, "configurationSha256") == expectation.ConfigurationSha256 &&
                      Lower(value, "rigProfileSha256") == expectation.RigProfileSha256 &&
                      Lower(value, "scheduleSha256") == expectation.ScheduleSha256 &&
                      value.GetProperty("deploymentLocationId").GetString() == expectation.DeploymentLocationId &&
                       value.GetProperty("deploymentLocationVersion").GetInt64() == expectation.DeploymentLocationVersion &&
                       Lower(value, "deploymentLocationSha256") == expectation.DeploymentLocationSha256 &&
                       value.GetProperty("rawIngressRoot").GetString() == "/app/data/raw" &&
                       replayProfileMatches &&
                       value.GetProperty("catalogId").GetString() == expectation.Catalog.CatalogId &&
                      value.GetProperty("packageVersion").GetString() == expectation.Catalog.PackageVersion &&
                      value.GetProperty("schemaVersion").GetString() == expectation.Catalog.SchemaVersion &&
                      value.GetProperty("preprocessingVersion").GetString() == expectation.Catalog.PreprocessingVersion &&
                      Lower(value, "databaseSha256") == expectation.Catalog.DatabaseSha256 &&
                      value.GetProperty("databaseLength").GetInt64() == expectation.Catalog.DatabaseLength &&
                      value.GetProperty("rowCount").GetInt64() == expectation.Catalog.RowCount;
        var hasDeepSky = value.TryGetProperty("deepSkyCatalog", out var deepSky) && deepSky.ValueKind != JsonValueKind.Null;
        matches &= expectation.DeepSkyCatalog is { } selectedDeepSky
            ? hasDeepSky && deepSky.GetProperty("catalogId").GetString() == selectedDeepSky.CatalogId &&
              deepSky.GetProperty("packageVersion").GetString() == selectedDeepSky.PackageVersion &&
              deepSky.GetProperty("schemaVersion").GetString() == selectedDeepSky.SchemaVersion &&
              deepSky.GetProperty("preprocessingVersion").GetString() == selectedDeepSky.PreprocessingVersion &&
              Lower(deepSky, "databaseSha256") == selectedDeepSky.DatabaseSha256
            : !hasDeepSky;
        if (!matches)
        {
            throw new InstallerException("CameraAgent reported installation identities that differ from the installer manifest.");
        }
        return ownerBootstrapState!;
    }

    public async Task<string> ReadInstallationStateAsync(
        string verificationToken,
        CancellationToken cancellationToken)
    {
        using var json = await ReadInstallationVerificationAsync(verificationToken, cancellationToken).ConfigureAwait(false);
        return json.RootElement.GetProperty("ownerBootstrapState").GetString()
            ?? throw new InstallerException("CameraAgent installation verification omitted its owner bootstrap state.");
    }

    private async Task<JsonDocument> ReadInstallationVerificationAsync(
        string verificationToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_protectedReadTimeout);
        using var ownedHandler = handler is null ? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            CheckCertificateRevocationList = true
        } : null;
        using var client = new HttpClient(handler ?? ownedHandler!, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            // One finite deadline covers both the request and response body, not just the headers.
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", verificationToken);
        try
        {
            while (true)
            {
                using (var response = await client.GetAsync(
                    new Uri("/api/internal/owner-bootstrap/installation-verification", UriKind.Relative), deadline.Token)
                    .ConfigureAwait(false))
                {
                    var scrubPending = response.StatusCode == HttpStatusCode.ServiceUnavailable &&
                        response.Headers.TryGetValues(VerificationStateHeader, out var states) &&
                        states.Contains(ScrubPendingState, StringComparer.Ordinal);
                    if (!scrubPending)
                    {
                        response.EnsureSuccessStatusCode();
                        var body = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        return JsonDocument.Parse(body);
                    }
                }
                // Only the explicit new-agent pending response is retryable. Requests,
                // retry delays and the final body all spend the same finite budget.
                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new InstallerException("CameraAgent installation verification request timed out.");
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned HttpClient owns the handler and the caller disposes the client.")]
    private async Task<HttpClient> LoginAsync(
        string ownerEmail,
        string password,
        CancellationToken cancellationToken)
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            CookieContainer = new CookieContainer(),
            CheckCertificateRevocationList = true
        })
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(15)
        };
        var succeeded = false;
        try
        {
            using var page = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative), cancellationToken)
                .ConfigureAwait(false);
            page.EnsureSuccessStatusCode();
            var html = await page.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var match = AntiforgeryToken.Match(html);
            if (!match.Success)
            {
                throw new InstallerException("The CameraAgent login page omitted its antiforgery token.");
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
                ["Input.Email"] = ownerEmail,
                ["Input.Password"] = password,
                ["Input.RememberMe"] = "false",
                ["_handler"] = "login"
            });
            using var login = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form, cancellationToken)
                .ConfigureAwait(false);
            if (login.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther) ||
                !IsLocalRedirect(login.Headers.Location))
            {
                throw new InstallerException("The CameraAgent login response was invalid.");
            }
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

    private bool IsLocalRedirect(Uri? location)
    {
        if (location is null)
        {
            return false;
        }
        if (!location.IsAbsoluteUri)
        {
            return !location.OriginalString.StartsWith("//", StringComparison.Ordinal) &&
                   !location.OriginalString.StartsWith("\\\\", StringComparison.Ordinal);
        }
        return location.Scheme == baseAddress.Scheme &&
               location.Host == baseAddress.Host &&
               location.Port == baseAddress.Port;
    }

    internal static bool IsAllowedOwnerBootstrapState(
        string installedState,
        string? currentState,
        bool allowCompletedPasswordReplacement)
        => currentState == installedState ||
           (allowCompletedPasswordReplacement &&
            installedState == "owner-password-change-required" && currentState == "owner-ready");

    private static string? Lower(JsonElement value, string property)
    {
        var text = value.GetProperty(property).GetString();
        return text is null ? null : Convert.ToHexStringLower(Convert.FromHexString(text));
    }
}
