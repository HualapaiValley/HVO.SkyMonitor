using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class ArtifactUploadClientTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task UploadAsync_MissingPayload_QuarantinesWithoutRequest()
    {
        using var client = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var uploadClient = CreateUploadClient(client);
        var manifest = new ArtifactUploadManifest("v1", "agent", Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", 1, new string('A', 64), DateTimeOffset.UnixEpoch, "raw-v1", "missing.bin");

        var result = await uploadClient.UploadAsync(
            Path.GetTempPath(), CreateRecord(manifest), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ArtifactUploadDisposition.Quarantine, result.Disposition);
        Assert.AreEqual("payload-missing", result.Reason);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task UploadAsync_TransportFailure_ReturnsRetry()
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

            var result = await uploadClient.UploadAsync(
                root, CreateRecord(manifest), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(ArtifactUploadDisposition.Retry, result.Disposition);
            Assert.AreEqual("transport-failure", result.Reason);
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
    [TestCategory("Unit")]
    public async Task UploadAsync_ExactStructuredAcknowledgement_IsAccepted()
    {
        using var root = new TemporaryRoot();
        var payload = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "payload.bin"), payload).ConfigureAwait(false);
        var manifest = CreateManifest(payload);
        var acknowledgement = new ArtifactUploadAcknowledgement(
            ArtifactUploadAcknowledgement.CurrentSchemaVersion,
            manifest.IdempotencyKey,
            manifest.ArtifactId,
            manifest.ChecksumSha256,
            manifest.ByteLength,
            DateTimeOffset.UnixEpoch,
            manifest.SchemaVersion);
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent(JsonSerializer.Serialize(acknowledgement), Encoding.UTF8, "application/json")
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        var result = await CreateUploadClient(client).UploadAsync(
            root.Path, CreateRecord(manifest), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ArtifactUploadDisposition.Acknowledged, result.Disposition);
        Assert.IsNotNull(result.Acknowledgement);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task UploadAsync_MismatchedAcknowledgement_IsQuarantined()
    {
        using var root = new TemporaryRoot();
        var payload = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "payload.bin"), payload).ConfigureAwait(false);
        var manifest = CreateManifest(payload);
        var acknowledgement = new ArtifactUploadAcknowledgement(
            "v1", manifest.IdempotencyKey, Guid.NewGuid(), manifest.ChecksumSha256,
            manifest.ByteLength, DateTimeOffset.UnixEpoch, "v1");
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(acknowledgement), Encoding.UTF8, "application/json")
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        var result = await CreateUploadClient(client).UploadAsync(
            root.Path, CreateRecord(manifest), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ArtifactUploadDisposition.Quarantine, result.Disposition);
        Assert.AreEqual("acknowledgement-mismatch", result.Reason);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(408, ArtifactUploadDisposition.Retry)]
    [DataRow(425, ArtifactUploadDisposition.Retry)]
    [DataRow(429, ArtifactUploadDisposition.Retry)]
    [DataRow(503, ArtifactUploadDisposition.Retry)]
    [DataRow(400, ArtifactUploadDisposition.Quarantine)]
    [DataRow(401, ArtifactUploadDisposition.Quarantine)]
    [DataRow(403, ArtifactUploadDisposition.Quarantine)]
    [DataRow(409, ArtifactUploadDisposition.Quarantine)]
    [DataRow(422, ArtifactUploadDisposition.Quarantine)]
    public async Task UploadAsync_HttpOutcome_IsClassified(int statusCode, ArtifactUploadDisposition expected)
    {
        using var root = new TemporaryRoot();
        var payload = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "payload.bin"), payload).ConfigureAwait(false);
        var manifest = CreateManifest(payload);
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage((HttpStatusCode)statusCode)))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        var result = await CreateUploadClient(client).UploadAsync(
            root.Path, CreateRecord(manifest), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(expected, result.Disposition);
    }

    [TestMethod]
    [TestCategory("Unit")]
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

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static ArtifactUploadClient CreateUploadClient(HttpClient client)
        => new(new TestHttpClientFactory(client), Options.Create(new CameraAgentHostOptions()), TimeProvider.System);

    private static ArtifactUploadManifest CreateManifest(byte[] payload)
        => new(
            "v1", "agent", Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw,
            "application/octet-stream", payload.LongLength,
            Convert.ToHexString(SHA256.HashData(payload)), DateTimeOffset.UnixEpoch,
            "raw-v1", "payload.bin");

    private static ArtifactOutboxRecord CreateRecord(ArtifactUploadManifest manifest)
        => new(
            manifest.IdempotencyKey,
            ArtifactOutboxManifestKind.LegacyV1,
            JsonSerializer.SerializeToUtf8Bytes(manifest),
            ArtifactManifestDocument.FromLegacy(manifest),
            manifest.ArtifactId,
            manifest.Role,
            manifest.RelativeArtifactPath,
            manifest.ChecksumSha256,
            manifest.ByteLength,
            manifest.MediaType,
            ArtifactOutboxStatus.Pending,
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            null,
            null,
            null,
            null);

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "skymonitor-upload-client", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
