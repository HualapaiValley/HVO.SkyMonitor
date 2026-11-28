using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>
/// Outbound bounded HTTP/1.1 POST webhook delivery transport supporting Generic JSON, Slack, ntfy, and Pushover
/// profiles. Connects directly over a DNS-pinned <see cref="Socket"/> and <see cref="SslStream"/> (never via
/// <see cref="HttpClient"/> or system HTTP proxies), guaranteeing zero proxy SSRF bypass, zero <c>System.Net.Http</c>
/// EventSource/DiagnosticListener credential-path leakage, zero redirects, strict send-time header validation,
/// explicit timeouts, and bounded response header parsing.
/// </summary>
public sealed class WebhookNotificationTransport(
    OperatorNotificationNetworkPolicy networkPolicy,
    TimeProvider timeProvider) : IWebhookNotificationTransport, IDisposable
{
    public const string ChannelName = "webhook";
    private const int MaximumResponseHeaderBytes = 32 * 1024;
    private const int MaximumResponseHeaderLines = 128;
    private const int MaximumInterimResponses = 4;

    private static readonly TimeSpan MinimumRetryAfter = TimeSpan.FromSeconds(OperatorNotificationsOptions.DefaultRetryInitialDelaySeconds);
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(OperatorNotificationsOptions.DefaultRetryMaximumDelaySeconds);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OperatorNotificationNetworkPolicy _networkPolicy = networkPolicy ?? throw new ArgumentNullException(nameof(networkPolicy));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Internal test seam for deterministic connect-time DNS resolution. Always null in production.</summary>
    internal Func<string, CancellationToken, Task<IPAddress[]>>? DnsResolver { get; init; }

    /// <summary>Internal test seam for inspecting or intercepting direct pinned TCP socket creation. Always null in production.</summary>
    internal Func<IPAddress, int, CancellationToken, Task<Stream>>? StreamConnector { get; init; }

    /// <summary>Internal test seam for ephemeral self-signed loopback TLS certificates in unit/integration tests. Always null in production.</summary>
    internal RemoteCertificateValidationCallback? CertificateValidationCallback { get; init; }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Webhook delivery maps all HTTP/network failures to fixed sanitized reason codes without leaking URLs or tokens.")]
    public async Task<OperatorNotificationDeliveryResult> SendAsync(
        WebhookDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = _timeProvider.GetTimestamp();

        if (!Enum.IsDefined(request.Format))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.InvalidConfiguration, transient: false, Elapsed(started));
        }

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

        var pathAndQuery = uri!.PathAndQuery;
        if (string.IsNullOrEmpty(pathAndQuery) ||
            !pathAndQuery.StartsWith('/') ||
            OperatorNotificationValidation.HasHeaderInjectionCharacters(pathAndQuery) ||
            !IsPrintableAscii(pathAndQuery))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.InvalidConfiguration, transient: false, Elapsed(started));
        }

        if (request.Format == WebhookPayloadFormat.Pushover)
        {
            if (string.IsNullOrWhiteSpace(request.AuthToken) || string.IsNullOrWhiteSpace(request.UserKey))
            {
                return OperatorNotificationDeliveryResult.Failure(
                    ChannelName, NotificationReasonCodes.MissingSecret, transient: false, Elapsed(started));
            }
            if (!OperatorNotificationValidation.IsValidHeaderValue(request.AuthToken, OperatorNotificationValidation.MaximumSecretLength) ||
                !OperatorNotificationValidation.IsValidHeaderValue(request.UserKey, OperatorNotificationValidation.MaximumSecretLength))
            {
                return OperatorNotificationDeliveryResult.Failure(
                    ChannelName, NotificationReasonCodes.InvalidConfiguration, transient: false, Elapsed(started));
            }
        }

        var ntfyTopic = OperatorNotificationValidation.NormalizeNtfyTopic(request.NtfyTopic) ?? string.Empty;
        if (ntfyTopic.Length > WebhookNotificationOptions.MaximumTopicLength ||
            OperatorNotificationValidation.HasHeaderInjectionCharacters(ntfyTopic))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.InvalidConfiguration, transient: false, Elapsed(started));
        }

        if (request.Format == WebhookPayloadFormat.Ntfy &&
            OperatorNotificationValidation.IsNtfyRootPath(uri) &&
            string.IsNullOrEmpty(ntfyTopic))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.MissingSecret, transient: false, Elapsed(started));
        }

        // Send-time validation of custom header name and value (protects host-configuration path as well as store path).
        var rawHeaderName = request.AuthHeaderName;
        if (!string.IsNullOrWhiteSpace(rawHeaderName) &&
            !OperatorNotificationValidation.IsValidAuthHeaderName(rawHeaderName.Trim()))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.InvalidConfiguration, transient: false, Elapsed(started));
        }

        var headerName = string.IsNullOrWhiteSpace(rawHeaderName)
            ? WebhookNotificationOptions.DefaultAuthHeaderName
            : rawHeaderName.Trim();

        var extraHeaders = new List<KeyValuePair<string, string>>(4);
        if (request.Format != WebhookPayloadFormat.Pushover && !string.IsNullOrEmpty(request.AuthToken))
        {
            if (!OperatorNotificationValidation.IsValidAuthHeaderName(headerName) ||
                !OperatorNotificationValidation.IsValidHeaderValue(request.AuthToken, OperatorNotificationValidation.MaximumSecretLength) ||
                !IsPrintableAscii(request.AuthToken))
            {
                return OperatorNotificationDeliveryResult.Failure(
                    ChannelName, NotificationReasonCodes.InvalidConfiguration, transient: false, Elapsed(started));
            }

            var authValue = string.Equals(headerName, WebhookNotificationOptions.DefaultAuthHeaderName, StringComparison.OrdinalIgnoreCase)
                ? $"Bearer {request.AuthToken}"
                : request.AuthToken;
            extraHeaders.Add(new KeyValuePair<string, string>(headerName, authValue));
        }

        var wirePayload = BuildWirePayload(request, uri, ntfyTopic, extraHeaders);
        if (!ValidateOutgoingHeaders(extraHeaders))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.InvalidConfiguration, transient: false, Elapsed(started));
        }

        if (!OperatorNotificationValidation.TryGetIdnHost(uri!, out var idnHost))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.InvalidConfiguration, transient: false, Elapsed(started));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout > TimeSpan.Zero
            ? request.Timeout
            : TimeSpan.FromSeconds(OperatorNotificationsOptions.DefaultRequestTimeoutSeconds));
        var ct = timeoutCts.Token;

        try
        {
            var resolution = await ResolveValidatedAddressesAsync(idnHost, request.AllowPrivateNetwork, ct).ConfigureAwait(false);
            if (resolution.FailureReasonCode is not null)
            {
                return OperatorNotificationDeliveryResult.Failure(
                    ChannelName, resolution.FailureReasonCode, resolution.TransientFailure, Elapsed(started));
            }

            var requestBytes = FormatHttp11PostRequest(uri!, idnHost, pathAndQuery, wirePayload.ContentType, wirePayload.BodyBytes, extraHeaders);

            var rawStream = await ConnectFirstAddressAsync(resolution.Addresses, uri!.Port, ct).ConfigureAwait(false);
            await using (rawStream.ConfigureAwait(false))
            {
                if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    var sslStream = await UpgradeToTlsAsync(rawStream, idnHost, ct).ConfigureAwait(false);
                    await using (sslStream.ConfigureAwait(false))
                    {
                        await sslStream.WriteAsync(requestBytes, ct).ConfigureAwait(false);
                        await sslStream.FlushAsync(ct).ConfigureAwait(false);

                        var response = await ReadHttp11ResponseHeadAsync(sslStream, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
                        return ClassifyHttpStatus(response.StatusCode, response.RetryAfter, Elapsed(started));
                    }
                }

                await rawStream.WriteAsync(requestBytes, ct).ConfigureAwait(false);
                await rawStream.FlushAsync(ct).ConfigureAwait(false);

                var plainResponse = await ReadHttp11ResponseHeadAsync(rawStream, _timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
                return ClassifyHttpStatus(plainResponse.StatusCode, plainResponse.RetryAfter, Elapsed(started));
            }
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
        catch (AuthenticationException)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.TlsFailed, transient: false, Elapsed(started));
        }
        catch (SocketException)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.ConnectFailed, transient: true, Elapsed(started));
        }
        catch (InvalidDataException)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.ProtocolError, transient: false, Elapsed(started));
        }
        catch (IOException)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.TransportError, transient: true, Elapsed(started));
        }
        catch (Exception)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.TransportError, transient: true, Elapsed(started));
        }
    }

    internal static string BuildPayloadJson(WebhookDeliveryRequest request)
    {
        var uri = Uri.TryCreate(request.Url, UriKind.Absolute, out var parsed)
            ? parsed
            : new Uri("https://webhook.invalid/");
        var wire = BuildWirePayload(
            request,
            uri,
            OperatorNotificationValidation.NormalizeNtfyTopic(request.NtfyTopic) ?? string.Empty,
            headers: null);
        return Encoding.UTF8.GetString(wire.BodyBytes);
    }

    private static WirePayload BuildWirePayload(
        WebhookDeliveryRequest request,
        Uri uri,
        string ntfyTopic,
        List<KeyValuePair<string, string>>? headers)
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

        switch (request.Format)
        {
            case WebhookPayloadFormat.Slack:
                {
                    // Escape Slack mrkdwn special entities (&, <, >) so untrusted text cannot trigger <!channel>/<!here>/<!everyone>/<@...>.
                    var escapedCamera = EscapeSlackMrkdwn(camera);
                    var escapedTitle = EscapeSlackMrkdwn(title);
                    var escapedMessage = EscapeSlackMrkdwn(message);
                    var json = JsonSerializer.Serialize(new
                    {
                        text = $"[{escapedCamera}] {escapedTitle}\n{escapedMessage}",
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
                            text = new { type = "mrkdwn", text = $"*{severity.ToUpperInvariant()}* (`{eventSlug}`)\n{escapedMessage}" }
                        }
                        }
                    }, JsonOptions);
                    return new WirePayload("application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                }

            case WebhookPayloadFormat.Ntfy:
                {
                    var priority = ev.Severity switch
                    {
                        OperatorNotificationSeverity.Critical => 5,
                        OperatorNotificationSeverity.Warning => 4,
                        _ => 3
                    };
                    var tags = new[] { "cameraagent", severity, eventSlug };

                    // Official ntfy contract:
                    // - Root URL (/) + non-empty topic -> POST JSON envelope including "topic".
                    // - Topic-path URL (/my-topic) -> POST plain-text body with Title, Priority, and Tags headers.
                    if (OperatorNotificationValidation.IsNtfyRootPath(uri))
                    {
                        var json = JsonSerializer.Serialize(new
                        {
                            topic = ntfyTopic,
                            title = $"[{camera}] {title}",
                            message,
                            priority,
                            tags
                        }, JsonOptions);
                        return new WirePayload("application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    }

                    var headerTitle = EncodeHttpHeaderText(Truncate($"[{camera}] {title}", 160));
                    headers?.Add(new KeyValuePair<string, string>("Title", headerTitle));
                    headers?.Add(new KeyValuePair<string, string>("Priority", priority.ToString(CultureInfo.InvariantCulture)));
                    headers?.Add(new KeyValuePair<string, string>("Tags", string.Join(',', tags)));
                    return new WirePayload("text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message));
                }

            case WebhookPayloadFormat.Pushover:
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        token = request.AuthToken,
                        user = request.UserKey,
                        title = Truncate($"[{camera}] {title}", 250),
                        message = Truncate(message, 1024),
                        priority = ev.Severity == OperatorNotificationSeverity.Critical ? 1 : 0,
                        timestamp = ev.OccurredUtc.ToUnixTimeSeconds()
                    }, JsonOptions);
                    return new WirePayload("application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                }

            default:
                {
                    var json = JsonSerializer.Serialize(new
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
                    }, JsonOptions);
                    return new WirePayload("application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                }
        }
    }

    internal static string EscapeSlackMrkdwn(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static string EncodeHttpHeaderText(string value)
    {
        if (IsPrintableAscii(value))
        {
            return value;
        }
        return $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";
    }

    private static bool ValidateOutgoingHeaders(List<KeyValuePair<string, string>> headers)
    {
        foreach (var header in headers)
        {
            if (!OperatorNotificationValidation.IsValidAuthHeaderName(header.Key) ||
                !OperatorNotificationValidation.IsValidHeaderValue(header.Value) ||
                !IsPrintableAscii(header.Value))
            {
                return false;
            }
        }
        return true;
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

    private async Task<AddressResolutionResult> ResolveValidatedAddressesAsync(
        string host,
        bool allowPrivateNetwork,
        CancellationToken ct)
    {
        if (OperatorNotificationValidation.IsBlockedWebhookHostName(host, _networkPolicy.AllowLoopbackForTesting))
        {
            return new AddressResolutionResult([], NotificationReasonCodes.SsrfBlocked, TransientFailure: false);
        }

        var trimmed = host.Trim('[', ']');
        IPAddress[] addresses;
        if (IPAddress.TryParse(trimmed, out var literal))
        {
            addresses = [literal];
        }
        else if (DnsResolver is not null)
        {
            addresses = await DnsResolver(trimmed, ct).ConfigureAwait(false);
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(trimmed, ct).ConfigureAwait(false);
        }

        if (addresses.Length == 0)
        {
            return new AddressResolutionResult([], NotificationReasonCodes.ConnectFailed, TransientFailure: true);
        }

        foreach (var address in addresses)
        {
            if (OperatorNotificationValidation.IsBlockedWebhookAddress(address, allowPrivateNetwork, _networkPolicy.AllowLoopbackForTesting))
            {
                return new AddressResolutionResult([], NotificationReasonCodes.SsrfBlocked, TransientFailure: false);
            }
        }

        return new AddressResolutionResult(addresses, FailureReasonCode: null, TransientFailure: false);
    }

    private async Task<Stream> ConnectFirstAddressAsync(IPAddress[] addresses, int port, CancellationToken ct)
    {
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            if (StreamConnector is not null)
            {
                try
                {
                    return await StreamConnector(address, port, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    lastError = ex;
                    continue;
                }
            }

            Socket? socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                await socket.ConnectAsync(address, port, ct).ConfigureAwait(false);
                var networkStream = new NetworkStream(socket, ownsSocket: true);
                socket = null;
                return networkStream;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                lastError = ex;
            }
            finally
            {
                socket?.Dispose();
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private async Task<SslStream> UpgradeToTlsAsync(Stream inner, string targetHost, CancellationToken ct)
    {
        var sslStream = new SslStream(inner, leaveInnerStreamOpen: true, CertificateValidationCallback);
        try
        {
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost.Trim('[', ']'),
                // Allow OS default TLS 1.2/1.3 selection (CA5398).
                EnabledSslProtocols = SslProtocols.None
            };
            await sslStream.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);
            return sslStream;
        }
        catch
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static byte[] FormatHttp11PostRequest(
        Uri uri,
        string idnHost,
        string pathAndQuery,
        string contentType,
        byte[] bodyBytes,
        List<KeyValuePair<string, string>> extraHeaders)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{idnHost}]" : idnHost;
        var hostHeader = uri.IsDefaultPort
            ? host
            : string.Create(CultureInfo.InvariantCulture, $"{host}:{uri.Port}");

        var sb = new StringBuilder(512);
        sb.Append("POST ").Append(pathAndQuery).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(hostHeader).Append("\r\n");
        sb.Append("User-Agent: HVO.SkyMonitor.CameraAgent/1.0\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Content-Length: {bodyBytes.Length}\r\n");
        sb.Append("Connection: close\r\n");
        foreach (var header in extraHeaders)
        {
            sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }
        sb.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        var combined = new byte[headerBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, combined, 0, headerBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, combined, headerBytes.Length, bodyBytes.Length);
        return combined;
    }

    private static async Task<HttpHeadResponse> ReadHttp11ResponseHeadAsync(
        Stream stream,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var reader = new BoundedAsciiLineReader(stream, MaximumResponseHeaderBytes);
        for (var interim = 0; interim <= MaximumInterimResponses; interim++)
        {
            var statusLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (!TryParseStatusLine(statusLine, out var statusCode))
            {
                throw new InvalidDataException("Malformed HTTP response status line.");
            }

            TimeSpan? retryAfter = null;
            var headerLines = 0;
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line.Length == 0)
                {
                    break;
                }
                if (++headerLines > MaximumResponseHeaderLines)
                {
                    throw new InvalidDataException("HTTP response exceeded maximum header line limit.");
                }
                if (line.StartsWith("Retry-After:", StringComparison.OrdinalIgnoreCase))
                {
                    retryAfter = ParseRetryAfter(line[12..].Trim(), nowUtc);
                }
            }

            if (statusCode is >= 100 and < 200)
            {
                continue;
            }

            return new HttpHeadResponse(statusCode, retryAfter);
        }

        throw new InvalidDataException("HTTP server sent too many interim 1xx responses.");
    }

    private static bool TryParseStatusLine(string line, out int statusCode)
    {
        statusCode = 0;
        if (line.Length < 12 || !line.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var firstSpace = line.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace < 8 || firstSpace + 4 > line.Length)
        {
            return false;
        }
        var codeSpan = line.AsSpan(firstSpace + 1, 3);
        if (!int.TryParse(codeSpan, NumberStyles.None, CultureInfo.InvariantCulture, out statusCode) ||
            statusCode is < 100 or > 599)
        {
            return false;
        }
        return firstSpace + 4 == line.Length || line[firstSpace + 4] == ' ';
    }

    private static OperatorNotificationDeliveryResult ClassifyHttpStatus(int status, TimeSpan? retryAfter, TimeSpan elapsed)
    {
        if (status is >= 200 and < 300)
        {
            return OperatorNotificationDeliveryResult.Success(ChannelName, elapsed);
        }
        if (status is >= 300 and < 400)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.RedirectBlocked, transient: false, elapsed);
        }
        if (status == 429)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.RateLimited, transient: true, elapsed, retryAfter);
        }
        if (status == 408 || status >= 500)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.ServerError, transient: true, elapsed, retryAfter);
        }
        if (status is 401 or 403)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.AuthFailed, transient: false, elapsed);
        }
        if (status is >= 400 and < 500)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.ClientRejected, transient: false, elapsed);
        }
        return OperatorNotificationDeliveryResult.Failure(
            ChannelName, NotificationReasonCodes.ProtocolError, transient: false, elapsed);
    }

    private static TimeSpan? ParseRetryAfter(string? rawHeader, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(rawHeader))
        {
            return null;
        }

        TimeSpan? raw = null;
        if (long.TryParse(rawHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deltaSeconds))
        {
            raw = TimeSpan.FromSeconds(Math.Clamp(deltaSeconds, 0, 86400));
        }
        else if (DateTimeOffset.TryParse(rawHeader, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var retryDate))
        {
            raw = retryDate - nowUtc;
        }

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

    private static bool IsPrintableAscii(string value)
    {
        foreach (var c in value)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }
        return true;
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private TimeSpan Elapsed(long started) => _timeProvider.GetElapsedTime(started);

    public void Dispose()
    {
        // Connections are short-lived and disposed per send; no pooled HttpClient state is retained.
    }

    private readonly record struct WirePayload(string ContentType, byte[] BodyBytes);

    private readonly record struct AddressResolutionResult(
        IPAddress[] Addresses,
        string? FailureReasonCode,
        bool TransientFailure);

    private readonly record struct HttpHeadResponse(int StatusCode, TimeSpan? RetryAfter);

    private sealed class BoundedAsciiLineReader(Stream stream, int maximumTotalBytes)
    {
        private readonly Stream _stream = stream;
        private readonly byte[] _buffer = new byte[1024];
        private int _pos;
        private int _len;
        private int _totalRead;

        public async Task<string> ReadLineAsync(CancellationToken ct)
        {
            var line = new List<byte>(128);
            while (true)
            {
                if (_pos >= _len)
                {
                    _len = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
                    _pos = 0;
                    if (_len == 0)
                    {
                        throw new IOException("Webhook server closed connection before completing HTTP response headers.");
                    }
                }

                var b = _buffer[_pos++];
                if (++_totalRead > maximumTotalBytes)
                {
                    throw new InvalidDataException("HTTP response headers exceeded maximum allowed size.");
                }

                if (b == (byte)'\n')
                {
                    if (line.Count > 0 && line[^1] == (byte)'\r')
                    {
                        line.RemoveAt(line.Count - 1);
                    }
                    return Encoding.ASCII.GetString(line.ToArray());
                }

                line.Add(b);
            }
        }
    }
}
