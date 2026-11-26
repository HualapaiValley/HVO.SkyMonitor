using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>
/// Outbound HTTP POST JSON webhook delivery transport supporting Generic JSON, Slack, ntfy, and Pushover formats.
/// Enforces URL/SSRF validation at both validation time and TCP connect time (mitigating DNS rebinding), refuses
/// redirects, strips cookies/trace headers, enforces strict timeouts, and never logs webhook URLs or tokens.
/// </summary>
public sealed class WebhookNotificationTransport : IWebhookNotificationTransport, IDisposable
{
    public const string ChannelName = "webhook";
    private const string SsrfBlockedMarkerKey = "HVO.Notifications.SsrfBlocked";
    private static readonly TimeSpan MinimumRetryAfter = TimeSpan.FromSeconds(OperatorNotificationsOptions.DefaultRetryInitialDelaySeconds);
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(OperatorNotificationsOptions.DefaultRetryMaximumDelaySeconds);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OperatorNotificationNetworkPolicy _networkPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly HttpClient _publicClient;
    private readonly HttpClient _privateAllowedClient;
    private readonly HttpClient? _injectedTestClient;
    private bool _disposed;

    public WebhookNotificationTransport(
        OperatorNotificationNetworkPolicy networkPolicy,
        TimeProvider timeProvider)
    {
        _networkPolicy = networkPolicy ?? throw new ArgumentNullException(nameof(networkPolicy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _publicClient = CreateSafeClient(allowPrivateNetwork: false, _networkPolicy);
        _privateAllowedClient = CreateSafeClient(allowPrivateNetwork: true, _networkPolicy);
    }

    /// <summary>Internal test constructor injecting a fake <see cref="HttpMessageHandler"/> for deterministic unit tests.</summary>
    internal WebhookNotificationTransport(
        OperatorNotificationNetworkPolicy networkPolicy,
        TimeProvider timeProvider,
        HttpMessageHandler testHandler)
    {
        _networkPolicy = networkPolicy ?? throw new ArgumentNullException(nameof(networkPolicy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _publicClient = new HttpClient(testHandler ?? throw new ArgumentNullException(nameof(testHandler)), disposeHandler: false);
        _privateAllowedClient = _publicClient;
        _injectedTestClient = _publicClient;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Webhook delivery maps all HTTP/network failures to fixed sanitized reason codes without leaking URLs or tokens.")]
    public async Task<OperatorNotificationDeliveryResult> SendAsync(
        WebhookDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = _timeProvider.GetTimestamp();

        if (!OperatorNotificationValidation.TryValidateWebhookUrl(
                request.Url,
                request.AllowPrivateNetwork,
                _networkPolicy,
                out var uri,
                out var validationCode,
                out _))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, validationCode ?? NotificationReasonCodes.InvalidConfiguration, transient: false, Elapsed(started));
        }

        if (request.Format == WebhookPayloadFormat.Pushover &&
            (string.IsNullOrWhiteSpace(request.AuthToken) || string.IsNullOrWhiteSpace(request.UserKey)))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.MissingSecret, transient: false, Elapsed(started));
        }

        var headerName = string.IsNullOrWhiteSpace(request.AuthHeaderName)
            ? WebhookNotificationOptions.DefaultAuthHeaderName
            : request.AuthHeaderName.Trim();
        if (request.Format != WebhookPayloadFormat.Pushover &&
            !string.IsNullOrWhiteSpace(request.AuthToken) &&
            !OperatorNotificationValidation.IsValidAuthHeaderName(headerName))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.InvalidConfiguration, transient: false, Elapsed(started));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout > TimeSpan.Zero ? request.Timeout : TimeSpan.FromSeconds(OperatorNotificationsOptions.DefaultRequestTimeoutSeconds));
        var ct = timeoutCts.Token;

