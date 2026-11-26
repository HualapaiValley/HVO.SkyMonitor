using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;

namespace HVO.SkyMonitor.CameraAgent.Tests.Notifications;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", CameraAgentTestAreas.Operations)]
public sealed class WebhookNotificationTransportTests
{
    private static readonly OperatorNotificationNetworkPolicy ProductionPolicy = new(isDevelopment: false);

    private static OperatorNotificationEvent SampleEvent() => new(
        OperatorNotificationEventKind.CaptureStalled,
        OperatorNotificationSeverity.Warning,
        "capture_stalled",
        "Capture stalled",
        "No successful capture in 20m.",
        new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero),
        "North Camera",
        new Dictionary<string, string>(StringComparer.Ordinal) { ["elapsedMinutes"] = "20.0" });

    [TestMethod]
    public async Task SendAsync_GenericProfile_SendsStructuredJsonAndBearerHeaderAsync()
    {
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var transport = new WebhookNotificationTransport(ProductionPolicy, TimeProvider.System, handler);

        var result = await transport.SendAsync(new WebhookDeliveryRequest(
            "https://webhook.example.test/alerts/synthetic-path",
            WebhookPayloadFormat.Generic,
            "Authorization",
            string.Empty,
            AllowPrivateNetwork: false,
            AuthToken: "synthetic-bearer-token-123",
            UserKey: null,
            SampleEvent(),
            CoalescedCount: 3,
            SuppressedDuringCooldown: 2,
            IsTest: false,
            Timeout: TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.Succeeded, result.ReasonCode);
        Assert.HasCount(1, handler.Captured);
        var captured = handler.Captured[0];
        Assert.AreEqual(HttpMethod.Post, captured.Method);
        Assert.AreEqual("Bearer synthetic-bearer-token-123", captured.Authorization);
        using var doc = JsonDocument.Parse(captured.Body);
        Assert.AreEqual("capture_stalled", doc.RootElement.GetProperty("event").GetString());
        Assert.AreEqual("North Camera", doc.RootElement.GetProperty("camera").GetString());
        Assert.AreEqual(3, doc.RootElement.GetProperty("coalescedCount").GetInt32());
        Assert.AreEqual(2, doc.RootElement.GetProperty("suppressedCount").GetInt32());
        StringAssert.Contains(doc.RootElement.GetProperty("text").GetString()!, "Capture stalled", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SendAsync_SlackNtfyAndPushoverProfiles_ProduceExpectedPayloadsAsync()
    {
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var transport = new WebhookNotificationTransport(ProductionPolicy, TimeProvider.System, handler);

        // Slack
        var slack = await transport.SendAsync(new WebhookDeliveryRequest(
            "https://slack.example.test/incoming/synthetic-hook",
            WebhookPayloadFormat.Slack,
            "Authorization",
            string.Empty,
            false, null, null, SampleEvent(), 1, 0, false, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(slack.Succeeded);
        using (var doc = JsonDocument.Parse(handler.Captured[0].Body))
        {
            StringAssert.Contains(doc.RootElement.GetProperty("text").GetString()!, "Capture stalled", StringComparison.Ordinal);
            Assert.IsTrue(doc.RootElement.GetProperty("blocks").GetArrayLength() >= 2);
        }

        // ntfy with custom auth header
        var ntfy = await transport.SendAsync(new WebhookDeliveryRequest(
            "https://ntfy.example.test/",
            WebhookPayloadFormat.Ntfy,
            "X-Custom-Token",
            "hvo-observatory",
            false, "synthetic-ntfy-token", null, SampleEvent(), 1, 0, false, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(ntfy.Succeeded);
        Assert.AreEqual("synthetic-ntfy-token", handler.Captured[1].CustomHeaderValue("X-Custom-Token"));
        using (var doc = JsonDocument.Parse(handler.Captured[1].Body))
        {
            Assert.AreEqual("hvo-observatory", doc.RootElement.GetProperty("topic").GetString());
            Assert.AreEqual(4, doc.RootElement.GetProperty("priority").GetInt32());
        }

        // Pushover
        var pushover = await transport.SendAsync(new WebhookDeliveryRequest(
            "https://pushover.example.test/1/messages.json",
            WebhookPayloadFormat.Pushover,
            "Authorization",
            string.Empty,
            false, "synthetic-app-tok-99", "synthetic-user-key-88", SampleEvent(), 1, 0, false, TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(pushover.Succeeded);
        Assert.IsNull(handler.Captured[2].Authorization);
        using (var doc = JsonDocument.Parse(handler.Captured[2].Body))
        {
            Assert.AreEqual("synthetic-app-tok-99", doc.RootElement.GetProperty("token").GetString());
            Assert.AreEqual("synthetic-user-key-88", doc.RootElement.GetProperty("user").GetString());
            Assert.AreEqual(0, doc.RootElement.GetProperty("priority").GetInt32());
        }
    }

    [TestMethod]
    public async Task SendAsync_RedirectsRateLimitsAndServerErrors_AreClassifiedSafelyAsync()
    {
        // 302 redirect refused
        using (var redirectHandler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://evil.example.test/redirect") }
        }))
        using (var transport = new WebhookNotificationTransport(ProductionPolicy, TimeProvider.System, redirectHandler))
        {
            var res = await transport.SendAsync(CreateGenericRequest("https://webhook.example.test/hook"), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(res.Succeeded);
            Assert.IsFalse(res.Transient);
            Assert.AreEqual(NotificationReasonCodes.RedirectBlocked, res.ReasonCode);
        }

        // 429 with Retry-After
        using (var rateHandler = new RecordingHttpHandler(_ =>
        {
            var msg = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            msg.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return msg;
        }))
        using (var transport = new WebhookNotificationTransport(ProductionPolicy, TimeProvider.System, rateHandler))
        {
            var res = await transport.SendAsync(CreateGenericRequest("https://webhook.example.test/hook"), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(res.Succeeded);
            Assert.IsTrue(res.Transient);
            Assert.AreEqual(NotificationReasonCodes.RateLimited, res.ReasonCode);
            Assert.AreEqual(TimeSpan.FromSeconds(45), res.RetryAfter);
        }

        // 503 transient vs 401 permanent (no secret echo)
        using (var errHandler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)))
        using (var transport = new WebhookNotificationTransport(ProductionPolicy, TimeProvider.System, errHandler))
        {
            var marker = Guid.NewGuid().ToString("N");
            var pathMarker = $"path-{marker[..8]}";
            var headerMarker = $"header-{marker[8..16]}";
            var res = await transport.SendAsync(CreateGenericRequest($"https://webhook.example.test/{pathMarker}", headerMarker), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(res.Succeeded);
            Assert.IsFalse(res.Transient);
            Assert.AreEqual(NotificationReasonCodes.AuthFailed, res.ReasonCode);
            Assert.DoesNotContain(pathMarker, res.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(headerMarker, res.ToString(), StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task SendAsync_SsrfBlockedUrls_NeverDispatchHttpRequestAsync()
    {
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var transport = new WebhookNotificationTransport(ProductionPolicy, TimeProvider.System, handler);

        foreach (var blocked in new[]
        {
            "http://webhook.example.test/hook",
            "https://127.0.0.1/hook",
            "https://169.254.169.254/latest/meta-data/",
            "https://10.0.0.2/hook"
        })
        {
            var res = await transport.SendAsync(CreateGenericRequest(blocked), CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(res.Succeeded, blocked);
            Assert.IsFalse(res.Transient, blocked);
            Assert.AreEqual(NotificationReasonCodes.SsrfBlocked, res.ReasonCode, blocked);
        }

        Assert.IsEmpty(handler.Captured);
    }

    [TestMethod]
    public async Task SendAsync_StalledHandler_ReturnsTimeoutPromptlyAsync()
    {
        using var handler = new HangingHttpHandler();
        using var transport = new WebhookNotificationTransport(ProductionPolicy, TimeProvider.System, handler);

        var res = await transport.SendAsync(new WebhookDeliveryRequest(
            "https://webhook.example.test/slow",
            WebhookPayloadFormat.Generic,
            "Authorization",
            string.Empty,
            false, null, null, SampleEvent(), 1, 0, false, TimeSpan.FromMilliseconds(200)), CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(res.Succeeded);
        Assert.IsTrue(res.Transient);
        Assert.AreEqual(NotificationReasonCodes.Timeout, res.ReasonCode);
    }

    private static WebhookDeliveryRequest CreateGenericRequest(string url, string? token = null) => new(
        url, WebhookPayloadFormat.Generic, "Authorization", string.Empty, false, token, null, SampleEvent(), 1, 0, false, TimeSpan.FromSeconds(5));

    private sealed record CapturedHttp(HttpMethod Method, Uri Uri, string? Authorization, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public string? CustomHeaderValue(string name) => Headers.TryGetValue(name, out var val) ? val : null;
    }

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<CapturedHttp> Captured { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers)
            {
                headers[h.Key] = string.Join(",", h.Value);
            }
            Captured.Add(new CapturedHttp(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), headers, body));
            return responder(request);
        }
    }

    private sealed class HangingHttpHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
