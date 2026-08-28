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
        var manifest = CreateManifest([1]) with { RelativeArtifactPath = "missing.bin" };

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
            var manifest = CreateManifest([1]);

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
            manifest.Descriptor.Artifact.ArtifactId,
            manifest.Descriptor.Artifact.ChecksumSha256,
            manifest.Descriptor.Layout.ByteLength,
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
    public async Task UploadAsync_RetryableCentralStatus_DoesNotReadOrTransmitPayloadAgain()
    {
        using var root = new TemporaryRoot();
        var payload = new byte[] { 1, 2, 3, 4 };
        var manifest = CreateManifest(payload);
        var requestCount = 0;
        using var client = new HttpClient(new ResponseHandler(request =>
        {
            requestCount++;
            Assert.AreEqual("/api/v1.0/artifacts/status", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("application/json", request.Content!.Headers.ContentType!.MediaType);
            return new HttpResponseMessage((HttpStatusCode)425);
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var retryRecord = CreateRecord(manifest) with { AttemptCount = 2 };

        var result = await CreateUploadClient(client).UploadAsync(
            root.Path, retryRecord, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ArtifactUploadDisposition.Retry, result.Disposition);
        Assert.AreEqual("http-425", result.Reason);
        Assert.AreEqual(1, requestCount);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(401)]
    [DataRow(403)]
    public async Task UploadAsync_ManifestPreflightAuthenticationRejection_IsQuarantined(int statusCode)
    {
        var manifest = CreateManifest([1, 2, 3, 4]);
        using var client = new HttpClient(new ResponseHandler(_ => new HttpResponseMessage((HttpStatusCode)statusCode)))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        var result = await CreateUploadClient(client).UploadAsync(
            Path.GetTempPath(), CreateRecord(manifest) with { AttemptCount = 2 }, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ArtifactUploadDisposition.Quarantine, result.Disposition);
        Assert.AreEqual("authentication-rejected", result.Reason);
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("credentials", ArtifactUploadDisposition.Quarantine, "authentication-rejected")]
    [DataRow("configuration", ArtifactUploadDisposition.Quarantine, "authentication-configuration-invalid")]
    [DataRow("json", ArtifactUploadDisposition.Quarantine, "authentication-response-invalid")]
    [DataRow("network", ArtifactUploadDisposition.Retry, "status-transport-failure")]
    public async Task UploadAsync_ManifestPreflightException_IsClassified(
        string failure,
        ArtifactUploadDisposition expectedDisposition,
        string expectedReason)
    {
        var manifest = CreateManifest([1, 2, 3, 4]);
        using var client = new HttpClient(new ExceptionHandler(() => failure switch
        {
            "credentials" => new HttpRequestException("Invalid client credentials.", null, HttpStatusCode.Unauthorized),
            "configuration" => new InvalidOperationException("Authentication is not configured."),
            "json" => new JsonException("Token response is invalid."),
            _ => new HttpRequestException("Authentication endpoint is unavailable.")
        }))
        {
            BaseAddress = new Uri("http://localhost/")
        };

        var result = await CreateUploadClient(client).UploadAsync(
            Path.GetTempPath(), CreateRecord(manifest) with { AttemptCount = 2 }, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(expectedDisposition, result.Disposition);
        Assert.AreEqual(expectedReason, result.Reason);
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
            "v1", manifest.IdempotencyKey, Guid.NewGuid(), manifest.Descriptor.Artifact.ChecksumSha256,
            manifest.Descriptor.Layout.ByteLength, DateTimeOffset.UnixEpoch, "v1");
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

    private sealed class ExceptionHandler(Func<Exception> exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception());
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static ArtifactUploadClient CreateUploadClient(HttpClient client)
        => new(new TestHttpClientFactory(client), Options.Create(new CameraAgentHostOptions()), TimeProvider.System);

    private static ArtifactManifestV2 CreateManifest(byte[] payload)
    {
        var manifest = Contracts.ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono8, payload.Length, 1, payload.Length, payload);
        return manifest with { RelativeArtifactPath = "payload.bin" };
    }

    private static ArtifactOutboxRecord CreateRecord(ArtifactManifestV2 manifest)
        => new(
            manifest.IdempotencyKey,
            ArtifactOutboxManifestKind.ManifestV2,
            CaptureContractJson.Serialize(manifest),
            ArtifactManifestDocument.FromCurrent(manifest),
            manifest.Descriptor.Artifact.ArtifactId,
            manifest.Descriptor.Artifact.Role,
            manifest.RelativeArtifactPath,
            manifest.Descriptor.Artifact.ChecksumSha256,
            manifest.Descriptor.Layout.ByteLength,
            manifest.Descriptor.Artifact.MediaType,
            ArtifactOutboxStatus.Pending,
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
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
