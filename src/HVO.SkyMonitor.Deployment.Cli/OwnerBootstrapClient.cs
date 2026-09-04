using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.Deployment;

internal interface IOwnerBootstrapClient
{
    Task WaitForHealthAsync(CancellationToken cancellationToken, TimeSpan? timeout = null);
    Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken);
    Task<string> ReadInstallationStateAsync(string verificationToken, CancellationToken cancellationToken);
    Task<string> VerifyInstallationAsync(
        string verificationToken,
        InstallationVerificationExpectation expectation,
        CancellationToken cancellationToken);
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
    bool AllowCompletedPasswordReplacement = false);

internal sealed class OwnerBootstrapClient(Uri baseAddress) : IOwnerBootstrapClient
{
    private static readonly Regex AntiforgeryToken = new(
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public async Task WaitForHealthAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            CheckCertificateRevocationList = true
        };
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(5)
        };
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        string? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var alive = await client.GetAsync(new Uri("/alive", UriKind.Relative), cancellationToken).ConfigureAwait(false);
                using var health = await client.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken).ConfigureAwait(false);
                latest = await health.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (alive.IsSuccessStatusCode && health.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(latest);
                    var catalog = document.RootElement.GetProperty("checks").EnumerateArray().Single(item =>
                        item.GetProperty("name").GetString() == "catalog");
                    var data = catalog.GetProperty("data");
                    if (data.GetProperty("CatalogId").GetString() == ProductionCatalog.CatalogId &&
                        data.GetProperty("CatalogVersion").GetString() == "4.2" &&
                        data.GetProperty("SchemaVersion").GetString() == "2" &&
                        data.GetProperty("PreprocessingVersion").GetString() == "3" &&
                        data.GetProperty("RowCount").GetInt64() == ProductionCatalog.RowCount)
                    {
                        return;
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                latest = exception.Message;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        throw new InstallerException($"CameraAgent health did not become ready: {Redaction.SafeDiagnostic(latest ?? string.Empty)}");
    }

    public async Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken)
    {
        using var client = await LoginAsync(ownerEmail, password, cancellationToken).ConfigureAwait(false);
        using var status = await client.GetAsync(
            new Uri("/api/internal/owner-bootstrap/status", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        if (status.StatusCode != HttpStatusCode.OK)
        {
            throw new InstallerException("The generated owner credential did not authenticate.");
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
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            CheckCertificateRevocationList = true
        };
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", verificationToken);
        using var response = await client.GetAsync(
            new Uri("/api/internal/owner-bootstrap/installation-verification", UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
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
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            CheckCertificateRevocationList = true
        };
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Add("X-HVO-Installation-Token", verificationToken);
        using var response = await client.GetAsync(
            new Uri("/api/internal/owner-bootstrap/installation-verification", UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        return json.RootElement.GetProperty("ownerBootstrapState").GetString()
            ?? throw new InstallerException("CameraAgent installation verification omitted its owner bootstrap state.");
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
