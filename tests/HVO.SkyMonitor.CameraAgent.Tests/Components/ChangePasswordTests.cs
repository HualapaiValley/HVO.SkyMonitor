using System.Security.Claims;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Account;
using HVO.SkyMonitor.CameraAgent.Components.Account.Pages.Manage;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ChangePasswordTests
{
    private static readonly string[] ExpectedInputNames =
        ["Input.OldPassword", "Input.NewPassword", "Input.ConfirmPassword"];

    private static readonly string[] ExpectedLabels = ["Current password", "New password", "Confirm new password"];

    /// <summary>
    /// A statically rendered form posts each input under the name Blazor derives from its binding expression, and
    /// only names under the form-bound property (Input) reach the model on the post.
    /// </summary>
    [TestMethod]
    public void Inputs_PostUnderTheFormBoundModelName()
    {
        using var fixture = new ComponentFixture();

        var inputs = fixture.Component.FindAll("form input[type=password]");

        CollectionAssert.AreEqual(ExpectedInputNames, inputs.Select(static input => input.GetAttribute("name")).ToArray());
        CollectionAssert.AreEqual(
            ExpectedLabels,
            fixture.Component.FindAll(".ops-field label").Select(static label => label.TextContent).ToArray());
        Assert.AreEqual("current-password", fixture.Component.Find("#Input\\.OldPassword").GetAttribute("autocomplete"));
    }

    [TestMethod]
    public void RejectedCurrentPassword_ShowsTheFailureBannerAndKeepsTheSession()
    {
        using var fixture = new ComponentFixture();
        fixture.UserManager
            .Setup(manager => manager.ChangePasswordAsync(fixture.Owner, "Current-Owner-4!", "Replacement-Owner-4!"))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "Incorrect password." }));
        fixture.Component.Find("#Input\\.OldPassword").Change("Current-Owner-4!");
        fixture.Component.Find("#Input\\.NewPassword").Change("Replacement-Owner-4!");
        fixture.Component.Find("#Input\\.ConfirmPassword").Change("Replacement-Owner-4!");

        fixture.Component.Find("form").Submit();

        fixture.Component.WaitForAssertion(() => Assert.AreEqual(
            "Error: Incorrect password.",
            fixture.Component.Find(".ops-note-banner.failure[role=alert]").TextContent.Trim()));
        fixture.UserManager.Verify(
            manager => manager.ChangePasswordAsync(fixture.Owner, "Current-Owner-4!", "Replacement-Owner-4!"),
            Times.Once);
        fixture.SignInManager.Verify(
            manager => manager.RefreshSignInAsync(It.IsAny<ApplicationUser>()),
            Times.Never);
    }

    private sealed class ComponentFixture : IDisposable
    {
        private readonly BunitContext _context = new();

        internal ComponentFixture()
        {
            Owner = new ApplicationUser { Id = "owner", IsSiteOwner = true };
            UserManager = new Mock<UserManager<ApplicationUser>>(
                Mock.Of<IUserStore<ApplicationUser>>(),
                Options.Create(new IdentityOptions()),
                new PasswordHasher<ApplicationUser>(),
                Array.Empty<IUserValidator<ApplicationUser>>(),
                Array.Empty<IPasswordValidator<ApplicationUser>>(),
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                Mock.Of<IServiceProvider>(),
                NullLogger<UserManager<ApplicationUser>>.Instance);
            UserManager.Setup(manager => manager.GetUserAsync(It.IsAny<ClaimsPrincipal>())).ReturnsAsync(Owner);
            UserManager.Setup(manager => manager.HasPasswordAsync(Owner)).ReturnsAsync(true);
            SignInManager = new Mock<SignInManager<ApplicationUser>>(
                UserManager.Object,
                Mock.Of<IHttpContextAccessor>(),
                Mock.Of<IUserClaimsPrincipalFactory<ApplicationUser>>(),
                Options.Create(new IdentityOptions()),
                NullLogger<SignInManager<ApplicationUser>>.Instance,
                Mock.Of<IAuthenticationSchemeProvider>(),
                Mock.Of<IUserConfirmation<ApplicationUser>>());
            _context.Services.AddSingleton(UserManager.Object);
            _context.Services.AddSingleton(SignInManager.Object);
            _context.Services.AddSingleton<IdentityRedirectManager>(provider =>
                new IdentityRedirectManager(provider.GetRequiredService<NavigationManager>()));
            _context.Services.AddSingleton(NullLogger<ChangePassword>.Instance);
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Owner.Id)],
                    IdentityConstants.ApplicationScheme))
            };
            Component = _context.Render<ChangePassword>(parameters => parameters.AddCascadingValue(httpContext));
        }

        internal ApplicationUser Owner { get; }

        internal Mock<UserManager<ApplicationUser>> UserManager { get; }

        internal Mock<SignInManager<ApplicationUser>> SignInManager { get; }

        internal IRenderedComponent<ChangePassword> Component { get; }

        public void Dispose() => _context.Dispose();
    }
}
