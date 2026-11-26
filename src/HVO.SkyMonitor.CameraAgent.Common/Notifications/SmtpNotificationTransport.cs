using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>
/// Bounded, zero-external-dependency RFC 5321 / RFC 3207 (STARTTLS) / RFC 8314 (Implicit TLS) / RFC 4954
/// (AUTH PLAIN and AUTH LOGIN) SMTP delivery transport. Works with standard SMTP mailboxes/relays and Azure
/// Communication Services Email SMTP endpoints while enforcing strict timeouts, fail-closed TLS whenever credentials
/// or non-local hosts are used, explicit AUTH capability checks, Mailpit production exclusion, SSRF IP checks,
/// RFC 2045/2047/5322 message formatting, and zero credential logging.
/// </summary>
public sealed class SmtpNotificationTransport(
    OperatorNotificationNetworkPolicy networkPolicy,
    TimeProvider timeProvider) : ISmtpNotificationTransport
{
    public const string ChannelName = "smtp";
    private const int MaximumResponseLines = 64;
    private const int MaximumLineLength = 2048;
    private const int MaximumRfc5322LineOctets = 998;
    private const int RecommendedHeaderFoldLength = 78;

    private readonly OperatorNotificationNetworkPolicy _networkPolicy = networkPolicy ?? throw new ArgumentNullException(nameof(networkPolicy));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Internal test seam for ephemeral self-signed loopback TLS certificates in unit tests. Always null in production.</summary>
    internal RemoteCertificateValidationCallback? CertificateValidationCallback { get; init; }

    /// <summary>Internal test seam for deterministic DNS resolution in unit tests. Always null in production.</summary>
    internal Func<string, CancellationToken, Task<IPAddress[]>>? DnsResolver { get; init; }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "SMTP delivery maps all network/protocol failures to fixed sanitized reason codes without leaking credentials.")]
    public async Task<OperatorNotificationDeliveryResult> SendAsync(
        SmtpDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = _timeProvider.GetTimestamp();

        if (!Enum.IsDefined(request.SecurityMode) ||
            !OperatorNotificationValidation.IsValidHost(request.Host) ||
            request.Port is < 1 or > 65535 ||
            !OperatorNotificationValidation.IsValidEmailAddress(request.FromAddress) ||
            request.Recipients.Count == 0 ||
            request.Recipients.Count > OperatorNotificationsOptions.MaximumRecipients ||
            request.Recipients.Any(static recipient => !OperatorNotificationValidation.IsValidEmailAddress(recipient)) ||
            OperatorNotificationValidation.HasHeaderInjectionCharacters(request.FromDisplayName) ||
            OperatorNotificationValidation.HasHeaderInjectionCharacters(request.Subject) ||
            OperatorNotificationValidation.HasHeaderInjectionCharacters(request.Username) ||
            OperatorNotificationValidation.HasHeaderInjectionCharacters(request.Password))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.InvalidConfiguration, transient: false, Elapsed(started));
        }

        if (!_networkPolicy.IsDevelopment && OperatorNotificationValidation.IsMailpitHost(request.Host))
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.MailpitProhibited, transient: false, Elapsed(started));
        }

        var hasCredentials = !string.IsNullOrEmpty(request.Username) || !string.IsNullOrEmpty(request.Password);
        if (hasCredentials && request.SecurityMode == SmtpSecurityMode.None)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.TlsRequired, transient: false, Elapsed(started));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout > TimeSpan.Zero ? request.Timeout : TimeSpan.FromSeconds(OperatorNotificationsOptions.DefaultRequestTimeoutSeconds));
        var ct = timeoutCts.Token;

        try
        {
            var resolution = await ResolveValidatedAddressesAsync(request.Host, request.SecurityMode, ct).ConfigureAwait(false);
            if (resolution.FailureReasonCode is not null)
            {
                return OperatorNotificationDeliveryResult.Failure(
                    ChannelName, resolution.FailureReasonCode, resolution.TransientFailure, Elapsed(started));
            }

            using var tcpClient = new TcpClient();
            await ConnectFirstAddressAsync(tcpClient, resolution.Addresses, request.Port, ct).ConfigureAwait(false);

            Stream stream = tcpClient.GetStream();
            if (request.SecurityMode == SmtpSecurityMode.ImplicitTls)
            {
                var implicitSsl = await UpgradeToTlsAsync(stream, request.Host, ct).ConfigureAwait(false);
                await using (implicitSsl.ConfigureAwait(false))
                {
                    return await RunSmtpSessionAsync(implicitSsl, request, tlsActive: true, started, ct).ConfigureAwait(false);
                }
            }

            return await RunSmtpSessionAsync(stream, request, tlsActive: false, started, ct).ConfigureAwait(false);
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
        catch (IOException)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.TransportError, transient: true, Elapsed(started));
        }
        catch (InvalidDataException)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.ProtocolError, transient: false, Elapsed(started));
        }
        catch (Exception)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.TransportError, transient: true, Elapsed(started));
        }
    }

    private async Task<OperatorNotificationDeliveryResult> RunSmtpSessionAsync(
        Stream stream,
        SmtpDeliveryRequest request,
        bool tlsActive,
        long started,
        CancellationToken ct)
    {
        var session = new SmtpLineSession(stream);
        var greeting = await session.ReadResponseAsync(ct).ConfigureAwait(false);
        if (greeting.Code != 220)
        {
            return ClassifySmtpFailure(greeting.Code, Elapsed(started));
        }

        var ehlo = await SendEhloAsync(session, ct).ConfigureAwait(false);
        if (ehlo.Code != 250)
        {
            return ClassifySmtpFailure(ehlo.Code, Elapsed(started));
        }

        switch (request.SecurityMode)
        {
            case SmtpSecurityMode.ImplicitTls:
                if (!tlsActive)
                {
                    return OperatorNotificationDeliveryResult.Failure(
                        ChannelName, NotificationReasonCodes.TlsRequired, transient: false, Elapsed(started));
                }
                return await DeliverAfterEhloAsync(session, ehlo, request, tlsActive: true, started, ct).ConfigureAwait(false);

            case SmtpSecurityMode.StartTls:
                if (!ehlo.SupportsCapability("STARTTLS"))
                {
                    return OperatorNotificationDeliveryResult.Failure(
                        ChannelName, NotificationReasonCodes.TlsUnsupported, transient: false, Elapsed(started));
                }

                await session.WriteLineAsync("STARTTLS", ct).ConfigureAwait(false);
                var startTlsResponse = await session.ReadResponseAsync(ct).ConfigureAwait(false);
                if (startTlsResponse.Code != 220)
                {
                    return ClassifySmtpFailure(startTlsResponse.Code, Elapsed(started));
                }

                var startTlsSsl = await UpgradeToTlsAsync(stream, request.Host, ct).ConfigureAwait(false);
                await using (startTlsSsl.ConfigureAwait(false))
                {
                    var tlsSession = new SmtpLineSession(startTlsSsl);

                    // RFC 3207 Section 4.2 requires a fresh EHLO after the TLS handshake.
                    var tlsEhlo = await SendEhloAsync(tlsSession, ct).ConfigureAwait(false);
                    if (tlsEhlo.Code != 250)
                    {
                        return ClassifySmtpFailure(tlsEhlo.Code, Elapsed(started));
                    }

                    return await DeliverAfterEhloAsync(tlsSession, tlsEhlo, request, tlsActive: true, started, ct).ConfigureAwait(false);
                }

            case SmtpSecurityMode.None:
                return await DeliverAfterEhloAsync(session, ehlo, request, tlsActive: false, started, ct).ConfigureAwait(false);

            default:
                return OperatorNotificationDeliveryResult.Failure(
                    ChannelName, NotificationReasonCodes.InvalidConfiguration, transient: false, Elapsed(started));
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Post-acceptance QUIT teardown errors are ignored after the server already accepted DATA with 250 OK.")]
    private async Task<OperatorNotificationDeliveryResult> DeliverAfterEhloAsync(
        SmtpLineSession session,
        SmtpResponse ehlo,
        SmtpDeliveryRequest request,
        bool tlsActive,
        long started,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.Username) || !string.IsNullOrEmpty(request.Password))
        {
            if (!tlsActive)
            {
                return OperatorNotificationDeliveryResult.Failure(
                    ChannelName, NotificationReasonCodes.TlsRequired, transient: false, Elapsed(started));
            }

            var authResult = await AuthenticateAsync(
                session,
                ehlo,
                request.Username ?? string.Empty,
                request.Password ?? string.Empty,
                Elapsed(started),
                ct).ConfigureAwait(false);
            if (authResult is not null)
            {
                return authResult;
            }
        }

        var supports8BitMime = ehlo.SupportsCapability("8BITMIME");
        var payload = BuildRfc5322Message(request, _timeProvider.GetUtcNow(), supports8BitMime, out var uses8BitMime);
        var mailFromCommand = uses8BitMime
            ? $"MAIL FROM:<{request.FromAddress}> BODY=8BITMIME"
            : $"MAIL FROM:<{request.FromAddress}>";

        await session.WriteLineAsync(mailFromCommand, ct).ConfigureAwait(false);
        var mailFrom = await session.ReadResponseAsync(ct).ConfigureAwait(false);
        if (mailFrom.Code != 250)
        {
            return ClassifySmtpFailure(mailFrom.Code, Elapsed(started));
        }

        foreach (var recipient in request.Recipients)
        {
            await session.WriteLineAsync($"RCPT TO:<{recipient}>", ct).ConfigureAwait(false);
            var rcpt = await session.ReadResponseAsync(ct).ConfigureAwait(false);
            if (rcpt.Code is not (250 or 251))
            {
                return ClassifySmtpFailure(rcpt.Code, Elapsed(started));
            }
        }

        await session.WriteLineAsync("DATA", ct).ConfigureAwait(false);
        var dataStart = await session.ReadResponseAsync(ct).ConfigureAwait(false);
        if (dataStart.Code != 354)
        {
            return ClassifySmtpFailure(dataStart.Code, Elapsed(started));
        }

        await session.WriteRawAsync(payload, ct).ConfigureAwait(false);
        var dataDone = await session.ReadResponseAsync(ct).ConfigureAwait(false);
        if (dataDone.Code != 250)
        {
            return ClassifySmtpFailure(dataDone.Code, Elapsed(started));
        }

        // Best-effort QUIT; the message is already accepted once DATA returns 250.
        try
        {
            await session.WriteLineAsync("QUIT", ct).ConfigureAwait(false);
            _ = await session.ReadResponseAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignore post-acceptance QUIT close errors.
        }

        return OperatorNotificationDeliveryResult.Success(ChannelName, Elapsed(started));
    }

    private async Task<AddressResolutionResult> ResolveValidatedAddressesAsync(
        string host,
        SmtpSecurityMode securityMode,
        CancellationToken cancellationToken)
    {
        var trimmed = host.Trim('[', ']');
        IPAddress[] resolved;
        if (IPAddress.TryParse(trimmed, out var literal))
        {
            resolved = [literal];
        }
        else if (DnsResolver is not null)
        {
            resolved = await DnsResolver(trimmed, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            resolved = await Dns.GetHostAddressesAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }

        if (resolved.Length == 0)
        {
            return new AddressResolutionResult([], NotificationReasonCodes.ConnectFailed, TransientFailure: true);
        }

        foreach (var address in resolved)
        {
            if (OperatorNotificationValidation.IsBlockedSmtpAddress(address))
            {
                return new AddressResolutionResult([], NotificationReasonCodes.SsrfBlocked, TransientFailure: false);
            }

            if (securityMode == SmtpSecurityMode.None &&
                !OperatorNotificationValidation.IsPrivateOrLoopbackSmtpAddress(address))
            {
                return new AddressResolutionResult([], NotificationReasonCodes.TlsRequired, TransientFailure: false);
            }
        }

        return new AddressResolutionResult(resolved, FailureReasonCode: null, TransientFailure: false);
    }

    private static async Task ConnectFirstAddressAsync(
        TcpClient client,
        IPAddress[] addresses,
        int port,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            try
            {
                await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SocketException ex)
            {
                lastFailure = ex;
            }
        }
        throw lastFailure ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private async Task<SslStream> UpgradeToTlsAsync(Stream inner, string targetHost, CancellationToken cancellationToken)
    {
        var sslStream = new SslStream(inner, leaveInnerStreamOpen: false, CertificateValidationCallback);
        try
        {
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost.Trim('[', ']'),
                // Allow OS default TLS 1.2/1.3 selection (CA5398).
                EnabledSslProtocols = SslProtocols.None
            };
            await sslStream.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
            return sslStream;
        }
        catch
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<SmtpResponse> SendEhloAsync(SmtpLineSession session, CancellationToken cancellationToken)
    {
        await session.WriteLineAsync("EHLO cameraagent.local", cancellationToken).ConfigureAwait(false);
        var ehlo = await session.ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        if (ehlo.Code == 250)
        {
            return ehlo;
        }

        // Fall back to HELO for minimal relays that return 500/502 to EHLO.
        if (ehlo.Code is 500 or 502)
        {
            await session.WriteLineAsync("HELO cameraagent.local", cancellationToken).ConfigureAwait(false);
            return await session.ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        }

        return ehlo;
    }

    private static async Task<OperatorNotificationDeliveryResult?> AuthenticateAsync(
        SmtpLineSession session,
        SmtpResponse ehlo,
        string username,
        string password,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        var supportsPlain = ehlo.SupportsAuthMechanism("PLAIN");
        var supportsLogin = ehlo.SupportsAuthMechanism("LOGIN");

        // Fail closed: never transmit credentials unless the server explicitly advertised a supported AUTH mechanism.
        if (!supportsPlain && !supportsLogin)
        {
            return OperatorNotificationDeliveryResult.Failure(
                ChannelName, NotificationReasonCodes.AuthFailed, transient: false, elapsed);
        }

        if (supportsPlain)
        {
            var plainBytes = Encoding.UTF8.GetBytes($"\0{username}\0{password}");
            var plainPayload = Convert.ToBase64String(plainBytes);
            await session.WriteLineAsync($"AUTH PLAIN {plainPayload}", cancellationToken).ConfigureAwait(false);
            var response = await session.ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            if (response.Code == 235)
            {
                return null;
            }
            if (response.Code == 334)
            {
                await session.WriteLineAsync(plainPayload, cancellationToken).ConfigureAwait(false);
                response = await session.ReadResponseAsync(cancellationToken).ConfigureAwait(false);
                if (response.Code == 235)
                {
                    return null;
                }
            }
            if (response.Code != 504 || !supportsLogin)
            {
                return ClassifyAuthFailure(response.Code, elapsed);
            }
        }

        await session.WriteLineAsync("AUTH LOGIN", cancellationToken).ConfigureAwait(false);
        var loginChallenge = await session.ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        if (loginChallenge.Code != 334)
        {
            return ClassifyAuthFailure(loginChallenge.Code, elapsed);
        }

        await session.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(username)), cancellationToken).ConfigureAwait(false);
        var passwordChallenge = await session.ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        if (passwordChallenge.Code != 334)
        {
            return ClassifyAuthFailure(passwordChallenge.Code, elapsed);
        }

        await session.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(password)), cancellationToken).ConfigureAwait(false);
        var loginDone = await session.ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        return loginDone.Code == 235 ? null : ClassifyAuthFailure(loginDone.Code, elapsed);
    }

    private static OperatorNotificationDeliveryResult ClassifyAuthFailure(int code, TimeSpan elapsed)
        => code is >= 400 and < 500
            ? OperatorNotificationDeliveryResult.Failure(ChannelName, NotificationReasonCodes.ServerError, transient: true, elapsed)
            : OperatorNotificationDeliveryResult.Failure(ChannelName, NotificationReasonCodes.AuthFailed, transient: false, elapsed);

    private static OperatorNotificationDeliveryResult ClassifySmtpFailure(int code, TimeSpan elapsed)
        => code switch
        {
            >= 400 and < 500 => OperatorNotificationDeliveryResult.Failure(ChannelName, NotificationReasonCodes.ServerError, transient: true, elapsed),
            530 or 534 or 535 or 538 => OperatorNotificationDeliveryResult.Failure(ChannelName, NotificationReasonCodes.AuthFailed, transient: false, elapsed),
            >= 500 and < 600 => OperatorNotificationDeliveryResult.Failure(ChannelName, NotificationReasonCodes.ClientRejected, transient: false, elapsed),
            _ => OperatorNotificationDeliveryResult.Failure(ChannelName, NotificationReasonCodes.ProtocolError, transient: false, elapsed)
        };

    internal static byte[] BuildRfc5322Message(
        SmtpDeliveryRequest request,
        DateTimeOffset nowUtc,
        bool serverSupports8BitMime = false)
        => BuildRfc5322Message(request, nowUtc, serverSupports8BitMime, out _);

    internal static byte[] BuildRfc5322Message(
        SmtpDeliveryRequest request,
        DateTimeOffset nowUtc,
        bool serverSupports8BitMime,
        out bool uses8BitMime)
    {
        var sb = new StringBuilder(1024);
        sb.Append("From: ").Append(FormatFromHeader(request.FromDisplayName, request.FromAddress)).Append("\r\n");
        sb.Append("To: ").Append(FormatFoldedRecipients(request.Recipients)).Append("\r\n");
        sb.Append("Date: ").Append(nowUtc.UtcDateTime.ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append("@cameraagent.local>\r\n");
        sb.Append("Subject: ").Append(EncodeHeaderValue(request.Subject)).Append("\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: text/plain; charset=utf-8\r\n");

        var normalizedBody = request.Body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalizedBody.Split('\n');

        var isSevenBit = IsSevenBitSafe(lines);
        if (isSevenBit)
        {
            uses8BitMime = false;
            sb.Append("Content-Transfer-Encoding: 7bit\r\n");
            sb.Append("Auto-Submitted: auto-generated\r\n");
            sb.Append("X-HVO-Notification-Event: ").Append(request.EventSlug).Append("\r\n");
            sb.Append("\r\n");
            AppendDotStuffedLines(sb, lines);
        }
        else if (serverSupports8BitMime && AllUtf8LinesWithinLimit(lines))
        {
            uses8BitMime = true;
            sb.Append("Content-Transfer-Encoding: 8bit\r\n");
            sb.Append("Auto-Submitted: auto-generated\r\n");
            sb.Append("X-HVO-Notification-Event: ").Append(request.EventSlug).Append("\r\n");
            sb.Append("\r\n");
            AppendDotStuffedLines(sb, lines);
        }
        else
        {
            uses8BitMime = false;
            sb.Append("Content-Transfer-Encoding: quoted-printable\r\n");
            sb.Append("Auto-Submitted: auto-generated\r\n");
            sb.Append("X-HVO-Notification-Event: ").Append(request.EventSlug).Append("\r\n");
            sb.Append("\r\n");
            AppendQuotedPrintableBody(sb, lines);
        }

        sb.Append(".\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string FormatFromHeader(string displayName, string address)
    {
        var trimmed = displayName.Trim();
        if (trimmed.Length == 0)
        {
            return $"<{address}>";
        }

        // RFC 2047 Section 5: encoded-words MUST NOT appear within a quoted-string.
        if (IsPrintableAscii(trimmed))
        {
            var escaped = trimmed
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
            return $"\"{escaped}\" <{address}>";
        }

        return $"{EncodeRfc2047Base64Words(trimmed)} <{address}>";
    }

    private static string FormatFoldedRecipients(IReadOnlyList<string> recipients)
    {
        var sb = new StringBuilder(recipients.Count * 40);
        var currentLineLength = 4; // "To: "
        for (var i = 0; i < recipients.Count; i++)
        {
            var token = $"<{recipients[i]}>";
            if (i == 0)
            {
                sb.Append(token);
                currentLineLength += token.Length;
            }
            else if (currentLineLength + 2 + token.Length > RecommendedHeaderFoldLength)
            {
                sb.Append(",\r\n ").Append(token);
                currentLineLength = 1 + token.Length;
            }
            else
            {
                sb.Append(", ").Append(token);
                currentLineLength += 2 + token.Length;
            }
        }
        return sb.ToString();
    }

    private static string EncodeHeaderValue(string value)
    {
        if (IsPrintableAscii(value))
        {
            return value;
        }
        return EncodeRfc2047Base64Words(value);
    }

    private static string EncodeRfc2047Base64Words(string value)
    {
        // RFC 2047 Section 2: each encoded-word is at most 75 octets: "=?utf-8?B?" (10) + base64 (<=60, 45 UTF-8 bytes) + "?=" (2) = 72 octets.
        const int maxRawBytesPerWord = 45;
        var sb = new StringBuilder();
        var chunk = new List<byte>(maxRawBytesPerWord);

        void FlushChunk()
        {
            if (chunk.Count == 0) return;
            if (sb.Length > 0) sb.Append("\r\n ");
            sb.Append("=?utf-8?B?").Append(Convert.ToBase64String(chunk.ToArray())).Append("?=");
            chunk.Clear();
        }

        Span<byte> runeBytes = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            var len = rune.EncodeToUtf8(runeBytes);
            if (chunk.Count + len > maxRawBytesPerWord)
            {
                FlushChunk();
            }
            for (var i = 0; i < len; i++)
            {
                chunk.Add(runeBytes[i]);
            }
        }
        FlushChunk();
        return sb.ToString();
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

    private static bool IsSevenBitSafe(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.Length > MaximumRfc5322LineOctets)
            {
                return false;
            }
            foreach (var c in line)
            {
                if (c != '\t' && c is < ' ' or > '~')
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool AllUtf8LinesWithinLimit(string[] lines)
    {
        foreach (var line in lines)
        {
            if (Encoding.UTF8.GetByteCount(line) > MaximumRfc5322LineOctets)
            {
                return false;
            }
        }
        return true;
    }

    private static void AppendDotStuffedLines(StringBuilder sb, string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith('.'))
            {
                sb.Append('.');
            }
            sb.Append(line).Append("\r\n");
        }
    }

    private static void AppendQuotedPrintableBody(StringBuilder sb, string[] lines)
    {
        const int maxQpLineLength = 75;
        foreach (var line in lines)
        {
            var utf8 = Encoding.UTF8.GetBytes(line);
            var col = 0;
            for (var i = 0; i < utf8.Length; i++)
            {
                var b = utf8[i];
                var isLastByte = i == utf8.Length - 1;
                var mustEncode = b is < 33 or > 126 or (byte)'=' ||
                                 (col == 0 && b == (byte)'.');
                if (b is (byte)' ' or (byte)'\t' && !isLastByte)
                {
                    mustEncode = false;
                }

                var tokenLen = mustEncode ? 3 : 1;
                if (col + tokenLen > maxQpLineLength)
                {
                    sb.Append("=\r\n");
                    col = 0;
                    if (b == (byte)'.')
                    {
                        mustEncode = true;
                        tokenLen = 3;
                    }
                }

                if (mustEncode)
                {
                    sb.Append('=').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append((char)b);
                }
                col += tokenLen;
            }
            sb.Append("\r\n");
        }
    }

    private TimeSpan Elapsed(long started) => _timeProvider.GetElapsedTime(started);

    private readonly record struct AddressResolutionResult(
        IPAddress[] Addresses,
        string? FailureReasonCode,
        bool TransientFailure);

    private sealed class SmtpLineSession(Stream stream)
    {
        private readonly Stream _stream = stream;
        private readonly byte[] _readBuffer = new byte[1024];
        private int _bufferPos;
        private int _bufferLen;

        public async Task WriteLineAsync(string command, CancellationToken cancellationToken)
        {
            var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteRawAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<SmtpResponse> ReadResponseAsync(CancellationToken cancellationToken)
        {
            var lines = new List<string>(4);
            var code = 0;

            for (var lineIndex = 0; lineIndex < MaximumResponseLines; lineIndex++)
            {
                var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line.Length < 3 ||
                    !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCode))
                {
                    throw new InvalidDataException("Malformed SMTP status line.");
                }
                if (code == 0)
                {
                    code = parsedCode;
                }
                else if (parsedCode != code)
                {
                    throw new InvalidDataException("Inconsistent multi-line SMTP response code.");
                }

                lines.Add(line.Length > 4 ? line[4..] : string.Empty);
                if (line.Length == 3 || line[3] == ' ')
                {
                    return new SmtpResponse(code, lines);
                }
                if (line[3] != '-')
                {
                    throw new InvalidDataException("Malformed SMTP continuation delimiter.");
                }
            }

            throw new InvalidDataException("SMTP response exceeded maximum line limit.");
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            var lineBytes = new List<byte>(128);
            while (true)
            {
                if (_bufferPos >= _bufferLen)
                {
                    _bufferLen = await _stream.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
                    _bufferPos = 0;
                    if (_bufferLen == 0)
                    {
                        throw new IOException("SMTP server closed connection unexpectedly.");
                    }
                }

                var b = _readBuffer[_bufferPos++];
                if (b == (byte)'\n')
                {
                    if (lineBytes.Count > 0 && lineBytes[^1] == (byte)'\r')
                    {
                        lineBytes.RemoveAt(lineBytes.Count - 1);
                    }
                    return Encoding.ASCII.GetString(lineBytes.ToArray());
                }

                if (lineBytes.Count >= MaximumLineLength)
                {
                    throw new InvalidDataException("SMTP response line exceeded maximum length.");
                }
                lineBytes.Add(b);
            }
        }
    }

    private sealed record SmtpResponse(int Code, IReadOnlyList<string> Lines)
    {
        public bool SupportsCapability(string capability) =>
            Lines.Any(line =>
                string.Equals(line.Trim(), capability, StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(capability + " ", StringComparison.OrdinalIgnoreCase));

        public bool SupportsAuthMechanism(string mechanism) =>
            Lines.Any(line =>
            {
                if (!line.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("AUTH=", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                var tokens = line[5..].Split([' ', '='], StringSplitOptions.RemoveEmptyEntries);
                return tokens.Any(token => string.Equals(token, mechanism, StringComparison.OrdinalIgnoreCase));
            });
    }
}
