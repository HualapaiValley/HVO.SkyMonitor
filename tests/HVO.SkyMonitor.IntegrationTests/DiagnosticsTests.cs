using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.LogicHost.Models.Diagnostics;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
public sealed class DiagnosticsTests
{
    private HttpClient? _client;

    [TestInitialize]
    public void SetUp()
    {
        _client = AssemblyHooks.Fixture.Factory.CreateClient();
    }

    [TestCleanup]
    public void TearDown()
    {
        _client?.Dispose();
    }

    [TestMethod]
    public async Task MinioDiagnosticsRoundTripsContentAsync()
    {
        await AuthenticateAsSystemAsync().ConfigureAwait(false);

        var request = new StorageDiagnosticsRequest
        {
            Content = $"Payload-{Guid.NewGuid():N}"
        };

        var response = await _client!.PostAsJsonAsync(new Uri("/api/v1.0/diagnostics/minio", UriKind.Relative), request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<StorageDiagnosticsResponse>().ConfigureAwait(false);
        Assert.IsNotNull(result);
        Assert.AreEqual(request.Content, result!.Content);
    }

    [TestMethod]
    public async Task MinioDiagnosticsRejectsForeignBucketAsync()
    {
        await AuthenticateAsSystemAsync().ConfigureAwait(false);

        var response = await _client!.PostAsJsonAsync(new Uri("/api/v1.0/diagnostics/minio", UriKind.Relative), new StorageDiagnosticsRequest
        {
            Bucket = "other-repository-data",
            Content = "must not be written"
        }).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task CacheDiagnosticsReportsCacheHitAsync()
    {
        await AuthenticateAsSystemAsync().ConfigureAwait(false);

        var key = $"diagnostics:{Guid.NewGuid():N}";
        var request = new CacheDiagnosticsRequest
        {
            Key = key,
            Value = "cached-value",
            ExpirationSeconds = 60
        };

        var response = await _client!.PostAsJsonAsync(new Uri("/api/v1.0/diagnostics/cache", UriKind.Relative), request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CacheDiagnosticsResponse>().ConfigureAwait(false);
        Assert.IsNotNull(result);
        Assert.IsTrue(result!.CacheHit);
        Assert.AreEqual(request.Value, result.RetrievedValue);
    }

    [TestMethod]
    public async Task EmailDiagnosticsSendsMessageToSmtpAsync()
    {
        await ClearMailboxAsync().ConfigureAwait(false);
        await AuthenticateAsSystemAsync().ConfigureAwait(false);

        var subject = $"Diagnostics-{Guid.NewGuid():N}";
        var request = new EmailDiagnosticsRequest
        {
            Recipient = TestEmail.AdminRecipient,
            Subject = subject,
            Body = "Integration test message"
        };

        var response = await _client!.PostAsJsonAsync(new Uri("/api/v1.0/diagnostics/email", UriKind.Relative), request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var delivered = await WaitForEmailAsync(subject, cts.Token).ConfigureAwait(false);
        Assert.IsTrue(delivered, "Expected email to appear in SMTP capture.");
    }

    private async Task AuthenticateAsSystemAsync()
    {
        var scope = string.Join(' ', TestClients.SystemInternal.Scopes);
        var token = await HttpHelpers.GetClientCredentialsTokenAsync(
            _client!,
            "/connect/token",
            TestClients.SystemInternal.ClientId,
            TestClients.SystemInternal.ClientSecret,
            scope).ConfigureAwait(false);

        HttpHelpers.WithBearerToken(_client!, token.AccessToken);
    }

    private static async Task ClearMailboxAsync()
    {
        using var http = new HttpClient();
        var endpoint = new Uri($"{AssemblyHooks.Fixture.SmtpHttpEndpoint}/api/v1/messages", UriKind.Absolute);
        var response = await http.DeleteAsync(endpoint).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<bool> WaitForEmailAsync(string expectedSubject, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var endpoint = new Uri($"{AssemblyHooks.Fixture.SmtpHttpEndpoint}/api/v1/messages", UriKind.Absolute);

        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await http.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (document.RootElement.TryGetProperty("messages", out var messages))
            {
                foreach (var item in messages.EnumerateArray())
                {
                    if (item.TryGetProperty("Subject", out var subjectProp) &&
                        string.Equals(subjectProp.GetString(), expectedSubject, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
