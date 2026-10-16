using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Authorization;

[TestClass]
[TestCategory("Unit")]
public sealed class OwnerBootstrapGateMiddlewareTests
{
    private const string SecurityStamp = "current-security-stamp";

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
            context.Response.Headers[OwnerBootstrapGateMiddleware.AuthorizationReasonHeader].ToString());
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

    [TestMethod]
    public async Task OwnerWithoutSecurityStamp_IsSignedOutAndDenied()
    {
        var nextCalled = false;
        var middleware = new OwnerBootstrapGateMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<OwnerBootstrapGateMiddleware>.Instance);
        var context = CreateContext("/api/v1/operations/summary", includeSecurityStamp: false);

        await middleware.InvokeAsync(
            context,
            CreateUserManager(passwordChangeRequired: true).Object,
            Options.Create(new IdentityOptions())).ConfigureAwait(false);

        Assert.IsFalse(nextCalled);
        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task DefaultSeededOwner_CannotReachOrdinaryPagesOrApisUntilThePasswordIsReplaced()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlite(connection);
            options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        });
        services.AddIdentityCore<ApplicationUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddCameraAgentPasswordPolicy()
            .AddDefaultTokenProviders();
        services.AddSingleton(Options.Create(new LocalIdentityOptions
        {
            AdminEmail = "owner@cameraagent.test",
            AdminPassword = "skymonitor"
        }));
        services.AddSingleton<CameraAgentIdentitySeeder>();
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<CameraAgentIdentitySeeder>().InitializeAsync(CancellationToken.None)
            .ConfigureAwait(false);
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var owner = await CameraAgentIdentityPolicy.FindSignInUserAsync(users, "admin").ConfigureAwait(false);
        Assert.IsNotNull(owner);
        Assert.IsTrue(await users.CheckPasswordAsync(owner, "skymonitor").ConfigureAwait(false));
        var identityOptions = Options.Create(new IdentityOptions());

        foreach (var path in new[] { "/", "/operations", "/Account/Manage/ChangePassword", "/api/v1/operations/summary" })
        {
            var nextCalled = false;
            var middleware = new OwnerBootstrapGateMiddleware(
                _ =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                },
                NullLogger<OwnerBootstrapGateMiddleware>.Instance);
            var context = CreateContext(path, owner.Id, owner.SecurityStamp!);

            await middleware.InvokeAsync(context, users, identityOptions).ConfigureAwait(false);

            Assert.IsFalse(nextCalled, path);
            if (path.StartsWith("/api", StringComparison.Ordinal))
            {
                Assert.AreEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode, path);
            }
            else
            {
                Assert.AreEqual(StatusCodes.Status302Found, context.Response.StatusCode, path);
                Assert.AreEqual(OwnerBootstrapGateMiddleware.ReplacementPath, context.Response.Headers.Location.ToString(), path);
            }
        }

        var replacement = new OwnerPasswordReplacementService(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            users);
        Assert.IsTrue((await replacement.ReplaceAsync(owner, "skymonitor", "abcdefgh").ConfigureAwait(false)).Succeeded);
        var readyNextCalled = false;
        var readyMiddleware = new OwnerBootstrapGateMiddleware(
            _ =>
            {
                readyNextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<OwnerBootstrapGateMiddleware>.Instance);

        await readyMiddleware.InvokeAsync(
            CreateContext("/api/v1/operations/summary", owner.Id, owner.SecurityStamp!),
            users,
            identityOptions).ConfigureAwait(false);

        Assert.IsTrue(readyNextCalled);
    }

    private static DefaultHttpContext CreateContext(string path, bool includeSecurityStamp = true)
        => CreateContext(path, "owner", includeSecurityStamp ? SecurityStamp : null);

    private static DefaultHttpContext CreateContext(string path, string ownerId, string? securityStamp)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, ownerId) };
        if (securityStamp is not null)
        {
            claims.Add(new Claim(
                new IdentityOptions().ClaimsIdentity.SecurityStampClaimType,
                securityStamp));
        }
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            IdentityConstants.ApplicationScheme));
        var authentication = new Mock<IAuthenticationService>();
        authentication.Setup(service => service.SignOutAsync(
                context,
                IdentityConstants.ApplicationScheme,
                It.IsAny<AuthenticationProperties?>()))
            .Returns(Task.CompletedTask);
        var services = new Mock<IServiceProvider>();
        services.Setup(provider => provider.GetService(typeof(IAuthenticationService)))
            .Returns(authentication.Object);
        context.RequestServices = services.Object;
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
                PasswordChangeRequired = passwordChangeRequired,
                SecurityStamp = SecurityStamp
            });
        manager.Setup(value => value.GetSecurityStampAsync(It.IsAny<ApplicationUser>()))
            .ReturnsAsync(SecurityStamp);
        return manager;
    }
}
