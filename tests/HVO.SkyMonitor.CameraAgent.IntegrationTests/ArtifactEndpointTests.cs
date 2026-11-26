using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ArtifactEndpointTests
{
    [TestMethod]
    public async Task ArtifactEndpointsEnforceOwnerPolicyAsync()
    {
        var service = new StubArtifactService();
        using var factory = AssemblyHooks.Fixture.CreateCameraAgentFactory(services =>
        {
            services.RemoveAll<ICameraAgentArtifactService>();
            services.AddSingleton<ICameraAgentArtifactService>(service);
        });
        var (ownerId, nonOwnerId) = await GetUsersAsync(factory.Services).ConfigureAwait(false);

        using var anonymous = factory.CreateClient();
        using var anonymousResponse = await anonymous.GetAsync(
            new Uri($"/api/v1/operations/artifacts/{service.ArtifactId:D}/content", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var nonOwner = factory.CreateClient();
        nonOwner.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
        using var nonOwnerResponse = await nonOwner.GetAsync(
            new Uri($"/api/v1/operations/artifacts/{service.ArtifactId:D}/content", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerResponse.StatusCode);

        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
        using var ownerResponse = await owner.GetAsync(
            new Uri($"/api/v1/operations/artifacts/{service.ArtifactId:D}/content", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, ownerResponse.StatusCode);
    }

    [TestMethod]
    public async Task ContentSupportsExactGetHeadConditionalAndSingleRangeAsync()
    {
        var service = new StubArtifactService();
        using var factory = AssemblyHooks.Fixture.CreateCameraAgentFactory(services =>
        {
            services.RemoveAll<ICameraAgentArtifactService>();
            services.AddSingleton<ICameraAgentArtifactService>(service);
        });
        var (ownerId, _) = await GetUsersAsync(factory.Services).ConfigureAwait(false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
        var uri = new Uri($"/api/v1/operations/artifacts/{service.ArtifactId:D}/content", UriKind.Relative);

        using var response = await client.GetAsync(uri).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(service.Content, await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        Assert.AreEqual(service.ETag, response.Headers.ETag?.Tag);
        Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        StringAssert.Contains(response.Headers.CacheControl!.ToString(), "private", StringComparison.Ordinal);
        StringAssert.Contains(response.Content.Headers.ContentDisposition!.FileName!, service.ArtifactId.ToString("D"), StringComparison.Ordinal);
        using var unsupportedInline = await client.GetAsync(new Uri($"{uri}?inline=true", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual("attachment", unsupportedInline.Content.Headers.ContentDisposition?.DispositionType,
            "Opt-in inline must never expose raw/unknown payloads as active browser content.");

        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        conditionalRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(service.ETag));
        using var conditional = await client.SendAsync(conditionalRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotModified, conditional.StatusCode);
        Assert.IsEmpty(await conditional.Content.ReadAsByteArrayAsync().ConfigureAwait(false));

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, uri);
        using var head = await client.SendAsync(headRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        Assert.AreEqual(service.Content.LongLength, head.Content.Headers.ContentLength);
        Assert.IsEmpty(await head.Content.ReadAsByteArrayAsync().ConfigureAwait(false));

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        rangeRequest.Headers.Range = new RangeHeaderValue(2, 4);
        using var range = await client.SendAsync(rangeRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.PartialContent, range.StatusCode);
        CollectionAssert.AreEqual(service.Content[2..5], await range.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        Assert.AreEqual("bytes 2-4/8", range.Content.Headers.ContentRange?.ToString());

        using var multipleRange = new HttpRequestMessage(HttpMethod.Get, uri);
        multipleRange.Headers.TryAddWithoutValidation("Range", "bytes=0-1,3-4");
        using var rejectedRange = await client.SendAsync(multipleRange).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.RequestedRangeNotSatisfiable, rejectedRange.StatusCode);

        service.MediaType = "image/png";
        using var supportedInline = await client.GetAsync(new Uri($"{uri}?inline=true", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual("inline", supportedInline.Content.Headers.ContentDisposition?.DispositionType);
        CollectionAssert.AreEqual(service.Content, await supportedInline.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        Assert.AreEqual(service.ETag, supportedInline.Headers.ETag?.Tag, "Display must preserve exact-artifact checksum identity.");
    }

    [TestMethod]
    public async Task ReplayOutputContentEnforcesOwnerAndExactExecutionScopeAsync()
    {
        var service = new StubArtifactService();
        using var factory = AssemblyHooks.Fixture.CreateCameraAgentFactory(services =>
        {
            services.RemoveAll<ICameraAgentArtifactService>();
            services.AddSingleton<ICameraAgentArtifactService>(service);
        });
        var (ownerId, nonOwnerId) = await GetUsersAsync(factory.Services).ConfigureAwait(false);
        var uri = new Uri(
            $"/api/v1/operations/processing-graphs/executions/{service.ExecutionId:D}/outputs/{service.ArtifactId:D}/content",
            UriKind.Relative);

        using var anonymous = factory.CreateClient();
        using var anonymousResponse = await anonymous.GetAsync(uri).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var nonOwner = factory.CreateClient();
        nonOwner.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
        using var nonOwnerResponse = await nonOwner.GetAsync(uri).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerResponse.StatusCode);

        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
        using var exact = await owner.GetAsync(uri).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, exact.StatusCode);
        CollectionAssert.AreEqual(service.Content, await exact.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        service.MediaType = "image/png";
        using var replayInline = await owner.GetAsync(new Uri($"{uri}?inline=true", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual("attachment", replayInline.Content.Headers.ContentDisposition?.DispositionType,
            "New artifact-page inline mode must not silently change replay download behavior.");

        using var wrongExecution = await owner.GetAsync(new Uri(
            $"/api/v1/operations/processing-graphs/executions/{Guid.NewGuid():D}/outputs/{service.ArtifactId:D}/content",
            UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, wrongExecution.StatusCode);

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        rangeRequest.Headers.Range = new RangeHeaderValue(1, 3);
        using var range = await owner.SendAsync(rangeRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.PartialContent, range.StatusCode);
        CollectionAssert.AreEqual(service.Content[1..4], await range.Content.ReadAsByteArrayAsync().ConfigureAwait(false));

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, uri);
        using var head = await owner.SendAsync(headRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        Assert.IsEmpty(await head.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PreviewHeadAndFailuresDoNotDiscloseStorageDetailsAsync()
    {
        var comparisonOutputBytes = new List<(long Bytes, string? Outcome)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CameraAgentOperatorTelemetry.InstrumentationName &&
                    instrument.Name == "camera_agent.processing.comparison.output.bytes")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            comparisonOutputBytes.Add((
                measurement,
                GetTagValue(tags, "outcome") as string)));
        meterListener.Start();
        var service = new StubArtifactService();
        using var factory = AssemblyHooks.Fixture.CreateCameraAgentFactory(services =>
        {
            services.RemoveAll<ICameraAgentArtifactService>();
            services.AddSingleton<ICameraAgentArtifactService>(service);
        });
        var (ownerId, _) = await GetUsersAsync(factory.Services).ConfigureAwait(false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);

        var previewUri = new Uri($"/api/v1/operations/artifacts/{service.ArtifactId:D}/preview", UriKind.Relative);
        using var preview = await client.GetAsync(previewUri).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode);
        Assert.AreEqual("image/jpeg", preview.Content.Headers.ContentType?.MediaType);
        var cacheControl = preview.Headers.CacheControl?.ToString();
        Assert.IsNotNull(cacheControl);
        StringAssert.Contains(cacheControl, "no-cache", StringComparison.Ordinal);
        Assert.IsFalse(cacheControl.Contains("immutable", StringComparison.Ordinal));
        CollectionAssert.AreEqual(service.Preview, await preview.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        var previewETag = preview.Headers.ETag?.Tag;
        Assert.IsNotNull(previewETag);

        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, previewUri);
        conditionalRequest.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(previewETag));
        using var conditional = await client.SendAsync(conditionalRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotModified, conditional.StatusCode);
        Assert.IsTrue(comparisonOutputBytes.Contains((0, "not_modified")));

        using var previewHeadRequest = new HttpRequestMessage(HttpMethod.Head, previewUri);
        using var previewHead = await client.SendAsync(previewHeadRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, previewHead.StatusCode);
        Assert.AreEqual(service.Preview.LongLength, previewHead.Content.Headers.ContentLength);
        Assert.IsEmpty(await previewHead.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        Assert.AreEqual("inline", preview.Content.Headers.ContentDisposition?.DispositionType);

        using var previewDownload = await client.GetAsync(new Uri(
            $"/api/v1/operations/artifacts/{service.ArtifactId:D}/preview?download=1", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, previewDownload.StatusCode);
        Assert.AreEqual("attachment", previewDownload.Content.Headers.ContentDisposition?.DispositionType);
        Assert.AreEqual($"{service.ArtifactId:D}-display.jpg", previewDownload.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        using var missing = await client.GetAsync(new Uri(
            $"/api/v1/operations/artifacts/{StubArtifactService.MissingId:D}/content", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        using var corrupt = await client.GetAsync(new Uri(
            $"/api/v1/operations/artifacts/{StubArtifactService.CorruptId:D}/content", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Conflict, corrupt.StatusCode);
        var failure = await corrupt.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.IsFalse(failure.Contains(AssemblyHooks.Fixture.StorageRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(failure.Contains("InvalidDataException", StringComparison.Ordinal));
        Assert.IsFalse(failure.Contains("relative_path", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ScientificExportsUseReadAuthorizationExactBindingAndVerifiedDownloadAsync()
    {
        var service = new StubArtifactService { MediaType = "application/x-skymonitor-mono16" };
        var utc = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var profile = new ProfileIdentityDescriptor("test", "1", new string('A', 64));
        service.Descriptor = new ReconstructionDescriptor(
            new("agent", "rig", 42, service.CaptureId),
            new(utc, utc, utc.AddSeconds(1), utc.AddSeconds(2), utc.AddSeconds(3)),
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 5, 5, null, null, null, null),
            new(profile, profile, profile, profile, profile),
            new(2, 2, 4, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian, 16, 16,
                FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, null, null, service.Content.Length),
            new(service.ArtifactId, FrameArtifactRole.Raw, "Test instrument", "native", utc.AddSeconds(3), [],
                RecipeIdentityDescriptor.Create("raw", "1.0.0", "test-v1", JsonSerializer.SerializeToElement(new { })),
                service.MediaType, PayloadChecksum.ComputeSha256(service.Content)));
        using var factory = AssemblyHooks.Fixture.CreateCameraAgentFactory(services =>
        {
            services.RemoveAll<ICameraAgentArtifactService>();
            services.AddSingleton<ICameraAgentArtifactService>(service);
        });
        var (ownerId, nonOwnerId) = await GetUsersAsync(factory.Services).ConfigureAwait(false);
        var uri = ExportUri(service.CaptureId, service.ArtifactId, "fits");
        using var anonymous = factory.CreateClient();
        using var anonymousResponse = await anonymous.GetAsync(uri).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var nonOwner = factory.CreateClient();
        nonOwner.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
        using var forbidden = await nonOwner.GetAsync(uri).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
        foreach (var (format, mediaType, signature) in new[]
        {
            ("fits", "image/fits", "SIMPLE  "),
            ("fits.fz", "application/fits", "SIMPLE  "),
            ("xisf", "application/xisf", "XISF0100")
        })
        {
            var download = ExportUri(service.CaptureId, service.ArtifactId, format);
            using var response = await owner.GetAsync(download).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var sha = PayloadChecksum.ComputeSha256(bytes);
            Assert.AreEqual(signature, Encoding.ASCII.GetString(bytes, 0, 8));
            Assert.AreEqual(bytes.LongLength, response.Content.Headers.ContentLength);
            Assert.AreEqual(sha, response.Headers.GetValues("X-Artifact-SHA256").Single());
            Assert.AreEqual(new EntityTagHeaderValue("\"" + sha + "\""), response.Headers.ETag);
            Assert.AreEqual(mediaType, response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.AreEqual($"capture-42-{service.ArtifactId:N}.{format}",
                response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
            Assert.IsTrue(response.Headers.CacheControl?.NoStore);
            Assert.AreEqual("Cookie", response.Headers.Vary.Single());
            Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

            using var head = new HttpRequestMessage(HttpMethod.Head, download);
            using var header = await owner.SendAsync(head).ConfigureAwait(false);
            Assert.AreEqual(response.Headers.ETag, header.Headers.ETag);
            Assert.AreEqual("Cookie", header.Headers.Vary.Single());
            Assert.IsEmpty(await header.Content.ReadAsByteArrayAsync().ConfigureAwait(false));

            using var range = new HttpRequestMessage(HttpMethod.Get, download);
            range.Headers.Range = new RangeHeaderValue(0, 7);
            using var part = await owner.SendAsync(range).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.PartialContent, part.StatusCode);
            CollectionAssert.AreEqual(bytes[..8], await part.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        }

        await AssertProblemAsync(ExportUri(Guid.NewGuid(), service.ArtifactId, "fits"),
            HttpStatusCode.NotFound, "scientific-export.source-not-found").ConfigureAwait(false);
        await AssertProblemAsync(ExportUri(service.CaptureId, StubArtifactService.MissingId, "fits"),
            HttpStatusCode.NotFound, "scientific-export.source-not-found").ConfigureAwait(false);
        await AssertProblemAsync(ExportUri(service.CaptureId, StubArtifactService.CorruptId, "xisf"),
            HttpStatusCode.Conflict, "scientific-export.corrupt-source").ConfigureAwait(false);
        await AssertProblemAsync(ExportUri(service.CaptureId, service.ArtifactId, "png"),
            HttpStatusCode.BadRequest, "scientific-export.unknown-format").ConfigureAwait(false);
        service.Content[4] ^= 1;
        await AssertProblemAsync(uri, HttpStatusCode.Conflict, "scientific-export.corrupt-source").ConfigureAwait(false);
        service.Descriptor = null;
        await AssertProblemAsync(uri, HttpStatusCode.UnsupportedMediaType, "scientific-export.unsupported-source").ConfigureAwait(false);

        async Task AssertProblemAsync(Uri request, HttpStatusCode status, string reason)
        {
            using var response = await owner.GetAsync(request).ConfigureAwait(false);
            Assert.AreEqual(status, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("Cookie", response.Headers.Vary.Single());
            Assert.IsFalse(response.Headers.Contains("X-Artifact-SHA256"));
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            Assert.AreEqual(reason, problem.RootElement.GetProperty("reasonCode").GetString());

            using var head = new HttpRequestMessage(HttpMethod.Head, request);
            using var headResponse = await owner.SendAsync(head).ConfigureAwait(false);
            Assert.AreEqual(status, headResponse.StatusCode);
            Assert.IsNull(headResponse.Content.Headers.ContentType);
            Assert.IsEmpty(await headResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Assert.AreEqual("Cookie", headResponse.Headers.Vary.Single());
        }
    }

    private static Uri ExportUri(Guid captureId, Guid artifactId, string format) =>
        new($"/api/v1/operations/gallery/{captureId:D}/artifacts/{artifactId:D}/export/{format}", UriKind.Relative);

    private static async Task<(string OwnerId, string NonOwnerId)> GetUsersAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await userManager.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
        Assert.IsNotNull(owner);
        var nonOwner = await userManager.FindByEmailAsync("artifact-non-owner@cameraagent.integration").ConfigureAwait(false);
        if (nonOwner is null)
        {
            nonOwner = new ApplicationUser
            {
                UserName = "artifact-non-owner@cameraagent.integration",
                Email = "artifact-non-owner@cameraagent.integration",
                EmailConfirmed = true
            };
            var created = await userManager.CreateAsync(nonOwner, "ArtifactNonOwner!123").ConfigureAwait(false);
            Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(static error => error.Description)));
        }
        return (owner.Id, nonOwner.Id);
    }

    private static object? GetTagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == key)
            {
                return tag.Value;
            }
        }
        return null;
    }

    private sealed class StubArtifactService : ICameraAgentArtifactService
    {
        internal static readonly Guid MissingId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        internal static readonly Guid CorruptId = Guid.Parse("10000000-0000-0000-0000-000000000002");

        internal Guid ArtifactId { get; } = Guid.Parse("10000000-0000-0000-0000-000000000003");

        internal Guid ExecutionId { get; } = Guid.Parse("10000000-0000-0000-0000-000000000004");

        internal byte[] Content { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

        internal Guid CaptureId { get; } = Guid.Parse("20000000-0000-0000-0000-000000000001");

        internal ReconstructionDescriptor? Descriptor { get; set; }

        internal byte[] Preview { get; } = [0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9];

        internal string ETag => $"\"{PayloadChecksum.ComputeSha256(Content)}\"";
        internal string MediaType { get; set; } = "application/octet-stream";

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The test transfers the memory stream to the returned stream lease.")]
        public async ValueTask<CameraAgentArtifactContentResult> OpenContentAsync(
            Guid artifactId,
            CancellationToken cancellationToken)
        {
            if (artifactId == MissingId)
            {
                return new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.NotFound);
            }
            if (artifactId == CorruptId)
            {
                return new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Conflict);
            }
            if (artifactId != ArtifactId)
            {
                return new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.NotFound);
            }
            await Task.Yield();
            var content = new CameraAgentArtifactContentStream(
                new MemoryStream(Content, writable: false),
                ArtifactId,
                CaptureId,
                Descriptor?.Artifact.Role ?? FrameArtifactRole.Preview,
                MediaType,
                Content.LongLength,
                PayloadChecksum.ComputeSha256(Content),
                $"{ArtifactId:D}.bin",
                Descriptor);
            return new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.Found, content);
        }

        public ValueTask<CameraAgentArtifactContentResult> OpenReplayOutputContentAsync(
            Guid executionId,
            Guid artifactId,
            CancellationToken cancellationToken)
            => executionId == ExecutionId
                ? OpenContentAsync(artifactId, cancellationToken)
                : ValueTask.FromResult(new CameraAgentArtifactContentResult(CameraAgentArtifactReadStatus.NotFound));

        public ValueTask<CameraAgentArtifactPreviewResult> GetPreviewAsync(
            Guid artifactId,
            CancellationToken cancellationToken,
            Guid? displayReference = null)
            => ValueTask.FromResult(artifactId == ArtifactId
                ? new CameraAgentArtifactPreviewResult(
                    CameraAgentArtifactReadStatus.Found,
                    Preview,
                    PayloadChecksum.ComputeSha256(Preview),
                    2,
                    2)
                : new CameraAgentArtifactPreviewResult(CameraAgentArtifactReadStatus.NotFound));

    }
}
