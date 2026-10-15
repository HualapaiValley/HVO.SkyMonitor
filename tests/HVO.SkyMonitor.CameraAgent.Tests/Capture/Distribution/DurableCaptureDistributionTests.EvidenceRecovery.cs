using System.Net;
using System.Net.Http.Json;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Distribution;

public sealed partial class DurableCaptureDistributionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EvidenceRecoveryHostStartsWithCaptureClosedAndOwnerReleaseResumesAdmission(bool writeOff)
    {
        using var fixture = CreateFixture(new() { UploadEnabled = false, PollIntervalMilliseconds = 50, ShutdownDrainSeconds = 1 });
        var receipt = await fixture.AcceptAsync(0).ConfigureAwait(false);
        var backup = Path.Combine(fixture.Root, "recovered-original.bin");
        File.Move(receipt.StoredFrame.AbsolutePath, backup);
        _ = fixture.RestartLaneStore();
        var state = new RawIngressState(fixture.Time);
        using var telemetry = new RawIngressTelemetry(state);
        using var ingress = new RawCaptureIngress(fixture.Options, new FixedCapacityProvider(), state, fixture.Time,
            telemetry, NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector(), fixture.Policy,
            new NullCaptureLaneFaultInjector(), fixture.LaneState, fixture.LaneTelemetry);
        using var controlTelemetry = new CaptureControlTelemetry();
        using var coordinator = new CaptureAdmissionCoordinator(ingress, fixture.Options, fixture.Time, controlTelemetry);
        using var standard = new StandardCaptureLaneHandler(new EmptyPipelineFactory(), NullLogger<StandardCaptureLaneHandler>.Instance, ingress);
        using var distribution = new CaptureDistributionService(new ConfigurationAccessor(fixture.Configuration), ingress,
            ingress, fixture.Policy, [standard], standard, fixture.Options, TimeProvider.System,
            new NullCaptureLaneFaultInjector(), fixture.LaneTelemetry, fixture.LaneState,
            NullLogger<CaptureDistributionService>.Instance);
        using var host = Host.CreateDefaultBuilder().ConfigureWebHostDefaults(web =>
        {
            web.UseKestrel().UseUrls("http://127.0.0.1:0");
            web.ConfigureServices(services =>
            {
                services.AddSingleton<IRawEvidenceOperations>(ingress);
                services.AddSingleton(new OutboxOperationsTokenService(new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(5)));
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddAuthentication(RecoveryOwnerAuthentication.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, RecoveryOwnerAuthentication>(RecoveryOwnerAuthentication.SchemeName, _ => { });
                services.AddAuthorization(options =>
                {
                    options.AddPolicy(CameraAgentAuthorizationPolicyNames.OperationsReadV1, policy => policy.RequireAuthenticatedUser().RequireRole("Owner"));
                    options.AddPolicy(CameraAgentAuthorizationPolicyNames.OperationsMutateV1, policy => policy.RequireAuthenticatedUser().RequireRole("Owner"));
                });
                services.AddAntiforgery();
                services.AddSingleton<IHostedService>(new CaptureAdmissionInitializationService(coordinator));
                services.AddSingleton<IHostedService>(distribution);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseAntiforgery();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapCameraAgentRawEvidenceOperationsEndpoints();
                    endpoints.MapGet("/recovery-test/antiforgery", (HttpContext context, IAntiforgery antiforgery)
                        => Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken!)).RequireAuthorization();
                });
            });
        }).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.StartAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            Assert.AreEqual(CaptureAdmissionState.Unavailable, coordinator.Snapshot.State);
            Assert.IsFalse(coordinator.Snapshot.IsInitialized);
            var admission = coordinator.EnterAsync(timeout.Token).AsTask();
            Assert.IsFalse(admission.IsCompleted, "Unreleased evidence must keep capture admission closed.");
            using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), CheckCertificateRevocationList = true };
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
            };
            using var anonymous = await client.GetAsync(new Uri("/api/v1/operations/raw-evidence", UriKind.Relative), timeout.Token).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            client.DefaultRequestHeaders.Add("X-Recovery-Owner", "owner");
            using var page = await client.GetFromJsonAsync<JsonDocument>(new Uri("/api/v1/operations/raw-evidence", UriKind.Relative), timeout.Token).ConfigureAwait(false);
            Assert.IsNotNull(page);
            var token = page.RootElement.GetProperty("items")[0].GetProperty(writeOff ? "writeOffToken" : "revalidateToken").GetString()!;
            if (!writeOff) File.Move(backup, receipt.StoredFrame.AbsolutePath);
            var antiforgery = await client.GetStringAsync(new Uri("/recovery-test/antiforgery", UriKind.Relative), timeout.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/operations/raw-evidence/{(writeOff ? "write-off" : "revalidate")}", UriKind.Relative));
            request.Headers.Add("Idempotency-Key", "host-evidence-recovery");
            request.Headers.Add("RequestVerificationToken", antiforgery);
            request.Content = JsonContent.Create(new { actionToken = token, reasonCode = writeOff ? "operator-approved-loss" : "evidence-restored" });
            using var released = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, released.StatusCode, await released.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            using var audit = JsonDocument.Parse(await released.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            Assert.AreEqual("recovery-owner", audit.RootElement.GetProperty("audit").GetProperty("actor").GetString());
            using var lease = await admission.ConfigureAwait(false);
            var fresh = await ingress.AcceptAsync(fixture.Configuration, fixture.CreateSubmission(1), timeout.Token).ConfigureAwait(false);
            Assert.IsNotNull(fresh);
            lease.MarkPublished();
            Assert.AreEqual(CaptureAdmissionState.Running, coordinator.Snapshot.State);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 0, 4 }, await File.ReadAllBytesAsync(writeOff ? backup : receipt.StoredFrame.AbsolutePath, timeout.Token).ConfigureAwait(false));
        }
        finally { await host.StopAsync(CancellationToken.None).ConfigureAwait(false); }
    }

    [TestMethod]
    [DataRow("evidence-invalid")]
    [DataRow("evidence-unavailable")]
    public async Task RevalidationSettlesActualEvidenceLaneFailurePreservesRawAndAllowsFreshCapture(string reason)
    {
        using var fixture = CreateFixture(new() { UploadEnabled = false });
        var receipt = await fixture.AcceptAsync(0).ConfigureAwait(false);
        var backup = Path.Combine(fixture.Root, "recovery-backup.bin");
        var sidecar = Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json");
        var sidecarBackup = Path.Combine(fixture.Root, "recovery-sidecar.json");
        if (reason == "evidence-unavailable")
        {
            var lease = await fixture.ClaimAsync("standard").ConfigureAwait(false);
            Assert.IsNotNull(lease);
            File.Move(receipt.StoredFrame.AbsolutePath, backup);
            File.Move(sidecar, sidecarBackup);
            using var standard = new StandardCaptureLaneHandler(new EmptyPipelineFactory(), NullLogger<StandardCaptureLaneHandler>.Instance, fixture.Ingress);
            var failed = await standard.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.TerminalFailure, failed.Outcome);
            Assert.AreEqual(reason, failed.Reason);
            await fixture.Store.FailAsync(lease, failed, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            File.Move(receipt.StoredFrame.AbsolutePath, backup);
            Assert.IsNull(await fixture.Store.ClaimAsync(fixture.Policy.Definitions.Single(lane => lane.Name == "standard"),
                "evidence-reader", fixture.Configuration, CancellationToken.None).ConfigureAwait(false));
        }
        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual("quarantined", await ScalarStringAsync(connection, "SELECT state FROM capture_lane_work WHERE lane_name='standard';").ConfigureAwait(false));
        var item = (await fixture.Ingress.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single();
        File.Move(backup, receipt.StoredFrame.AbsolutePath);
        if (File.Exists(sidecarBackup)) File.Move(sidecarBackup, sidecar);
        await fixture.Ingress.ReleaseEvidenceAsync(item.Target, RawEvidenceReleaseAction.Revalidate,
            "lane-revalidation", "recovery-owner", "evidence-restored", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("abandoned", await ScalarStringAsync(connection, "SELECT state FROM capture_lane_work WHERE lane_name='standard';").ConfigureAwait(false));
        Assert.AreEqual("raw-evidence-revalidated", await ScalarStringAsync(connection, "SELECT failure_reason FROM capture_lane_work WHERE lane_name='standard';").ConfigureAwait(false));
        Assert.IsNotNull(await fixture.AcceptAsync(1).ConfigureAwait(false));
        using (var recompute = connection.CreateCommand())
        {
            recompute.CommandText = "UPDATE raw_captures SET retention_hold=0 WHERE capture_sequence=1;";
            await recompute.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var holds = await fixture.Ingress.GetRetentionHoldsAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(holds.Any(hold => hold.ArtifactId == receipt.Manifest.Descriptor.Artifact.ArtifactId));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 0, 4 }, await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task RevalidationPreservesUnrelatedRequiredLaneQuarantine()
    {
        using var fixture = CreateFixture(new() { UploadEnabled = true });
        var receipt = await fixture.AcceptAsync(0).ConfigureAwait(false);
        var upload = await fixture.ClaimAsync("upload").ConfigureAwait(false);
        Assert.IsNotNull(upload);
        await fixture.Store.FailAsync(upload, CaptureLaneHandlerResult.Terminal("provider-configuration-invalid"), CancellationToken.None).ConfigureAwait(false);
        var backup = Path.Combine(fixture.Root, "unrelated-recovery.bin");
        File.Move(receipt.StoredFrame.AbsolutePath, backup);
        Assert.IsNull(await fixture.Store.ClaimAsync(fixture.Policy.Definitions.Single(lane => lane.Name == "standard"),
            "evidence-reader", fixture.Configuration, CancellationToken.None).ConfigureAwait(false));
        var item = (await fixture.Ingress.ReadEvidencePageAsync(10, null, CancellationToken.None).ConfigureAwait(false)).Items.Single();
        File.Move(backup, receipt.StoredFrame.AbsolutePath);
        await fixture.Ingress.ReleaseEvidenceAsync(item.Target, RawEvidenceReleaseAction.Revalidate,
            "unrelated-revalidation", "recovery-owner", "evidence-restored", CancellationToken.None).ConfigureAwait(false);
        using var connection = await OpenAsync(fixture.Root).ConfigureAwait(false);
        Assert.AreEqual("quarantined", await ScalarStringAsync(connection, "SELECT state FROM capture_lane_work WHERE lane_name='upload';").ConfigureAwait(false));
        Assert.AreEqual("provider-configuration-invalid", await ScalarStringAsync(connection, "SELECT failure_reason FROM capture_lane_work WHERE lane_name='upload';").ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<CaptureLaneBackpressureException>(() => fixture.AcceptAsync(1)).ConfigureAwait(false);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 0, 4 }, await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath).ConfigureAwait(false));
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "ASP.NET instantiates the registered authentication scheme handler through dependency injection.")]
    private sealed class RecoveryOwnerAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "EvidenceRecoveryOwner";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Recovery-Owner"] != "owner") return Task.FromResult(AuthenticateResult.NoResult());
            var user = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "recovery-owner"), new Claim(ClaimTypes.Role, "Owner"),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ], IdentityConstants.ApplicationScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, SchemeName)));
        }
    }
}
