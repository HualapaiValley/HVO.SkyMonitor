using System.Net;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class ArtifactUploadClientTests
{
    [TestMethod]
    public async Task UploadAsync_MissingPayload_ReturnsFalseWithoutRequest()
    {
        using var client = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var uploadClient = CreateUploadClient(client);
        var manifest = new ArtifactUploadManifest("v1", "agent", Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 1, new string('A', 64), DateTimeOffset.UnixEpoch, "raw-v1", "missing.bin");

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
            var uploadClient = CreateUploadClient(client);
            var manifest = new ArtifactUploadManifest("v1", "agent", Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
                "application/octet-stream", 1, new string('A', 64), DateTimeOffset.UnixEpoch, "raw-v1", "payload.bin");

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

    [TestMethod]
    public async Task BandwidthLimitedReadStream_BoundsEachStreamingChunk()
    {
        using var source = new MemoryStream(new byte[100]);
        using var limited = new ArtifactUploadClient.BandwidthLimitedReadStream(source, 20, TimeProvider.System);
        var buffer = new byte[100];

        var bytesRead = await limited.ReadAsync(buffer).ConfigureAwait(false);

        Assert.AreEqual(2, bytesRead);
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

    private static ArtifactUploadClient CreateUploadClient(HttpClient client)
        => new(new TestHttpClientFactory(client), Options.Create(new CameraAgentHostOptions()), TimeProvider.System);
}
