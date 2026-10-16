using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Account;
using HVO.SkyMonitor.CameraAgent.Components.Account.Pages;
using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class LoginTests
{
    private const string OwnerEmail = "owner@cameraagent.test";

    [TestMethod]
    public void Login_RendersOneUserNameOrEmailFieldUnderItsStableFormName()
    {
        using var fixture = new LoginFixture();

        var field = fixture.Component.Find("#Input\\.Email");

        Assert.AreEqual("Username or email", fixture.Component.Find("label[for='Input.Email']").TextContent);
        Assert.AreEqual("Input.Email", field.GetAttribute("name"));
        Assert.AreNotEqual("email", field.GetAttribute("type"));
        Assert.HasCount(1, fixture.Component.FindAll("input[type=password]"));
    }

    [TestMethod]
    public void Login_UserName_SignsInTheNamedAccountWithLockoutAndRequiresReplacement()
    {
        using var fixture = new LoginFixture();
        fixture.SignInManager
            .Setup(manager => manager.PasswordSignInAsync(fixture.Owner, "skymonitor", false, true))
            .ReturnsAsync(SignInResult.Success);

        fixture.Submit("admin", "skymonitor");

        fixture.SignInManager.Verify(
            manager => manager.PasswordSignInAsync(fixture.Owner, "skymonitor", false, true),
            Times.Once);
        fixture.UserManager.Verify(manager => manager.FindByEmailAsync(It.IsAny<string>()), Times.Never);
        StringAssert.EndsWith(fixture.Navigation.Uri, "/Account/ReplaceTemporaryPassword", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Login_Email_ResolvesTheAccountByEmail()
    {
        using var fixture = new LoginFixture();
        fixture.Owner.PasswordChangeRequired = false;
        fixture.SignInManager
            .Setup(manager => manager.PasswordSignInAsync(fixture.Owner, "replacement", false, true))
            .ReturnsAsync(SignInResult.Success);

        fixture.Submit(OwnerEmail, "replacement");

        fixture.UserManager.Verify(manager => manager.FindByEmailAsync(OwnerEmail), Times.Once);
        fixture.UserManager.Verify(manager => manager.FindByNameAsync(It.IsAny<string>()), Times.Never);
        fixture.SignInManager.Verify(
            manager => manager.PasswordSignInAsync(fixture.Owner, "replacement", false, true),
            Times.Once);
        Assert.IsFalse(fixture.Navigation.Uri.EndsWith("/Account/ReplaceTemporaryPassword", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Login_LockedOutAccount_ShowsTheLockoutMessage()
    {
        using var fixture = new LoginFixture();
        fixture.SignInManager
            .Setup(manager => manager.PasswordSignInAsync(fixture.Owner, It.IsAny<string>(), false, true))
            .ReturnsAsync(SignInResult.LockedOut);

        fixture.Submit("admin", "wrong-password");

        fixture.Component.WaitForAssertion(() => StringAssert.Contains(
            fixture.Component.Markup,
            "Error: This account is locked after 5 failed sign-in attempts. Try again in 5 minutes.",
            StringComparison.Ordinal));
        Assert.AreEqual("http://localhost/Account/Login", fixture.Navigation.Uri);
    }

    [TestMethod]
    public void Login_WrongPassword_ShowsTheGenericError()
    {
        using var fixture = new LoginFixture();
        fixture.SignInManager
            .Setup(manager => manager.PasswordSignInAsync(fixture.Owner, It.IsAny<string>(), false, true))
            .ReturnsAsync(SignInResult.Failed);

        fixture.Submit("admin", "wrong-password");

        fixture.Component.WaitForAssertion(() => StringAssert.Contains(
            fixture.Component.Markup, Login.InvalidSignInMessage, StringComparison.Ordinal));
        Assert.IsFalse(fixture.Component.Markup.Contains("locked", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Login_UnknownAccount_ShowsTheGenericErrorWithoutASignInAttempt()
    {
        using var fixture = new LoginFixture();

        fixture.Submit("someone-else", "skymonitor");

        fixture.Component.WaitForAssertion(() => StringAssert.Contains(
            fixture.Component.Markup, Login.InvalidSignInMessage, StringComparison.Ordinal));
        fixture.SignInManager.Verify(
            manager => manager.PasswordSignInAsync(
                It.IsAny<ApplicationUser>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Never);
    }

    private sealed class LoginFixture : IDisposable
    {
        private readonly BunitContext _context = new();

        internal LoginFixture()
        {
            Owner = new ApplicationUser
            {
                Id = "owner",
                UserName = "admin",
                Email = OwnerEmail,
                IsSiteOwner = true,
                PasswordChangeRequired = true
            };
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
            UserManager.Setup(manager => manager.FindByNameAsync("admin")).ReturnsAsync(Owner);
            UserManager.Setup(manager => manager.FindByEmailAsync(OwnerEmail)).ReturnsAsync(Owner);
            SignInManager = new Mock<SignInManager<ApplicationUser>>(
                UserManager.Object,
                Mock.Of<IHttpContextAccessor>(),
                Mock.Of<IUserClaimsPrincipalFactory<ApplicationUser>>(),
                Options.Create(new IdentityOptions()),
                NullLogger<SignInManager<ApplicationUser>>.Instance,
                Mock.Of<IAuthenticationSchemeProvider>(),
                Mock.Of<IUserConfirmation<ApplicationUser>>());
            _context.Services.AddSingleton(SignInManager.Object);
            _context.Services.AddSingleton<IdentityRedirectManager>(provider =>
                new IdentityRedirectManager(provider.GetRequiredService<NavigationManager>()));
            _context.Services.AddSingleton(NullLogger<Login>.Instance);
            Navigation = _context.Services.GetRequiredService<NavigationManager>();
            Navigation.NavigateTo("/Account/Login");
            Component = _context.Render<Login>(parameters => parameters.AddCascadingValue(new DefaultHttpContext()));
        }

        internal ApplicationUser Owner { get; }
        internal Mock<UserManager<ApplicationUser>> UserManager { get; }
        internal Mock<SignInManager<ApplicationUser>> SignInManager { get; }
        internal NavigationManager Navigation { get; }
        internal IRenderedComponent<Login> Component { get; }

        internal void Submit(string userNameOrEmail, string password)
        {
            Component.Find("#Input\\.Email").Change(userNameOrEmail);
            Component.Find("#Input\\.Password").Change(password);
            Component.Find("form").Submit();
        }

        public void Dispose() => _context.Dispose();
    }
}
