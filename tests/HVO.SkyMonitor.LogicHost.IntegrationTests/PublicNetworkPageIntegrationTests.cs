using System.Net;
using FluentAssertions;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class PublicNetworkPageIntegrationTests
{
    [TestMethod]
    public async Task AnonymousPagesRenderAllowlistedContentAndOperationsRedirectsToLogin()
    {
        foreach (var path in new[] { "/", "/observatories", "/events" })
        {
            using var client = AssemblyHooks.Fixture.Factory.CreateClient(new()
            {
                AllowAutoRedirect = false
            });
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
            var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            response.Headers.CacheControl!.Public.Should().BeTrue(
                "anonymous page {0} returned {1}", path, response.Headers.CacheControl);
            response.Headers.CacheControl.NoCache.Should().BeTrue(
                "anonymous page {0} returned {1}", path, response.Headers.CacheControl);
            response.Headers.CacheControl.MustRevalidate.Should().BeTrue(
                "anonymous page {0} returned {1}", path, response.Headers.CacheControl);
            response.Headers.Vary.Should().ContainSingle().Which.Should().Be("Cookie");
            html.Should().Contain("HVO SkyMonitor")
                .And.NotContain("s3://")
                .And.NotContain("StorageReference")
                .And.NotContain("DevicePublicId")
                .And.NotContain("OwnerUserId");
        }

        using var protectedClient = AssemblyHooks.Fixture.Factory.CreateClient(new()
        {
            AllowAutoRedirect = false
        });
        using var protectedResponse = await protectedClient.GetAsync(new Uri("/app", UriKind.Relative)).ConfigureAwait(false);

        protectedResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        protectedResponse.Headers.Location!.AbsolutePath.Should().Be("/Account/Login");
        protectedResponse.Headers.Location.Query.Should().Contain("ReturnUrl=%2Fapp");
        protectedResponse.Headers.CacheControl!.Private.Should().BeTrue();
        protectedResponse.Headers.CacheControl.NoStore.Should().BeTrue();
    }

    [TestMethod]
    public async Task AuthenticatedPublicAndProtectedPagesAreNotCacheable()
    {
        using var client = await CreateCookieClientAsync().ConfigureAwait(false);

        using var publicResponse = await client.GetAsync(new Uri("/", UriKind.Relative)).ConfigureAwait(false);
        using var protectedResponse = await client.GetAsync(new Uri("/app", UriKind.Relative)).ConfigureAwait(false);

        publicResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        protectedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var response in new[] { publicResponse, protectedResponse })
        {
            response.Headers.CacheControl!.Private.Should().BeTrue();
            response.Headers.CacheControl.NoStore.Should().BeTrue();
            string.Join(", ", response.Headers.Vary)
                .Should().Be("Cookie, Authorization, X-API-Key");
        }
    }

    [TestMethod]
    public async Task CookieAndApiKeyOnProtectedPageAreForbidden()
    {
        using var client = await CreateCookieClientAsync().ConfigureAwait(false);
        client.DefaultRequestHeaders.Add(
            ApiKeyAuthenticationOptions.HeaderName,
            TestApiKeys.InternalService.Key);

        using var response = await client.GetAsync(new Uri("/app", UriKind.Relative)).ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<HttpClient> CreateCookieClientAsync()
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var user = await services.GetRequiredService<ApplicationDbContext>().Users
            .SingleAsync(item => item.Email == TestUsers.Operator.Email)
            .ConfigureAwait(false);
        var principal = await services.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>()
            .CreateAsync(user)
            .ConfigureAwait(false);
        var cookieOptions = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var cookie = cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(
            principal,
            IdentityConstants.ApplicationScheme));
        var client = AssemblyHooks.Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookie}");
        return client;
    }
}
