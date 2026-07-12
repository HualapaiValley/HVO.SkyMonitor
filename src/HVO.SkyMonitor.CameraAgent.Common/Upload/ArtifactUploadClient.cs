using System.Net.Http.Headers;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

/// <summary>Streams queued artifacts to the versioned central multipart endpoint.</summary>
public sealed class ArtifactUploadClient(IHttpClientFactory httpClientFactory)
{
    public const string CentralClientName = "SkyMonitor.Api";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Uploads one manifest and payload, returning false for retryable unsuccessful responses.</summary>
    public async Task<bool> UploadAsync(string storageRoot, ArtifactUploadManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        var path = Path.Combine(Path.GetFullPath(storageRoot), manifest.RelativeArtifactPath);
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        using var content = new MultipartFormDataContent();
        using var manifestContent = new StringContent(JsonSerializer.Serialize(manifest, SerializerOptions));
        content.Add(manifestContent, "manifest");
        using var payload = new StreamContent(stream);
        payload.Headers.ContentType = MediaTypeHeaderValue.Parse(manifest.MediaType);
        content.Add(payload, "payload", Path.GetFileName(path));
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1.0/artifacts") { Content = content };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", manifest.IdempotencyKey);
        var httpClient = httpClientFactory.CreateClient(CentralClientName);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // Network loss is expected for an offline agent; retain the outbox manifest for a later retry.
            return false;
        }
    }
}
