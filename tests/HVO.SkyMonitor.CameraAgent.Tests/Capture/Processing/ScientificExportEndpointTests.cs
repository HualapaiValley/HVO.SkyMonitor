using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Unit")]
    [DataRow(false, StatusCodes.Status503ServiceUnavailable, "scientific-export.astrometry-unavailable", "1")]
    [DataRow(true, StatusCodes.Status409Conflict, "scientific-export.astrometry-missing", null)]
    public async Task ScientificExportEndpointDistinguishesPendingAndMissingAstrometry(
        bool missing, int status, string reason, string? retryAfter)
    {
        var root = CreateTestRoot();
        try
        {
            var descriptor = ScientificExportTestDescriptor();
            var capacity = new Mock<IStorageCapacityProvider>(MockBehavior.Strict);
            using var exports = CreateScientificExports(root, descriptor, () => new MemoryStream(new byte[4]),
                capacityProvider: capacity.Object, retained: () => new(descriptor.Capture.CaptureId, "Unavailable",
                    CameraAgentRetainedAstrometryService.TemporaryEvidenceUnavailable)
                { HasMissingEvidence = missing });
            using var app = ScientificExportApp(exports);

            using var getBody = new MemoryStream();
            var get = await SendAsync(HttpMethods.Get, getBody).ConfigureAwait(false);
            Assert.AreEqual("application/problem+json", get.ContentType);
            using var problem = JsonDocument.Parse(getBody.ToArray());
            Assert.AreEqual(reason, problem.RootElement.GetProperty("reasonCode").GetString());

            using var headBody = new MemoryStream();
            await SendAsync(HttpMethods.Head, headBody).ConfigureAwait(false);
            Assert.AreEqual(0L, headBody.Length);
            capacity.VerifyNoOtherCalls();

            async Task<HttpResponse> SendAsync(string method, Stream body)
            {
                var response = await InvokeScientificExportAsync(app, descriptor.Capture.CaptureId,
                    descriptor.Artifact.ArtifactId, method, body).ConfigureAwait(false);
                Assert.AreEqual(status, response.StatusCode);
                if (retryAfter is null) Assert.IsFalse(response.Headers.ContainsKey(HeaderNames.RetryAfter));
                else Assert.AreEqual(retryAfter, response.Headers.RetryAfter.ToString());
                return response;
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Unit")]
    [DataRow("HEAD")]
    [DataRow("Range")]
    [DataRow("If-None-Match")]
    public async Task ScientificExportEndpointReusesRecentBytesWithoutStaging(string request)
    {
        var root = CreateTestRoot();
        try
        {
            var descriptor = ScientificExportTestDescriptor();
            var available = long.MaxValue;
            var capacity = new Mock<IStorageCapacityProvider>(MockBehavior.Strict);
            capacity.Setup(provider => provider.GetCapacity(root)).Returns(() => new StorageCapacity(long.MaxValue, available));
            using var exports = CreateScientificExports(root, descriptor, () => new MemoryStream(new byte[4]),
                capacityProvider: capacity.Object);
            using var app = ScientificExportApp(exports);
            using var originalBody = new MemoryStream();
            var original = await InvokeScientificExportAsync(app, descriptor.Capture.CaptureId,
                descriptor.Artifact.ArtifactId, HttpMethods.Get, originalBody).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, original.StatusCode);
            Assert.IsNotEmpty(originalBody.ToArray());

            available = 0;
            using var reusedBody = new MemoryStream();
            var method = request == "HEAD" ? HttpMethods.Head : HttpMethods.Get;
            var reused = await InvokeScientificExportAsync(app, descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
                method, reusedBody, headers =>
                {
                    if (request == "Range") headers.Range = "bytes=0-7";
                    if (request == "If-None-Match") headers.IfNoneMatch = original.Headers.ETag;
                }).ConfigureAwait(false);
            Assert.AreEqual(original.Headers.ETag, reused.Headers.ETag);
            Assert.AreEqual(original.Headers["X-Artifact-SHA256"], reused.Headers["X-Artifact-SHA256"]);
            switch (request)
            {
                case "HEAD":
                    Assert.AreEqual(StatusCodes.Status200OK, reused.StatusCode);
                    Assert.AreEqual(originalBody.Length, reused.ContentLength);
                    Assert.AreEqual(0L, reusedBody.Length);
                    break;
                case "Range":
                    Assert.AreEqual(StatusCodes.Status206PartialContent, reused.StatusCode);
                    CollectionAssert.AreEqual(originalBody.ToArray()[..8], reusedBody.ToArray());
                    break;
                default:
                    Assert.AreEqual(StatusCodes.Status304NotModified, reused.StatusCode);
                    Assert.AreEqual(0L, reusedBody.Length);
                    break;
            }
            capacity.Verify(provider => provider.GetCapacity(root), Times.Once());

            using var freshBody = new MemoryStream();
            var fresh = await InvokeScientificExportAsync(app, descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
                HttpMethods.Get, freshBody).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, fresh.StatusCode);
            capacity.Verify(provider => provider.GetCapacity(root), Times.Exactly(2));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static WebApplication ScientificExportApp(ICameraAgentScientificExports exports)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(exports);
        builder.Services.AddSingleton<CameraAgentOperatorTelemetry>();
        var app = builder.Build();
        app.MapGroup("/gallery").MapScientificExports();
        return app;
    }

    private static async Task<HttpResponse> InvokeScientificExportAsync(WebApplication app, Guid captureId, Guid artifactId,
        string method, Stream body, Action<IHeaderDictionary>? configure = null)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints).Single();
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = method;
        context.Request.RouteValues["captureId"] = captureId.ToString("D");
        context.Request.RouteValues["artifactId"] = artifactId.ToString("D");
        context.Request.RouteValues["format"] = "fits";
        context.Response.Body = body;
        configure?.Invoke(context.Request.Headers);
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        return context.Response;
    }
}
