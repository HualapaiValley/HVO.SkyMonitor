using System.Net;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    private const string ApiKeysPath = "/Account/Manage/ApiKeys";

    private static readonly string[] ChangePasswordFields = ["Input.OldPassword", "Input.NewPassword", "Input.ConfirmPassword"];

    private static readonly string[] CreateApiKeyInputs = ["Input.DisplayName", "Input.ExpiresOnUtc"];

    private static readonly string[] CreateApiKeySelects = ["Input.AccessLevel", "Input.ObservatoryId"];

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

    [TestMethod]
    public async Task ApiKeys_CreatesDeactivatesAndDeletesAKeyThroughThePageAsync()
    {
        var observatoryId = await AddViewerObservatoryMembershipAsync().ConfigureAwait(false);
        var displayName = $"Form post key {Guid.NewGuid():N}";
        using var client = AssemblyHooks.Fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        await SignInAsync(client, TestUsers.Viewer.Email, TestUsers.Viewer.Password, ApiKeysPath).ConfigureAwait(false);

        var html = await GetPageAsync(client, ApiKeysPath).ConfigureAwait(false);
        CollectionAssert.IsSubsetOf(CreateApiKeyInputs, RenderedNames(html, "input"));
        CollectionAssert.IsSubsetOf(CreateApiKeySelects, RenderedNames(html, "select"));

        // A scope the user does not belong to is refused on the page, not by an unhandled exception.
        var refused = await PostAsync(client, ApiKeysPath, html, "create-api-key", new Dictionary<string, string>
        {
            ["Input.DisplayName"] = displayName,
            ["Input.AccessLevel"] = nameof(ApiKeyAccessLevel.Read),
            ["Input.ObservatoryId"] = Guid.NewGuid().ToString()
        }).ConfigureAwait(false);
        Assert.Contains("Error: Choose an observatory you are a member of.", refused);
        Assert.IsNull(await FindViewerKeyAsync(displayName).ConfigureAwait(false));

        var created = await PostAsync(client, ApiKeysPath, html, "create-api-key", new Dictionary<string, string>
        {
            ["Input.DisplayName"] = displayName,
            ["Input.AccessLevel"] = nameof(ApiKeyAccessLevel.Read),
            ["Input.ObservatoryId"] = observatoryId.ToString()
        }).ConfigureAwait(false);
        Assert.Contains("New API key created.", created);
        Assert.DoesNotContain("field is required", created);
        var key = await FindViewerKeyAsync(displayName).ConfigureAwait(false);
        Assert.IsNotNull(key);
        Assert.AreEqual(observatoryId, key.ObservatoryId);
        Assert.IsTrue(key.IsActive);

        // The row buttons post the key under the action's name; only the clicked button's name is sent.
        var deactivated = await PostAsync(client, ApiKeysPath, created, "api-key-action", new Dictionary<string, string>
        {
            ["KeyAction.Deactivate"] = key.Id
        }).ConfigureAwait(false);
        Assert.Contains("API key deactivated.", deactivated);
        Assert.IsFalse((await FindViewerKeyAsync(displayName).ConfigureAwait(false))!.IsActive);

        var deleted = await PostAsync(client, ApiKeysPath, deactivated, "api-key-action", new Dictionary<string, string>
        {
            ["KeyAction.Delete"] = key.Id
        }).ConfigureAwait(false);
        Assert.Contains("API key deleted.", deleted);
        Assert.IsNull(await FindViewerKeyAsync(displayName).ConfigureAwait(false));
    }

    private static async Task<Guid> AddViewerObservatoryMembershipAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email).ConfigureAwait(false);
        var viewer = await db.Users.SingleAsync(user => user.Email == TestUsers.Viewer.Email).ConfigureAwait(false);
        var observatory = await services.GetRequiredService<IObservatoryService>().CreateOrUpdateAsync(new ObservatoryUpsertRequest(
            null,
            owner.Id,
            $"Account form post {Guid.NewGuid():N}",
            19.5,
            -155.5,
            1200,
            "Pacific/Honolulu",
            true)).ConfigureAwait(false);
        db.ObservatoryMemberships.Add(new ObservatoryMembership
        {
            ObservatoryId = observatory.Id,
            UserId = viewer.Id,
            Role = ObservatoryMembershipRole.Viewer,
            AddedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        return observatory.Id;
    }

    private static async Task<ApiKey?> FindViewerKeyAsync(string displayName)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var viewerId = await db.Users
            .Where(user => user.Email == TestUsers.Viewer.Email)
            .Select(user => user.Id)
            .SingleAsync()
            .ConfigureAwait(false);
        return await db.ApiKeys
            .AsNoTracking()
            .SingleOrDefaultAsync(key => key.UserId == viewerId && key.DisplayName == displayName)
            .ConfigureAwait(false);
    }

    private static async Task<string> GetPageAsync(HttpClient client, string path)
    {
        using var page = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        return await page.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    // Posts a form the way a browser does: the page's antiforgery token, the named handler and the fields.
    private static async Task<string> PostAsync(
        HttpClient client,
        string path,
        string renderedPage,
        string formName,
        Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = AntiforgeryToken(renderedPage);
        fields["_handler"] = formName;
        using var form = new FormUrlEncodedContent(fields);
        using var response = await client.PostAsync(new Uri(path, UriKind.Relative), form).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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

    private static string[] RenderedInputNames(string html) => RenderedNames(html, "input");

    private static string[] RenderedNames(string html, string element)
        => Regex.Matches(html, $"<{element}[^>]*\\sname=\"([^\"]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .ToArray();
}
