using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class SkyMapPageTests
{
    private static readonly DateTimeOffset Instant = new(2026, 3, 1, 4, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Render_ShowsCatalogIdentityOwnerEditableCoordinatesGeometryAndTheBoundedScene()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(
            new SkyMapUiService(SkyMapTestData.Result(Instant), manual: ManualState()));

        var cut = context.Render<SkyMapPage>();

        cut.WaitForElement("#sky-observer");
        Assert.AreEqual("Sky map & catalog", cut.Find("h1").TextContent.Trim());
        Assert.AreEqual("Observer location", cut.Find("#sky-observer").TextContent.Trim());
        Assert.AreEqual("Catalog identity", cut.Find("#sky-catalog").TextContent.Trim());
        Assert.AreEqual("Edit observer coordinates", cut.Find("#sky-observer-edit").TextContent.Trim());
        Assert.Contains("Owner editable", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("next CameraAgent start", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("keeps version 3", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("America/Phoenix", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("hvo-hyg-v3", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Sirius", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Lyr", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("2 / 200", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("carries scene provenance", cut.Markup, StringComparison.Ordinal);
        // The entry fields are seeded from durable state rather than from anything the browser knows.
        Assert.AreEqual("35.200000", cut.Find("#sky-map-latitude").GetAttribute("value"));
        Assert.AreEqual("-111.650000", cut.Find("#sky-map-longitude").GetAttribute("value"));
        Assert.AreEqual("2100.00", cut.Find("#sky-map-elevation").GetAttribute("value"));
        Assert.AreEqual("America/Phoenix", cut.Find("#sky-map-time-zone").GetAttribute("value"));
        // Nothing on this page reaches for a map tile, a script, or any remote asset.
        Assert.IsEmpty(cut.FindAll("img"));
        Assert.IsEmpty(cut.FindAll("iframe"));
        Assert.IsEmpty(cut.FindAll("script"));
        Assert.HasCount(1, cut.FindAll("svg.sky-dial"));
        Assert.HasCount(2, cut.FindAll("svg.sky-dial circle.sky-dial__object"));
    }

    [TestMethod]
    public void Render_WhenTheManualContractIsUnsupported_KeepsCoordinatesReadOnly()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(
            new SkyMapUiService(SkyMapTestData.Result(Instant), manual: null));

        var cut = context.Render<SkyMapPage>();

        cut.WaitForElement("#sky-observer");
        Assert.Contains("Read only", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("does not expose the audited local mutation contract", cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("#sky-map-latitude"));
        Assert.IsEmpty(cut.FindAll($"#{SkyMapPage.EditTriggerId}"));
    }

    [TestMethod]
    public void Render_WhenUnauthorized_NavigatesToAccessDenied()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SkyMapUiService(null));
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        _ = context.Render<SkyMapPage>();

        Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Render_WhenTheRequestedInstantIsRejected_ShowsTheBoundedWindowNotice()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SkyMapUiService(
            null,
            OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                OperatorUiResultKind.Invalid, CameraAgentSkyMapInstantBounds.RejectionMessage)));

        var cut = context.Render<SkyMapPage>();

        Assert.Contains("Instant outside the accepted window", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(CameraAgentSkyMapInstantBounds.RejectionMessage, cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("#sky-observer"));
    }

    [TestMethod]
    public void Render_WhenTheProjectionIsUnavailable_ShowsTheErrorStateWithRetry()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SkyMapUiService(
            null,
            OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                OperatorUiResultKind.Unavailable, "The sky map projection is unavailable.")));

        var cut = context.Render<SkyMapPage>();

        Assert.Contains("Sky map unavailable", cut.Markup, StringComparison.Ordinal);
        Assert.IsNotNull(cut.FindAll("button").SingleOrDefault(static button =>
            button.TextContent.Contains("Try again", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SaveCoordinates_RequiresConfirmationBeforeAnyDurableCommand()
    {
        using var context = CreateContext();
        var service = new SkyMapUiService(
            SkyMapTestData.Result(Instant), manual: ManualState(knownVersion: 4, activeVersion: 3));
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<SkyMapPage>();
        cut.WaitForElement("#sky-map-latitude");

        cut.Find("#sky-map-latitude").Change("31.500000");
        cut.Find($"#{SkyMapPage.EditTriggerId}").Click();

        Assert.HasCount(1, cut.FindAll("dialog.confirmation-panel"));
        // The dialog names the version a capture keeps, which is the active one, not the expected token.
        Assert.Contains("Append manual version 5?", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("keep version 3", cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(service.Commands);

        cut.FindAll("dialog.confirmation-panel button")
            .Single(static button => button.TextContent.Contains("Cancel", StringComparison.Ordinal))
            .Click();

        Assert.IsEmpty(cut.FindAll("dialog.confirmation-panel"));
        Assert.IsEmpty(service.Commands);
    }

    [TestMethod]
    public void SaveCoordinates_AppliesTheEnteredValuesAndStatesWhichCapturesKeepWhichVersion()
    {
        using var context = CreateContext();
        var applied = ManualState(
            knownVersion: 4,
            activeVersion: 3,
            manualSequence: 1,
            pending: new ManualDeploymentLocationOverride(
                31.5, -110.25, 1400, "America/Phoenix", PendingRestart: true, Instant, "owner-1", "relocated"));
        var service = new SkyMapUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(),
            applyResults:
            [
                OperatorUiResult<ManualDeploymentLocationResult>.Success(new ManualDeploymentLocationResult(
                    ManualDeploymentLocationStatus.Applied, null, null, applied))
            ]);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<SkyMapPage>();
        cut.WaitForElement("#sky-map-latitude");

        cut.Find("#sky-map-latitude").Change("31.500000");
        cut.Find("#sky-map-longitude").Change("-110.250000");
        cut.Find("#sky-map-elevation").Change("1400.00");
        cut.Find("#sky-map-reason").Change("relocated");
        cut.Find($"#{SkyMapPage.EditTriggerId}").Click();
        cut.FindAll("dialog.confirmation-panel button")
            .Single(static button => button.TextContent.Contains("Confirm change", StringComparison.Ordinal))
            .Click();

        var command = service.Commands.Single();
        Assert.AreEqual(31.5, command.LatitudeDegrees);
        Assert.AreEqual(-110.25, command.LongitudeDegrees);
        Assert.AreEqual(1400d, command.ElevationMeters);
        Assert.AreEqual("America/Phoenix", command.TimeZoneId);
        Assert.AreEqual(3L, command.ExpectedVersion);
        Assert.AreEqual("relocated", command.Reason);
        Assert.IsFalse(string.IsNullOrWhiteSpace(command.IdempotencyKey));
        Assert.Contains("appends it as deployment version 5", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("captures already recorded keep version 3", cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("dialog.confirmation-panel"));
    }

    [TestMethod]
    public void SaveRetry_AfterUnavailable_ReusesTheIdempotencyKeyAndExpectedVersion()
    {
        using var context = CreateContext();
        var service = new SkyMapUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(),
            applyResults:
            [
                OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Unavailable, "The coordinate change could not be completed.")
            ]);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<SkyMapPage>();
        cut.WaitForElement("#sky-map-latitude");
        cut.Find("#sky-map-latitude").Change("31.500000");
        cut.Find($"#{SkyMapPage.EditTriggerId}").Click();

        cut.FindAll("dialog.confirmation-panel button")
            .Single(static button => button.TextContent.Contains("Confirm change", StringComparison.Ordinal))
            .Click();
        cut.FindAll("dialog.confirmation-panel button")
            .Single(static button => button.TextContent.Contains("Confirm change", StringComparison.Ordinal))
            .Click();

        Assert.HasCount(2, service.Commands);
        Assert.AreEqual(service.Commands[0].IdempotencyKey, service.Commands[1].IdempotencyKey);
        Assert.AreEqual(service.Commands[0].ExpectedVersion, service.Commands[1].ExpectedVersion);
        // An unavailable command keeps the confirmation open so the same command can be retried.
        Assert.HasCount(1, cut.FindAll("dialog.confirmation-panel"));
    }

    [TestMethod]
    public void SaveConflict_ReReadsDurableStateAndReportsTheExpectedVersionConflict()
    {
        using var context = CreateContext();
        var service = new SkyMapUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(),
            applyResults:
            [
                OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Conflict,
                    "The deployment location changed since this page was read. Refresh before retrying.")
            ]);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<SkyMapPage>();
        cut.WaitForElement("#sky-map-latitude");
        cut.Find("#sky-map-latitude").Change("31.500000");
        cut.Find($"#{SkyMapPage.EditTriggerId}").Click();
        var readsBeforeConfirm = service.ManualReads;

        cut.FindAll("dialog.confirmation-panel button")
            .Single(static button => button.TextContent.Contains("Confirm change", StringComparison.Ordinal))
            .Click();

        Assert.Contains("Refresh before retrying", cut.Markup, StringComparison.Ordinal);
        Assert.IsTrue(service.ManualReads > readsBeforeConfirm);
        Assert.IsEmpty(cut.FindAll("dialog.confirmation-panel"));
    }

    [TestMethod]
    public void SaveCoordinates_WithAnUnparsableValue_BlocksTheCommandWithLabelledGuidance()
    {
        using var context = CreateContext();
        var service = new SkyMapUiService(SkyMapTestData.Result(Instant), manual: ManualState());
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<SkyMapPage>();
        cut.WaitForElement("#sky-map-latitude");

        cut.Find("#sky-map-latitude").Change("not-a-number");
        cut.Find($"#{SkyMapPage.EditTriggerId}").Click();

        Assert.IsEmpty(cut.FindAll("dialog.confirmation-panel"));
        Assert.IsEmpty(service.Commands);
        Assert.Contains(
            "Latitude must be a number between -90 and 90 degrees.", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_WithCentralIntegrationStagedAndSupersededState_StatesEachOneTruthfully()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SkyMapUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(
                knownVersion: 4,
                activeVersion: 3,
                pendingVersion: 4,
                manualSequence: 2,
                centralAcknowledgementRequired: true,
                stagedAcknowledgementPending: true,
                candidateAwaitingAcknowledgement: true,
                pending: new ManualDeploymentLocationOverride(
                    31.5, -110.25, 1400, "America/Phoenix", PendingRestart: true, Instant, "owner-1", "moved"),
                history:
                [
                    new ManualDeploymentLocationAuditEntry(
                        2, Instant, "owner-1", "moved", "key-2", 3, 31.5, -110.25, 1400, "America/Phoenix"),
                    new ManualDeploymentLocationAuditEntry(
                        1, Instant.AddDays(-2), "owner-1", null, "key-1", 2, 30, -110, 1200, "UTC")
                ])));

        var cut = context.Render<SkyMapPage>();

        cut.WaitForElement("#sky-map-latitude");
        Assert.Contains("proposed to LogicHost first", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("A central acknowledgement is already staged", cut.Markup, StringComparison.Ordinal);
        // The pending row names the version the candidate already occupies, not the next one.
        Assert.Contains(
            "Version 4 entered", cut.Markup, StringComparison.Ordinal);
        // The staged snapshot has already been acknowledged, so the row must not still say it is awaited.
        Assert.Contains("is acknowledged and activates at the next start", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("awaits central acknowledgement", cut.Markup, StringComparison.Ordinal);
        // Captures keep the active version, never the candidate or staged one.
        Assert.Contains("keeps version 3", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Expected version 4", cut.Markup, StringComparison.Ordinal);
        Assert.HasCount(2, cut.FindAll("table.facts-table tbody tr th[scope='row']")
            .Where(static cell => cell.TextContent.Contains("2026", StringComparison.Ordinal))
            .ToArray());
        Assert.Contains("owner-1", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Not recorded", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_WhenAConfigurationChangeSupersededTheEntry_SaysConfiguredCoordinatesGovern()
    {
        using var context = CreateContext();
        // A superseded record no longer governs, so the projection reports no override alongside it.
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SkyMapUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(
                manualSequence: 0,
                pending: null,
                supersededAtUtc: Instant.AddDays(-1),
                history:
                [
                    new ManualDeploymentLocationAuditEntry(
                        1, Instant.AddDays(-2), "owner-1", "moved", "key-1", 2, 30, -110, 1200, "UTC")
                ])));

        var cut = context.Render<SkyMapPage>();

        cut.WaitForElement("#sky-map-latitude");
        Assert.Contains("was superseded at", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(
            "Configured coordinates currently govern this deployment", cut.Markup, StringComparison.Ordinal);
        // No override governs, so the observer card must not advertise a pending manual version.
        var pendingRow = cut.FindAll("dl.fact-grid > div")
            .Single(static row => row.QuerySelector("dt")!.TextContent == "Pending manual version");
        Assert.AreEqual("None", pendingRow.QuerySelector("dd")!.TextContent);
    }

    [TestMethod]
    public void Render_WithACandidateNotYetAcknowledged_SaysThePendingVersionAwaitsAcknowledgement()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(new SkyMapUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(
                knownVersion: 4,
                activeVersion: 3,
                pendingVersion: 4,
                manualSequence: 1,
                centralAcknowledgementRequired: true,
                candidateAwaitingAcknowledgement: true,
                pending: new ManualDeploymentLocationOverride(
                    31.5, -110.25, 1400, "America/Phoenix", PendingRestart: true, Instant, "owner-1", "moved"))));

        var cut = context.Render<SkyMapPage>();

        cut.WaitForElement("#sky-map-latitude");
        Assert.Contains(
            "Version 4 entered 2026-03-01 04:00:00Z awaits central acknowledgement, then a restart",
            cut.Markup,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void SaveRetry_AfterEditingTheValues_MintsANewKeyAndReReadsTheExpectedTokens()
    {
        using var context = CreateContext();
        var service = new SkyMapUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(manualSequence: 4),
            applyResults:
            [
                OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Unavailable, "The coordinate change could not be completed.")
            ]);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<SkyMapPage>();
        cut.WaitForElement("#sky-map-latitude");
        cut.Find("#sky-map-latitude").Change("31.500000");
        cut.Find($"#{SkyMapPage.EditTriggerId}").Click();
        cut.FindAll("dialog.confirmation-panel button")
            .Single(static button => button.TextContent.Contains("Confirm change", StringComparison.Ordinal))
            .Click();

        // Editing the payload makes it a different command, so it must not reuse the previous key.
        cut.Find("#sky-map-longitude").Change("-110.250000");
        cut.FindAll("dialog.confirmation-panel button")
            .Single(static button => button.TextContent.Contains("Confirm change", StringComparison.Ordinal))
            .Click();

        Assert.HasCount(2, service.Commands);
        Assert.AreNotEqual(service.Commands[0].IdempotencyKey, service.Commands[1].IdempotencyKey);
        // An unavailable outcome must not re-read, so both commands carry the tokens from the first read.
        Assert.AreEqual(4L, service.Commands[0].ExpectedManualSequence);
        Assert.AreEqual(4L, service.Commands[1].ExpectedManualSequence);
        Assert.AreEqual(service.Commands[0].ExpectedVersion, service.Commands[1].ExpectedVersion);
        Assert.AreEqual(1, service.ManualReads);
        Assert.AreEqual(-111.65, service.Commands[0].LongitudeDegrees);
        Assert.AreEqual(-110.25, service.Commands[1].LongitudeDegrees);
    }

    [TestMethod]
    public void SaveCoordinates_WhenTheCommandIsUnavailable_AnnouncesInsideTheOpenDialog()
    {
        using var context = CreateContext();
        var service = new SkyMapUiService(
            SkyMapTestData.Result(Instant),
            manual: ManualState(),
            applyResults:
            [
                OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Unavailable, "The coordinate change could not be completed.")
            ]);
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<SkyMapPage>();
        cut.WaitForElement("#sky-map-latitude");
        cut.Find("#sky-map-latitude").Change("31.500000");
        cut.Find($"#{SkyMapPage.EditTriggerId}").Click();

        cut.FindAll("dialog.confirmation-panel button")
            .Single(static button => button.TextContent.Contains("Confirm change", StringComparison.Ordinal))
            .Click();

        // A modal dialog makes the rest of the document inert, so the outcome has to be announced inside it.
        var alert = cut.Find("dialog.confirmation-panel p[role='alert']");
        Assert.Contains("could not be completed", alert.TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void SaveCoordinates_WithAnOutOfRangeValue_BlocksTheCommandWithTheAdvertisedBounds()
    {
        using var context = CreateContext();
        var service = new SkyMapUiService(SkyMapTestData.Result(Instant), manual: ManualState());
        context.Services.AddSingleton<ICameraAgentSkyMapUiService>(service);
        var cut = context.Render<SkyMapPage>();
        cut.WaitForElement("#sky-map-latitude");

        cut.Find("#sky-map-latitude").Change("500");
        cut.Find($"#{SkyMapPage.EditTriggerId}").Click();

        Assert.IsEmpty(cut.FindAll("dialog.confirmation-panel"));
        Assert.IsEmpty(service.Commands);
        Assert.Contains(
            "Latitude must be a number between -90 and 90 degrees.", cut.Markup, StringComparison.Ordinal);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static ManualDeploymentLocationState ManualState(
        long knownVersion = 3,
        long? activeVersion = null,
        long? pendingVersion = null,
        long manualSequence = 0,
        bool centralAcknowledgementRequired = false,
        bool stagedAcknowledgementPending = false,
        bool candidateAwaitingAcknowledgement = false,
        ManualDeploymentLocationOverride? pending = null,
        DateTimeOffset? supersededAtUtc = null,
        IReadOnlyList<ManualDeploymentLocationAuditEntry>? history = null)
        => new(
            Supported: true,
            LocationId: "hvo-observatory",
            ActiveVersion: activeVersion ?? knownVersion,
            KnownVersion: knownVersion,
            NextVersion: knownVersion + 1,
            PendingVersion: pendingVersion,
            ManualSequence: manualSequence,
            CentralAcknowledgementRequired: centralAcknowledgementRequired,
            StagedAcknowledgementPending: stagedAcknowledgementPending,
            CandidateAwaitingAcknowledgement: candidateAwaitingAcknowledgement,
            Override: pending,
            OverrideSupersededAtUtc: supersededAtUtc,
            History: history ?? []);

    internal sealed class SkyMapUiService(
        CameraAgentSkyMapProjectionResult? state,
        OperatorUiResult<CameraAgentSkyMapProjectionResult>? failure = null,
        ManualDeploymentLocationState? manual = null,
        IReadOnlyList<OperatorUiResult<ManualDeploymentLocationResult>>? applyResults = null)
        : ICameraAgentSkyMapUiService
    {
        private int _applyIndex;

        internal List<ManualCommand> Commands { get; } = [];

        internal int ManualReads { get; private set; }

        public ValueTask<OperatorUiResult<CameraAgentSkyMapProjectionResult>> GetSkyMapAsync(
            DateTimeOffset? atUtc,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(state is null
                ? failure ?? OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                    OperatorUiResultKind.Unauthorized, "Authorization is required.")
                : OperatorUiResult<CameraAgentSkyMapProjectionResult>.Success(state));

        public ValueTask<OperatorUiResult<ManualDeploymentLocationState>> GetManualLocationAsync(
            CancellationToken cancellationToken)
        {
            ManualReads++;
            return ValueTask.FromResult(manual is null
                ? OperatorUiResult<ManualDeploymentLocationState>.Failure(
                    OperatorUiResultKind.Unavailable, "Manual deployment-location state is unavailable.")
                : OperatorUiResult<ManualDeploymentLocationState>.Success(manual));
        }

        public ValueTask<OperatorUiResult<ManualDeploymentLocationResult>> ApplyManualLocationAsync(
            double latitudeDegrees,
            double longitudeDegrees,
            double elevationMeters,
            string timeZoneId,
            long expectedVersion,
            long expectedManualSequence,
            string idempotencyKey,
            string? reason,
            CancellationToken cancellationToken)
        {
            Commands.Add(new ManualCommand(
                latitudeDegrees,
                longitudeDegrees,
                elevationMeters,
                timeZoneId,
                expectedVersion,
                expectedManualSequence,
                idempotencyKey,
                reason));
            var results = applyResults ?? [];
            var result = results.Count == 0
                ? OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Unavailable, "The coordinate change could not be completed.")
                : results[Math.Min(_applyIndex, results.Count - 1)];
            _applyIndex++;
            return ValueTask.FromResult(result);
        }

        internal sealed record ManualCommand(
            double LatitudeDegrees,
            double LongitudeDegrees,
            double ElevationMeters,
            string TimeZoneId,
            long ExpectedVersion,
            long ExpectedManualSequence,
            string IdempotencyKey,
            string? Reason);
    }
}
