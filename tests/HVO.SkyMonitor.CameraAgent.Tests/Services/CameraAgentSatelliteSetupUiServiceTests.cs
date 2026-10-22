using System.Security.Claims;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Settings;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.CameraAgent.Tests.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Tests.Settings;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
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
    [DataRow("final")]
    [DataRow("predicted")]
    [DataRow("expired")]
    public async Task Status_ShowsTheBundledSourceCoverageAndExpiryAsync(string state)
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var options = new PlanningTestInputs.Settings();
        using var times = new CameraAgentPinnedOrbitalTimeSource(options);
        var data = (await times.ReadAsync(CancellationToken.None).ConfigureAwait(false))!;
        // Instants come from the bundled pair itself, so the states hold for every approved release snapshot.
        var start = state switch
        {
            "final" => data.EarthOrientationFinalCoverage!.StartUtc.AddDays(1),
            "predicted" => data.EarthOrientationPredictedCoverage!.StartUtc.AddDays(1),
            _ => CameraAgentSatellitePlanner.CoverageEndsBefore(data, DateTimeOffset.MaxValue)!.Value.AddDays(1)
        };
        var service = new CameraAgentSatelliteSetupUiService(new Authentication(), Authorization(true, true),
            harness.Service, new TestOrbitalUiService(), times, options);
        var view = (await service.GetAsync(start, start.AddHours(6), CancellationToken.None).ConfigureAwait(false)).Value!;
        Assert.AreEqual("Bundled with this CameraAgent release", view.ActiveSource);
        Assert.AreEqual(data.EarthOrientationFinalCoverage, view.FinalCoverage);
        Assert.AreEqual(data.EarthOrientationPredictedCoverage, view.PredictedCoverage);
        Assert.AreEqual(data.LeapTableExpiresUtc, view.LeapSecondsExpireUtc);
        foreach (var file in view.Files)
        {
            Assert.AreEqual("Bundled and hash verified", file.Status);
            Assert.Contains("Retrieved", file.Origin!, StringComparison.Ordinal);
        }
        if (state == "expired")
        {
            var problem = view.Problems.Single();
            Assert.AreEqual("coverage-expired", problem.Code);
            Assert.Contains("The bundled time data covers planning only until", problem.Message, StringComparison.Ordinal);
            Assert.IsNull(view.WindowEarthOrientation);
        }
        else
        {
            Assert.IsEmpty(view.Problems);
            Assert.AreEqual(state == "final" ? OrbitalEarthOrientationStatus.Final : OrbitalEarthOrientationStatus.Predicted,
                view.WindowEarthOrientation);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Status_ShowsARefreshedPairItsProvenanceAndTheRefreshFactsAsync(bool expired)
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        using var host = new CameraAgentTimeDataRefreshTests.Host();
        var pointer = await host.AdoptLongerPairAsync(days: 100).ConfigureAwait(false);
        var data = (await host.Source.ReadAsync(CancellationToken.None).ConfigureAwait(false))!;
        var end = CameraAgentPinnedOrbitalTimeSource.PlanningCoverageEnd(data);
        var start = expired ? end.AddDays(1) : data.EarthOrientationPredictedCoverage!.StartUtc.AddDays(1);
        var service = new CameraAgentSatelliteSetupUiService(new Authentication(), Authorization(true, true),
            harness.Service, new TestOrbitalUiService(), host.Source, host.Options, host.Operations);
        var view = (await service.GetAsync(start, start.AddHours(6), CancellationToken.None).ConfigureAwait(false)).Value!;
        Assert.AreEqual("Refreshed pair (managed time-data refresh)", view.ActiveSource);
        CollectionAssert.Contains(view.Groups.Select(static group => group.Group).ToArray(), CameraAgentSettingsCatalogue.TimeDataRefreshGroup);
        foreach (var file in view.Files)
        {
            Assert.AreEqual("Refreshed and hash verified", file.Status);
            StringAssert.StartsWith(file.Path, host.Store.PairDirectory(pointer.Pair), StringComparison.Ordinal);
            Assert.Contains("https://", file.Origin!, StringComparison.Ordinal);
        }
        var status = view.TimeData!;
        Assert.AreEqual("managed", status.ActiveSource);
        Assert.AreEqual(pointer.Pair, status.ManagedPair!.Pair);
        Assert.AreEqual(end, status.CoverageEndUtc);
        if (expired)
            Assert.Contains("The refreshed time data covers planning only until",
                view.Problems.Single(static problem => problem.Code == "coverage-expired").Message, StringComparison.Ordinal);
        else
            Assert.IsEmpty(view.Problems);
    }

    [TestMethod]
    public async Task Status_AnIncompleteOverrideIsShownAndTheBundledPairIsNotUsedAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var options = new PlanningTestInputs.Settings();
        options.CurrentValue.LeapSecondsPath = "/absent/leap-seconds.list";
        using var times = new CameraAgentPinnedOrbitalTimeSource(options);
        var service = new CameraAgentSatelliteSetupUiService(new Authentication(), Authorization(true, true),
            harness.Service, new TestOrbitalUiService(), times, options);
        var view = (await service.GetAsync(PlanningTestInputs.Start, PlanningTestInputs.Start.AddHours(6), CancellationToken.None)
            .ConfigureAwait(false)).Value!;
        Assert.AreEqual("Configured override", view.ActiveSource);
        var problem = view.Problems.Single();
        Assert.AreEqual("no-time-data", problem.Code);
        Assert.Contains("override is incomplete", problem.Message, StringComparison.Ordinal);
        Assert.AreEqual("/absent/leap-seconds.list", view.Files[1].Path);
        Assert.IsNull(view.EarthOrientationCoverage);
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
        Assert.IsTrue(result.IsSuccess, result.Message);
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
            new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "owner"),
                new(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)], IdentityConstants.ApplicationScheme))));
    }
}
