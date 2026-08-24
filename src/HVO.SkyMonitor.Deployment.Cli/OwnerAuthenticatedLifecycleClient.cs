using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment;

internal sealed class OwnerAuthenticatedLifecycleClient(
    Uri baseAddress,
    string ownerEmail,
    string passwordFile) : ICameraAgentLifecycleClient
{
    private static readonly Regex AntiforgeryToken = new(
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public async Task<LifecycleContinuity> PauseAndDrainAsync(
        Guid operationId,
        string verificationToken,
        CancellationToken cancellationToken)
    {
        using var client = await LoginAsync(cancellationToken).ConfigureAwait(false);
        var before = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
        var (header, token) = await GetAntiforgeryAsync(client, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/operations/capture/pause")
        {
            Content = JsonContent.Create(new { expectedVersion = before.CaptureVersion, reason = "transactional lifecycle operation" })
        };
        request.Headers.Add(header, token);
        request.Headers.Add("Idempotency-Key", $"lifecycle:{operationId:D}:pause");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
            if (state.CaptureState == "Paused" && state.RawLeased == 0 && state.LaneLeased == 0 &&
                state.ProcessingLeased == 0 && state.OutboxLeased == 0) return state;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        throw new InstallerException("CameraAgent did not reach a drained legacy lifecycle boundary.");
    }

    public async Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken)
    {
        using var client = await LoginAsync(cancellationToken).ConfigureAwait(false);
        var before = await ReadAsync(client, cancellationToken).ConfigureAwait(false);
        var (header, token) = await GetAntiforgeryAsync(client, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/operations/capture/resume")
        {
            Content = JsonContent.Create(new { expectedVersion = before.CaptureVersion, reason = "transactional lifecycle operation completed" })
        };
        request.Headers.Add(header, token);
        request.Headers.Add("Idempotency-Key", $"lifecycle:{operationId:D}:resume");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned HttpClient owns the handler and callers dispose the client.")]
    private async Task<HttpClient> LoginAsync(CancellationToken cancellationToken)
    {
        string password;
        await using (var stream = SafeFileSystem.OpenOwnerFileRead(passwordFile, allowReadOnly: true))
        using (var reader = new StreamReader(stream))
            password = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).TrimEnd('\r', '\n');
        if (password.Length == 0 || password.Any(char.IsControl)) throw new InstallerException("The owner password file is invalid.");
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer(),
            CheckCertificateRevocationList = true
        })
        { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var page = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            page.EnsureSuccessStatusCode();
            var match = AntiforgeryToken.Match(await page.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!match.Success) throw new InstallerException("The CameraAgent login page omitted its antiforgery token.");
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
                ["Input.Email"] = ownerEmail,
                ["Input.Password"] = password,
                ["Input.RememberMe"] = "false",
                ["_handler"] = "login"
            });
            using var login = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form, cancellationToken).ConfigureAwait(false);
            login.EnsureSuccessStatusCode();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<LifecycleContinuity> ReadAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/operations/summary", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        static JsonElement Value(JsonElement value, string name) => value.GetProperty(name).GetProperty("value");
        var control = Value(root, "captureControl");
        var raw = Value(root, "rawIngress");
        var lanes = Value(root, "captureLanes");
        var processing = Value(root, "captureProcessing");
        var outbox = Value(root, "artifactOutbox");
        using var continuityResponse = await client.GetAsync(
            new Uri("/api/internal/deployment/continuity", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        continuityResponse.EnsureSuccessStatusCode();
        using var continuityDocument = JsonDocument.Parse(
            await continuityResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        var durable = continuityDocument.RootElement.GetProperty("durable");
        var captureSequence = durable.GetProperty("captureSequences").EnumerateArray()
            .Select(static value => value.GetProperty("lastSequence").GetInt64())
            .Append(durable.GetProperty("fleetMaximumSequence").ValueKind == JsonValueKind.Number
                ? durable.GetProperty("fleetMaximumSequence").GetInt64()
                : 0)
            .Append(durable.GetProperty("fleetNextSequence").ValueKind == JsonValueKind.Number
                ? Math.Max(0, durable.GetProperty("fleetNextSequence").GetInt64() - 1)
                : 0)
            .Max();
        return new LifecycleContinuity(
            control.GetProperty("state").GetString() ?? string.Empty, control.GetProperty("version").GetInt64(),
            captureSequence,
            raw.GetProperty("pendingCount").GetInt64(), raw.GetProperty("leasedCount").GetInt64(),
            lanes.GetProperty("pendingCount").GetInt64(), lanes.GetProperty("leasedCount").GetInt64(),
            processing.GetProperty("pendingCount").GetInt64(), processing.GetProperty("leasedCount").GetInt64(),
            outbox.GetProperty("pendingCount").GetInt64(), outbox.GetProperty("leasedCount").GetInt64());
    }

    private static async Task<(string Header, string Token)> GetAntiforgeryAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri("/api/internal/deployment/antiforgery", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        return (
            document.RootElement.GetProperty("headerName").GetString() ?? "RequestVerificationToken",
            document.RootElement.GetProperty("requestToken").GetString() ?? throw new InstallerException("Antiforgery token is missing."));
    }
}
