using System.Net;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Upload;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class ArtifactUploadClientTests
{
    [TestMethod]
    public async Task UploadAsync_MissingPayload_ReturnsFalseWithoutRequest()
    {
        using var client = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var uploadClient = new ArtifactUploadClient(new TestHttpClientFactory(client));
        var manifest = new ArtifactUploadManifest("v1", "agent", Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 1, "checksum", DateTimeOffset.UnixEpoch, "raw-v1", "missing.bin");

        var result = await uploadClient.UploadAsync(Path.GetTempPath(), manifest, CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task UploadAsync_TransportFailure_ReturnsFalseForLaterRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(Path.Combine(root, "payload.bin"), [1]).ConfigureAwait(false);
            using var client = new HttpClient(new TransportFailureHandler()) { BaseAddress = new Uri("http://localhost/") };
            var uploadClient = new ArtifactUploadClient(new TestHttpClientFactory(client));
            var manifest = new ArtifactUploadManifest("v1", "agent", Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
                "application/octet-stream", 1, "checksum", DateTimeOffset.UnixEpoch, "raw-v1", "payload.bin");

            var result = await uploadClient.UploadAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new AssertFailedException("No HTTP request should be made for a missing payload.");
    }

    private sealed class TransportFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("LogicHost is unavailable.");
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
