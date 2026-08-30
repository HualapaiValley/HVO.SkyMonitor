using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
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
    [DoNotParallelize]
    public async Task PendingOwnerCanReadBootstrapStatusButCannotUseOperationsAsync()
    {
        string ownerId;
        string nonOwnerId;
        using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
            Assert.IsNotNull(owner);
            ownerId = owner.Id;
            owner.PasswordChangeRequired = true;
            Assert.IsTrue((await users.UpdateAsync(owner).ConfigureAwait(false)).Succeeded);
            var nonOwner = await users.FindByEmailAsync("bootstrap-status-non-owner@cameraagent.integration")
                .ConfigureAwait(false);
            if (nonOwner is null)
            {
                nonOwner = new ApplicationUser
                {
                    UserName = "bootstrap-status-non-owner@cameraagent.integration",
                    Email = "bootstrap-status-non-owner@cameraagent.integration",
                    EmailConfirmed = true
                };
                Assert.IsTrue((await users.CreateAsync(nonOwner, "BootstrapStatusNonOwner!418")
                    .ConfigureAwait(false)).Succeeded);
            }
            nonOwnerId = nonOwner.Id;
        }

        try
        {
            using var anonymousClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
            using var anonymousStatus = await anonymousClient.GetAsync(
                new Uri(OwnerBootstrapGateMiddleware.StatusPath, UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousStatus.StatusCode);
            using var nonOwnerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
            nonOwnerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
            using var nonOwnerStatus = await nonOwnerClient.GetAsync(
                new Uri(OwnerBootstrapGateMiddleware.StatusPath, UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerStatus.StatusCode);

            using var client = AssemblyHooks.Fixture.CreateCameraAgentClient();
            client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
            using var status = await client.GetAsync(
                new Uri(OwnerBootstrapGateMiddleware.StatusPath, UriKind.Relative)).ConfigureAwait(false);
            var statusJson = await status.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, status.StatusCode, statusJson);
            StringAssert.Contains(statusJson, OwnerBootstrapStates.TemporaryPassword, StringComparison.Ordinal);
            using var denied = await client.GetAsync(
                new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.AreEqual(OwnerBootstrapStates.PasswordChangeRequired,
                denied.Headers.GetValues("X-HVO-Authorization-Reason").Single());
            using var deniedCurrent = await client.GetAsync(
                new Uri("/api/v1/operations/gallery/current", UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Forbidden, deniedCurrent.StatusCode);

            using var healthClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
            using var health = await healthClient.GetAsync(
                new Uri("/health", UriKind.Relative)).ConfigureAwait(false);
            var healthJson = await health.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode, healthJson);
            StringAssert.Contains(healthJson, "owner-bootstrap", StringComparison.Ordinal);
            StringAssert.Contains(healthJson, "Owner bootstrap is operational.", StringComparison.Ordinal);
            Assert.IsFalse(healthJson.Contains(OwnerBootstrapStates.TemporaryPassword, StringComparison.Ordinal));
            Assert.IsFalse(healthJson.Contains(OwnerBootstrapStates.PasswordChangeRequired, StringComparison.Ordinal));
        }
        finally
        {
            using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
            Assert.IsNotNull(owner);
            owner.PasswordChangeRequired = false;
            Assert.IsTrue((await users.UpdateAsync(owner).ConfigureAwait(false)).Succeeded);
        }
    }

    [TestMethod]
    public async Task BlazorFrameworkAssetIsServedWithoutAuthenticationAsync()
    {
        using var client = AssemblyHooks.Fixture.CreateCameraAgentClient();
        using var response = await client.GetAsync(
            new Uri("/_framework/blazor.web.js", UriKind.Relative)).ConfigureAwait(false);
        var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.IsTrue(mediaType is "application/javascript" or "text/javascript", $"Unexpected media type '{mediaType}'.");
        Assert.IsGreaterThan(10_000, payload.Length);
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(payload), "window.Blazor", StringComparison.Ordinal);
    }

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
        using var anonymousCurrent = await anonymousClient.GetAsync(
            new Uri("/api/v1/operations/gallery/current", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousCurrent.StatusCode);
        using var anonymousPresentation = await anonymousClient.GetAsync(
            new Uri($"/api/v1/operations/gallery/{Guid.NewGuid():D}/presentation", UriKind.Relative))
            .ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousPresentation.StatusCode);
        using var anonymousMaterialization = await anonymousClient.PostAsJsonAsync(
            new Uri($"/api/v1/operations/gallery/{Guid.NewGuid():D}/materializations", UriKind.Relative),
            new { enabledLayerIdentitySha256 = Array.Empty<string>() }).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousMaterialization.StatusCode);

        using var nonOwnerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        nonOwnerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
        using var nonOwnerResponse = await nonOwnerClient.GetAsync(
            new Uri("/api/v1/operations/gallery", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerResponse.StatusCode);
        using var nonOwnerCurrent = await nonOwnerClient.GetAsync(
            new Uri("/api/v1/operations/gallery/current", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerCurrent.StatusCode);
        using var nonOwnerPresentation = await nonOwnerClient.GetAsync(
            new Uri($"/api/v1/operations/gallery/{Guid.NewGuid():D}/presentation.svg", UriKind.Relative))
            .ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerPresentation.StatusCode);
        using var nonOwnerMaterialization = await nonOwnerClient.PostAsJsonAsync(
            new Uri($"/api/v1/operations/gallery/{Guid.NewGuid():D}/materializations", UriKind.Relative),
            new { enabledLayerIdentitySha256 = Array.Empty<string>() }).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerMaterialization.StatusCode);

        using var ownerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        ownerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
        using var ownerResponse = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/gallery", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, ownerResponse.StatusCode);
        using var ownerCurrent = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/gallery/current", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, ownerCurrent.StatusCode);
        using var invalidQuery = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/gallery?pageSize=101", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidQuery.StatusCode);
        using var missingDetail = await ownerClient.GetAsync(
            new Uri($"/api/v1/operations/gallery/{Guid.NewGuid():D}", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, missingDetail.StatusCode);
        var missingCaptureId = Guid.NewGuid();
        using var missingPresentation = await ownerClient.GetAsync(
            new Uri($"/api/v1/operations/gallery/{missingCaptureId:D}/presentation", UriKind.Relative))
            .ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, missingPresentation.StatusCode);
        using var missingSvg = await ownerClient.GetAsync(
            new Uri($"/api/v1/operations/gallery/{missingCaptureId:D}/presentation.svg", UriKind.Relative))
            .ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, missingSvg.StatusCode);
        using var missingAntiforgery = await ownerClient.PostAsJsonAsync(
            new Uri($"/api/v1/operations/gallery/{missingCaptureId:D}/materializations", UriKind.Relative),
            new { enabledLayerIdentitySha256 = Array.Empty<string>() }).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
        var antiforgery = await GetAntiforgeryTokenAsync(ownerClient).ConfigureAwait(false);
        using var missingMaterializationRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/api/v1/operations/gallery/{missingCaptureId:D}/materializations", UriKind.Relative))
        {
            Content = JsonContent.Create(new { enabledLayerIdentitySha256 = Array.Empty<string>() })
        };
        missingMaterializationRequest.Headers.Add("RequestVerificationToken", antiforgery);
        using var missingMaterialization = await ownerClient.SendAsync(missingMaterializationRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, missingMaterialization.StatusCode);
    }

    [TestMethod]
    public async Task DeploymentContinuityEndpointExposesOnlyOwnerSafeStateAsync()
    {
        string ownerId;
        string nonOwnerId;
        using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await userManager.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
            Assert.IsNotNull(owner);
            ownerId = owner.Id;
            var nonOwner = await userManager.FindByEmailAsync("deployment-non-owner@cameraagent.integration")
                .ConfigureAwait(false);
            if (nonOwner is null)
            {
                nonOwner = new ApplicationUser
                {
                    UserName = "deployment-non-owner@cameraagent.integration",
                    Email = "deployment-non-owner@cameraagent.integration",
                    EmailConfirmed = true
                };
                var created = await userManager.CreateAsync(nonOwner, "DeploymentNonOwner!123").ConfigureAwait(false);
                Assert.IsTrue(created.Succeeded, string.Join(", ", created.Errors.Select(static error => error.Description)));
            }
            nonOwnerId = nonOwner.Id;
        }

        using var anonymousClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        using var anonymous = await anonymousClient.GetAsync(
            new Uri("/api/internal/deployment/continuity", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var nonOwnerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        nonOwnerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
        using var forbidden = await nonOwnerClient.GetAsync(
            new Uri("/api/internal/deployment/continuity", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var ownerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        ownerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
        using var response = await ownerClient.GetAsync(
            new Uri("/api/internal/deployment/continuity", UriKind.Relative)).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, json);
        StringAssert.Contains(json, AssemblyHooks.Fixture.DeviceId, StringComparison.Ordinal);
        Assert.IsFalse(json.Contains("verificationCode", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("deviceKey", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("registrationToken", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains(AssemblyHooks.Fixture.StorageRoot, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DeploymentTelemetryRequiresOwnerAndExactCaptureArtifactCorrelationAsync()
    {
        var artifactId = Guid.NewGuid();
        string ownerId;
        using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ownerId = (await userManager.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false))!.Id;
            scope.ServiceProvider.GetRequiredService<CapturePipelineTraceStore>().Record(
                42,
                Guid.NewGuid(),
                artifactId,
                new ActivityContext(
                    ActivityTraceId.CreateFromString("11111111111111111111111111111111"),
                    ActivitySpanId.CreateFromString("2222222222222222"),
                    ActivityTraceFlags.Recorded));
        }

        var path = $"/api/internal/deployment/telemetry?captureSequence=42&artifactId={artifactId:D}";
        using var anonymousClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        using var anonymous = await anonymousClient.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var ownerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        ownerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
        using var mismatch = await ownerClient.GetAsync(
            new Uri($"/api/internal/deployment/telemetry?captureSequence=41&artifactId={artifactId:D}", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, mismatch.StatusCode);
        using var response = await ownerClient.GetAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, json);
        StringAssert.Contains(json, artifactId.ToString("D"), StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(json, "11111111111111111111111111111111", StringComparison.Ordinal);
        Assert.IsFalse(json.Contains("request", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("payload", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DeploymentMutationUsesOwnerCookieAuthorityAndAntiforgery()
    {
        string ownerId;
        string nonOwnerId;
        using (var scope = AssemblyHooks.Fixture.CreateCameraAgentScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ownerId = (await userManager.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false))!.Id;
            var nonOwner = await userManager.FindByEmailAsync("deployment-antiforgery-non-owner@cameraagent.integration").ConfigureAwait(false);
            if (nonOwner is null)
            {
                nonOwner = new ApplicationUser
                {
                    UserName = "deployment-antiforgery-non-owner@cameraagent.integration",
                    Email = "deployment-antiforgery-non-owner@cameraagent.integration",
                    EmailConfirmed = true
                };
                var created = await userManager.CreateAsync(nonOwner, "DeploymentNonOwner!456").ConfigureAwait(false);
                Assert.IsTrue(created.Succeeded);
            }
            nonOwnerId = nonOwner.Id;
        }

        using var anonymousClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        using var removedPasswordEndpoint = await anonymousClient.PostAsync(
            new Uri("/api/internal/deployment/session", UriKind.Relative), null).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, removedPasswordEndpoint.StatusCode);
        using var anonymousMutation = await anonymousClient.PostAsync(
            new Uri("/api/internal/deployment/identity", UriKind.Relative), null).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousMutation.StatusCode);

        using var nonOwnerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        nonOwnerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
        using var nonOwnerToken = await nonOwnerClient.GetAsync(
            new Uri("/api/internal/deployment/antiforgery", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerToken.StatusCode);

        using var ownerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        ownerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
        using var missingToken = await ownerClient.PostAsync(
            new Uri("/api/internal/deployment/identity", UriKind.Relative), null).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);
        using var missingResetToken = await ownerClient.PostAsync(
            new Uri("/api/internal/deployment/measurement/reset", UriKind.Relative), null).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingResetToken.StatusCode);
        using var tokenResponse = await ownerClient.GetAsync(
            new Uri("/api/internal/deployment/antiforgery", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, tokenResponse.StatusCode);
        using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        ownerClient.DefaultRequestHeaders.Add(
            tokenJson.RootElement.GetProperty("headerName").GetString()!,
            tokenJson.RootElement.GetProperty("requestToken").GetString()!);
        using var mutation = await ownerClient.PostAsync(
            new Uri("/api/internal/deployment/identity", UriKind.Relative), null).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, mutation.StatusCode);
        using var reset = await ownerClient.PostAsync(
            new Uri("/api/internal/deployment/measurement/reset", UriKind.Relative), null).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, reset.StatusCode);
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
        using var anonymousPipeline = await anonymousClient.GetAsync(
            new Uri("/api/v1/operations/pipeline", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousPipeline.StatusCode);
        using var anonymousCalibration = await anonymousClient.GetAsync(
            new Uri("/api/v1/operations/calibration/status", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousCalibration.StatusCode);
        using var anonymousEnvironmental = await anonymousClient.GetAsync(
            new Uri("/api/v1/operations/environmental/sources", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousEnvironmental.StatusCode);

        using var nonOwnerClient = AssemblyHooks.Fixture.CreateCameraAgentClient();
        nonOwnerClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
        using var nonOwnerSummary = await nonOwnerClient.GetAsync(
            new Uri("/api/v1/operations/summary", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerSummary.StatusCode);
        using var nonOwnerSchedule = await nonOwnerClient.GetAsync(
            new Uri("/api/v1/operations/schedule", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerSchedule.StatusCode);
        using var nonOwnerPipeline = await nonOwnerClient.GetAsync(
            new Uri("/api/v1/operations/pipeline", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerPipeline.StatusCode);
        using var nonOwnerCalibration = await nonOwnerClient.GetAsync(
            new Uri("/api/v1/operations/calibration/status", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerCalibration.StatusCode);
        using var nonOwnerEnvironmental = await nonOwnerClient.GetAsync(
            new Uri("/api/v1/operations/environmental/history", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, nonOwnerEnvironmental.StatusCode);
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
        var scheduleJson = await ownerSchedule.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.IsFalse(scheduleJson.Contains(AssemblyHooks.Fixture.StorageRoot, StringComparison.OrdinalIgnoreCase));
        using var ownerPipeline = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/pipeline", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, ownerPipeline.StatusCode);
        var pipelineJson = await ownerPipeline.Content.ReadAsStringAsync().ConfigureAwait(false);
        StringAssert.Contains(pipelineJson, "desiredSha256", StringComparison.Ordinal);
        StringAssert.Contains(pipelineJson, "effectiveSha256", StringComparison.Ordinal);
        Assert.IsFalse(pipelineJson.Contains(AssemblyHooks.Fixture.StorageRoot, StringComparison.OrdinalIgnoreCase));
        using var ownerCalibration = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/calibration/status", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, ownerCalibration.StatusCode);
        var calibrationJson = await ownerCalibration.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.IsFalse(calibrationJson.Contains(AssemblyHooks.Fixture.StorageRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(calibrationJson.Contains("relativePath", StringComparison.OrdinalIgnoreCase));
        using var ownerEnvironmental = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/environmental/sources", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, ownerEnvironmental.StatusCode);
        var environmentalJson = await ownerEnvironmental.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.IsFalse(environmentalJson.Contains(AssemblyHooks.Fixture.StorageRoot, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(environmentalJson.Contains("options", StringComparison.OrdinalIgnoreCase));
        using var invalidEnvironmentalPage = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/environmental/history?pageSize=101", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidEnvironmentalPage.StatusCode);
        using var invalidCalibrationPage = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/calibration/bundles?pageSize=101", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidCalibrationPage.StatusCode);
        using var missingCalibrationDetail = await ownerClient.GetAsync(
            new Uri("/api/v1/operations/calibration/bundles/missing", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, missingCalibrationDetail.StatusCode);

        using var missingAntiforgery = await ownerClient.PostAsJsonAsync(
            new Uri("/api/v1/operations/capture/resume", UriKind.Relative),
            new { reason = "test" }).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
        using var missingScheduleAntiforgery = await ownerClient.PostAsJsonAsync(
            new Uri("/api/v1/operations/schedule/stage", UriKind.Relative),
            new { }).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingScheduleAntiforgery.StatusCode);
        using var missingCalibrationAntiforgery = await ownerClient.PostAsJsonAsync(
            new Uri("/api/v1/operations/calibration/acquisitions", UriKind.Relative),
            new { }).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingCalibrationAntiforgery.StatusCode);
        using var missingEnvironmentalAntiforgery = await ownerClient.PostAsJsonAsync(
            new Uri("/api/v1/operations/environmental/sources/missing/acquisitions", UriKind.Relative),
            new { reason = "test" }).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingEnvironmentalAntiforgery.StatusCode);

        var token = await GetAntiforgeryTokenAsync(ownerClient).ConfigureAwait(false);
        using (var missingEnvironmentalRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/v1/operations/environmental/sources/missing/acquisitions", UriKind.Relative)))
        {
            missingEnvironmentalRequest.Headers.Add("Idempotency-Key", $"environment-{Guid.NewGuid():N}");
            missingEnvironmentalRequest.Headers.Add("RequestVerificationToken", token);
            missingEnvironmentalRequest.Content = JsonContent.Create(new { reason = "integration verification" });
            using var missingEnvironmental = await ownerClient.SendAsync(missingEnvironmentalRequest).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.NotFound, missingEnvironmental.StatusCode);
        }
        using (var invalidCalibrationIdempotency = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/v1/operations/calibration/bundles/missing/activate", UriKind.Relative)))
        {
            invalidCalibrationIdempotency.Headers.Add("Idempotency-Key", new string('a', 129));
            invalidCalibrationIdempotency.Headers.Add("RequestVerificationToken", token);
            invalidCalibrationIdempotency.Content = JsonContent.Create(new
            {
                expectedVersion = 0,
                reason = "invalid idempotency key"
            });
            using var invalidCalibration = await ownerClient.SendAsync(invalidCalibrationIdempotency)
                .ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidCalibration.StatusCode);
        }
        using (var scheduleDocument = JsonDocument.Parse(scheduleJson))
        using (var missingVersionRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/v1/operations/schedule/stage", UriKind.Relative)))
        {
            missingVersionRequest.Headers.Add("Idempotency-Key", $"integration-stage-{Guid.NewGuid():N}");
            missingVersionRequest.Headers.Add("RequestVerificationToken", token);
            missingVersionRequest.Content = JsonContent.Create(new
            {
                profile = scheduleDocument.RootElement.GetProperty("activeRevision").GetProperty("profile"),
                basisRevisionId = scheduleDocument.RootElement.GetProperty("activeRevision").GetProperty("revisionId"),
                reason = "missing concurrency version"
            });
            using var missingVersion = await ownerClient.SendAsync(missingVersionRequest).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.BadRequest, missingVersion.StatusCode);
        }
        using (var scheduleDocument = JsonDocument.Parse(scheduleJson))
        using (var staleBasisRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/v1/operations/schedule/stage", UriKind.Relative)))
        {
            staleBasisRequest.Headers.Add("Idempotency-Key", $"integration-stale-basis-{Guid.NewGuid():N}");
            staleBasisRequest.Headers.Add("RequestVerificationToken", token);
            staleBasisRequest.Content = JsonContent.Create(new
            {
                profile = scheduleDocument.RootElement.GetProperty("activeRevision").GetProperty("profile"),
                basisRevisionId = Guid.NewGuid().ToString("N"),
                expectedVersion = scheduleDocument.RootElement.GetProperty("stateVersion").GetInt64(),
                reason = "stale basis verification"
            });
            using var staleBasis = await ownerClient.SendAsync(staleBasisRequest).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.NotFound, staleBasis.StatusCode);
        }
        using (var scheduleDocument = JsonDocument.Parse(scheduleJson))
        {
            using var pipelinePreview = await ownerClient.PostAsJsonAsync(
                new Uri("/api/v1/operations/pipeline/preview", UriKind.Relative),
                new
                {
                    profile = scheduleDocument.RootElement.GetProperty("activeRevision").GetProperty("profile"),
                    basisRevisionId = scheduleDocument.RootElement.GetProperty("activeRevision").GetProperty("revisionId")
                }).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, pipelinePreview.StatusCode);
            var previewJson = await pipelinePreview.Content.ReadAsStringAsync().ConfigureAwait(false);
            StringAssert.Contains(previewJson, "desiredNodes", StringComparison.Ordinal);
            Assert.IsFalse(previewJson.Contains(AssemblyHooks.Fixture.StorageRoot, StringComparison.OrdinalIgnoreCase));
        }
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
            (Path: "/", Expected: "Current sky"),
            (Path: "/operations", Expected: "Capture operations"),
            (Path: "/gallery", Expected: "Archive"),
            (Path: "/schedule", Expected: "Schedule control"),
            (Path: "/calibration", Expected: "Calibration library"),
            (Path: "/environmental", Expected: "Environmental acquisition"),
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
