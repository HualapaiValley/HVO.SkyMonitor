using System.Security.Claims;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Account;
using HVO.SkyMonitor.CameraAgent.Components.Account.Pages;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ReplaceTemporaryPasswordTests
{
    private static readonly string[] ExpectedInputNames =
        ["Input.CurrentPassword", "Input.NewPassword", "Input.ConfirmPassword"];

    [TestMethod]
    public void PendingOwner_RendersCurrentNewAndConfirmationFields()
    {
        using var fixture = new ComponentFixture();
        var component = fixture.Component;

        Assert.HasCount(3, component.FindAll("input[type=password]"));
        Assert.AreEqual("current-password", component.Find("#Input\\.CurrentPassword").GetAttribute("autocomplete"));
        CollectionAssert.AreEqual(
            ExpectedInputNames,
            component.FindAll("input[type=password]")
                .Select(static input => input.GetAttribute("name"))
                .ToArray());
        Assert.AreEqual(2, component.FindAll("input[autocomplete=new-password]").Count);
        StringAssert.Contains(component.Markup, "Replace password", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ConfirmationMismatch_DoesNotSubmitPasswordReplacement()
    {
        using var fixture = new ComponentFixture();
        fixture.Component.Find("#Input\\.CurrentPassword").Change("TemporaryOwner!418");
        fixture.Component.Find("#Input\\.NewPassword").Change("ReplacementOwner!418");
        fixture.Component.Find("#Input\\.ConfirmPassword").Change("DifferentReplacement!418");

        fixture.Component.Find("form").Submit();

        fixture.Component.WaitForAssertion(() => StringAssert.Contains(
            fixture.Component.Markup,
            "The new password and confirmation password do not match.",
            StringComparison.Ordinal));
        Assert.IsTrue(fixture.Owner.PasswordChangeRequired);
        fixture.SignInManager.Verify(
            manager => manager.RefreshSignInAsync(It.IsAny<ApplicationUser>()),
            Times.Never);
    }

    private static Mock<UserManager<ApplicationUser>> CreateUserManager(ApplicationUser user)
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
        manager.Setup(value => value.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(user);
        return manager;
    }

    private sealed class ComponentFixture : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly ApplicationDbContext _dbContext;
        private readonly BunitContext _context = new();

        internal ComponentFixture()
        {
            _connection.Open();
            _dbContext = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
            Owner = new ApplicationUser
            {
                Id = "owner",
                IsSiteOwner = true,
                PasswordChangeRequired = true
            };
            var userManager = CreateUserManager(Owner);
            SignInManager = new Mock<SignInManager<ApplicationUser>>(
                userManager.Object,
                Mock.Of<IHttpContextAccessor>(),
                Mock.Of<IUserClaimsPrincipalFactory<ApplicationUser>>(),
                Options.Create(new IdentityOptions()),
                NullLogger<SignInManager<ApplicationUser>>.Instance,
                Mock.Of<IAuthenticationSchemeProvider>(),
                Mock.Of<IUserConfirmation<ApplicationUser>>());
            _context.Services.AddSingleton(userManager.Object);
            _context.Services.AddSingleton(SignInManager.Object);
            _context.Services.AddSingleton(_dbContext);
            _context.Services.AddSingleton(new OwnerPasswordReplacementService(_dbContext, userManager.Object));
            _context.Services.AddSingleton<IdentityRedirectManager>(provider =>
                new IdentityRedirectManager(provider.GetRequiredService<NavigationManager>()));
            _context.Services.AddSingleton(NullLogger<ReplaceTemporaryPassword>.Instance);
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Owner.Id)],
                    IdentityConstants.ApplicationScheme))
            };
            Component = _context.Render<ReplaceTemporaryPassword>(parameters =>
                parameters.AddCascadingValue(httpContext));
        }

        internal ApplicationUser Owner { get; }
        internal Mock<SignInManager<ApplicationUser>> SignInManager { get; }
        internal IRenderedComponent<ReplaceTemporaryPassword> Component { get; }

        public void Dispose()
        {
            _context.Dispose();
            _dbContext.Dispose();
            _connection.Dispose();
        }
    }
}
