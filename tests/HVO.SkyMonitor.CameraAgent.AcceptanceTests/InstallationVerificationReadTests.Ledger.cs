using System.Net;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class InstallationVerificationReadTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task OrdinaryConfigurationAndContinuityReadsDoNotScrubOrInvalidateTheEndpoint()
    {
        await using var fixture = await InstallationReadAttributionFixture.CreateAsync(null, solar: false, completeOwner: true).ConfigureAwait(false);
        _ = await fixture.SeedAsync(2, 2).ConfigureAwait(false);
        var store = (ProtectedDeploymentLocationStore)fixture.Locations;
        // Fixture bulk construction is followed by one explicit full qualification.
        _ = await fixture.Loader.LoadForRetainedIntegrityScrubAsync(CancellationToken.None).ConfigureAwait(false);
        using var client = fixture.CreateClient();
        using var ownerClient = await fixture.Host.CreateOwnerClientAsync().ConfigureAwait(false);
        var before = await fixture.ReadAsync(client).ConfigureAwait(false);
        store.BeforeRetainedManifestValidationForTest = () => Assert.Fail("Ordinary load/continuity must not scan retained history.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            _ = await fixture.Loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            using var continuity = await ownerClient.GetAsync(new Uri("/api/internal/deployment/continuity", UriKind.Relative)).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, continuity.StatusCode);
            var after = await fixture.ReadAsync(client).ConfigureAwait(false);
            Assert.AreEqual(before.GetRawText(), after.GetRawText());
        }
        var inspected = 0;
        store.BeforeRetainedManifestValidationForTest = () => inspected++;
        _ = await fixture.Loader.LoadForRetainedIntegrityScrubAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsGreaterThan(0, inspected, "Explicit maintenance must still perform a full scrub.");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "Both requests and the scrub are drained before fixture/event disposal.")]
    public async Task ContinuityDuringRunningScrubWaitsAndReturnsSuccess()
    {
        await using var fixture = await InstallationReadAttributionFixture.CreateAsync(null, solar: false, completeOwner: true).ConfigureAwait(false);
        _ = await fixture.SeedAsync(1, 1).ConfigureAwait(false);
        using var owner = await fixture.Host.CreateOwnerClientAsync().ConfigureAwait(false);
        var store = (ProtectedDeploymentLocationStore)fixture.Locations;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeRetainedManifestValidationForTest = () =>
        {
            entered.TrySetResult();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var scrub = Task.Run(async () => await fixture.Loader.LoadForRetainedIntegrityScrubAsync(CancellationToken.None).ConfigureAwait(false));
        Task<HttpResponseMessage>? continuity = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            continuity = owner.GetAsync(new Uri("/api/internal/deployment/continuity", UriKind.Relative));
            await Task.Delay(TimeSpan.FromMilliseconds(1250)).ConfigureAwait(false);
            Assert.IsFalse(continuity.IsCompleted, "Busy ordinary loading must wait, rather than become HTTP 500.");
        }
        finally
        {
            release.Set();
            _ = await scrub.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (continuity is not null)
            {
                using var response = await continuity.ConfigureAwait(false);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "Every task is awaited and the blocked scrub is released in finally before fixture disposal.")]
    public async Task DirtyEndpointRefusesPromptlyAndCoalescedScrubSurvivesCallerCancellation()
    {
        await using var fixture = await InstallationReadAttributionFixture.CreateAsync(null, solar: false).ConfigureAwait(false);
        _ = await fixture.SeedAsync(2, 2).ConfigureAwait(false);
        var store = (ProtectedDeploymentLocationStore)fixture.Locations;
        using var client = fixture.CreateClient();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = 0;
        store.BeforeRetainedManifestValidationForTest = () =>
        {
            if (Interlocked.Increment(ref observed) != 1) return;
            entered.TrySetResult();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(30)), "Test must release the independent scrub.");
        };
        var checkpoint = Path.Combine(fixture.IngressRoot, ".location", RetainedIntegrityLedger.CheckpointName);
        await File.WriteAllBytesAsync(checkpoint, [1, 2, 3]).ConfigureAwait(false);
        try
        {
            using (var request = AuthorizedRequest())
            using (var response = await client.SendAsync(request).ConfigureAwait(false))
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.AreEqual(TimeSpan.FromSeconds(1), response.Headers.RetryAfter!.Delta);
                Assert.AreEqual("scrub-pending", response.Headers.GetValues("X-HVO-Installation-Verification").Single());
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Assert.IsFalse(text.Contains(CameraAgentKestrelFixture.InstallationVerificationToken, StringComparison.Ordinal));
                Assert.IsFalse(text.Contains(fixture.Host.Root, StringComparison.Ordinal));
            }
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                using var request = AuthorizedRequest();
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                {
                    using var response = await client.SendAsync(request, cancellation.Token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            Assert.AreEqual(1, observed, "Canceled callers must neither restart nor cancel the host-owned scrub.");
        }
        finally { release.Set(); }
        // Wait for the actual scrub's completion through the store gate, rather than an arbitrary delay.
        var gate = (SemaphoreSlim)typeof(ProtectedDeploymentLocationStore).GetField("_gate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
        Assert.IsTrue(await gate.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false));
        gate.Release();
        var identity = await fixture.ReadAsync(client).ConfigureAwait(false);
        Assert.AreEqual(1L, identity.GetProperty("deploymentLocationVersion").GetInt64());
        Assert.AreEqual(4, observed);
    }

    private static HttpRequestMessage AuthorizedRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/internal/owner-bootstrap/installation-verification");
        request.Headers.Add("X-HVO-Installation-Token", CameraAgentKestrelFixture.InstallationVerificationToken);
        return request;
    }
}
