using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "MSTest requires a public test class.")]
public sealed class RawEvidenceOperationsEndpointTests
{
    [TestMethod]
    public async Task RawEvidenceReleaseEnforcesAuthenticationOwnershipAntiforgeryAndActualActor()
    {
        var operations = new RawEvidenceOperations();
        using var factory = AssemblyHooks.Fixture.CreateCameraAgentFactory(services =>
        {
            services.RemoveAll<IRawEvidenceOperations>();
            services.AddSingleton<IRawEvidenceOperations>(operations);
        });
        string ownerId;
        string nonOwnerId;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ownerId = (await users.FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false))!.Id;
            var outsider = new ApplicationUser { UserName = $"raw-evidence-{Guid.NewGuid():N}", EmailConfirmed = true };
            Assert.IsTrue((await users.CreateAsync(outsider, "RawEvidence!123").ConfigureAwait(false)).Succeeded);
            nonOwnerId = outsider.Id;
        }
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var anonymousResponse = await anonymous.GetAsync(new Uri("/api/v1/operations/raw-evidence", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        using var outsiderClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        outsiderClient.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, nonOwnerId);
        using var forbidden = await outsiderClient.GetAsync(new Uri("/api/v1/operations/raw-evidence", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        owner.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, ownerId);
        using var pageResponse = await owner.GetAsync(new Uri("/api/v1/operations/raw-evidence", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, pageResponse.StatusCode);
        using var page = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        var token = page.RootElement.GetProperty("items")[0].GetProperty("writeOffToken").GetString()!;
        using var unprotectedRequest = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/operations/raw-evidence/write-off", UriKind.Relative));
        unprotectedRequest.Headers.Add("Idempotency-Key", "raw-evidence-unprotected");
        unprotectedRequest.Content = JsonContent.Create(new { actionToken = token, reasonCode = "operator-approved-loss" });
        using var missingAntiforgery = await owner.SendAsync(unprotectedRequest).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
        Assert.AreEqual(0, operations.Mutations);
        var antiforgery = await GetAntiforgeryTokenAsync(owner).ConfigureAwait(false);
        using var wrongAction = await SendMutationAsync(owner, "/api/v1/operations/raw-evidence/revalidate", "raw-evidence-1", token, "evidence-restored", antiforgery).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotFound, wrongAction.StatusCode);
        using var applied = await SendMutationAsync(owner, "/api/v1/operations/raw-evidence/write-off", "raw-evidence-1", token, "operator-approved-loss", antiforgery).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, applied.StatusCode);
        Assert.AreEqual(ownerId, operations.Actor);
        Assert.AreEqual("operator-approved-loss", operations.Reason);
        Assert.AreEqual(1, operations.Mutations);
    }

    private sealed class RawEvidenceOperations : IRawEvidenceOperations
    {
        internal int Mutations { get; private set; }
        internal string? Actor { get; private set; }
        internal string? Reason { get; private set; }
        public ValueTask<RawEvidencePage> ReadEvidencePageAsync(int pageSize, long? beforeRowId, CancellationToken cancellationToken)
            => ValueTask.FromResult(new RawEvidencePage([new(new(Guid.NewGuid(), new string('A', 64), "missing_evidence", "required-file-missing"),
                "agent-test", 1, Guid.NewGuid(), 4, DateTimeOffset.UnixEpoch, false, null)], null));
        public ValueTask<RawEvidenceReleaseReceipt> ReleaseEvidenceAsync(RawEvidenceTarget target, RawEvidenceReleaseAction action,
            string idempotencyKey, string actor, string reason, CancellationToken cancellationToken)
        {
            Mutations++;
            Actor = actor;
            Reason = reason;
            return ValueTask.FromResult(new RawEvidenceReleaseReceipt(false, new(action, actor, reason, DateTimeOffset.UnixEpoch)));
        }
    }
    private static async Task<HttpResponseMessage> SendMutationAsync(
        HttpClient client,
        string path,
        string operationKey,
        string actionToken,
        string reasonCode,
        string antiforgery)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add("Idempotency-Key", operationKey);
        request.Headers.Add("RequestVerificationToken", antiforgery);
        request.Content = JsonContent.Create(new { actionToken, reasonCode });
        return await client.SendAsync(request).ConfigureAwait(false);
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
        var valueIndex = html.IndexOf(value, nameIndex, StringComparison.Ordinal) + value.Length;
        var endIndex = html.IndexOf('"', valueIndex);
        return WebUtility.HtmlDecode(html[valueIndex..endIndex]);
    }
}
