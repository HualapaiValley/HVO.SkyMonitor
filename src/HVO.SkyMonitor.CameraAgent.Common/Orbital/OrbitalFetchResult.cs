using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>Outcome of one outbound orbital-elements request. Only <see cref="Retrieved"/> carries raw bytes.</summary>
public enum OrbitalFetchDisposition
{
    /// <summary>HTTP 200 with an accepted media type, encoding and size; the raw bytes are present.</summary>
    Retrieved,

    /// <summary>
    /// Any valid status (100..599) other than 200, or a status line outside 100..599
    /// (<c>orbital.invalid-http-status</c>, recorded as a halt with no HTTP status). The provider policy halts on it;
    /// the body was not read.
    /// </summary>
    HttpStatusHalt,

    /// <summary>TLS, authentication, connection, protocol or I/O failure. Keep last-known-good; no retry.</summary>
    TransportFailure,

    /// <summary>The bounded request timeout elapsed. Keep last-known-good; no retry.</summary>
    TimedOut,

    /// <summary>A 200 response whose media type, encoding, declared length or size bounds were not accepted.</summary>
    ResponseRejected
}

/// <summary>
/// Evidence for one outbound attempt. <paramref name="RawBytes"/> and <paramref name="RawSha256"/> are the decoded
/// source bytes and are present only when <paramref name="Disposition"/> is <see cref="OrbitalFetchDisposition.Retrieved"/>.
/// <paramref name="ReasonCode"/> is a stable <c>orbital.*</c> code and <paramref name="Detail"/> holds only bounded
/// type, status and count text, never exception messages, header values or body text.
/// </summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays",
    Justification = "The decoded provider bytes are handed once to the normalizer and the store; nothing mutates them after the fetch.")]
public sealed record OrbitalFetchResult(OrbitalFetchDisposition Disposition, int? HttpStatusCode,
    DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, byte[]? RawBytes, string? RawSha256,
    long WireLengthBytes, string? ContentEncoding, string? EntityTag, string? LastModified,
    string ReasonCode, string Detail);

/// <summary>One bounded, policy-compliant request to the provider.</summary>
public interface IOrbitalElementsProviderClient
{
    /// <summary>
    /// Never retries or follows redirects. A timeout returns <see cref="OrbitalFetchDisposition.TimedOut"/>.
    /// Caller cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    ValueTask<OrbitalFetchResult> FetchAsync(CancellationToken cancellationToken);
}
