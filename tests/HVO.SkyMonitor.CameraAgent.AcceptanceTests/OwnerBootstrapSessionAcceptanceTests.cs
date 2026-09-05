using System.Diagnostics.CodeAnalysis;
using System.Net;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Data;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

/// <summary>
/// Pins the owner bootstrap contract that <see cref="OwnerBootstrapSession"/> exists to satisfy,
/// and the helper's own behaviour, over the in-process Kestrel host. A newly provisioned agent
/// seeds its owner with a temporary password, so posting the login form alone yields a session the
/// owner bootstrap gate refuses for every operations request; issue #602 reproduced exactly that as
/// an opaque 403 from the dual standalone smoke's first gallery poll. This test does not cover the
/// Docker harnesses' call sites, which only the Manual smokes exercise; it fails when the helper
/// stops completing the bootstrap.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured async disposal would hide the strongly typed acceptance fixture.")]
public sealed class OwnerBootstrapSessionAcceptanceTests
{
    private const string SessionName = "kestrel-owner";

    [TestMethod]
    public async Task LoggedInOwnerReachesOperationsOnlyAfterBootstrapCompletesAsync()
    {
        await using var host = await CameraAgentKestrelFixture.CreateAsync(
            requireOwnerPasswordReplacement: true).ConfigureAwait(false);
        using var client = await host.CreateOwnerClientAsync().ConfigureAwait(false);

        Assert.AreEqual(
            OwnerBootstrapStates.TemporaryPassword,
            await OwnerBootstrapSession.ReadBootstrapStateAsync(client, SessionName).ConfigureAwait(false));

        using (var refused = await client.GetAsync(
            new Uri(OwnerBootstrapSession.OperationsProbePath, UriKind.Relative)).ConfigureAwait(false))
        {
            var body = await refused.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, body);
            Assert.AreEqual(
                OwnerBootstrapStates.PasswordChangeRequired,
                refused.Headers.GetValues(OwnerBootstrapSession.AuthorizationReasonHeader).Single());
        }

        var replacement = await OwnerBootstrapSession.EnsureReadyOwnerAsync(
            client, CameraAgentKestrelFixture.OwnerPassword, SessionName).ConfigureAwait(false);

        await OwnerBootstrapSession.AssertOperationsAuthorizedAsync(client, SessionName).ConfigureAwait(false);
        using (var granted = await client.GetAsync(
            new Uri(OwnerBootstrapSession.OperationsProbePath, UriKind.Relative)).ConfigureAwait(false))
        {
            Assert.IsFalse(granted.Headers.Contains(OwnerBootstrapSession.AuthorizationReasonHeader));
        }

        Assert.AreEqual(
            OwnerBootstrapStates.Ready,
            await OwnerBootstrapSession.ReadBootstrapStateAsync(client, SessionName).ConfigureAwait(false));
        Assert.AreEqual(
            CameraAgentKestrelFixture.OwnerPassword + OwnerBootstrapSession.ReplacementPasswordSuffix,
            replacement);

        // The helper runs once per agent per trial and the crash-recovery phase restarts agents, so
        // it must be a no-op against an owner that is already ready rather than attempting a second
        // replacement with a password that is no longer current.
        var repeated = await OwnerBootstrapSession.EnsureReadyOwnerAsync(
            client, replacement, SessionName).ConfigureAwait(false);
        Assert.AreEqual(replacement, repeated);
        await OwnerBootstrapSession.AssertOperationsAuthorizedAsync(client, SessionName).ConfigureAwait(false);
    }
}
