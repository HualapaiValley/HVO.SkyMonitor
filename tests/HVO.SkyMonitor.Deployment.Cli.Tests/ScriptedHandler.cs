namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Scripts HTTP responses by request sequence for clients that accept an injected handler.
/// </summary>
internal sealed class ScriptedHandler(
    Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responseFactory)
    : HttpMessageHandler
{
    internal int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        return responseFactory(request, RequestCount, cancellationToken);
    }
}
