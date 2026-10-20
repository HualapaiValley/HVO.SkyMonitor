using System.Security.Claims;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Settings;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.CameraAgent.Tests.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Tests.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSatelliteSetupUiServiceTests
{
    [TestMethod]
    [DataRow("disabled")]
    [DataRow("no-snapshot")]
    [DataRow("no-time-data")]
    [DataRow("hash-mismatch")]
    [DataRow("coverage-expired")]
    [DataRow("ready")]
    public async Task Status_ExplainsEveryUnavailableReasonAndLinksItsFixAsync(string scenario)
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var options = new PlanningTestInputs.Settings();
        options.CurrentValue.Enabled = scenario != "disabled";
        var orbital = new TestOrbitalUiService();
        if (scenario == "no-snapshot")
            orbital.GetHandler = _ => ValueTask.FromResult(OperatorUiResult<OrbitalDataStatusView>.Success(
                TestOrbitalUiService.View(TestOrbitalUiService.Status() with { CurrentManifestSha256 = null, CurrentPayloadSha256 = null }, null)));
        var times = new PlanningTestInputs.ResolvedTime();
        if (scenario is "no-time-data" or "hash-mismatch" or "disabled") times.Current = null;
        if (scenario == "hash-mismatch")
        {
            options.CurrentValue.EarthOrientationPath = Path.Combine(Path.GetDirectoryName(harness.SettingsPath)!, "test-time-data");
            options.CurrentValue.EarthOrientationSha256 = new('0', 64);
            await File.WriteAllTextAsync(options.CurrentValue.EarthOrientationPath, "synthetic content").ConfigureAwait(false);
        }
        var service = new CameraAgentSatelliteSetupUiService(new Authentication(), Authorization(true, true),
            harness.Service, orbital, times, options);
        var start = scenario == "coverage-expired" ? PlanningTestInputs.Start.AddYears(10) : PlanningTestInputs.Start;
        var result = await service.GetAsync(start, start.AddHours(6), CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(result.IsSuccess);
        if (scenario == "ready") Assert.IsEmpty(result.Value!.Problems);
        else
        {
            var problem = result.Value!.Problems.Single(item => item.Code == scenario);
            Assert.IsNotEmpty(problem.Message);
            Assert.IsTrue(problem.Link.StartsWith("/operations/", StringComparison.Ordinal));
            Assert.IsNotEmpty(problem.Action);
        }
        if (scenario == "hash-mismatch") Assert.AreEqual("Hash mismatch", result.Value!.Files[0].Status);
        if (scenario == "ready")
        {
            Assert.AreEqual("Resolved and hash verified", result.Value!.Files[0].Status);
            Assert.AreEqual(times.Current!.EarthOrientationCoverage, result.Value.EarthOrientationCoverage);
            Assert.AreEqual(times.Current.LeapTableExpiresUtc, result.Value.LeapSecondsExpireUtc);
        }
    }

    [TestMethod]
    public async Task Authorization_DeniedReadsAndWritesDoNotCallDependenciesAsync()
    {
        var service = new CameraAgentSatelliteSetupUiService(new Authentication(), Authorization(false, false),
            null!, null!, null!, null!);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, (await service.GetAsync(PlanningTestInputs.Start,
            PlanningTestInputs.Start.AddHours(6), CancellationToken.None).ConfigureAwait(false)).Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, (await service.SetAsync("orbitalElements", "absent", "true",
            CancellationToken.None).ConfigureAwait(false)).Kind);
    }

    [TestMethod]
    public async Task Mutation_UsesSettingsValidationAndVersionAndReadOnlyRightsAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var service = new CameraAgentSatelliteSetupUiService(new Authentication(), Authorization(true, true),
            harness.Service, new TestOrbitalUiService(), new PlanningTestInputs.ResolvedTime(), new PlanningTestInputs.Settings());
        var result = await service.SetAsync("orbitalElements", "absent", "true", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.PendingRestart, result.Value!.Outcome);
        var invalid = await service.SetAsync("orbitalElements", result.Value.Group!.Version, "sometimes", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Invalid, invalid.Value!.Outcome);
        var stale = await service.SetAsync("orbitalElements", "absent", "false", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Conflict, stale.Value!.Outcome);
        Assert.AreEqual(OperatorUiResultKind.Invalid, (await service.SetAsync("site", "absent", "true", CancellationToken.None).ConfigureAwait(false)).Kind);
        var readOnly = new CameraAgentSatelliteSetupUiService(new Authentication(), Authorization(true, false),
            harness.Service, new TestOrbitalUiService(), new PlanningTestInputs.ResolvedTime(), new PlanningTestInputs.Settings());
        var view = await readOnly.GetAsync(PlanningTestInputs.Start, PlanningTestInputs.Start.AddHours(6), CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(view.Value!.Groups.All(group => !group.CanWrite));
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, (await readOnly.SetAsync("orbitalElements", result.Value.Group.Version,
            "false", CancellationToken.None).ConfigureAwait(false)).Kind);
    }

    private static IAuthorizationService Authorization(bool read, bool mutate)
    {
        var service = new Mock<IAuthorizationService>();
        service.Setup(auth => auth.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), null, It.IsAny<string>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, string policy) =>
                (policy == CameraAgentAuthorizationPolicyNames.OperationsReadV1 ? read : mutate)
                    ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return service.Object;
    }

    private sealed class Authentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "owner")], "test"))));
    }
}
