namespace HVO.SkyMonitor.TestSupport;

/// <summary>
/// Shared test HMAC secrets for signed URLs, webhooks, and presigned requests.
/// </summary>
public static class TestHmacSecrets
{
    /// <summary>
    /// HMAC secret for signed URL generation and validation.
    /// WARNING: DO NOT use these values in production - for testing only.
    /// </summary>
    public static class SignedUrls
    {
        /// <summary>
        /// Primary secret for signing URLs (base64-encoded 256-bit key).
        /// Generated via: openssl rand -base64 32
        /// </summary>
        public const string PrimarySecret = "dGVzdC1zaWduZWQtdXJsLXNlY3JldC0xMjM0NTY3ODkwMTIzNDU2Nzg5MDEy";

        /// <summary>
        /// Secondary secret for key rotation scenarios.
        /// </summary>
        public const string SecondarySecret = "dGVzdC1zaWduZWQtdXJsLXNlY3JldC1zZWNvbmRhcnktYWJjZGVmZ2hpamts";

        /// <summary>
        /// Default TTL for signed URLs in tests (in seconds).
        /// </summary>
        public const int DefaultTtlSeconds = 300; // 5 minutes

        /// <summary>
        /// Clock skew tolerance for signature validation (in seconds).
        /// </summary>
        public const int ClockSkewSeconds = 30;
    }

    /// <summary>
    /// HMAC secret for webhook signature verification.
    /// </summary>
    public static class Webhooks
    {
        /// <summary>
        /// Primary webhook signing secret (base64-encoded 256-bit key).
        /// </summary>
        public const string PrimarySecret = "dGVzdC13ZWJob29rLXNlY3JldC0xMjM0NTY3ODkwMTIzNDU2Nzg5MDEyMzQ1";

        /// <summary>
        /// Header name for webhook signatures.
        /// </summary>
        public const string SignatureHeader = "X-Webhook-Signature";

        /// <summary>
        /// Timestamp header name for replay attack prevention.
        /// </summary>
        public const string TimestampHeader = "X-Webhook-Timestamp";
    }

    /// <summary>
    /// HMAC secret for presigned request authentication.
    /// </summary>
    public static class PresignedRequests
    {
        /// <summary>
        /// Primary secret for presigned requests (base64-encoded 256-bit key).
        /// </summary>
        public const string PrimarySecret = "dGVzdC1wcmVzaWduZWQtc2VjcmV0LTEyMzQ1Njc4OTAxMjM0NTY3ODkwMTI=";

        /// <summary>
        /// Query parameter name for signature.
        /// </summary>
        public const string SignatureParam = "signature";

        /// <summary>
        /// Query parameter name for expiration timestamp.
        /// </summary>
        public const string ExpiresParam = "expires";
    }
}
