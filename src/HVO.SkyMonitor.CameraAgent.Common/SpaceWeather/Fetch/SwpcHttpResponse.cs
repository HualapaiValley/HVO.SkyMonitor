using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;

/// <summary>Transport failures only; HTTP statuses and body facts have no policy classification here.</summary>
public enum SwpcTransportFailure
{
    /// <summary>An HTTP response was received and its required body processing completed.</summary>
    None,
    /// <summary>The whole-request deadline or a handler/connect timeout expired.</summary>
    Timeout,
    /// <summary>A transport operation failed.</summary>
    NetworkError
}

/// <summary>The optional raw conditional validators sent on a request.</summary>
/// <param name="EntityTag">The stored ETag, or null.</param>
/// <param name="LastModified">The stored Last-Modified string, or null.</param>
public sealed record SwpcRequestValidators(string? EntityTag, string? LastModified);

/// <summary>
/// Immutable transport facts, constructible without HTTP for scenario replay. Owner decision T1: when a body is
/// undecodable its size cannot be judged, so Oversize is false. Policy checks content type before mapping
/// BodyUndecodable to encoding, preserving the contract's oversize, content-type, encoding rejection order.
/// Owner decision T2: gzip is exactly one complete RFC 1952 member, with no trailing bytes or second member.
/// ID1/ID2 and CM=8 are required; reserved flags are rejected; MTIME, XFL and OS are ignored. FEXTRA is skipped;
/// FNAME and FCOMMENT are zero-terminated within 1,024 bytes each, including the terminator. FHCRC is the low
/// 16 bits of the header's IEEE CRC-32. Raw deflate must finish immediately before the eight-byte trailer,
/// whose IEEE CRC-32 and ISIZE must match the decoded body (length modulo 2^32). Oversize takes priority over framing checks.
/// Owner decision T3: before any gzip rejection after opening the body, drain the counting wire under the same
/// linked token with one fixed buffer of at most 8 KiB. Discard at most maximum+1 further wire bytes, with each
/// read bounded by the remaining allowance; stop at that bound without another read and return BodyUndecodable.
/// At EOF before the bound (or EOF already observed), check declared Content-Length: a mismatch is NetworkError;
/// a match or absent length is BodyUndecodable. Drain IOException/HttpIOException propagate as NetworkError;
/// the deadline is Timeout and caller cancellation throws. Decoded oversize keeps priority and never drains.
/// Unsupported codings and identity oversize by declared length are never opened or read, so their wire length
/// is never judged. The response owns an exact-length copy of its body.
/// </summary>
public sealed record SwpcHttpResponse
{
    /// <summary>
    /// Creates transport facts and copies caller body bytes into independently owned exact-length storage.
    /// A failure has no status and a completed HTTP response has a status. Record copies share owned body storage.
    /// </summary>
    public SwpcHttpResponse(SwpcProduct product, DateTimeOffset startedUtc, DateTimeOffset completedUtc,
        SwpcTransportFailure failure, int? statusCode, bool validatorsSent = false, string? mediaType = null,
        string? entityTag = null, string? lastModified = null, string? retryAfter = null,
        ReadOnlyMemory<byte> body = default, bool oversize = false, bool bodyUndecodable = false)
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        if ((failure == SwpcTransportFailure.None) != statusCode.HasValue)
            throw new ArgumentException("Only completed HTTP responses have a status.", nameof(statusCode));
        if (oversize && bodyUndecodable)
            throw new ArgumentException("An undecodable body has no size verdict.", nameof(oversize));
        if ((failure != SwpcTransportFailure.None || oversize || bodyUndecodable) && !body.IsEmpty)
            throw new ArgumentException("Failed or unreadable bodies contain no bytes.", nameof(body));
        Product = product;
        StartedUtc = startedUtc;
        CompletedUtc = completedUtc;
        Failure = failure;
        StatusCode = statusCode;
        ValidatorsSent = validatorsSent;
        MediaType = mediaType;
        EntityTag = entityTag;
        LastModified = lastModified;
        RetryAfter = retryAfter;
        // This is also the client's only copy: its cap+1 read buffer is sliced but never retained here.
        Body = body.IsEmpty ? ReadOnlyMemory<byte>.Empty : body.ToArray();
        Oversize = oversize;
        BodyUndecodable = bodyUndecodable;
    }

    /// <summary>The requested core product.</summary>
    public SwpcProduct Product { get; }
    /// <summary>The request start from the injected clock.</summary>
    public DateTimeOffset StartedUtc { get; }
    /// <summary>The completion from the injected clock.</summary>
    public DateTimeOffset CompletedUtc { get; }
    /// <summary>The transport failure, independent of HTTP status.</summary>
    public SwpcTransportFailure Failure { get; }
    /// <summary>The integer status, null exactly when Failure is not None.</summary>
    public int? StatusCode { get; }
    /// <summary>Whether at least one conditional validator was sent.</summary>
    public bool ValidatorsSent { get; }
    /// <summary>One bounded printable-ASCII raw Content-Type, including parameters.</summary>
    public string? MediaType { get; }
    /// <summary>One sanitized response ETag.</summary>
    public string? EntityTag { get; }
    /// <summary>One sanitized raw response Last-Modified.</summary>
    public string? LastModified { get; }
    /// <summary>One raw Retry-After field of at most 29 characters, with its comma preserved.</summary>
    public string? RetryAfter { get; }
    /// <summary>Owned exact-length decoded bytes for a readable, bounded 200 body; otherwise empty.</summary>
    public ReadOnlyMemory<byte> Body { get; }
    /// <summary>The decoded size exceeded the cap; no body is returned.</summary>
    public bool Oversize { get; }
    /// <summary>Unsupported content coding or invalid gzip; no body or size verdict is returned (T1).</summary>
    public bool BodyUndecodable { get; }
}

/// <summary>One bounded SWPC transport exchange, with no retry or fetch policy.</summary>
public interface ISwpcClient
{
    /// <summary>Fetches a core product; caller cancellation is thrown rather than reported as a transport fact.</summary>
    ValueTask<SwpcHttpResponse> FetchAsync(SwpcProduct product, SwpcRequestValidators? validators,
        CancellationToken cancellationToken);
}
