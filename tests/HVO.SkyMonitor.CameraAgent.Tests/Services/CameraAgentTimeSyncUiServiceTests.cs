using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentTimeSyncUiServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 19, 0, 0, TimeSpan.Zero);
    private static readonly KernelClockState Kernel = new(KernelClockStatus.Synchronized, TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(3));

    [TestMethod]
    public async Task GetAsync_WhenTheReadPolicyFails_DeniesWithoutProjectingAsync()
    {
        var monitor = new StubMonitor { ThrowOnRead = true };
        var service = CreateService(monitor, read: false, mutate: false);

        var result = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.IsNull(result.Value);
    }

    [TestMethod]
    public async Task GetAsync_ProjectsTheLatestRoundAndMarksTheServerItChoseAsync()
    {
        var chosen = new TimeServerResult("b.example", null, TimeSpan.FromMilliseconds(-12), TimeSpan.FromMilliseconds(15), 1);
        var monitor = new StubMonitor
        {
            Settings = TimeSyncSettings.Default with { Servers = ["a.example", "https://bad.example", "b.example"] },
            Latest = new ClockSyncSnapshot(
                Now.AddMinutes(-4),
                true,
                chosen,
                [TimeServerResult.Failed("a.example", SntpFailure.Timeout), chosen],
                1,
                Kernel),
        };
        var service = CreateService(monitor, read: true, mutate: true);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(Now, view.AgentUtc);
        Assert.AreEqual(ClockSyncStatus.InTolerance, view.Status);
        Assert.AreEqual(ClockAssessment.Evaluate(monitor.Settings, monitor.Latest).Description, view.StatusDetail);
        Assert.AreEqual(TimeSyncSettings.DefaultTolerance, view.Tolerance);
        Assert.AreEqual(TimeSyncSettings.DefaultInterval, view.Interval);
        Assert.AreEqual(Now.AddMinutes(-4), view.MeasuredUtc);
        Assert.AreEqual(new TimeSyncServerView("b.example", null, chosen.Offset, chosen.RoundTrip, 1, true), view.Selected);
        Assert.HasCount(2, view.Servers);
        Assert.IsFalse(view.Servers[0].Selected);
        Assert.AreEqual(SntpFailure.Timeout, view.Servers[0].Failure);
        Assert.AreEqual(1, view.IgnoredEntries);
        Assert.AreEqual(Kernel, view.Kernel);
        string[] expectedServers = ["a.example", "b.example"];
        CollectionAssert.AreEqual(expectedServers, view.Setting.Servers.ToArray());
        Assert.IsFalse(view.Setting.UsingDefault);
        Assert.IsNull(view.Setting.Version, "No settings file is loaded.");
        Assert.IsTrue(view.CanChange);
        Assert.IsNull(view.SiteTimeZoneId);
        Assert.IsNull(view.SiteUtcOffset);
    }

    [TestMethod]
    public async Task GetAsync_WithoutTheMutatePolicy_IsReadOnlyAsync()
    {
        var service = CreateService(new StubMonitor(), read: true, mutate: false);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.IsFalse(view.CanChange);
    }

    [TestMethod]
    public async Task GetAsync_WhenCheckingIsOff_ShowsNoMeasurementAsync()
    {
        var monitor = new StubMonitor
        {
            Settings = TimeSyncSettings.Default with { Enabled = false },
            Latest = new ClockSyncSnapshot(
                Now, true, null, [TimeServerResult.Failed("pool.ntp.org", SntpFailure.Timeout)], 0, Kernel),
        };
        var service = CreateService(monitor, read: true, mutate: true);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(ClockSyncStatus.Disabled, view.Status);
        Assert.IsNull(view.MeasuredUtc);
        Assert.IsNull(view.Selected);
        Assert.IsEmpty(view.Servers);
    }

    [TestMethod]
    public async Task GetAsync_BeforeTheFirstRound_IsNotMeasuredAndTheKernelIsUnknownAsync()
    {
        var service = CreateService(new StubMonitor(), read: true, mutate: true);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(ClockSyncStatus.NotMeasured, view.Status);
        Assert.AreEqual(KernelClockState.Unknown, view.Kernel);
        CollectionAssert.AreEqual(TimeSyncSettings.DefaultServers.ToArray(), view.Setting.Servers.ToArray());
        Assert.IsTrue(view.Setting.UsingDefault);
    }

    [TestMethod]
    public async Task GetAsync_WhenEveryConfiguredEntryIsInvalid_ListsNoServerRatherThanTheDefaultAsync()
    {
        var monitor = new StubMonitor { Settings = TimeSyncSettings.Default with { Servers = ["ntp://x.example", "user@y.example"] } };
        var service = CreateService(monitor, read: true, mutate: true);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.IsEmpty(view.Setting.Servers);
        Assert.IsFalse(view.Setting.UsingDefault);
        Assert.AreEqual(2, view.IgnoredEntries);
    }

    [TestMethod]
    [DataRow("America/Phoenix", "America/Phoenix", -7d)]
    [DataRow("Mars/Olympus_Mons", "Mars/Olympus_Mons", double.NaN)]
    [DataRow("", null, double.NaN)]
    public async Task GetAsync_ReportsTheActiveLocationsTimeZoneAsync(string zone, string? expectedZone, double expectedHours)
    {
        var locations = new Mock<IDeploymentLocationStore>();
        locations.SetupGet(store => store.Active).Returns(new DeploymentLocationSnapshot(
            "hvo", 1, "sha", "manual", null, Now, null, 35.1, -114.1, 1100, zone));
        var service = CreateService(new StubMonitor(), read: true, mutate: true, locations: locations.Object);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(expectedZone, view.SiteTimeZoneId);
        Assert.AreEqual(double.IsNaN(expectedHours) ? null : TimeSpan.FromHours(expectedHours), view.SiteUtcOffset);
    }

    [TestMethod]
    public async Task GetAsync_ReportsTheSettingsFileVersionAndWhetherTheListIsOverriddenAsync()
    {
        using var plain = new TemporarySettingsFile();
        using var overridden = new TemporarySettingsFile(overridden: [TimeSyncSettings.ServersKey + ":0"]);

        var plainView = (await CreateService(new StubMonitor(), read: true, mutate: true, settingsFile: plain.File)
            .GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;
        var overriddenView = (await CreateService(new StubMonitor(), read: true, mutate: true, settingsFile: overridden.File)
            .GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.AreEqual(new TimeSyncServersSetting(plainView.Setting.Servers, true, OperatorSettingsFile.AbsentVersion, false), plainView.Setting);
        Assert.IsTrue(overriddenView.Setting.Overridden);
    }

    [TestMethod]
    public async Task GetAsync_AnUnreadableSettingsFile_IsReportedNotHiddenAsync()
    {
        using var settings = new TemporarySettingsFile();
        await File.WriteAllTextAsync(settings.Path, "{ \"CameraAgent\": ").ConfigureAwait(false);
        var service = CreateService(new StubMonitor(), read: true, mutate: true, settingsFile: settings.File);

        var view = (await service.GetAsync(CancellationToken.None).ConfigureAwait(false)).Value!;

        Assert.IsTrue(view.Setting.Unreadable);
    }

    [TestMethod]
    public async Task GetAsync_WhenTheMonitorFails_ReturnsAFixedMessageAsync()
    {
        var service = CreateService(new StubMonitor { ThrowOnRead = true }, read: true, mutate: true);

        var result = await service.GetAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("The clock state could not be read.", result.Message);
    }

    [TestMethod]
    public async Task CheckNowAsync_WhenTheMutatePolicyFails_RunsNoCheckAsync()
    {
        var monitor = new StubMonitor();
        var service = CreateService(monitor, read: true, mutate: false);

        var result = await service.CheckNowAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.AreEqual(0, monitor.Checks);
    }

    [TestMethod]
    public async Task CheckNowAsync_WhenCheckingIsOff_SaysSoAndRunsNoCheckAsync()
    {
        var monitor = new StubMonitor { Settings = TimeSyncSettings.Default with { Enabled = false } };
        var service = CreateService(monitor, read: true, mutate: true);

        var result = await service.CheckNowAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
        Assert.AreEqual("Clock checking is turned off in this agent's settings.", result.Message);
        Assert.AreEqual(0, monitor.Checks);
    }

    [TestMethod]
    [DataRow(ClockCheckOutcome.Measured)]
    [DataRow(ClockCheckOutcome.Joined)]
    [DataRow(ClockCheckOutcome.RateLimited)]
    public async Task CheckNowAsync_ReportsHowTheRequestWasAnsweredAsync(ClockCheckOutcome outcome)
    {
        DateTimeOffset? retryAfter = outcome == ClockCheckOutcome.RateLimited ? Now.AddSeconds(12) : null;
        var monitor = new StubMonitor { CheckResult = new ClockCheckResult(outcome, null, retryAfter) };
        var service = CreateService(monitor, read: true, mutate: true);

        var result = await service.CheckNowAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(new TimeSyncCheckView(outcome, retryAfter), result.Value);
        Assert.AreEqual(1, monitor.Checks);
    }

    [TestMethod]
    public async Task CheckNowAsync_WhenTheMonitorFails_ReturnsAFixedMessageAsync()
    {
        var service = CreateService(new StubMonitor { ThrowOnCheck = true }, read: true, mutate: true);

        var result = await service.CheckNowAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("The clock check could not be run.", result.Message);
    }

    [TestMethod]
    public async Task SaveServersAsync_WhenTheMutatePolicyFails_DeniesWithoutWritingAsync()
    {
        using var settings = new TemporarySettingsFile();
        var service = CreateService(new StubMonitor(), read: true, mutate: false, settingsFile: settings.File);

        var result = await service.SaveServersAsync(["a.example"], OperatorSettingsFile.AbsentVersion, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        Assert.IsFalse(File.Exists(settings.Path));
    }

    [TestMethod]
    public async Task SaveServersAsync_WritesTheListAndThenRemovesItForTheDefaultAsync()
    {
        using var settings = new TemporarySettingsFile();
        var service = CreateService(new StubMonitor(), read: true, mutate: true, settingsFile: settings.File);

        var saved = await service.SaveServersAsync(
            ["time.example.org", "192.168.1.10:123"], OperatorSettingsFile.AbsentVersion, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, saved.Kind);
        string[] expectedServers = ["time.example.org", "192.168.1.10:123"];
        CollectionAssert.AreEqual(expectedServers, saved.Value!.Servers.ToArray());
        Assert.IsFalse(saved.Value.UsingDefault);
        Assert.AreNotEqual(OperatorSettingsFile.AbsentVersion, saved.Value.Version);
        Assert.AreEqual(1, settings.Reloads);
        Assert.Contains(
            "\"Servers\": \"time.example.org, 192.168.1.10:123\"",
            await File.ReadAllTextAsync(settings.Path).ConfigureAwait(false),
            StringComparison.Ordinal);

        var cleared = await service.SaveServersAsync([], saved.Value.Version!, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Success, cleared.Kind);
        Assert.IsTrue(cleared.Value!.UsingDefault);
        CollectionAssert.AreEqual(TimeSyncSettings.DefaultServers.ToArray(), cleared.Value.Servers.ToArray());
        Assert.AreEqual("{}\n", await File.ReadAllTextAsync(settings.Path).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SaveServersAsync_WhenAnotherSourceSetsTheList_SaysSoAsync()
    {
        using var settings = new TemporarySettingsFile(overridden: [TimeSyncSettings.ServersKey]);
        var service = CreateService(new StubMonitor(), read: true, mutate: true, settingsFile: settings.File);

        var result = await service.SaveServersAsync(["a.example"], OperatorSettingsFile.AbsentVersion, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsTrue(result.Value!.Overridden);
    }

    [TestMethod]
    [DataRow("five", "List at most 4 time servers.")]
    [DataRow("url", "Each time server must be a host name or IP address")]
    [DataRow("credentials", "Each time server must be a host name or IP address")]
    [DataRow("duplicate", "Each time server can be listed only once.")]
    public async Task SaveServersAsync_AnInvalidList_WritesNothingAsync(string scenario, string expected)
    {
        using var settings = new TemporarySettingsFile();
        var service = CreateService(new StubMonitor(), read: true, mutate: true, settingsFile: settings.File);
        IReadOnlyList<string> servers = scenario switch
        {
            "five" => ["a.example", "b.example", "c.example", "d.example", "e.example"],
            "url" => ["a.example", "ntp://b.example"],
            "credentials" => ["user:secret@b.example"],
            _ => ["a.example", "A.EXAMPLE"],
        };

        var result = await service.SaveServersAsync(servers, OperatorSettingsFile.AbsentVersion, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
        Assert.Contains(expected, result.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.Message!, StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(settings.Path));
    }

    [TestMethod]
    [DataRow("stale", "changed since this page was read", "Conflict")]
    [DataRow("blank-version", "changed since this page was read", "Conflict")]
    [DataRow("unreadable", "not valid settings JSON", "Invalid")]
    [DataRow("no-file", "loads no operator settings file", "Unavailable")]
    public async Task SaveServersAsync_WhenTheFileCannotTakeTheEdit_LeavesItAsItIsAsync(
        string scenario,
        string expected,
        string kind)
    {
        using var settings = new TemporarySettingsFile();
        const string content = "{ \"CameraAgent\": ";
        await File.WriteAllTextAsync(settings.Path, scenario == "unreadable" ? content : "{}").ConfigureAwait(false);
        var version = (await settings.File.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Version;
        var service = CreateService(
            new StubMonitor(), read: true, mutate: true, settingsFile: scenario == "no-file" ? null : settings.File);

        var result = await service.SaveServersAsync(
            ["a.example"],
            scenario switch
            {
                "stale" => OperatorSettingsFile.AbsentVersion,
                "blank-version" => " ",
                _ => version,
            },
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(Enum.Parse<OperatorUiResultKind>(kind), result.Kind);
        Assert.Contains(expected, result.Message!, StringComparison.Ordinal);
        Assert.AreEqual(scenario == "unreadable" ? content : "{}", await File.ReadAllTextAsync(settings.Path).ConfigureAwait(false));
        Assert.AreEqual(0, settings.Reloads);
    }

    [TestMethod]
    public void ValidateServers_AcceptsUpToFourDistinctServers()
    {
        Assert.IsNull(CameraAgentTimeSyncUiService.ValidateServers([]));
        Assert.IsNull(CameraAgentTimeSyncUiService.ValidateServers(["a.example", "b.example:1123", "10.0.0.1", "[::1]"]));
    }

    private static CameraAgentTimeSyncUiService CreateService(
        StubMonitor monitor,
        bool read,
        bool mutate,
        IDeploymentLocationStore? locations = null,
        OperatorSettingsFile? settingsFile = null)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "owner-id"),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ],
            IdentityConstants.ApplicationScheme));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization
            .Setup(service => service.AuthorizeAsync(user, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(read ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        authorization
            .Setup(service => service.AuthorizeAsync(user, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(mutate ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return new CameraAgentTimeSyncUiService(
            new StubAuthenticationStateProvider(user),
            authorization.Object,
            monitor,
            new FixedTimeProvider(Now),
            NullLogger<CameraAgentTimeSyncUiService>.Instance,
            locations,
            settingsFile);
    }

    private sealed class StubMonitor : IClockSyncMonitor
    {
        private readonly TimeSyncSettings _settings = TimeSyncSettings.Default;

        internal bool ThrowOnRead { get; init; }

        internal bool ThrowOnCheck { get; init; }

        internal ClockCheckResult CheckResult { get; init; } = new(ClockCheckOutcome.Measured, null, null);

        internal int Checks { get; private set; }

        public TimeSyncSettings Settings
        {
            get => ThrowOnRead ? throw new InvalidOperationException("fixture failure") : _settings;
            init => _settings = value;
        }

        public ClockSyncSnapshot? Latest { get; init; }

        public Task<ClockCheckResult> CheckNowAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return ThrowOnCheck
                ? Task.FromException<ClockCheckResult>(new InvalidOperationException("fixture failure"))
                : Task.FromResult(CheckResult);
        }
    }

    /// <summary>An operator settings file in its own temporary directory.</summary>
    private sealed class TemporarySettingsFile : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "hvo-time-sync-ui-settings", Guid.NewGuid().ToString("N"));

        internal TemporarySettingsFile(IReadOnlyList<string>? overridden = null)
        {
            Directory.CreateDirectory(_root);
            Path = System.IO.Path.Combine(_root, "appsettings.local.json");
            File = new OperatorSettingsFile(
                Path,
                () => Reloads++,
                keys => keys.Where(key => overridden?.Contains(key) == true).ToArray());
        }

        internal string Path { get; }

        internal OperatorSettingsFile File { get; }

        internal int Reloads { get; private set; }

        public void Dispose()
        {
            File.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class StubAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
