using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Authorization;

[TestClass]
[TestCategory("Unit")]
public sealed class OwnerBootstrapGateMiddlewareTests
{
    [TestMethod]
    public async Task PendingOwner_ApiOperationIsDeniedWithStableReason()
    {
        var nextCalled = false;
        var middleware = new OwnerBootstrapGateMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<OwnerBootstrapGateMiddleware>.Instance);
        var userManager = CreateUserManager(passwordChangeRequired: true);
        var context = CreateContext("/api/v1/operations/summary");

        await middleware.InvokeAsync(context, userManager.Object, Options.Create(new IdentityOptions()))
            .ConfigureAwait(false);

        Assert.IsFalse(nextCalled);
        Assert.AreEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.AreEqual(OwnerBootstrapStates.PasswordChangeRequired,
            context.Response.Headers["X-HVO-Authorization-Reason"].ToString());
    }

    [TestMethod]
    public async Task PendingOwner_StatusAndReplacementPathsRemainAvailable()
    {
        foreach (var path in new[]
        {
            OwnerBootstrapGateMiddleware.StatusPath,
            OwnerBootstrapGateMiddleware.ReplacementPath,
            "/Account/Logout",
            "/health",
            "/HVO.SkyMonitor.CameraAgent.styles.css"
        })
        {
            var nextCalled = false;
            var middleware = new OwnerBootstrapGateMiddleware(
                _ =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                },
                NullLogger<OwnerBootstrapGateMiddleware>.Instance);
            var context = CreateContext(path);

            await middleware.InvokeAsync(
                context,
                CreateUserManager(passwordChangeRequired: true).Object,
                Options.Create(new IdentityOptions())).ConfigureAwait(false);

            Assert.IsTrue(nextCalled, path);
        }
    }

    [TestMethod]
    public async Task ReadyOwner_OrdinaryOperationContinues()
    {
        var nextCalled = false;
        var middleware = new OwnerBootstrapGateMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<OwnerBootstrapGateMiddleware>.Instance);

        await middleware.InvokeAsync(
            CreateContext("/api/v1/operations/summary"),
            CreateUserManager(passwordChangeRequired: false).Object,
            Options.Create(new IdentityOptions())).ConfigureAwait(false);

        Assert.IsTrue(nextCalled);
    }

    [TestMethod]
    public async Task PendingOwner_ApiPathWithStaticExtensionIsNotBypassed()
    {
        var nextCalled = false;
        var middleware = new OwnerBootstrapGateMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<OwnerBootstrapGateMiddleware>.Instance);
        var context = CreateContext("/api/v1/operations/export.js");

        await middleware.InvokeAsync(
            context,
            CreateUserManager(passwordChangeRequired: true).Object,
            Options.Create(new IdentityOptions())).ConfigureAwait(false);

        Assert.IsFalse(nextCalled);
        Assert.AreEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "owner")],
            IdentityConstants.ApplicationScheme));
        return context;
    }

    private static Mock<UserManager<ApplicationUser>> CreateUserManager(bool passwordChangeRequired)
    {
        var manager = new Mock<UserManager<ApplicationUser>>(
            Mock.Of<IUserStore<ApplicationUser>>(),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            Mock.Of<IServiceProvider>(),
            NullLogger<UserManager<ApplicationUser>>.Instance);
        manager.Setup(value => value.GetUserAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(new ApplicationUser
            {
                Id = "owner",
                IsSiteOwner = true,
                PasswordChangeRequired = passwordChangeRequired
            });
        return manager;
    }
}
