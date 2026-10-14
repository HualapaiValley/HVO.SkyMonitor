using System.IO.Pipelines;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentRawEvidenceOperationsEndpointsTests
{
    [TestMethod]
    public async Task RawEvidenceReadFailureDoesNotExposeStoragePaths()
    {
        using var app = CreateApp(new RecordingOperations { FailRead = true });
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = HttpMethods.Get;
        using var response = new MemoryStream();
        context.Response.Body = response;
        await FindEndpoint(app, "ReadCameraAgentRawEvidence").RequestDelegate!(context).ConfigureAwait(false);
        Assert.AreEqual(500, context.Response.StatusCode);
        Assert.IsFalse(Encoding.UTF8.GetString(response.ToArray()).Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RawEvidenceEndpoints_RequireOwnerPoliciesAndAntiforgery()
    {
        using var app = CreateApp(new RecordingOperations());
        foreach (var name in new[] { "ReadCameraAgentRawEvidence", "RevalidateCameraAgentRawEvidence", "WriteOffCameraAgentRawEvidence" })
        {
            var endpoint = FindEndpoint(app, name);
            CollectionAssert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).ToArray(), CameraAgentAuthorizationPolicyNames.OperationsReadV1);
            if (name != "ReadCameraAgentRawEvidence")
            {
                CollectionAssert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).ToArray(), CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
                Assert.IsTrue(endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>()!.RequiresValidation);
            }
        }
    }

    [TestMethod]
    public async Task RawEvidenceMutation_BindsActionTokensActorReasonAndOperationKey()
    {
        var operations = new RecordingOperations();
        using var app = CreateApp(operations);
        var tokens = app.Services.GetRequiredService<OutboxOperationsTokenService>();
        var token = tokens.ProtectRawEvidenceAction(RawEvidenceReleaseAction.WriteOff, RecordingOperations.Target);
        var response = await PostAsync(app, "WriteOffCameraAgentRawEvidence", token, "operator-approved-loss").ConfigureAwait(false);
        Assert.AreEqual(200, response.Status);
        Assert.AreEqual("owner-alice", operations.Actor);
        Assert.AreEqual("operation-1", operations.Key);
        Assert.AreEqual("operator-approved-loss", operations.Reason);
        Assert.AreEqual(RawEvidenceReleaseAction.WriteOff, operations.Action);
        Assert.AreEqual(RecordingOperations.Target, operations.ReceivedTarget);
        var wrong = await PostAsync(app, "RevalidateCameraAgentRawEvidence", token, "evidence-restored").ConfigureAwait(false);
        Assert.AreEqual(404, wrong.Status);
        Assert.AreEqual(1, operations.Calls);
        operations.Fail = true;
        var failed = await PostAsync(app, "WriteOffCameraAgentRawEvidence", token, "operator-approved-loss").ConfigureAwait(false);
        Assert.AreEqual(500, failed.Status);
        Assert.IsFalse(failed.Body.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RawEvidenceTokens_CannotCrossActionKindOrCursorPurposes()
    {
        var tokens = new OutboxOperationsTokenService(new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(5));
        var token = tokens.ProtectRawEvidenceAction(RawEvidenceReleaseAction.Revalidate, RecordingOperations.Target);
        Assert.IsTrue(tokens.TryReadRawEvidenceAction(RawEvidenceReleaseAction.Revalidate, token, out var target));
        Assert.AreEqual(RecordingOperations.Target, target);
        Assert.IsFalse(tokens.TryReadRawEvidenceAction(RawEvidenceReleaseAction.WriteOff, token, out _));
        Assert.IsFalse(tokens.TryReadRawEvidenceCursor(token, out _));
        Assert.IsFalse(tokens.TryReadEnvironmentalAction(HVO.SkyMonitor.CameraAgent.Common.Operations.OutboxOperationAction.Replay, token, out _));
        Assert.IsFalse(tokens.TryReadRawEvidenceAction(RawEvidenceReleaseAction.Revalidate, token + "tamper", out _));
    }

    private static WebApplication CreateApp(RecordingOperations operations)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IRawEvidenceOperations>(operations);
        builder.Services.AddSingleton(new OutboxOperationsTokenService(new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(5)));
        var app = builder.Build();
        app.MapCameraAgentRawEvidenceOperationsEndpoints();
        return app;
    }

    private static RouteEndpoint FindEndpoint(WebApplication app, string name)
        => ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);

    private static async Task<(int Status, string Body)> PostAsync(WebApplication app, string name, string token, string reason)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "owner-alice"),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ], IdentityConstants.ApplicationScheme))
        };
        context.Request.Method = HttpMethods.Post;
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { actionToken = token, reasonCode = reason }));
        using var stream = new MemoryStream(payload);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = payload.Length;
        context.Request.Body = stream;
        context.Request.Headers["Idempotency-Key"] = "operation-1";
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature());
        context.Features.Set<IRequestBodyPipeFeature>(new BodyPipeFeature(stream));
        using var response = new MemoryStream();
        context.Response.Body = response;
        await FindEndpoint(app, name).RequestDelegate!(context).ConfigureAwait(false);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray()));
    }

    private sealed class BodyDetectionFeature : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
    private sealed class BodyPipeFeature(Stream stream) : IRequestBodyPipeFeature { public PipeReader Reader { get; } = PipeReader.Create(stream); }

    private sealed class RecordingOperations : IRawEvidenceOperations
    {
        internal static RawEvidenceTarget Target { get; } = new(Guid.NewGuid(), new string('A', 64), "missing_evidence", "required-file-missing");
        internal string? Actor { get; private set; }
        internal string? Key { get; private set; }
        internal string? Reason { get; private set; }
        internal RawEvidenceReleaseAction Action { get; private set; }
        internal RawEvidenceTarget? ReceivedTarget { get; private set; }
        internal int Calls { get; private set; }
        internal bool Fail { get; set; }
        internal bool FailRead { get; set; }
        public ValueTask<RawEvidencePage> ReadEvidencePageAsync(int pageSize, long? beforeRowId, CancellationToken cancellationToken)
            => FailRead ? ValueTask.FromException<RawEvidencePage>(new IOException("/secret/raw-ingress-root"))
                : ValueTask.FromResult(new RawEvidencePage([], null));
        public ValueTask<RawEvidenceReleaseReceipt> ReleaseEvidenceAsync(RawEvidenceTarget target, RawEvidenceReleaseAction action,
            string idempotencyKey, string actor, string reason, CancellationToken cancellationToken)
        {
            Calls++;
            ReceivedTarget = target;
            Actor = actor;
            Key = idempotencyKey;
            Reason = reason;
            Action = action;
            return Fail ? ValueTask.FromException<RawEvidenceReleaseReceipt>(new IOException("/secret/path"))
                : ValueTask.FromResult(new RawEvidenceReleaseReceipt(false, new(action, actor, reason, DateTimeOffset.UnixEpoch)));
        }
    }
}
