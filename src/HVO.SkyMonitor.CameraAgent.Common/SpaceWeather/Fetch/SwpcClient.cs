using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;

/// <summary>The SWPC product facade over the shared bounded HTTP transport.</summary>
public sealed class SwpcClient(HttpClient httpClient, TimeProvider timeProvider, string userAgent) : ISwpcClient
{
    private readonly BoundedHttpClient transport = new(httpClient, timeProvider, userAgent);
    internal static bool IsValidUserAgent(string? value) => BoundedHttpClient.IsValidUserAgent(value);

    public async ValueTask<SwpcHttpResponse> FetchAsync(SwpcProduct product, SwpcRequestValidators? validators,
        CancellationToken cancellationToken)
    {
        var response = await transport.FetchAsync(SwpcSource.RequestUri(product), SwpcSource.MaximumDecodedBytes(product),
            SwpcSource.MediaType(product), validators, cancellationToken).ConfigureAwait(false);
        return new(product, response.StartedUtc, response.CompletedUtc, response.Failure, response.StatusCode,
            response.ValidatorsSent, response.MediaType, response.EntityTag, response.LastModified, response.RetryAfter,
            response.Body, response.Oversize, response.BodyUndecodable);
    }
}
