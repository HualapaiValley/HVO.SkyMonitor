using System.Diagnostics.CodeAnalysis;
using System.Net;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Data;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

/// <summary>
/// Guards the session the Docker acceptance harnesses establish before they poll operations.
/// A newly provisioned agent seeds its owner with a temporary password, so posting the login form
/// alone yields an authenticated session that the owner bootstrap gate refuses for every
/// <c>/api</c> request. Issue #602 reproduced exactly that as an opaque 403 from the dual
/// standalone smoke's first gallery poll.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured async disposal would hide the strongly typed acceptance fixture.")]
public sealed class OwnerBootstrapSessionAcceptanceTests
{
    [TestMethod]
    public async Task LoggedInOwnerReachesOperationsOnlyAfterBootstrapCompletesAsync()
    {
        await using var host = await CameraAgentKestrelFixture.CreateAsync(
            requireOwnerPasswordReplacement: true).ConfigureAwait(false);
        using var client = await host.CreateOwnerClientAsync().ConfigureAwait(false);

        Assert.AreEqual(
            OwnerBootstrapStates.TemporaryPassword,
            await OwnerBootstrapSession.ReadBootstrapStateAsync(client).ConfigureAwait(false));

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
            client, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);

        await OwnerBootstrapSession.AssertOperationsAuthorizedAsync(client, "kestrel-owner").ConfigureAwait(false);
        using var granted = await client.GetAsync(
            new Uri(OwnerBootstrapSession.OperationsProbePath, UriKind.Relative)).ConfigureAwait(false);
        var grantedBody = await granted.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, granted.StatusCode, grantedBody);
        Assert.IsFalse(granted.Headers.Contains(OwnerBootstrapSession.AuthorizationReasonHeader));
        Assert.AreEqual(
            OwnerBootstrapStates.Ready,
            await OwnerBootstrapSession.ReadBootstrapStateAsync(client).ConfigureAwait(false));
        Assert.AreEqual(
            CameraAgentKestrelFixture.OwnerPassword + OwnerBootstrapSession.ReplacementPasswordSuffix,
            replacement);
    }
}
