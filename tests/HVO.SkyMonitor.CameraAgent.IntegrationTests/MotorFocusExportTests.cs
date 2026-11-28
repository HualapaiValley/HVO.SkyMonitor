using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest discovers public test classes.")]
public sealed class MotorFocusExportTests
{
    [TestMethod]
    public async Task RegisteredMotorAbortedSweepExportsVerifiedEvidenceAndPreservesThePublishedProfile()
    {
        using var host = new CameraAgentIntegrationFixture();
        var motor = new VirtualMotorizedFocuser(new(MaximumPosition: 1000, InitialPosition: 500, BacklashSteps: 8));
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CameraAgent:FirstBootPause:Id"] = Guid.NewGuid().ToString("D")
        };
        await host.InitializeAsync(services => services.AddSingleton<IMotorizedFocuser>(motor), settings).ConfigureAwait(false);
        using var scope = host.CreateCameraAgentScope();
        var session = scope.ServiceProvider.GetRequiredService<ManualFocusSessionCoordinator>();
        var ownership = scope.ServiceProvider.GetRequiredService<CameraModuleOwnership>();
        var admission = scope.ServiceProvider.GetRequiredService<CaptureAdmissionCoordinator>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!session.Availability.Available) await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        var original = ReadProfile(ownership);
        var started = await session.StartAsync(new(new(TimeSpan.FromMilliseconds(25), 5), Motor: new(SweepSpacing: 100)),
            host.OwnerId, timeout.Token).ConfigureAwait(false);
        while (!motor.State.IsMoving) await Task.Delay(5, timeout.Token).ConfigureAwait(false);
        motor.AdvanceBy(TimeSpan.FromMilliseconds(300));
        Assert.AreEqual(470, motor.State.Position);
        var stopping = session.StopAsync(started.SessionId, host.OwnerId, timeout.Token);
        using var clock = TimeProvider.System.CreateTimer(_ => motor.AdvanceBy(TimeSpan.MaxValue),
            null, TimeSpan.Zero, TimeSpan.FromMilliseconds(2));
        var ended = await stopping.ConfigureAwait(false);
        Assert.AreEqual(ManualFocusReasonCodes.StoppedByOperator, ended.EndReason);
        Assert.AreEqual(500, motor.OpticalPosition);
        Assert.AreEqual(CaptureAdmissionState.Paused, admission.Snapshot.State);
        Assert.AreEqual(original, ReadProfile(ownership));
        var saved = await session.SaveAsync(started.SessionId, host.OwnerId, timeout.Token).ConfigureAwait(false);
        var uri = new Uri($"/api/v1/operations/focus-sessions/{saved.SavedRecordId}/export", UriKind.Relative);
        using var client = host.CreateCameraAgentClient();
        using var denied = await client.GetAsync(uri, timeout.Token).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, host.OwnerId);
        using var listed = await client.GetAsync(new Uri("/api/v1/operations/focus-sessions/", UriKind.Relative), timeout.Token).ConfigureAwait(false);
        listed.EnsureSuccessStatusCode();
        StringAssert.Contains(await listed.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false), saved.SavedRecordId!, StringComparison.Ordinal);
        using var response = await client.GetAsync(uri, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)), response.Headers.GetValues("X-Artifact-SHA256").Single());
        using var document = JsonDocument.Parse(bytes);
        var record = document.RootElement.GetProperty("session");
        Assert.AreEqual(ManualFocusReasonCodes.StoppedByOperator, record.GetProperty("endReason").GetString());
        var result = record.GetProperty("motor");
        Assert.AreEqual(100, result.GetProperty("command").GetProperty("sweepSpacing").GetInt32());
        Assert.AreEqual(500, result.GetProperty("final").GetProperty("position").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("appliedPosition").ValueKind);
        Assert.AreEqual("Starting position verified.", result.GetProperty("recovery").GetString());
    }

    private static (long Generation, string Profile) ReadProfile(CameraModuleOwnership ownership)
    {
        Assert.IsTrue(ownership.TryAcquire(out var lease));
        using (lease) return (lease.Generation, JsonSerializer.Serialize(lease.Config));
    }
}
