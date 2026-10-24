using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class CaptureSatelliteIntegrationTests
{
    private static async Task AssertAuthenticatedReadAsync(ServiceProvider retained, CameraAgentLayeredPresentation presentation)
    {
        var manager = new Mock<UserManager<ApplicationUser>>(
            Mock.Of<IUserStore<ApplicationUser>>(), Options.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
            Array.Empty<IUserValidator<ApplicationUser>>(), Array.Empty<IPasswordValidator<ApplicationUser>>(),
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), Mock.Of<IServiceProvider>(),
            NullLogger<UserManager<ApplicationUser>>.Instance);
        manager.SetupGet(value => value.SupportsUserSecurityStamp).Returns(true);
        manager.Setup(value => value.FindByIdAsync(It.IsAny<string>())).ReturnsAsync((string id) => new ApplicationUser
        { Id = id, IsSiteOwner = id == "owner", NormalizedEmail = "OWNER@SATELLITE.TEST", SecurityStamp = "test-stamp" });
        manager.Setup(value => value.GetSecurityStampAsync(It.IsAny<ApplicationUser>())).ReturnsAsync("test-stamp");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton(manager.Object);
        builder.Services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        builder.Services.AddSingleton(Options.Create(new LocalIdentityOptions { AdminEmail = "owner@satellite.test" }));
        builder.Services.AddCameraAgentAuthorization();
        builder.Services.AddAuthentication("satellite-test").AddScheme<AuthenticationSchemeOptions, SatelliteAuthenticationHandler>("satellite-test", _ => { });
        builder.Services.AddSingleton(retained.GetRequiredService<ICameraAgentLayeredPresentationService>());
        builder.Services.AddSingleton(retained.GetRequiredService<ICameraAgentGallery>());
        builder.Services.AddSingleton(retained.GetRequiredService<ICameraAgentArchive>());
        builder.Services.AddSingleton(retained.GetRequiredService<ICameraAgentCurrentImagePresentationService>());
        builder.Services.AddSingleton(retained.GetRequiredService<ICameraAgentCapturePresentationProjector>());
        builder.Services.AddSingleton(retained.GetRequiredService<ICameraAgentPresentationMaterializer>());
        var app = builder.Build();
        await using var appLease = app.ConfigureAwait(false);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCameraAgentGalleryEndpoints();
        await app.StartAsync().ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var path = $"/api/v1/operations/gallery/{presentation.CaptureId:D}/presentation.svg";
        using (var denied = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false))
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Add("X-Satellite-Test-User", "reader");
        using (var denied = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false))
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Satellite-Test-User");
        client.DefaultRequestHeaders.Add("X-Satellite-Test-User", "owner");
        using var read = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        CollectionAssert.AreEqual(presentation.Svg.ToArray(), await read.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by authentication dependency injection.")]
    private sealed class SatelliteAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var id = Request.Headers["X-Satellite-Test-User"].ToString();
            if (id.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, id),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType),
                new Claim(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, "test-stamp")
            ], IdentityConstants.ApplicationScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
