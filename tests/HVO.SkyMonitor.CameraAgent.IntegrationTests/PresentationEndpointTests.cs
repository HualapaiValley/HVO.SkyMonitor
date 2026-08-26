using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PresentationEndpointTests
{
    [TestMethod]
    public async Task SvgSupportsExactGetHeadConditionalCachingAndContentSecurityAsync()
    {
        var service = new StubPresentationService();
        using var factory = AssemblyHooks.Fixture.CreateCameraAgentFactory(services =>
        {
            services.RemoveAll<ICameraAgentLayeredPresentationService>();
            services.AddSingleton<ICameraAgentLayeredPresentationService>(service);
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            IntegrationUserAuthenticationHandler.UserIdHeader,
            await GetOwnerIdAsync(factory.Services).ConfigureAwait(false));
        var uri = new Uri(
            $"/api/v1/operations/gallery/{service.Presentation.CaptureId:D}/presentation.svg",
            UriKind.Relative);

        using var response = await client.GetAsync(uri).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(service.Presentation.Svg.ToArray(),
            await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        Assert.AreEqual($"\"{service.Presentation.SvgChecksumSha256}\"", response.Headers.ETag?.Tag);
        Assert.AreEqual("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        StringAssert.Contains(response.Headers.CacheControl!.ToString(), "private", StringComparison.Ordinal);
        StringAssert.Contains(response.Headers.CacheControl.ToString(), "immutable", StringComparison.Ordinal);
        StringAssert.Contains(response.Headers.GetValues("Content-Security-Policy").Single(), "default-src 'none'", StringComparison.Ordinal);

        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        Assert.IsNotNull(response.Headers.ETag);
        conditionalRequest.Headers.IfNoneMatch.Add(response.Headers.ETag);
        using var conditional = await client.SendAsync(conditionalRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotModified, conditional.StatusCode);
        Assert.IsEmpty(await conditional.Content.ReadAsByteArrayAsync().ConfigureAwait(false));

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, uri);
        using var head = await client.SendAsync(headRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        Assert.AreEqual(service.Presentation.Svg.Length, head.Content.Headers.ContentLength);
        Assert.IsEmpty(await head.Content.ReadAsByteArrayAsync().ConfigureAwait(false));

        using var descriptor = await client.GetAsync(new Uri(
            $"/api/v1/operations/gallery/{service.Presentation.CaptureId:D}/presentation",
            UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, descriptor.StatusCode);
        var descriptorJson = await descriptor.Content.ReadAsStringAsync().ConfigureAwait(false);
        StringAssert.Contains(descriptorJson, service.Presentation.PresentationIdentitySha256, StringComparison.Ordinal);
        Assert.IsFalse(descriptorJson.Contains(Convert.ToBase64String(service.Presentation.Svg.ToArray()), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SvgMapsBoundedFailuresWithoutDisclosingReasonsAsync()
    {
        var service = new StubPresentationService
        {
            Result = new(CameraAgentLayeredPresentationStatus.TooLarge, Reason: "/private/storage/presentation.json")
        };
        using var factory = AssemblyHooks.Fixture.CreateCameraAgentFactory(services =>
        {
            services.RemoveAll<ICameraAgentLayeredPresentationService>();
            services.AddSingleton<ICameraAgentLayeredPresentationService>(service);
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            IntegrationUserAuthenticationHandler.UserIdHeader,
            await GetOwnerIdAsync(factory.Services).ConfigureAwait(false));

        using var response = await client.GetAsync(new Uri(
            $"/api/v1/operations/gallery/{service.Presentation.CaptureId:D}/presentation.svg",
            UriKind.Relative)).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var failure = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.IsFalse(failure.Contains("/private/storage", StringComparison.Ordinal));
        Assert.IsTrue(response.Headers.CacheControl?.Private);
        Assert.IsTrue(response.Headers.CacheControl?.NoCache);
    }

    private static async Task<string> GetOwnerIdAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await userManager.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
        Assert.IsNotNull(owner);
        return owner.Id;
    }

    private sealed class StubPresentationService : ICameraAgentLayeredPresentationService
    {
        internal CameraAgentLayeredPresentation Presentation { get; } = new(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000002"),
            new string('A', 64),
            new string('B', 64),
            new string('C', 64),
            640,
            480,
            [new(new string('D', 64), "scene-annotation", "hvo-layer-0", 10, true, 1_000_000, "renderer-v1", "style-v1")],
            Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 640 480\"><g id=\"hvo-layer-0\"/></svg>"));

        internal CameraAgentLayeredPresentationResult? Result { get; init; }

        public ValueTask<CameraAgentLayeredPresentationResult> GetAsync(
            Guid captureId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                Result ?? new(CameraAgentLayeredPresentationStatus.Found, Presentation));
    }
}
