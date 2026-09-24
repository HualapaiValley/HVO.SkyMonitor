using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

public sealed partial class CameraAgentArtifactServiceTests
{
    [TestMethod]
    public async Task PreviewHttpQueryAuthorizationAndPolicyEtagsUseRealServiceAsync()
    {
        using var fixture = await ArtifactFixture.CreateAsync().ConfigureAwait(false);
        var (payload, _) = CreateCombinedFixtureFrame();
        var raw = await fixture.AddRawAsync(payload, 100, 100).ConfigureAwait(false);
        var combined = await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Combined, width: 100, height: 100,
            pixelFormat: CameraPixelFormat.Mono16, payload: payload).ConfigureAwait(false);
        await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.Calibrated, width: 100, height: 100,
            pixelFormat: CameraPixelFormat.Mono16, payload: payload).ConfigureAwait(false);
        await fixture.AddOutputAsync(raw.Manifest, FrameArtifactRole.AnnotatedPreview).ConfigureAwait(false);
        var first = await AddDisplayReferenceAsync(fixture, raw, combined, new(0.5, 0.9997, 8), variant: "first").ConfigureAwait(false);
        var second = await AddDisplayReferenceAsync(fixture, raw, raw, new(0.5, 0.9997, 8), variant: "second").ConfigureAwait(false);

        // Only Identity persistence is mocked; actual routing, authentication/owner policy, query parsing,
        // artifact validation, encoding, ETags, GET and HEAD run over a disposable loopback HTTP listener.
        var manager = new Mock<UserManager<ApplicationUser>>(
            Mock.Of<IUserStore<ApplicationUser>>(), Options.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
            Array.Empty<IUserValidator<ApplicationUser>>(), Array.Empty<IPasswordValidator<ApplicationUser>>(),
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), Mock.Of<IServiceProvider>(),
            NullLogger<UserManager<ApplicationUser>>.Instance);
        manager.SetupGet(value => value.SupportsUserSecurityStamp).Returns(true);
        manager.Setup(value => value.FindByIdAsync(It.IsAny<string>())).ReturnsAsync((string id) => new ApplicationUser
        {
            Id = id,
            IsSiteOwner = id == "owner",
            NormalizedEmail = "OWNER@DISPLAY.TEST",
            SecurityStamp = "test-stamp"
        });
        manager.Setup(value => value.GetSecurityStampAsync(It.IsAny<ApplicationUser>())).ReturnsAsync("test-stamp");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(manager.Object);
        builder.Services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        builder.Services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = "owner@display.test" }));
        builder.Services.AddCameraAgentAuthorization();
        builder.Services.AddAuthentication("display-test").AddScheme<AuthenticationSchemeOptions, DisplayTestAuthenticationHandler>("display-test", _ => { });
        builder.Services.AddSingleton<ICameraAgentArtifactService>(fixture.Service);
        builder.Services.AddSingleton<CameraAgentOperatorTelemetry>();
        var app = builder.Build();
        await using var appLease = app.ConfigureAwait(false);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCameraAgentArtifactEndpoints();
        await app.StartAsync().ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var path = $"/api/v1/operations/artifacts/{raw.ArtifactId:D}/preview";
        var query = $"{path}?displayReference={first.ArtifactId:D}";
        using (var denied = await client.GetAsync(new Uri(query, UriKind.Relative)).ConfigureAwait(false))
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Add("X-Display-Test-User", "reader");
        using (var denied = await client.GetAsync(new Uri(query, UriKind.Relative)).ConfigureAwait(false))
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Display-Test-User");
        client.DefaultRequestHeaders.Add("X-Display-Test-User", "owner");

        using var baseline = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        using var explicitPolicy = await client.GetAsync(new Uri(query, UriKind.Relative)).ConfigureAwait(false);
        using var otherReference = await client.GetAsync(new Uri($"{path}?displayReference={second.ArtifactId:D}", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, explicitPolicy.StatusCode);
        Assert.AreNotEqual(baseline.Headers.ETag, explicitPolicy.Headers.ETag);
        Assert.AreNotEqual(explicitPolicy.Headers.ETag, otherReference.Headers.ETag,
            "Even identical settings/pixels retain the selected reference identity in the ETag.");
        CollectionAssert.AreEqual(await explicitPolicy.Content.ReadAsByteArrayAsync().ConfigureAwait(false),
            await otherReference.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        Assert.IsTrue(explicitPolicy.Headers.Contains("X-Display-Policy"));
        Assert.IsTrue(explicitPolicy.Headers.CacheControl!.Private);
        Assert.IsTrue(explicitPolicy.Headers.CacheControl.NoCache);
        using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, query);
        unchangedRequest.Headers.IfNoneMatch.Add(explicitPolicy.Headers.ETag!);
        using var unchanged = await client.SendAsync(unchangedRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
        using var staleRequest = new HttpRequestMessage(HttpMethod.Get, query);
        staleRequest.Headers.IfNoneMatch.Add(baseline.Headers.ETag!);
        using var changed = await client.SendAsync(staleRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, query);
        using var head = await client.SendAsync(headRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        Assert.AreEqual(explicitPolicy.Headers.ETag, head.Headers.ETag);
        Assert.IsEmpty(await head.Content.ReadAsByteArrayAsync().ConfigureAwait(false));

        var current = new CameraAgentCurrentImagePresentationService(fixture.Gallery,
            new CameraAgentCapturePresentationProjector(Options.Create(new CameraAgentHostOptions())), fixture.Service,
            new ComparisonRuntime(), new NoStructuredLayers(), TimeProvider.System);
        var stages = (await current.GetAsync(CancellationToken.None).ConfigureAwait(false)).Stages;
        Assert.AreEqual(4, stages.Count);
        foreach (var slot in stages)
        {
            Assert.AreEqual(CameraAgentPresentationSlotAvailability.Available, slot.Availability);
            using var shown = await client.GetAsync(slot.PreviewUrl).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, shown.StatusCode);
            var validated = await fixture.Service.GetPreviewAsync(slot.DisplayArtifactId!.Value, CancellationToken.None, slot.DisplayReferenceId).ConfigureAwait(false);
            CollectionAssert.AreEqual(validated.Content.ToArray(), await shown.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            Assert.AreEqual(validated.Operation.ToString(), shown.Headers.GetValues("X-Display-Operation").Single());
            if (slot.DisplayReferenceId is not null)
            {
                StringAssert.Contains(slot.PreviewUrl!.OriginalString, $"displayReference={first.ArtifactId:D}", StringComparison.Ordinal);
                Assert.AreEqual(validated.DisplayPolicyIdentity, shown.Headers.GetValues("X-Display-Policy").Single());
                Assert.AreEqual($"\"{validated.ChecksumSha256}-{validated.DisplayPolicyIdentity}\"", shown.Headers.ETag!.Tag);
            }
            else Assert.IsFalse(shown.Headers.Contains("X-Display-Policy"));
        }
        var meanSlot = stages.Single(static slot => slot.Stage == CameraAgentPresentationStage.Combined);
        using var mean = await client.GetAsync(meanSlot.PreviewUrl).ConfigureAwait(false);
        var layeredUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(meanSlot.PreviewUrl!.OriginalString, "attempt", "0");
        using var layered = await client.GetAsync(new Uri(layeredUrl, UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(mean.Headers.ETag, layered.Headers.ETag);
        CollectionAssert.AreEqual(await mean.Content.ReadAsByteArrayAsync().ConfigureAwait(false), await layered.Content.ReadAsByteArrayAsync().ConfigureAwait(false));

        foreach (var invalidQuery in new[] { "", "not-a-guid", Guid.Empty.ToString("D"), $"{first.ArtifactId:D}&displayReference={second.ArtifactId:D}" })
        {
            using var invalid = await client.GetAsync(new Uri($"{path}?displayReference={invalidQuery}", UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            StringAssert.Contains(await invalid.Content.ReadAsStringAsync().ConfigureAwait(false), "Artifact preview request is invalid.", StringComparison.Ordinal);
        }
        using var unknown = await client.GetAsync(new Uri($"{path}?displayReference={Guid.NewGuid():D}", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Conflict, unknown.StatusCode);
        var unknownBody = await unknown.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.IsFalse(unknownBody.Contains(fixture.Root, StringComparison.Ordinal));
        File.Delete(first.PayloadPath);
        using var deletedRequest = new HttpRequestMessage(HttpMethod.Get, query);
        deletedRequest.Headers.IfNoneMatch.Add(explicitPolicy.Headers.ETag!);
        using var deleted = await client.SendAsync(deletedRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Conflict, deleted.StatusCode, "A stale ETag must not bypass reference validation.");
        await app.StopAsync().ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by authentication dependency injection.")]
    private sealed class DisplayTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var id = Request.Headers["X-Display-Test-User"].ToString();
            if (id.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, id),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType),
                new Claim(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, "test-stamp")
            ], IdentityConstants.ApplicationScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
