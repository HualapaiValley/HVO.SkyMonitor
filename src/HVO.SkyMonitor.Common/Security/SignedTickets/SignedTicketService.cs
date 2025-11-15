using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.SkyMonitor.Common.Security.SignedTickets;

/// <summary>
/// Implementation of signed ticket service using HMAC-SHA256 for integrity protection.
/// </summary>
public sealed class SignedTicketService : ISignedTicketService
{
    private readonly SignedTicketOptions _options;
    private readonly byte[] _secretKey;

    public SignedTicketService(IOptions<SignedTicketOptions> options)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.Secret))
        {
            throw new InvalidOperationException("SignedTicketOptions.Secret must be configured.");
        }

        // Convert secret to bytes (using UTF-8 encoding)
        // In production, use a proper 256-bit key from Key Vault
        _secretKey = Encoding.UTF8.GetBytes(_options.Secret);

        if (_secretKey.Length < 32)
        {
            throw new InvalidOperationException("SignedTicketOptions.Secret must be at least 256 bits (32 bytes).");
        }
    }

    public string GenerateSignedTicket(SignedTicket ticket)
    {
        if (ticket == null)
        {
            throw new ArgumentNullException(nameof(ticket));
        }

        // Canonicalize the ticket data
        var canonicalPayload = CanonicalizeTicket(ticket);

        // Compute HMAC-SHA256 signature
        var signature = ComputeHmac(canonicalPayload);

        // Encode ticket data as JSON
        var ticketJson = JsonSerializer.Serialize(ticket);
        var ticketBytes = Encoding.UTF8.GetBytes(ticketJson);

        // Format: {base64url(ticket_data)}.{base64url(signature)}
        var ticketBase64 = Base64UrlEncode(ticketBytes);
        var signatureBase64 = Base64UrlEncode(signature);

        return $"{ticketBase64}.{signatureBase64}";
    }

    public bool ValidateSignedTicket(string signedTicket, string httpMethod, string path, string? query = null)
    {
        return TryValidateAndExtractTicket(signedTicket, httpMethod, path, query, out _);
    }

    public bool TryValidateAndExtractTicket(string signedTicket, string httpMethod, string path, string? query, out SignedTicket? ticket)
    {
        ticket = null;

        if (string.IsNullOrWhiteSpace(signedTicket))
        {
            return false;
        }

        // Parse ticket format: {base64url(data)}.{base64url(signature)}
        var parts = signedTicket.Split('.');
        if (parts.Length != 2)
        {
            return false;
        }

        try
        {
            // Decode ticket data
            var ticketBytes = Base64UrlDecode(parts[0]);
            var providedSignature = Base64UrlDecode(parts[1]);

            // Deserialize ticket
            var ticketJson = Encoding.UTF8.GetString(ticketBytes);
            ticket = JsonSerializer.Deserialize<SignedTicket>(ticketJson);

            if (ticket == null)
            {
                return false;
            }

            // Validate expiration (with clock skew tolerance)
            var now = DateTime.UtcNow;
            var maxClockSkew = TimeSpan.FromSeconds(_options.MaxClockSkewSeconds);
            if (ticket.ExpiresUtc.Add(maxClockSkew) < now)
            {
                return false; // Expired
            }

            // Validate HTTP method
            if (!string.Equals(ticket.HttpMethod, httpMethod, StringComparison.OrdinalIgnoreCase))
            {
                return false; // Method mismatch
            }

            // Validate path
            var normalizedPath = NormalizePath(path);
            var ticketPath = NormalizePath(ticket.Path);
            if (!string.Equals(ticketPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return false; // Path mismatch
            }

            // Validate allowed paths if enforced
            if (_options.EnforceAllowedPaths && _options.AllowedPaths.Length > 0)
            {
                var isAllowed = _options.AllowedPaths.Any(allowed =>
                    normalizedPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));

                if (!isAllowed)
                {
                    return false; // Path not in allowed list
                }
            }

            // Validate query (if ticket includes it)
            if (!string.IsNullOrEmpty(ticket.Query))
            {
                var normalizedQuery = CanonicalizeQuery(query);
                var ticketQuery = CanonicalizeQuery(ticket.Query);
                if (!string.Equals(ticketQuery, normalizedQuery, StringComparison.Ordinal))
                {
                    return false; // Query mismatch
                }
            }

            // Verify HMAC signature
            var canonicalPayload = CanonicalizeTicket(ticket);
            var expectedSignature = ComputeHmac(canonicalPayload);

            // Constant-time comparison to prevent timing attacks
            if (!CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
            {
                return false; // Signature mismatch
            }

            return true;
        }
        catch
        {
            // Any parsing/validation error = invalid ticket
            return false;
        }
    }

    private string CanonicalizeTicket(SignedTicket ticket)
    {
        // Create canonical representation for HMAC signing
        // Format: v={version}|exp={expiresUtc}|sub={subjectId}|m={method}|p={path}|q={query}|s={scopes}
        var normalizedPath = NormalizePath(ticket.Path);
        var canonicalQuery = CanonicalizeQuery(ticket.Query);

        return $"v={ticket.Version}|" +
               $"exp={ticket.ExpiresUtc:O}|" + // ISO 8601 format
               $"sub={ticket.SubjectId}|" +
               $"m={ticket.HttpMethod.ToUpperInvariant()}|" +
               $"p={normalizedPath}|" +
               $"q={canonicalQuery}|" +
               $"s={ticket.Scopes}";
    }

    private string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        // Remove trailing slash, lowercase
        path = path.TrimEnd('/');
        return path.ToLowerInvariant();
    }

    private string CanonicalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        // Parse query string, sort parameters alphabetically
        var parameters = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var parts = p.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0]);
                var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
                return (key, value);
            })
            .OrderBy(p => p.key, StringComparer.Ordinal)
            .ThenBy(p => p.value, StringComparer.Ordinal);

        // Rebuild canonical query string
        return string.Join("&", parameters.Select(p => $"{p.key}={p.value}"));
    }

    private byte[] ComputeHmac(string payload)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(_secretKey);
        return hmac.ComputeHash(payloadBytes);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        // Convert to base64, then make URL-safe
        var base64 = Convert.ToBase64String(data);
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string base64Url)
    {
        // Convert from URL-safe base64
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');

        // Add padding if needed
        var padding = (4 - base64.Length % 4) % 4;
        if (padding > 0)
        {
            base64 += new string('=', padding);
        }

        return Convert.FromBase64String(base64);
    }
}
