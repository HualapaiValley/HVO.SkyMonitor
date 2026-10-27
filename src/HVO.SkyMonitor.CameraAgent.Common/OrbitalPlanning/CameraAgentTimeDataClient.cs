using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>
/// One bounded download. <paramref name="Bytes"/> is present exactly when <paramref name="ReasonCode"/> is null;
/// otherwise the reason is a stable <c>time-data.*</c> code and <paramref name="Detail"/> holds only bounded status and
/// count text, never a header value, body text or exception message.
/// </summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays",
    Justification = "The downloaded bytes are verified and written once; nothing mutates them.")]
public sealed record CameraAgentTimeDataDownload(byte[]? Bytes, DateTimeOffset? LastModifiedUtc, string? ReasonCode, string Detail);

/// <summary>Fetches one approved time-data URL.</summary>
public interface ICameraAgentTimeDataClient
{
    /// <summary>
    /// One GET of <paramref name="uri"/> with no retry and no redirect, within
    /// <see cref="CameraAgentTimeDataSources.RequestTimeout"/> and <paramref name="maximumBytes"/>. Caller cancellation
    /// throws <see cref="OperationCanceledException"/>; every other failure is returned.
    /// </summary>
    ValueTask<CameraAgentTimeDataDownload> FetchAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken);
}

/// <summary>The HTTPS client of the approved endpoints. Only HTTP 200 with a body inside the bound is accepted.</summary>
public sealed class CameraAgentTimeDataClient(HttpClient http, TimeProvider timeProvider, string userAgent) : ICameraAgentTimeDataClient
{
    public const string TimedOutReasonCode = "time-data.timed-out";
    public const string TransportReasonCode = "time-data.transport-failed";
    public const string HttpStatusReasonCode = "time-data.http-status";
    public const string OversizeReasonCode = "time-data.oversize";
    public const string EmptyReasonCode = "time-data.empty";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every transport failure becomes a bounded reason code; the exception text never leaves.")]
    public async ValueTask<CameraAgentTimeDataDownload> FetchAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Only HTTPS endpoints are approved.", nameof(uri));
        using var deadline = new CancellationTokenSource(CameraAgentTimeDataSources.RequestTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(userAgent);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return Failed(HttpStatusReasonCode, string.Create(CultureInfo.InvariantCulture, $"status={(int)response.StatusCode}"));
            if (response.Content.Headers.ContentLength is { } declared && declared > maximumBytes)
                return Failed(OversizeReasonCode, string.Create(CultureInfo.InvariantCulture, $"declared={declared} limit={maximumBytes}"));
            var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[maximumBytes + 1];
                var length = 0;
                int read;
                while (length < buffer.Length &&
                       (read = await stream.ReadAsync(buffer.AsMemory(length), linked.Token).ConfigureAwait(false)) > 0)
                    length += read;
                if (length > maximumBytes)
                    return Failed(OversizeReasonCode, string.Create(CultureInfo.InvariantCulture, $"read>{maximumBytes}"));
                if (length == 0) return Failed(EmptyReasonCode, "length=0");
                return new(buffer[..length], response.Content.Headers.LastModified?.ToUniversalTime(), null,
                    string.Create(CultureInfo.InvariantCulture, $"length={length}"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed(TimedOutReasonCode, string.Create(CultureInfo.InvariantCulture,
                $"deadline={CameraAgentTimeDataSources.RequestTimeout.TotalSeconds}s"));
        }
        catch (Exception exception)
        {
            return Failed(TransportReasonCode, string.Concat("exception=", exception.GetType().Name));
        }
    }

    private static CameraAgentTimeDataDownload Failed(string code, string detail) => new(null, null, code, detail);
}
