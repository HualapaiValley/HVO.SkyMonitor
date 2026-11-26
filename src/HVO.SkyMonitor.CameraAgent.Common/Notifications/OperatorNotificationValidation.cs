using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>
/// Environment-bound network policy for operator notification transports. Test-only loopback/HTTP seams are strictly
/// internal and never configurable from production configuration or UI.
/// </summary>
public sealed class OperatorNotificationNetworkPolicy(bool isDevelopment = false)
{
    /// <summary>True only in the ASP.NET Core <c>Development</c> environment; production always excludes Mailpit.</summary>
    public bool IsDevelopment { get; } = isDevelopment;

    /// <summary>Test-only seam permitting loopback targets during local in-process unit/integration capture tests.</summary>
    internal bool AllowLoopbackForTesting { get; init; }

    /// <summary>Test-only seam permitting plain HTTP loopback webhooks during local in-process integration tests.</summary>
    internal bool AllowInsecureHttpWebhookForTesting { get; init; }
}

/// <summary>
/// Strict configuration, SSRF, header-injection, and Mailpit production-exclusion validation for operator notifications.
/// </summary>
public static partial class OperatorNotificationValidation
{
    public const int MaximumWebhookUrlLength = 2048;
    public const int MaximumSecretLength = 1024;

    private static readonly HashSet<string> ForbiddenWebhookHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Content-Type",
        "Transfer-Encoding",
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Upgrade",
        "Cookie",
        "Set-Cookie",
        "Origin",
        "Referer",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto"
    };

    private static readonly HashSet<string> MailpitHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mailpit",
        "skymonitor-mailpit",
        "mailhog"
    };

    public static bool TryParseTimeOfDay(string? value, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length != 5)
        {
            return false;
        }
        return TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    /// <summary>
    /// Identifies development-only Mailpit hostnames that are excluded from production releases.
    /// </summary>
    public static bool IsMailpitHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }
        var trimmed = host.Trim().TrimEnd('.');
        if (MailpitHostNames.Contains(trimmed))
        {
            return true;
        }
        var labels = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Any(static label =>
            string.Equals(label, "mailpit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(label, "skymonitor-mailpit", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }
        var trimmed = host.Trim();
        if (trimmed.Length != host.Length || trimmed.Length > SmtpNotificationOptions.MaximumHostLength ||
            trimmed.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return false;
        }
        if (trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.Contains('/', StringComparison.Ordinal) ||
            trimmed.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }
        if (IPAddress.TryParse(trimmed, out _))
        {
            return true;
        }
        return Uri.CheckHostName(trimmed) == UriHostNameType.Dns && DnsHostRegex().IsMatch(trimmed);
    }

    public static bool IsValidEmailAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) ||
            address.Length > SmtpNotificationOptions.MaximumAddressLength ||
            address.Trim().Length != address.Length ||
            HasHeaderInjectionCharacters(address))
        {
            return false;
        }
        var at = address.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != address.LastIndexOf('@') || at >= address.Length - 1)
        {
            return false;
        }
        var local = address[..at];
        var domain = address[(at + 1)..];
        if (local.Length > 64 || domain.Length > 253 || local.Any(char.IsWhiteSpace) || domain.Any(char.IsWhiteSpace))
        {
            return false;
        }
        if (!domain.Contains('.', StringComparison.Ordinal) || !DnsHostRegex().IsMatch(domain))
        {
            return false;
        }
        return EmailLocalPartRegex().IsMatch(local);
    }

    public static bool HasHeaderInjectionCharacters(string? value)
        => value is not null && value.Any(static c => c is '\r' or '\n' or '\0' || char.IsControl(c));

    public static bool IsValidHeaderValue(string? value, int maximumLength = MaximumSecretLength + 16)
        => !string.IsNullOrEmpty(value) && value.Length <= maximumLength && !HasHeaderInjectionCharacters(value);

    public static bool IsNtfyRootPath(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var path = uri.AbsolutePath;
        return string.IsNullOrEmpty(path) || path == "/";
    }

    public static bool IsValidAuthHeaderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > WebhookNotificationOptions.MaximumHeaderNameLength ||
            HasHeaderInjectionCharacters(name))
        {
            return false;
        }
        var trimmed = name.Trim();
        if (trimmed.Length != name.Length || ForbiddenWebhookHeaders.Contains(trimmed))
        {
            return false;
        }
        return HttpHeaderTokenRegex().IsMatch(trimmed);
    }

    /// <summary>
    /// Validates a webhook URL syntax, scheme, embedded credentials, and literal IP/hostname SSRF constraints.
    /// </summary>
    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "Validates unparsed raw operator-supplied webhook URL text before constructing a Uri.")]
    public static bool TryValidateWebhookUrl(
        string? rawUrl,
        bool allowPrivateNetwork,
        OperatorNotificationNetworkPolicy policy,
        out Uri? uri,
        out string? reasonCode,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(policy);
        uri = null;
        reasonCode = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            reasonCode = NotificationReasonCodes.MissingSecret;
            problem = "Webhook URL is required when webhook notifications are enabled.";
            return false;
        }
        if (rawUrl.Length > MaximumWebhookUrlLength || rawUrl.Any(char.IsControl) || rawUrl.Trim().Length != rawUrl.Length)
        {
            reasonCode = NotificationReasonCodes.InvalidConfiguration;
            problem = "Webhook URL must be a single-line URL of at most 2048 characters without control characters.";
            return false;
        }
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsed))
        {
            reasonCode = NotificationReasonCodes.InvalidConfiguration;
            problem = "Webhook URL must be a valid absolute HTTPS address.";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            reasonCode = NotificationReasonCodes.InvalidConfiguration;
            problem = "Webhook URL must not embed credentials in the userinfo segment; use the protected auth token field instead.";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.Fragment))
        {
            reasonCode = NotificationReasonCodes.InvalidConfiguration;
            problem = "Webhook URL must not contain a fragment (#).";
            return false;
        }

        var isHttps = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isAllowedTestHttp = policy.AllowInsecureHttpWebhookForTesting &&
            string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !isAllowedTestHttp)
        {
            reasonCode = NotificationReasonCodes.SsrfBlocked;
            problem = "Webhook URL must use HTTPS.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Host) || !TryGetIdnHost(parsed, out var idnHost))
        {
            reasonCode = NotificationReasonCodes.InvalidConfiguration;
            problem = "Webhook URL must include a valid host name.";
            return false;
        }

        if (IsBlockedWebhookHostName(parsed.Host, policy.AllowLoopbackForTesting) ||
            IsBlockedWebhookHostName(idnHost, policy.AllowLoopbackForTesting))
        {
            reasonCode = NotificationReasonCodes.SsrfBlocked;
            problem = "Webhook target host is blocked by outbound network safety policy.";
            return false;
        }

        if ((IPAddress.TryParse(parsed.Host.Trim('[', ']'), out var literalIp) ||
             IPAddress.TryParse(idnHost.Trim('[', ']'), out literalIp)) &&
            IsBlockedWebhookAddress(literalIp, allowPrivateNetwork, policy.AllowLoopbackForTesting))
        {
            reasonCode = NotificationReasonCodes.SsrfBlocked;
            problem = "Webhook target IP address is blocked by outbound network safety policy.";
            return false;
        }

        uri = parsed;
        return true;
    }

    internal static bool TryGetIdnHost(Uri uri, out string idnHost)
    {
        ArgumentNullException.ThrowIfNull(uri);
        try
        {
            var value = uri.IdnHost;
            if (string.IsNullOrWhiteSpace(value))
            {
                idnHost = string.Empty;
                return false;
            }
            idnHost = value;
            return true;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            idnHost = string.Empty;
            return false;
        }
    }

    public static bool IsBlockedWebhookHostName(string host, bool allowLoopbackForTesting = false)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }
        var trimmed = host.Trim().TrimEnd('.').Trim('[', ']');
        if (string.Equals(trimmed, "metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "metadata", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "instance-data", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!allowLoopbackForTesting &&
            (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase) ||
             trimmed.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Enforces SSRF IP blocking for webhook destinations. Unspecified, loopback, link-local, cloud metadata
    /// (169.254.169.254, 100.100.100.200, fd00:ec2::254, etc.), CGNAT (100.64.0.0/10), multicast, broadcast,
    /// embedded-IPv4 IPv6 forms, and reserved ranges are always blocked (except loopback under the internal test seam).
    /// Private RFC 1918 / ULA ranges are blocked unless <paramref name="allowPrivateNetwork"/> is explicitly opted in.
    /// </summary>
    public static bool IsBlockedWebhookAddress(
        IPAddress address,
        bool allowPrivateNetwork,
        bool allowLoopbackForTesting = false)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return !allowLoopbackForTesting;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (!address.TryWriteBytes(bytes, out _))
            {
                return true;
            }
            var b0 = bytes[0];
            var b1 = bytes[1];
            var b2 = bytes[2];
            var b3 = bytes[3];

            // Unconditional cloud-metadata, loopback, link-local, CGNAT, and special-purpose exclusions
            // MUST run BEFORE private-LAN opt-in exceptions.
            // 0.0.0.0/8 (this network / unspecified)
            if (b0 == 0) return true;
            // 127.0.0.0/8 (loopback)
            if (b0 == 127) return !allowLoopbackForTesting;
            // 169.254.0.0/16 (link-local + AWS/Azure/GCP/DO/OCI IMDS 169.254.169.254)
            if (b0 == 169 && b1 == 254) return true;
            // 100.100.100.200 (Alibaba Cloud ECS IMDS) and entire 100.64.0.0/10 (RFC 6598 shared/CGNAT space)
            if (b0 == 100 && b1 == 100 && b2 == 100 && b3 == 200) return true;
            if (b0 == 100 && b1 is >= 64 and <= 127) return true;
            // 224.0.0.0/4 (multicast) and 240.0.0.0/4 (reserved + 255.255.255.255 broadcast)
            if (b0 >= 224) return true;
            // 192.0.0.0/24 (IETF protocol assignments + Oracle Cloud IMDS 192.0.0.192)
            if (b0 == 192 && b1 == 0 && b2 == 0) return true;
            // 192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24 (TEST-NET documentation)
            if ((b0 == 192 && b1 == 0 && b2 == 2) ||
                (b0 == 198 && b1 == 51 && b2 == 100) ||
                (b0 == 203 && b1 == 0 && b2 == 113))
            {
                return true;
            }
            // 192.88.99.0/24 (6to4 relay anycast, RFC 7526)
            if (b0 == 192 && b1 == 88 && b2 == 99) return true;
            // 198.18.0.0/15 (benchmarking)
            if (b0 == 198 && (b1 == 18 || b1 == 19)) return true;

            if (!allowPrivateNetwork)
            {
                // 10.0.0.0/8 (RFC 1918)
                if (b0 == 10) return true;
                // 172.16.0.0/12 (RFC 1918)
                if (b0 == 172 && b1 is >= 16 and <= 31) return true;
                // 192.168.0.0/16 (RFC 1918)
                if (b0 == 192 && b1 == 168) return true;
            }

            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) ||
                address.Equals(IPAddress.IPv6None) ||
                address.IsIPv6LinkLocal ||
                address.IsIPv6Multicast ||
                address.IsIPv6SiteLocal)
            {
                return true;
            }

            Span<byte> bytes = stackalloc byte[16];
            if (!address.TryWriteBytes(bytes, out _))
            {
                return true;
            }

            // AWS IPv6 IMDS fd00:ec2::254 (fd00:ec2::/32) is always blocked even when private LAN is enabled.
            if (bytes[0] == 0xfd && bytes[1] == 0x00 && bytes[2] == 0x0e && bytes[3] == 0xc2)
            {
                return true;
            }

            // ::/96 (deprecated IPv4-compatible IPv6 such as ::127.0.0.1, ::169.254.169.254 — ::1 already handled above)
            if (IsAllZero(bytes[..12]))
            {
                return true;
            }

            // ::ffff:0:0/96 (IPv4-translated SIIT, RFC 2765)
            if (IsAllZero(bytes[..8]) && bytes[8] == 0xff && bytes[9] == 0xff && bytes[10] == 0 && bytes[11] == 0)
            {
                return true;
            }

            // 64:ff9b::/96 (NAT64 well-known prefix — inspect embedded IPv4)
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b &&
                IsAllZero(bytes[4..12]))
            {
                var embedded = new IPAddress(bytes[12..]);
                return IsBlockedWebhookAddress(embedded, allowPrivateNetwork, allowLoopbackForTesting);
            }

            // 64:ff9b:1::/48 (NAT64 local-use prefix, RFC 8215)
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b &&
                bytes[4] == 0x00 && bytes[5] == 0x01)
            {
                return true;
            }

            // 100::/64 (Discard-Only Address Block, RFC 6666)
            if (bytes[0] == 0x01 && bytes[1] == 0x00 && IsAllZero(bytes[2..8]))
            {
                return true;
            }

            // 2002::/16 (6to4, RFC 3056 — embeds IPv4 in bytes[2..6])
            if (bytes[0] == 0x20 && bytes[1] == 0x02)
            {
                return true;
            }

            // 2001::/32 (Teredo, RFC 4380)
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                return true;
            }

            // 2001:db8::/32 (documentation)
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            {
                return true;
            }

            // fc00::/7 (Unique Local Addresses)
            if (!allowPrivateNetwork && (bytes[0] & 0xfe) == 0xfc)
            {
                return true;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Enforces network safety for SMTP relays: private LAN relays are allowed, while link-local/IMDS, cloud metadata,
    /// unspecified, multicast/broadcast, and deprecated embedded-IPv4 IPv6 addresses are always blocked.
    /// </summary>
    public static bool IsBlockedSmtpAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (!address.TryWriteBytes(bytes, out _)) return true;
            var b0 = bytes[0];
            var b1 = bytes[1];
            var b2 = bytes[2];
            var b3 = bytes[3];
            if (b0 == 0) return true;
            if (b0 == 169 && b1 == 254) return true;
            if (b0 == 100 && b1 == 100 && b2 == 100 && b3 == 200) return true;
            if (b0 == 192 && b1 == 88 && b2 == 99) return true;
            if (b0 >= 224) return true;
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(address))
            {
                return false;
            }
            if (address.Equals(IPAddress.IPv6Any) ||
                address.Equals(IPAddress.IPv6None) ||
                address.IsIPv6LinkLocal ||
                address.IsIPv6Multicast ||
                address.IsIPv6SiteLocal)
            {
                return true;
            }
            Span<byte> bytes = stackalloc byte[16];
            if (!address.TryWriteBytes(bytes, out _)) return true;
            if (bytes[0] == 0xfd && bytes[1] == 0x00 && bytes[2] == 0x0e && bytes[3] == 0xc2)
            {
                return true;
            }
            if (IsAllZero(bytes[..12])) return true;
            if (IsAllZero(bytes[..8]) && bytes[8] == 0xff && bytes[9] == 0xff && bytes[10] == 0 && bytes[11] == 0) return true;
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && IsAllZero(bytes[4..12]))
            {
                return IsBlockedSmtpAddress(new IPAddress(bytes[12..]));
            }
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes[4] == 0x00 && bytes[5] == 0x01) return true;
            if (bytes[0] == 0x01 && bytes[1] == 0x00 && IsAllZero(bytes[2..8])) return true;
            if (bytes[0] == 0x20 && bytes[1] == 0x02) return true;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Returns true ONLY when <paramref name="address"/> is a loopback, RFC 1918 private IPv4, or IPv6 ULA peer
    /// (excluding all blocked/metadata addresses). Used to confine unauthenticated <see cref="SmtpSecurityMode.None"/>
    /// strictly to trusted local LAN relays.
    /// </summary>
    public static bool IsPrivateOrLoopbackSmtpAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (IsBlockedSmtpAddress(address))
        {
            return false;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (!address.TryWriteBytes(bytes, out _)) return false;
            var b0 = bytes[0];
            var b1 = bytes[1];
            return b0 == 127 || b0 == 10 || (b0 == 172 && b1 is >= 16 and <= 31) || (b0 == 192 && b1 == 168);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (!address.TryWriteBytes(bytes, out _)) return false;
            // fc00::/7 ULA (fd00:ec2::/32 is already rejected by IsBlockedSmtpAddress above)
            return (bytes[0] & 0xfe) == 0xfc;
        }
        return false;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Validates an entire operator notifications configuration + effective secrets before persisting or sending.
    /// Never echoes secret values into validation problem descriptions.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Validate(
        OperatorNotificationsOptions options,
        OperatorNotificationSecrets effectiveSecrets,
        OperatorNotificationNetworkPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(effectiveSecrets);
        ArgumentNullException.ThrowIfNull(policy);

        var problems = new Dictionary<string, string>(StringComparer.Ordinal);

        if (options.QueueCapacity is < 4 or > 256)
            problems["queueCapacity"] = "Queue capacity must be between 4 and 256.";
        if (options.MaxNotificationsPerHour is < 1 or > 60)
            problems["maxNotificationsPerHour"] = "Hourly notification cap must be between 1 and 60.";
        if (options.RequestTimeoutSeconds is < 1 or > 30)
            problems["requestTimeoutSeconds"] = "Request timeout must be between 1 and 30 seconds.";
        if (options.MaxRetryAttempts is < 0 or > 5)
            problems["maxRetryAttempts"] = "Maximum retry attempts must be between 0 and 5.";
        if (options.RetryInitialDelaySeconds is < 1 or > 300)
            problems["retryInitialDelaySeconds"] = "Initial retry delay must be between 1 and 300 seconds.";
        if (options.RetryMaximumDelaySeconds is < 1 or > 3600 ||
            options.RetryMaximumDelaySeconds < options.RetryInitialDelaySeconds)
            problems["retryMaximumDelaySeconds"] = "Maximum retry delay must be at least the initial delay and at most 3600 seconds.";
        if (options.OutageCooldownSeconds is < 10 or > 3600)
            problems["outageCooldownSeconds"] = "Outage cooldown must be between 10 and 3600 seconds.";
        if (options.ConsecutiveOutageThreshold is < 1 or > 10)
            problems["consecutiveOutageThreshold"] = "Consecutive outage threshold must be between 1 and 10.";
        if (options.EvaluationIntervalSeconds is < 5 or > 600)
            problems["evaluationIntervalSeconds"] = "Evaluation interval must be between 5 and 600 seconds.";
        if (options.CaptureStallThresholdMinutes is < 1 or > 1440)
            problems["captureStallThresholdMinutes"] = "Capture stall threshold must be between 1 and 1440 minutes.";
        if (options.CameraDisconnectedThresholdSeconds is < 0 or > 3600)
            problems["cameraDisconnectedThresholdSeconds"] = "Camera disconnected threshold must be between 0 and 3600 seconds.";
        if (options.TestSendMinimumIntervalSeconds is < 0 or > 300)
            problems["testSendMinimumIntervalSeconds"] = "Test send minimum interval must be between 0 and 300 seconds.";

        ValidateEvents(options.Events, problems);
        ValidateSecretsLengths(effectiveSecrets, problems);
        ValidateSmtp(options.Smtp, effectiveSecrets, policy, options.Enabled, problems);
        ValidateWebhook(options.Webhook, effectiveSecrets, policy, options.Enabled, problems);

        return problems;
    }

    private static void ValidateEvents(OperatorNotificationEventsOptions events, Dictionary<string, string> problems)
    {
        if (events.CaptureStalledCooldownMinutes is < 1 or > 1440)
            problems["events.captureStalledCooldownMinutes"] = "Cooldown must be between 1 and 1440 minutes.";
        if (events.CameraDisconnectedCooldownMinutes is < 1 or > 1440)
            problems["events.cameraDisconnectedCooldownMinutes"] = "Cooldown must be between 1 and 1440 minutes.";
        if (events.DiskPressureCooldownMinutes is < 1 or > 1440)
            problems["events.diskPressureCooldownMinutes"] = "Cooldown must be between 1 and 1440 minutes.";
        if (events.UpgradeOrRecoveryFailedCooldownMinutes is < 1 or > 1440)
            problems["events.upgradeOrRecoveryFailedCooldownMinutes"] = "Cooldown must be between 1 and 1440 minutes.";
        if (!TryParseTimeOfDay(events.DailyHeartbeatTime, out _))
            problems["events.dailyHeartbeatTime"] = "Daily heartbeat time must be in 24-hour HH:mm format (00:00 to 23:59).";
    }

    private static void ValidateSecretsLengths(OperatorNotificationSecrets secrets, Dictionary<string, string> problems)
    {
        if (secrets.SmtpUsername is { Length: > MaximumSecretLength } || HasHeaderInjectionCharacters(secrets.SmtpUsername))
            problems["smtp.username"] = "SMTP username must be a single line of at most 1024 characters without control characters.";
        if (secrets.SmtpPassword is { Length: > MaximumSecretLength } || HasHeaderInjectionCharacters(secrets.SmtpPassword))
            problems["smtp.password"] = "SMTP password must be a single line of at most 1024 characters without control characters.";
        if (secrets.WebhookAuthToken is { Length: > MaximumSecretLength } || HasHeaderInjectionCharacters(secrets.WebhookAuthToken))
            problems["webhook.authToken"] = "Webhook auth token must be a single line of at most 1024 characters without control characters.";
        if (secrets.WebhookUserKey is { Length: > MaximumSecretLength } || HasHeaderInjectionCharacters(secrets.WebhookUserKey))
            problems["webhook.userKey"] = "Webhook user key must be a single line of at most 1024 characters without control characters.";
        if (secrets.WebhookNtfyTopic is { Length: > WebhookNotificationOptions.MaximumTopicLength } || HasHeaderInjectionCharacters(secrets.WebhookNtfyTopic))
            problems["webhook.ntfyTopic"] = "ntfy topic must be at most 128 printable characters without control characters.";
    }

    private static void ValidateSmtp(
        SmtpNotificationOptions smtp,
        OperatorNotificationSecrets secrets,
        OperatorNotificationNetworkPolicy policy,
        bool masterEnabled,
        Dictionary<string, string> problems)
    {
        if (!Enum.IsDefined(smtp.SecurityMode))
            problems["smtp.securityMode"] = "Unsupported SMTP security mode.";
        if (smtp.Port is < 1 or > 65535)
            problems["smtp.port"] = "SMTP port must be between 1 and 65535.";
        if (smtp.FromDisplayName.Length > SmtpNotificationOptions.MaximumDisplayNameLength ||
            HasHeaderInjectionCharacters(smtp.FromDisplayName))
            problems["smtp.fromDisplayName"] = "From display name must be at most 120 printable characters without line breaks.";

        var hasCredentials = !string.IsNullOrEmpty(secrets.SmtpUsername) || !string.IsNullOrEmpty(secrets.SmtpPassword);
        var hasHost = !string.IsNullOrWhiteSpace(smtp.Host);
        IPAddress? parsedSmtpIp = null;
        if (hasHost)
        {
            if (!IsValidHost(smtp.Host))
            {
                problems["smtp.host"] = "SMTP host must be a valid hostname or IP address without scheme or path.";
            }
            else if (!policy.IsDevelopment && IsMailpitHost(smtp.Host))
            {
                problems["smtp.host"] = "Mailpit is a development/test-only capture tool and is excluded from production.";
            }
            else if (IPAddress.TryParse(smtp.Host.Trim('[', ']'), out parsedSmtpIp) && IsBlockedSmtpAddress(parsedSmtpIp))
            {
                problems["smtp.host"] = "SMTP target IP address is blocked by outbound network safety policy.";
            }
        }

        if (smtp.SecurityMode == SmtpSecurityMode.None)
        {
            if (hasCredentials && !problems.ContainsKey("smtp.securityMode"))
            {
                problems["smtp.securityMode"] = "STARTTLS or Implicit TLS is required whenever SMTP credentials are configured.";
            }
            else if (parsedSmtpIp is not null &&
                     !IsBlockedSmtpAddress(parsedSmtpIp) &&
                     !IsPrivateOrLoopbackSmtpAddress(parsedSmtpIp) &&
                     !problems.ContainsKey("smtp.securityMode"))
            {
                problems["smtp.securityMode"] = "Plaintext SMTP (SecurityMode=None) is restricted to unauthenticated local LAN or loopback relays; use STARTTLS or Implicit TLS for public addresses.";
            }
        }

        if (!string.IsNullOrWhiteSpace(smtp.FromAddress) && !IsValidEmailAddress(smtp.FromAddress))
            problems["smtp.fromAddress"] = "From address must be a valid email address without line breaks.";

        if (smtp.Recipients.Count > OperatorNotificationsOptions.MaximumRecipients)
        {
            problems["smtp.recipients"] = $"Specify at most {OperatorNotificationsOptions.MaximumRecipients} recipient email addresses.";
        }
        else if (smtp.Recipients.Any(static recipient => !IsValidEmailAddress(recipient)))
        {
            problems["smtp.recipients"] = "Every recipient must be a valid email address without line breaks.";
        }

        if (masterEnabled && smtp.Enabled)
        {
            if (!hasHost && !problems.ContainsKey("smtp.host"))
                problems["smtp.host"] = "SMTP host is required when SMTP notifications are enabled.";
            if (string.IsNullOrWhiteSpace(smtp.FromAddress) && !problems.ContainsKey("smtp.fromAddress"))
                problems["smtp.fromAddress"] = "From email address is required when SMTP notifications are enabled.";
            if (smtp.Recipients.Count == 0 && !problems.ContainsKey("smtp.recipients"))
                problems["smtp.recipients"] = "At least one recipient email address is required when SMTP notifications are enabled.";
            if (!string.IsNullOrEmpty(secrets.SmtpPassword) && string.IsNullOrEmpty(secrets.SmtpUsername) &&
                !problems.ContainsKey("smtp.username"))
                problems["smtp.username"] = "SMTP username is required when an SMTP password is set.";
        }
    }

    private static void ValidateWebhook(
        WebhookNotificationOptions webhook,
        OperatorNotificationSecrets secrets,
        OperatorNotificationNetworkPolicy policy,
        bool masterEnabled,
        Dictionary<string, string> problems)
    {
        if (!Enum.IsDefined(webhook.Format))
            problems["webhook.format"] = "Unsupported webhook payload format.";

        if (!string.IsNullOrWhiteSpace(webhook.AuthHeaderName) && !IsValidAuthHeaderName(webhook.AuthHeaderName))
            problems["webhook.authHeaderName"] = "Webhook auth header name must be a valid, non-reserved HTTP header token.";

        Uri? parsedWebhookUri = null;
        if (!string.IsNullOrWhiteSpace(secrets.WebhookUrl))
        {
            if (!TryValidateWebhookUrl(secrets.WebhookUrl, webhook.AllowPrivateNetwork, policy, out parsedWebhookUri, out _, out var urlProblem))
            {
                problems["webhook.url"] = urlProblem!;
            }
        }
        else if (masterEnabled && webhook.Enabled)
        {
            problems["webhook.url"] = "Webhook HTTPS URL is required when webhook notifications are enabled.";
        }

        if (masterEnabled && webhook.Enabled && webhook.Format == WebhookPayloadFormat.Ntfy &&
            parsedWebhookUri is not null && IsNtfyRootPath(parsedWebhookUri) &&
            string.IsNullOrWhiteSpace(secrets.WebhookNtfyTopic) &&
            !problems.ContainsKey("webhook.ntfyTopic"))
        {
            problems["webhook.ntfyTopic"] = "An ntfy topic is required when the webhook URL targets the ntfy server root.";
        }

        if (masterEnabled && webhook.Enabled && webhook.Format == WebhookPayloadFormat.Pushover)
        {
            if (string.IsNullOrWhiteSpace(secrets.WebhookAuthToken) && !problems.ContainsKey("webhook.authToken"))
                problems["webhook.authToken"] = "Pushover application API token is required when Pushover format is selected.";
            if (string.IsNullOrWhiteSpace(secrets.WebhookUserKey) && !problems.ContainsKey("webhook.userKey"))
                problems["webhook.userKey"] = "Pushover user key is required when Pushover format is selected.";
        }
    }

    [GeneratedRegex(@"^(?=.{1,253}$)(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)*(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)$", RegexOptions.CultureInvariant)]
    private static partial Regex DnsHostRegex();

    [GeneratedRegex(@"^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailLocalPartRegex();

    [GeneratedRegex(@"^[A-Za-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HttpHeaderTokenRegex();
}

/// <summary>Redaction utilities ensuring webhook URLs, tokens, credentials, and control characters never leak.</summary>
public static partial class OperatorNotificationRedactor
{
    /// <summary>
    /// Returns ONLY the scheme and host (plus non-default port) of a webhook URL, stripping userinfo, path, query, and
    /// fragment so secret path segments (such as Slack/ntfy tokens) or query tokens are never displayed or logged.
    /// </summary>
    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings",
        Justification = "Parses raw stored URL text safely to extract only scheme and host for redacted display.")]
    public static string? FormatEndpointHost(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }
        var scheme = uri.Scheme;
        if (!OperatorNotificationValidation.TryGetIdnHost(uri, out var host))
        {
            return null;
        }
        if (uri.HostNameType == UriHostNameType.IPv6)
        {
            host = $"[{host}]";
        }
        return uri.IsDefaultPort
            ? $"{scheme}://{host}"
            : string.Create(CultureInfo.InvariantCulture, $"{scheme}://{host}:{uri.Port}");
    }

    /// <summary>
    /// Sanitizes an operational text field for delivery payloads: removes control characters, collapses line breaks
    /// where disallowed, redacts URLs and credential-like tokens, and enforces a strict character bound.
    /// </summary>
    public static string SanitizeOperationalText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var cleaned = ControlCharactersRegex().Replace(value.Trim(), " ");
        cleaned = EmbeddedUrlRegex().Replace(cleaned, "[REDACTED-URL]");
        cleaned = AuthHeaderRegex().Replace(cleaned, "[REDACTED-CREDENTIAL]");
        return cleaned.Length <= maximumLength ? cleaned : cleaned[..maximumLength];
    }

    [GeneratedRegex(@"[\x00-\x1F\x7F]+", RegexOptions.CultureInvariant)]
    private static partial Regex ControlCharactersRegex();

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9+.-]*://\S+", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedUrlRegex();

    [GeneratedRegex(@"(?:authorization\s*:\s*(?:basic|bearer)\s+\S+|(?:password|token|secret)\s*=\s*\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthHeaderRegex();
}
