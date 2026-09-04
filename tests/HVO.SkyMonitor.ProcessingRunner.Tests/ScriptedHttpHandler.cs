using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.ProcessingRunner.Tests;

/// <summary>A minimal LogicHost stand-in: scripted responses keyed by method and path, with request capture.</summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, byte[]?, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public ConcurrentQueue<(string Method, string Path, byte[] Body, HttpRequestHeaders Headers, string? ContentType)> Requests { get; } = new();

    public int TokenRequests { get; private set; }

    public void Map(HttpMethod method, string path, Func<HttpRequestMessage, byte[]?, HttpResponseMessage> responder)
        => _routes[$"{method.Method} {path}"] = responder;

    public void MapJson<T>(HttpMethod method, string path, Func<byte[]?, T> responder, HttpStatusCode status = HttpStatusCode.OK)
        => Map(method, path, (_, body) => Json(responder(body), status));

    public static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, ProcessingRunnerProtocol.SerializerOptions))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
            }
        };

    public static HttpResponseMessage Problem(HttpStatusCode status, string reasonCode, string message)
        => Json(new ProcessingRunnerProblem(reasonCode, message), status);

    public static HttpResponseMessage Bytes(byte[] payload)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var path = request.RequestUri!.AbsolutePath;
        Requests.Enqueue((request.Method.Method, path, body ?? [], request.Headers, request.Content?.Headers.ContentType?.ToString()));
        if (path.EndsWith("/connect/token", StringComparison.Ordinal))
        {
            TokenRequests++;
            var form = Encoding.UTF8.GetString(body ?? []);
            if (!form.Contains("grant_type=client_credentials", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"test-token\",\"token_type\":\"Bearer\",\"expires_in\":300}", Encoding.UTF8, "application/json")
            };
        }
        if (_routes.TryGetValue($"{request.Method.Method} {path}", out var responder))
        {
            return responder(request, body);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"no route for {request.Method.Method} {path}")
        };
    }
}