        try
        {
            var payloadJson = BuildPayloadJson(request);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri!)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.UserAgent.Clear();
            httpRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("HVO.SkyMonitor.CameraAgent", "1.0"));

            ApplyHeaders(httpRequest, request, headerName);

            var client = _injectedTestClient ?? (request.AllowPrivateNetwork ? _privateAllowedClient : _publicClient);
            using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
            {
                return OperatorNotificationDeliveryResult.Success(ChannelName, Elapsed(started));
            }
            if (status is >= 300 and < 400)
            {
                return OperatorNotificationDeliveryResult.Failure(
                    ChannelName, NotificationReasonCodes.RedirectBlocked, transient: false, Elapsed(started));
            }
            if (status == 429)
            {
                var retryAfter = ParseRetryAfter(response.Headers.RetryAfter, _timeProvider.GetUtcNow());
                return OperatorNotificationDeliveryResult.Failure(
                    ChannelName, NotificationReasonCodes.RateLimited, transient: true, Elapsed(started), retryAfter);
            }
            if (status == 408 || status >= 500)
            {
                var retryAfter = ParseRetryAfter(response.Headers.RetryAfter, _timeProvider.GetUtcNow());
                return OperatorNotificationDeliveryResult.Failure(
                    ChannelName, NotificationReasonCodes.ServerError, transient: true, Elapsed(started), retryAfter);
            }
            if (status is 401 or 403)
            {
                return OperatorNotificationDeliveryResult.Failure(
                    ChannelName, NotificationReasonCodes.AuthFailed, transient: false, Elapsed(started));
            }

            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.ClientRejected, transient: false, Elapsed(started));
        }
        catch (InvalidOperationException ex) when (ex.Data.Contains(SsrfBlockedMarkerKey))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.SsrfBlocked, transient: false, Elapsed(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.Cancelled, transient: true, Elapsed(started));
        }
        catch (OperationCanceledException)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.Timeout, transient: true, Elapsed(started));
        }
        catch (HttpRequestException ex) when (ex.InnerException is InvalidOperationException inner && inner.Data.Contains(SsrfBlockedMarkerKey))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.SsrfBlocked, transient: false, Elapsed(started));
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.TlsFailed, transient: false, Elapsed(started));
        }
        catch (HttpRequestException)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.ConnectFailed, transient: true, Elapsed(started));
        }
        catch (Exception)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.TransportError, transient: true, Elapsed(started));
        }
    }

    internal static string BuildPayloadJson(WebhookDeliveryRequest request)
    {
        var ev = request.Event;
        var eventSlug = request.IsTest ? "test" : OperatorNotificationEvent.EventSlug(ev.Kind);
        var severity = ev.Severity switch
        {
            OperatorNotificationSeverity.Info => "info",
            OperatorNotificationSeverity.Warning => "warning",
            OperatorNotificationSeverity.Critical => "critical",
            _ => "warning"
        };
        var camera = OperatorNotificationRedactor.SanitizeOperationalText(ev.CameraName, 120);
        if (string.IsNullOrEmpty(camera))
        {
            camera = "CameraAgent";
        }

        var title = OperatorNotificationRedactor.SanitizeOperationalText(ev.Title, OperatorNotificationEvent.MaximumTitleLength);
        var message = BuildMessageBody(ev, camera, request.CoalescedCount, request.SuppressedDuringCooldown);

        return request.Format switch
        {
            WebhookPayloadFormat.Slack => JsonSerializer.Serialize(new
            {
                text = $"[{camera}] {title}\n{message}",
                blocks = new object[]
                {
                    new
                    {
                        type = "header",
                        text = new { type = "plain_text", text = Truncate($"[{camera}] {title}", 150) }
                    },
                    new
                    {
                        type = "section",
                        text = new { type = "mrkdwn", text = $"*{severity.ToUpperInvariant()}* (`{eventSlug}`)\n{message}" }
                    }
                }
            }, JsonOptions),

            WebhookPayloadFormat.Ntfy => BuildNtfyPayload(request, ev, eventSlug, severity, camera, title, message),

            WebhookPayloadFormat.Pushover => JsonSerializer.Serialize(new
            {
                token = request.AuthToken,
                user = request.UserKey,
                title = Truncate($"[{camera}] {title}", 250),
                message = Truncate(message, 1024),
                priority = ev.Severity == OperatorNotificationSeverity.Critical ? 1 : 0,
                timestamp = ev.OccurredUtc.ToUnixTimeSeconds()
            }, JsonOptions),

            _ => JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                source = "HVO.SkyMonitor.CameraAgent",
                @event = eventSlug,
                severity,
                test = request.IsTest,
                camera,
                title,
                text = $"[{camera}] {title}: {message}",
                message,
                occurredUtc = ev.OccurredUtc.ToUniversalTime(),
                coalescedCount = request.CoalescedCount,
                suppressedCount = request.SuppressedDuringCooldown
            }, JsonOptions)
        };
    }

    private static string BuildNtfyPayload(
        WebhookDeliveryRequest request,
        OperatorNotificationEvent ev,
        string eventSlug,
        string severity,
        string camera,
        string title,
        string message)
    {
        var priority = ev.Severity switch
        {
            OperatorNotificationSeverity.Critical => 5,
            OperatorNotificationSeverity.Warning => 4,
            _ => 3
        };
        var tags = new[] { "cameraagent", severity, eventSlug };
        if (!string.IsNullOrWhiteSpace(request.NtfyTopic))
        {
            return JsonSerializer.Serialize(new
            {
                topic = request.NtfyTopic.Trim(),
                title = $"[{camera}] {title}",
                message,
                priority,
                tags
            }, JsonOptions);
        }

        return JsonSerializer.Serialize(new
        {
            title = $"[{camera}] {title}",
            message,
            priority,
            tags,
            @event = eventSlug,
            severity,
            camera,
            occurredUtc = ev.OccurredUtc.ToUniversalTime()
        }, JsonOptions);
    }

    private static string BuildMessageBody(
        OperatorNotificationEvent ev,
        string camera,
        int coalescedCount,
        int suppressedDuringCooldown)
    {
        var sb = new StringBuilder(256);
        var summary = OperatorNotificationRedactor.SanitizeOperationalText(ev.Summary, OperatorNotificationEvent.MaximumSummaryLength);
        sb.Append(summary);
        if (coalescedCount > 1)
        {
            sb.Append(CultureInfo.InvariantCulture, $" (coalesced {coalescedCount} occurrences)");
        }
        if (suppressedDuringCooldown > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" ({suppressedDuringCooldown} earlier occurrences suppressed during cooldown)");
        }
        if (ev.Details is { Count: > 0 })
        {
            var count = 0;
            foreach (var pair in ev.Details)
            {
                if (count++ >= OperatorNotificationEvent.MaximumDetailsCount) break;
                var key = OperatorNotificationRedactor.SanitizeOperationalText(pair.Key, 48);
                var val = OperatorNotificationRedactor.SanitizeOperationalText(pair.Value, 160);
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                {
                    sb.Append('\n').Append(key).Append(": ").Append(val);
                }
            }
        }
        sb.Append(CultureInfo.InvariantCulture, $"\nCamera: {camera}\nTime (UTC): {ev.OccurredUtc.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z");
        return sb.ToString();
    }

    private static void ApplyHeaders(HttpRequestMessage httpRequest, WebhookDeliveryRequest request, string headerName)
    {
        if (request.Format == WebhookPayloadFormat.Ntfy)
        {
            var title = OperatorNotificationRedactor.SanitizeOperationalText(request.Event.Title, 120);
            var priority = request.Event.Severity switch
            {
                OperatorNotificationSeverity.Critical => "5",
                OperatorNotificationSeverity.Warning => "4",
                _ => "3"
            };
            httpRequest.Headers.TryAddWithoutValidation("X-Title", title);
            httpRequest.Headers.TryAddWithoutValidation("X-Priority", priority);
            httpRequest.Headers.TryAddWithoutValidation("X-Tags", $"cameraagent,{OperatorNotificationEvent.EventSlug(request.Event.Kind)}");
        }

        if (request.Format != WebhookPayloadFormat.Pushover && !string.IsNullOrWhiteSpace(request.AuthToken))
        {
            if (string.Equals(headerName, WebhookNotificationOptions.DefaultAuthHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.AuthToken);
            }
            else
            {
                httpRequest.Headers.TryAddWithoutValidation(headerName, request.AuthToken);
            }
        }
    }

    private static HttpClient CreateSafeClient(bool allowPrivateNetwork, OperatorNotificationNetworkPolicy policy)
    {
        SocketsHttpHandler? handler = null;
        try
        {
            handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                PreAuthenticate = false,
                Credentials = null,
                ActivityHeadersPropagator = null,
                MaxResponseHeadersLength = 32,
                MaxResponseDrainSize = 0,
                MaxConnectionsPerServer = 2,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = async (context, ct) =>
                {
                    var host = context.DnsEndPoint.Host;
                    if (OperatorNotificationValidation.IsBlockedWebhookHostName(host, policy.AllowLoopbackForTesting))
                    {
                        throw CreateSsrfBlockedException();
                    }

                    IPAddress[] addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                        ? [literal]
                        : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

                    if (addresses.Length == 0)
                    {
                        throw new SocketException((int)SocketError.HostNotFound);
                    }

                    foreach (var address in addresses)
                    {
                        if (OperatorNotificationValidation.IsBlockedWebhookAddress(address, allowPrivateNetwork, policy.AllowLoopbackForTesting))
                        {
                            throw CreateSsrfBlockedException();
                        }
                    }

                    Exception? lastError = null;
                    foreach (var address in addresses)
                    {
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                        {
                            NoDelay = true
                        };
                        try
                        {
                            await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch (OperationCanceledException)
                        {
                            socket.Dispose();
                            throw;
                        }
                        catch (Exception ex) when (ex is SocketException or IOException)
                        {
                            socket.Dispose();
                            lastError = ex;
                        }
                    }

                    throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
                }
            };

            var client = new HttpClient(handler, disposeHandler: true);
            handler = null;
            return client;
        }
        finally
        {
            handler?.Dispose();
        }
    }

    private static InvalidOperationException CreateSsrfBlockedException()
    {
        var ex = new InvalidOperationException("Webhook target address is blocked by outbound network safety policy.");
        ex.Data[SsrfBlockedMarkerKey] = true;
        return ex;
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? retryAfter, DateTimeOffset nowUtc)
    {
        if (retryAfter is null)
        {
            return null;
        }
        TimeSpan? raw = retryAfter.Delta ?? (retryAfter.Date is { } date ? date - nowUtc : null);
        if (raw is null || raw.Value <= TimeSpan.Zero)
        {
            return null;
        }
        return raw.Value < MinimumRetryAfter
            ? MinimumRetryAfter
            : raw.Value > MaximumRetryAfter
                ? MaximumRetryAfter
                : raw.Value;
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private TimeSpan Elapsed(long started) => _timeProvider.GetElapsedTime(started);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _publicClient.Dispose();
        if (!ReferenceEquals(_privateAllowedClient, _publicClient))
        {
            _privateAllowedClient.Dispose();
        }
    }
}
