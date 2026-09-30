using System.Net;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// The account pages render statically, so a posted form reaches its page only under the names the inputs render.
/// These posts use those rendered names, which is what a browser sends.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class AccountManageFormPostTests
{
    private const string ChangePasswordPath = "/Account/Manage/ChangePassword";

    private static readonly string[] ChangePasswordFields = ["Input.OldPassword", "Input.NewPassword", "Input.ConfirmPassword"];

    [TestMethod]
    public async Task ChangePassword_PostsTheRenderedFieldsToTheSubmitHandlerAsync()
    {
        using var client = AssemblyHooks.Fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        await SignInAsync(client, TestUsers.Viewer.Email, TestUsers.Viewer.Password, ChangePasswordPath).ConfigureAwait(false);

        using var page = await client.GetAsync(new Uri(ChangePasswordPath, UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync().ConfigureAwait(false);
        var fields = RenderedInputNames(html);
        CollectionAssert.IsSubsetOf(ChangePasswordFields, fields);

        // A wrong current password leaves the account unchanged. It reaches UserManager only when all three fields
        // bind; unbound fields fail [Required] validation before the handler runs.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(html),
            ["Input.OldPassword"] = "Not-The-Current-Password-1!",
            ["Input.NewPassword"] = "Replacement-Password-1!",
            ["Input.ConfirmPassword"] = "Replacement-Password-1!",
            ["_handler"] = "change-password"
        });
        using var post = await client.PostAsync(new Uri(ChangePasswordPath, UriKind.Relative), form).ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, post.StatusCode);
        var result = await post.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.Contains("Error: Incorrect password.", result);
        Assert.DoesNotContain("field is required", result);
    }

    private static async Task SignInAsync(HttpClient client, string email, string password, string returnUrl)
    {
        var loginUri = new Uri($"/Account/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}", UriKind.Relative);
        using var loginPage = await client.GetAsync(loginUri).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, loginPage.StatusCode);
        var html = await loginPage.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(html),
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.RememberMe"] = "false",
            ["_handler"] = "login"
        });
        using var login = await client.PostAsync(loginUri, loginForm).ConfigureAwait(false);
        Assert.IsTrue((int)login.StatusCode is >= 300 and < 400, $"Sign-in returned {(int)login.StatusCode}.");
        var location = login.Headers.Location ?? throw new AssertFailedException("Sign-in did not redirect.");
        Assert.AreEqual(returnUrl, (location.IsAbsoluteUri ? location : new Uri(client.BaseAddress!, location)).PathAndQuery);
    }

    private static string AntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        Assert.IsTrue(match.Success, "The page omitted its antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string[] RenderedInputNames(string html)
        => Regex.Matches(html, "<input[^>]*\\sname=\"([^\"]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .ToArray();
}
