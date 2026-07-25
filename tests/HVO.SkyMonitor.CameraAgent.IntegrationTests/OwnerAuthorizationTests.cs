using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class OwnerAuthorizationTests
{
    [TestMethod]
    public async Task OperationsPoliciesAuthorizeOnlyConfiguredOwnerAsync()
    {
        using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var services = scope.ServiceProvider;
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var principalFactory = services.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var authorizationService = services.GetRequiredService<IAuthorizationService>();
        var owner = await userManager.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
        Assert.IsNotNull(owner);

        var nonOwner = await userManager.FindByEmailAsync("non-owner@cameraagent.integration").ConfigureAwait(false);
        if (nonOwner is null)
        {
            nonOwner = new ApplicationUser
            {
                UserName = "non-owner@cameraagent.integration",
                Email = "non-owner@cameraagent.integration",
                EmailConfirmed = true
            };
            var createResult = await userManager.CreateAsync(nonOwner, "IntegrationNonOwner!123").ConfigureAwait(false);
            Assert.IsTrue(createResult.Succeeded, string.Join(", ", createResult.Errors.Select(error => error.Description)));
        }

        var ownerPrincipal = await principalFactory.CreateAsync(owner).ConfigureAwait(false);
        var nonOwnerPrincipal = await principalFactory.CreateAsync(nonOwner).ConfigureAwait(false);
        var anonymousPrincipal = new ClaimsPrincipal(new ClaimsIdentity());

        foreach (var policyName in new[]
        {
            CameraAgentAuthorizationPolicyNames.OperationsReadV1,
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1
        })
        {
            Assert.IsTrue((await authorizationService.AuthorizeAsync(ownerPrincipal, policyName).ConfigureAwait(false)).Succeeded);
            Assert.IsFalse((await authorizationService.AuthorizeAsync(nonOwnerPrincipal, policyName).ConfigureAwait(false)).Succeeded);
            Assert.IsFalse((await authorizationService.AuthorizeAsync(anonymousPrincipal, policyName).ConfigureAwait(false)).Succeeded);
        }
    }

    [TestMethod]
    public async Task RegistrationPagesDoNotEnumerateAccountsAsync()
    {
        using var client = AssemblyHooks.Fixture.CreateCameraAgentClient();
        using var knownResponse = await client.GetAsync(
            new Uri("/Account/RegisterConfirmation?email=owner%40cameraagent.integration", UriKind.Relative))
            .ConfigureAwait(false);
        using var unknownResponse = await client.GetAsync(
            new Uri("/Account/RegisterConfirmation?email=unknown%40cameraagent.integration", UriKind.Relative))
            .ConfigureAwait(false);
        var knownContent = await knownResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        var unknownContent = await unknownResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.AreEqual(knownResponse.StatusCode, unknownResponse.StatusCode);
        StringAssert.Contains(knownContent, "Local self-registration is disabled.", StringComparison.Ordinal);
        StringAssert.Contains(unknownContent, "Local self-registration is disabled.", StringComparison.Ordinal);
        Assert.IsFalse(knownContent.Contains("Error finding user", StringComparison.Ordinal));
        Assert.IsFalse(unknownContent.Contains("Error finding user", StringComparison.Ordinal));
        Assert.IsFalse(knownContent.Contains("Please check your email", StringComparison.Ordinal));
        Assert.IsFalse(unknownContent.Contains("Please check your email", StringComparison.Ordinal));
        Assert.IsFalse(knownContent.Contains("owner@cameraagent.integration", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(unknownContent.Contains("unknown@cameraagent.integration", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task GalleryEndpointsEnforceOwnerReadPolicyAsync()
    {
        string ownerId;
        string nonOwnerId;
        using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await userManager.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
            Assert.IsNotNull(owner);
            ownerId = owner.Id;
            var nonOwner = await userManager.FindByEmailAsync("gallery-non-owner@cameraagent.integration").ConfigureAwait(false);
            if (nonOwner is null)
            {
                nonOwner = new ApplicationUser
                {
                    UserName = "gallery-non-owner@cameraagent.integration",
                    Email = "gallery-non-owner@cameraagent.integration",
                    EmailConfirmed = true
                };
                var created = await userManager.CreateAsync(nonOwner, "GalleryNonOwner!123").ConfigureAwait(false);
                Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(static error => error.Description)));
            }
            nonOwnerId = nonOwner.Id;
        }

        using var anonymousClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        using var anonymousResponse = await anonymousClient.GetAsync(
            new Uri("/api/v1/operations/gallery", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var nonOwnerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        nonOwnerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
        using var nonOwnerResponse = await nonOwnerClient.GetAsync(
            new Uri("/api/v1/operations/gallery", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerResponse.StatusCode);

        using var ownerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        ownerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
        using var ownerResponse = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/gallery", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, ownerResponse.StatusCode);
        using var invalidQuery = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/gallery?pageSize=101", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidQuery.StatusCode);
        using var missingDetail = await ownerClient.GetAsync(
            new Uri($"/api/v1/operations/gallery/{Guid.NewGuid():D}", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, missingDetail.StatusCode);
    }

    [TestMethod]
    public async Task OperationsSummaryAndMutationEndpointsEnforceOwnerAndAntiforgeryAsync()
    {
        string ownerId;
        string nonOwnerId;
        using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await userManager.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
            Assert.IsNotNull(owner);
            ownerId = owner.Id;
            var nonOwner = await userManager.FindByEmailAsync("operations-non-owner@cameraagent.integration").ConfigureAwait(false);
            if (nonOwner is null)
            {
                nonOwner = new ApplicationUser
                {
                    UserName = "operations-non-owner@cameraagent.integration",
                    Email = "operations-non-owner@cameraagent.integration",
                    EmailConfirmed = true
                };
                var created = await userManager.CreateAsync(nonOwner, "OperationsNonOwner!123").ConfigureAwait(false);
                Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(static error => error.Description)));
            }
            nonOwnerId = nonOwner.Id;
        }

        using var anonymousClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        using var anonymousSummary = await anonymousClient.GetAsync(
            new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousSummary.StatusCode);
        using var anonymousSchedule = await anonymousClient.GetAsync(
            new Uri("/api/v1/operations/schedule", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousSchedule.StatusCode);

        using var nonOwnerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        nonOwnerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
        using var nonOwnerSummary = await nonOwnerClient.GetAsync(
            new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerSummary.StatusCode);
        using var nonOwnerSchedule = await nonOwnerClient.GetAsync(
            new Uri("/api/v1/operations/schedule", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerSchedule.StatusCode);
        using var nonOwnerMutation = await nonOwnerClient.PostAsJsonAsync(
            new Uri("/api/v1/operations/capture/resume", UriKind.Relative),
            new { reason = "test" }).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerMutation.StatusCode);

        using var ownerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        ownerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
        using var ownerSummary = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, ownerSummary.StatusCode);
        var summaryJson = await ownerSummary.Content.ReadAsStringAsync().ConfigureAwait(false);
        StringAssert.Contains(summaryJson, "captureControl", StringComparison.Ordinal);
        Assert.IsFalse(summaryJson.Contains(AssemblyHooks.Fixture.StorageRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(summaryJson.Contains("leaseToken", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(summaryJson.Contains("deviceKey", StringComparison.OrdinalIgnoreCase));
        using var ownerSchedule = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/schedule", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, ownerSchedule.StatusCode);

        using var missingAntiforgery = await ownerClient.PostAsJsonAsync(
            new Uri("/api/v1/operations/capture/resume", UriKind.Relative),
            new { reason = "test" }).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
        using var missingScheduleAntiforgery = await ownerClient.PostAsJsonAsync(
            new Uri("/api/v1/operations/schedule/stage", UriKind.Relative),
            new { }).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingScheduleAntiforgery.StatusCode);

        var token = await GetAntiforgeryTokenAsync(ownerClient).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/v1/operations/capture/resume", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", $"integration-resume-{Guid.NewGuid():N}");
        request.Headers.Add("RequestVerificationToken", token);
        request.Content = JsonContent.Create(new { reason = "integration verification" });
        using var mutation = await ownerClient.SendAsync(request).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, mutation.StatusCode);
        using var result = JsonDocument.Parse(await mutation.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual((int)HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureAdmissionState.Running,
            result.RootElement.GetProperty("state").GetInt32());
    }

    [TestMethod]
    public async Task LatestFramesDeviceAndOperatorPagesAreOwnerGatedAsync()
    {
        string ownerId;
        string nonOwnerId;
        using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await userManager.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
            Assert.IsNotNull(owner);
            ownerId = owner.Id;
            var nonOwner = await userManager.FindByEmailAsync("page-non-owner@cameraagent.integration").ConfigureAwait(false);
            if (nonOwner is null)
            {
                nonOwner = new ApplicationUser
                {
                    UserName = "page-non-owner@cameraagent.integration",
                    Email = "page-non-owner@cameraagent.integration",
                    EmailConfirmed = true
                };
                var created = await userManager.CreateAsync(nonOwner, "PageNonOwner!123").ConfigureAwait(false);
                Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(static error => error.Description)));
            }
            nonOwnerId = nonOwner.Id;
        }

        foreach (var endpoint in new[]
        {
            "/api/v1.0/frames/latest",
            "/api/v1.0/frames/raw",
            "/api/v1.0/frames/processed"
        })
        {
            using var anonymousClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
            using var anonymous = await anonymousClient.GetAsync(new Uri(endpoint, UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode, endpoint);

            using var nonOwnerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
            nonOwnerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
            using var forbidden = await nonOwnerClient.GetAsync(new Uri(endpoint, UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode, endpoint);

            using var ownerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
            ownerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
            using var owner = await ownerClient.GetAsync(new Uri(endpoint, UriKind.Relative)).ConfigureAwait(false);
            Assert.IsTrue(owner.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound, endpoint);
        }

        foreach (var page in new[]
        {
            (Path: "/", Expected: "Capture operations"),
            (Path: "/operations", Expected: "Capture operations"),
            (Path: "/gallery", Expected: "Capture gallery"),
            (Path: "/schedule", Expected: "Schedule control"),
            (Path: "/system", Expected: "System snapshot"),
            (Path: "/devices/bootstrap", Expected: "Device Bootstrap")
        })
        {
            using var anonymousClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
            using var anonymous = await anonymousClient.GetAsync(new Uri(page.Path, UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Redirect, anonymous.StatusCode, page.Path);
            StringAssert.Contains(anonymous.Headers.Location?.OriginalString ?? string.Empty, "/Account/Login", StringComparison.Ordinal, page.Path);

            using var nonOwnerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
            nonOwnerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
            using var nonOwner = await nonOwnerClient.GetAsync(new Uri(page.Path, UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Redirect, nonOwner.StatusCode, page.Path);
            Assert.AreEqual("/Account/AccessDenied", nonOwner.Headers.Location?.OriginalString, page.Path);

            using var ownerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
            ownerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
            using var owner = await ownerClient.GetAsync(new Uri(page.Path, UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, owner.StatusCode, page.Path);
            var ownerHtml = await owner.Content.ReadAsStringAsync().ConfigureAwait(false);
            StringAssert.Contains(ownerHtml, page.Expected, StringComparison.Ordinal, page.Path);
        }
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative)).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        const string name = "name=\"__RequestVerificationToken\"";
        var nameIndex = html.IndexOf(name, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, nameIndex);
        const string value = "value=\"";
        var valueIndex = html.IndexOf(value, nameIndex, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, valueIndex);
        valueIndex += value.Length;
        var endIndex = html.IndexOf('"', valueIndex);
        Assert.IsGreaterThan(valueIndex, endIndex);
        return WebUtility.HtmlDecode(html[valueIndex..endIndex]);
    }
}
