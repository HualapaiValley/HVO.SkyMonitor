using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class SchedulePageTests
{
    private static readonly string[] RawDependency = ["$raw"];

    private static BunitContext CreateContext(NamedRigSelection? selection = null, bool unauthorized = false,
        Mock<ICameraAgentNamedRigUiService>? rig = null, TimeProvider? time = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(time ?? TimeProvider.System);
        rig ??= new Mock<ICameraAgentNamedRigUiService>();
        rig.Setup(service => service.GetAsync(It.IsAny<CancellationToken>())).Returns(() =>
            ValueTask.FromResult(unauthorized
                ? OperatorUiResult<NamedRigUiCatalog>.Failure(OperatorUiResultKind.Unauthorized, "Denied")
                : OperatorUiResult<NamedRigUiCatalog>.Success(new(
                    selection ?? new NamedRigSelection(null, null, 1), [], null, null))));
        context.Services.AddSingleton(rig.Object);
        return context;
    }

    private static void OpenEditor(IRenderedComponent<SchedulePage> cut)
        => cut.Find("#schedule-edit-open").Click();

    private static AngleSharp.Dom.IElement SaveDraft(IRenderedComponent<SchedulePage> cut)
        => cut.FindAll("button").Single(button => button.TextContent.Contains("Save draft", StringComparison.Ordinal));

    [TestMethod]
    public void Render_ShowsDurableStateEditorPreviewAndRollbackHistory()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new ScheduleUiService(State())
        {
            Calendar = Calendar("UTC", new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))
        });

        var cut = context.Render<SchedulePage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Schedule", cut.Find("#schedule-heading").TextContent);
            StringAssert.Contains(cut.Find(".schedule-state").TextContent, "Revision 2 is open", StringComparison.Ordinal);
            Assert.IsTrue(cut.Markup.Contains("Revision history", StringComparison.Ordinal));
            Assert.AreEqual("night", cut.Find(".ops-regime-card.active h4").TextContent);
            Assert.HasCount(4, cut.FindAll(".ops-progress-step"));
            Assert.IsEmpty(cut.FindAll("dialog"));
        });

        OpenEditor(cut);
        var editor = cut.Find("dialog.schedule-editor");
        StringAssert.Contains(editor.TextContent, "Schedule draft / basis revision 2", StringComparison.Ordinal);
        foreach (var text in new[] { "Setpoint profiles", "Weekly windows", "Desired and effective graph", "Preview / required", "Advanced canonical JSON" })
        {
            StringAssert.Contains(editor.TextContent, text, StringComparison.Ordinal);
        }
        Assert.AreEqual("step", cut.Find(".schedule-steps button[aria-current]").GetAttribute("aria-current"));
        StringAssert.Contains(cut.Find(".schedule-steps button[aria-current]").TextContent, "Capture policy", StringComparison.Ordinal);
        cut.FindAll("dialog footer button").Single(button => button.TextContent == "Next").Click();
        StringAssert.Contains(cut.Find(".schedule-steps button[aria-current]").TextContent, "Setpoints", StringComparison.Ordinal);
        Assert.IsFalse(cut.Find("section[aria-labelledby='schedule-step-setpoints']").HasAttribute("hidden"));
        Assert.IsTrue(cut.Find("section[aria-labelledby='schedule-step-policy']").HasAttribute("hidden"));

        cut.Find("dialog .icon-button[aria-label='Close dialog']").Click();
        Assert.IsEmpty(cut.FindAll("dialog"));
        cut.Find("#schedule-override-open").Click();
        StringAssert.Contains(cut.Find("dialog").TextContent, "Create override", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Calendar_DrawsTonightAndTheWeekFromResolvedSegments()
    {
        using var context = CreateContext();
        var noon = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var nights = Enumerable.Range(0, 7).Select(index =>
        {
            var start = noon.AddDays(index);
            CameraAgentScheduleSegment[] segments = index == 1
                ? [new(start, start.AddDays(1), false, CaptureScheduleAdmissionReason.Blackout, null, null)]
                :
                [
                    new(start, start.AddHours(8), false, CaptureScheduleAdmissionReason.DefaultClosed, null, null),
                    new(start.AddHours(8), start.AddHours(16), true, CaptureScheduleAdmissionReason.WeeklyWindow, "night", null),
                    new(start.AddHours(16), start.AddDays(1), false, CaptureScheduleAdmissionReason.DefaultClosed, null, null),
                ];
            return new CameraAgentScheduleNight(DateOnly.FromDateTime(start.UtcDateTime), start, start.AddDays(1), segments);
        }).ToArray();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new ScheduleUiService(State())
        {
            Calendar = new CameraAgentScheduleCalendar("UTC", noon.AddHours(9), nights)
        });

        var cut = context.Render<SchedulePage>();

        cut.WaitForAssertion(() =>
        {
            var clock = cut.Find(".schedule-clock");
            Assert.HasCount(1, clock.QuerySelectorAll(".schedule-segment.open"));
            StringAssert.Contains(clock.QuerySelector(".schedule-segment.open")!.GetAttribute("style")!, "left: 33.3", StringComparison.Ordinal);
            StringAssert.Contains(clock.QuerySelector(".schedule-now")!.GetAttribute("style")!, "left: 37.5", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".schedule-window-list").TextContent, "20:00 to 04:00", StringComparison.Ordinal);
            Assert.HasCount(7, cut.FindAll(".ops-week-row"));
            Assert.HasCount(1, cut.FindAll(".ops-week-row .schedule-segment.blackout"));
            StringAssert.Contains(cut.Find("#schedule-week-heading").ParentElement!.ParentElement!.TextContent, "6 of 7 open", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void OverrideDialog_ConvertsLocalTimesAndRejectsAnEndBeforeTheStart()
    {
        using var context = CreateContext();
        var service = new ScheduleUiService(State())
        {
            Calendar = Calendar("UTC", new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))
        };
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();

        cut.Find("#schedule-override-open").Click();
        // Blazor's event normalization appends the seconds a step=60 browser input omits; bUnit bypasses it.
        cut.FindAll("dialog input[type='datetime-local']")[0].Change("2026-10-01T21:15:00");
        cut.FindAll("dialog input[type='datetime-local']")[1].Change("2026-10-01T20:00:00");
        cut.FindAll("dialog button").Single(button => button.TextContent == "Create override").Click();

        Assert.IsEmpty(service.OverrideCommands);
        StringAssert.Contains(cut.Find("dialog .schedule-message[role='alert']").TextContent, "The override must end after it starts.", StringComparison.Ordinal);

        cut.FindAll("dialog input[type='datetime-local']")[1].Change("2026-10-01T23:45:00");
        cut.Find("#schedule-override-mode").Change("ForceOpen");
        cut.FindAll("dialog button").Single(button => button.TextContent == "Create override").Click();

        var command = service.OverrideCommands.Single();
        Assert.AreEqual(CaptureScheduleOverrideMode.ForceOpen, command.Mode);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 21, 15, 0, TimeSpan.Zero), command.StartUtc);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 23, 45, 0, TimeSpan.Zero), command.EndUtc);
        Assert.AreEqual("night", command.SetpointProfileId);
        Assert.AreEqual(State().ActiveRevision.ScheduleSha256, command.ScheduleRevisionSha256);
        Assert.HasCount(1, cut.FindAll("dialog"));
    }

    [TestMethod]
    public void OverrideDialog_IsUnavailableUntilTheSiteTimezoneIsKnown()
    {
        using var context = CreateContext();
        var service = new ScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);

        var cut = context.Render<SchedulePage>();

        cut.WaitForAssertion(() =>
        {
            var open = cut.Find("#schedule-override-open");
            Assert.IsTrue(open.HasAttribute("disabled"));
            StringAssert.Contains(cut.Find("#" + open.GetAttribute("aria-describedby")).TextContent,
                "site timezone is unknown", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".schedule-state").TextContent, "UTC (site timezone unknown)", StringComparison.Ordinal);
        });
        Assert.IsEmpty(service.OverrideCommands);
    }

    [TestMethod]
    public void OverrideDialog_RejectsALocalTimeThatADaylightSavingChangeRepeats()
    {
        using var context = CreateContext();
        var service = new ScheduleUiService(State())
        {
            Calendar = Calendar("America/Denver", new DateTimeOffset(2026, 10, 31, 18, 0, 0, TimeSpan.Zero))
        };
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#schedule-override-open").HasAttribute("disabled")));
        cut.Find("#schedule-override-open").Click();
        // Denver falls back at 02:00 on 1 November 2026, so 01:30 occurs twice.
        cut.FindAll("dialog input[type='datetime-local']")[0].Change("2026-11-01T01:30:00");
        cut.FindAll("dialog input[type='datetime-local']")[1].Change("2026-11-01T04:00:00");
        cut.FindAll("dialog button").Single(button => button.TextContent == "Create override").Click();

        Assert.IsEmpty(service.OverrideCommands);
        StringAssert.Contains(cut.Find("dialog .schedule-message[role='alert']").TextContent, "occurs twice", StringComparison.Ordinal);

        cut.FindAll("dialog input[type='datetime-local']")[0].Change("2026-11-01T02:30:00");
        cut.FindAll("dialog button").Single(button => button.TextContent == "Create override").Click();

        var command = service.OverrideCommands.Single();
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero), command.StartUtc);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero), command.EndUtc);
    }

    [TestMethod]
    public void Calendar_RefreshesOnALongLivedPageSoTonightAdvancesAtTheNightBoundary()
    {
        var noon = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(noon.AddHours(23).AddMinutes(58));
        using var context = CreateContext(time: time);
        var service = new ScheduleUiService(State()) { Calendar = Calendar("UTC", noon, generatedUtc: time.GetUtcNow()) };
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Find("#schedule-tonight-heading").ParentElement!.ParentElement!.TextContent, "1 Oct", StringComparison.Ordinal));

        var first = time.Timers.Single();
        Assert.AreEqual(TimeSpan.FromMinutes(2), first.DueTime);
        time.Now = noon.AddDays(1).AddMinutes(1);
        service.Calendar = Calendar("UTC", noon.AddDays(1), generatedUtc: time.GetUtcNow());
        first.Fire();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(2, service.CalendarCalls);
            StringAssert.Contains(
                cut.Find("#schedule-tonight-heading").ParentElement!.ParentElement!.TextContent, "2 Oct", StringComparison.Ordinal);
            Assert.IsTrue(first.Disposed);
            Assert.AreEqual(TimeSpan.FromMinutes(5), time.Timers[^1].DueTime);
        });

        cut.Instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Assert.IsTrue(time.Timers[^1].Disposed);
        time.Timers[^1].Fire();
        Assert.AreEqual(2, service.CalendarCalls);
    }

    private static CameraAgentScheduleCalendar Calendar(string timeZoneId, DateTimeOffset firstNoon, DateTimeOffset? generatedUtc = null)
    {
        var nights = Enumerable.Range(0, 7).Select(index =>
        {
            var start = firstNoon.AddDays(index);
            CameraAgentScheduleSegment[] segments =
                [new(start, start.AddDays(1), false, CaptureScheduleAdmissionReason.DefaultClosed, null, null)];
            return new CameraAgentScheduleNight(DateOnly.FromDateTime(start.UtcDateTime), start, start.AddDays(1), segments);
        }).ToArray();
        return new CameraAgentScheduleCalendar(timeZoneId, generatedUtc ?? firstNoon, nights);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;

        internal List<ManualTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        internal TimeSpan DueTime { get; } = dueTime;

        internal bool Disposed { get; private set; }

        internal void Fire()
        {
            if (!Disposed)
            {
                callback(state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    public void EditBlackouts_OpensTheEditorOnTheBlackoutStep()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new ScheduleUiService(State()));
        var cut = context.Render<SchedulePage>();

        cut.Find("#schedule-exceptions-edit").Click();

        Assert.IsFalse(cut.Find("section[aria-labelledby='schedule-step-blackouts']").HasAttribute("hidden"));
        StringAssert.Contains(cut.Find(".schedule-steps button[aria-current]").TextContent, "Blackouts", StringComparison.Ordinal);
    }

    [TestMethod]
    public void WeeklyDayCheckbox_ExpandsCanonicalWindowsAndJsonEditsTakePrecedence()
    {
        using var context = CreateContext();
        var service = new RetryingScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);
        var friday = cut.FindAll(".window-days input[type=checkbox]")[5];
        friday.Change(true);
        SaveDraft(cut).Click();
        Assert.HasCount(1, service.StageCommands);
        var expanded = CameraAgentScheduleUiService.ParseProfile(service.StageCommands[0].Payload);
        Assert.HasCount(2, expanded.Schedule.WeeklyWindows);
        Assert.AreEqual(Profile().Schedule.WeeklyWindows[0].Id, expanded.Schedule.WeeklyWindows[0].Id);

        var json = CameraAgentScheduleUiService.SerializeProfile(Profile());
        cut.Find("textarea[aria-label='Local profile JSON']").Input(json);
        SaveDraft(cut).Click();
        Assert.HasCount(2, service.StageCommands);
        Assert.AreEqual(json, service.StageCommands[1].Payload);
    }

    [TestMethod]
    public void NewlySelectedDay_CanSplitBeforePreviewAndEditSeparately()
    {
        using var context = CreateContext();
        var service = new RetryingScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);

        cut.FindAll(".window-days input[type=checkbox]")[5].Change(true);
        cut.Find("button[aria-label^='Split Friday']").Click();
        Assert.HasCount(2, cut.FindAll(".window-item"));
        cut.FindAll(".window-item input[aria-label='Start local time']")[1].Change("18:30");
        SaveDraft(cut).Click();

        Assert.HasCount(1, service.StageCommands);
        var windows = CameraAgentScheduleUiService.ParseProfile(service.StageCommands[0].Payload).Schedule.WeeklyWindows;
        Assert.HasCount(2, windows);
        Assert.AreEqual(DayOfWeek.Friday, windows[1].Day);
        Assert.AreEqual(new TimeOnly(18, 30), windows[1].Start.LocalTime);
        Assert.AreEqual(Profile().Schedule.WeeklyWindows[0], windows[0]);
    }

    [TestMethod]
    public void UncheckingLastDay_ShowsValidationAndDoesNotStage()
    {
        using var context = CreateContext();
        var service = new RetryingScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);

        cut.FindAll(".window-days input[type=checkbox]")[4].Change(false);
        SaveDraft(cut).Click();

        Assert.IsTrue(cut.Markup.Contains("needs at least one day", StringComparison.Ordinal));
        Assert.HasCount(0, service.StageCommands);
    }

    [TestMethod]
    public void RemovingGroupedWindow_NamesAndRemovesEverySelectedDay()
    {
        using var context = CreateContext();
        var service = new RetryingScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);

        cut.FindAll(".window-days input[type=checkbox]")[5].Change(true);
        cut.Find("button[aria-label='Remove window for Thursday, Friday']").Click();
        Assert.HasCount(0, cut.FindAll(".window-item"));
        SaveDraft(cut).Click();

        Assert.HasCount(1, service.StageCommands);
        Assert.HasCount(0, CameraAgentScheduleUiService.ParseProfile(service.StageCommands[0].Payload).Schedule.WeeklyWindows);
    }

    [TestMethod]
    public void PipelineToggle_ChangesOnlyV2EnabledStateAndRejectsLegacyProfile()
    {
        var options = JsonSerializer.SerializeToElement(new { outputVariant = "display" });
        var profile = Profile() with
        {
            SchemaVersion = LocalCaptureProfileDefinition.CurrentSchemaVersion,
            DependencyPolicy = CapturePipelineDependencyPolicy.RejectEnabledDependent,
            ProcessingSteps =
            [
                new CaptureProcessingStepConfig(
                    "Preview", "preview", 10, options, RawDependency, Enabled: true)
            ]
        };

        var toggled = CameraAgentPipelineOperatorProjection.Toggle(profile, "preview", enabled: false);

        Assert.IsFalse(toggled.ProcessingSteps.Single().Enabled);
        Assert.AreEqual(options.GetRawText(), toggled.ProcessingSteps.Single().Options!.Value.GetRawText());
        CollectionAssert.AreEqual(RawDependency, toggled.ProcessingSteps.Single().DependsOn!.ToArray());
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            CameraAgentPipelineOperatorProjection.Toggle(Profile(), "preview", enabled: false));
        Assert.AreEqual(
            "Graph toggles require a cameraagent-local-profile-v2 revision.",
            CameraAgentPipelineOperatorProjection.SanitizeValidationFailure(exception));
    }

    [TestMethod]
    public void PipelineState_CanToggleOnlyStrictCanonicalV2Profile()
    {
        var baseline = Profile();
        var canonical = baseline with
        {
            SchemaVersion = LocalCaptureProfileDefinition.CurrentSchemaVersion,
            DependencyPolicy = CapturePipelineDependencyPolicy.RejectEnabledDependent,
            Rig = baseline.Rig with
            {
                ControlPolicy = new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                }
            }
        };
        var legacyScheduleV2 = canonical with
        {
            Schedule = canonical.Schedule with
            {
                WeeklyWindows = [],
                LegacyAlwaysOpen = true,
                LegacySetpointProfileId = "night"
            }
        };
        var legacyV1 = canonical with
        {
            SchemaVersion = LocalCaptureProfileDefinition.LegacySchemaVersion,
            DependencyPolicy = CapturePipelineDependencyPolicy.LegacyInference
        };
        var currentConfiguration = new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            canonical.Module,
            canonical.Rig,
            CapturePipelineConfig.Empty,
            AgentId: "test-agent")
        {
            Schedule = canonical.Schedule
        };

        Assert.IsTrue(CanToggle(canonical));
        Assert.IsFalse(CanToggle(legacyScheduleV2));

        bool CanToggle(LocalCaptureProfileDefinition profile)
        {
            var state = State();
            var revision = state.ActiveRevision with
            {
                Profile = profile,
                ProfileSha256 = LocalCaptureProfileContract.ComputeEffectiveSha256(profile)
            };
            return CameraAgentPipelineOperatorProjection.CreateState(
                state with { ActiveRevision = revision, PendingRevision = null },
                currentConfiguration,
                new ProjectionPipelineFactory()).Active.CanToggle;
        }
    }

    [TestMethod]
    public void Render_WhenAuthorizationIsRevoked_NavigatesToAccessDenied()
    {
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new ScheduleUiService(null));

        _ = context.Render<SchedulePage>();

        Assert.IsTrue(context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HistoricalRevision_UsesRollbackMutation()
    {
        using var context = CreateContext();
        var service = new ScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.FindAll("button").Any(button =>
            button.TextContent.Contains("Review rollback", StringComparison.Ordinal))));
        cut.FindAll("button").Single(button =>
            button.TextContent.Contains("Review rollback", StringComparison.Ordinal)).Click();
        cut.FindAll("button").Single(button =>
            button.TextContent.Contains("Confirm rollback", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(1, service.RollbackRevisionIds);
            Assert.AreEqual("profile-00000001-123456ABCDEF", service.RollbackRevisionIds[0]);
        });
    }

    [TestMethod]
    public void NewerHistoricalRevision_UsesActivationMutation()
    {
        using var context = CreateContext();
        var state = State();
        var prior = state.History.Single(static revision => revision.RevisionNumber == 1);
        var service = new ScheduleUiService(state with { ActiveRevision = prior });
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.FindAll("button").Any(button =>
            button.TextContent.Contains("Review apply", StringComparison.Ordinal))));
        cut.FindAll("button").Single(button =>
            button.TextContent.Contains("Review apply", StringComparison.Ordinal)).Click();
        cut.FindAll("button").Single(button =>
            button.TextContent.Contains("Confirm apply", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(1, service.ActivationRevisionIds);
            Assert.AreEqual("profile-00000002-ABCDEF123456", service.ActivationRevisionIds[0]);
        });
    }

    [TestMethod]
    public void OlderPendingRevision_UsesRollbackMutation()
    {
        using var context = CreateContext();
        var state = State();
        var prior = state.History.Single(static revision => revision.RevisionNumber == 1);
        var service = new ScheduleUiService(state with { PendingRevision = prior });
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.FindAll("button").Any(button =>
            button.TextContent.Contains("Review rollback", StringComparison.Ordinal))));
        cut.FindAll("button").First(button =>
            button.TextContent.Contains("Review rollback", StringComparison.Ordinal)).Click();
        cut.FindAll("button").Single(button =>
            button.TextContent.Contains("Confirm rollback", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
            CollectionAssert.Contains(service.RollbackRevisionIds, prior.RevisionId));
    }

    [TestMethod]
    public void PendingNamedRig_ShowsRestartAndCancelWithoutScheduleCommands()
    {
        var state = State();
        var pending = state.ActiveRevision with { RevisionId = "rig-schedule-v3", RevisionNumber = 3 };
        var rig = new Mock<ICameraAgentNamedRigUiService>();
        rig.Setup(read => read.GetRestartStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentRestartStatus>.Success(new(true, true, false)));
        using var context = CreateContext(new NamedRigSelection("rig-v1", "rig-v2", 2,
            PendingScheduleRevisionId: pending.RevisionId), rig: rig);
        var service = new RetryingScheduleUiService(state with
        {
            PendingRevision = pending,
            History = [pending, .. state.History]
        });
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);

        var cut = context.Render<SchedulePage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".schedule-state").TextContent, "Revision 2 is open", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".schedule-state").TextContent, "night", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".schedule-pending").TextContent, "awaiting restart", StringComparison.Ordinal);
            Assert.AreEqual("/operations/camera", cut.Find("a[href='/operations/camera']").GetAttribute("href"));
            Assert.AreEqual("Restart now", cut.Find(".schedule-pending button.restart-now").TextContent);
            Assert.IsEmpty(cut.FindAll("button").Where(button => button.TextContent.Contains("Review apply", StringComparison.Ordinal)
                || button.TextContent.Contains("Review rollback", StringComparison.Ordinal)));
        });
        OpenEditor(cut);
        Assert.IsTrue(SaveDraft(cut).HasAttribute("disabled"));
        Assert.IsEmpty(service.StageCommands);
        rig.Verify(read => read.GetAsync(It.IsAny<CancellationToken>()), Times.Once);
        rig.Verify(read => read.GetRestartStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
        rig.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void OrdinaryPendingDraft_StillOffersScheduleApplyAndStage()
    {
        var state = State();
        var pending = state.ActiveRevision with { RevisionId = "draft-v3", RevisionNumber = 3 };
        using var context = CreateContext();
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new ScheduleUiService(state with { PendingRevision = pending }));

        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);

        Assert.IsTrue(cut.FindAll("button").Any(button => button.TextContent.Contains("Review apply", StringComparison.Ordinal)));
        Assert.IsFalse(SaveDraft(cut).HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll("a[href='/operations/camera']"));
    }

    [TestMethod]
    public void UnauthorizedNamedRigRead_NavigatesToAccessDenied()
    {
        using var context = CreateContext(unauthorized: true);
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new ScheduleUiService(State()));

        _ = context.Render<SchedulePage>();

        Assert.IsTrue(context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SerializeProfile_RemovesOpaqueModuleAndProcessingOptions()
    {
        var profile = Profile() with
        {
            Module = new CameraModuleDescriptor(
                "test",
                JsonSerializer.SerializeToElement(new { secret = "module-secret" })),
            ProcessingSteps =
            [
                new CaptureProcessingStepConfig(
                    "test-step",
                    Options: JsonSerializer.SerializeToElement(new { storageRoot = "/private/root" }))
            ]
        };

        var serialized = CameraAgentScheduleUiService.SerializeProfile(profile);

        Assert.IsFalse(serialized.Contains("module-secret", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("/private/root", StringComparison.Ordinal));

        var basis = profile with
        {
            SchemaVersion = LocalCaptureProfileDefinition.CurrentSchemaVersion,
            DependencyPolicy = CapturePipelineDependencyPolicy.RejectEnabledDependent,
            ProcessingSteps =
            [
                new CaptureProcessingStepConfig(
                    "First", Options: JsonSerializer.SerializeToElement(new { marker = "first" }),
                    DependsOn: RawDependency),
                new CaptureProcessingStepConfig(
                    "Second", Options: JsonSerializer.SerializeToElement(new { marker = "second" }),
                    DependsOn: RawDependency)
            ]
        };
        var reordered = CameraAgentScheduleOperatorProjection.Sanitize(basis) with
        {
            ProcessingSteps = CameraAgentScheduleOperatorProjection.Sanitize(basis).ProcessingSteps.Reverse().ToArray()
        };

        var restored = CameraAgentScheduleOperatorProjection.RestoreOpaqueOptions(reordered, basis);

        Assert.AreEqual(
            "second",
            restored.ProcessingSteps[0].Options!.Value.GetProperty("marker").GetString());
        Assert.AreEqual(
            "first",
            restored.ProcessingSteps[1].Options!.Value.GetProperty("marker").GetString());

        var spacedIdBasis = basis with
        {
            ProcessingSteps = [basis.ProcessingSteps[0] with { Id = " first " }]
        };
        var normalizedIdCandidate = CameraAgentScheduleOperatorProjection.Sanitize(spacedIdBasis) with
        {
            ProcessingSteps = [spacedIdBasis.ProcessingSteps[0] with { Id = "first", Options = null }]
        };
        var normalizedIdRestored = CameraAgentScheduleOperatorProjection.RestoreOpaqueOptions(
            normalizedIdCandidate,
            spacedIdBasis);
        Assert.AreEqual(
            "first",
            normalizedIdRestored.ProcessingSteps[0].Options!.Value.GetProperty("marker").GetString());
        Assert.IsFalse(CameraAgentPipelineOperatorProjection.Toggle(spacedIdBasis, "first", enabled: false)
            .ProcessingSteps[0].Enabled);
    }

    [TestMethod]
    public void StageRetry_AfterUnavailable_ReusesIdempotencyKeyAndPayload()
    {
        using var context = CreateContext();
        var service = new RetryingScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);
        var save = SaveDraft(cut);

        save.Click();
        save.Click();

        Assert.HasCount(2, service.StageCommands);
        Assert.AreEqual(service.StageCommands[0].Key, service.StageCommands[1].Key);
        Assert.AreEqual(service.StageCommands[0].Payload, service.StageCommands[1].Payload);
        Assert.AreEqual(service.StageCommands[0].ExpectedVersion, service.StageCommands[1].ExpectedVersion);
    }

    [TestMethod]
    public async Task ActivationCancel_WhileCommandIsPending_KeepsConfirmationOpenAsync()
    {
        using var context = CreateContext();
        var service = new DelayedScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.FindAll("button").Any(button =>
            button.TextContent.Contains("Review rollback", StringComparison.Ordinal))));
        await cut.FindAll("button").Single(button =>
            button.TextContent.Contains("Review rollback", StringComparison.Ordinal)).ClickAsync().ConfigureAwait(false);
        var dialog = cut.Find("dialog");

        var command = cut.FindAll("button").Single(button =>
            button.TextContent.Contains("Confirm rollback", StringComparison.Ordinal))
            .TriggerEventAsync("onclick", EventArgs.Empty);
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Find("dialog .button.primary").HasAttribute("disabled")));

        await dialog.TriggerEventAsync("oncancel", EventArgs.Empty).ConfigureAwait(false);

        Assert.HasCount(1, cut.FindAll("dialog"));
        service.Complete();
        await command.ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
    }

    [TestMethod]
    public void TypedSetpointEdit_StagesTheRewrittenProfile()
    {
        using var context = CreateContext();
        var service = new RetryingScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);
        cut.WaitForElement("input[aria-label='Setpoint gain']");

        cut.Find("input[aria-label='Setpoint gain']").Change("1.125");
        SaveDraft(cut).Click();

        Assert.HasCount(1, service.StageCommands);
        var staged = CameraAgentScheduleUiService.ParseProfile(service.StageCommands[0].Payload);
        Assert.AreEqual(1.125, staged.Schedule.SetpointProfiles[0].Gain);
        Assert.AreEqual("night", staged.Schedule.SetpointProfiles[0].Id);
        Assert.AreEqual(Profile().Schedule.WeeklyWindows[0].Id, staged.Schedule.WeeklyWindows[0].Id);
        Assert.IsTrue(cut.Find("textarea").GetAttribute("value")!.Contains("1.125", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OperatingPolicyEdit_StagesOnBasisWithoutChangingSchedule()
    {
        using var context = CreateContext();
        var state = State();
        var service = new RetryingScheduleUiService(state);
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);
        cut.WaitForElement(".policy-fields input");

        Assert.IsEmpty(service.StageCommands);
        cut.FindAll(".policy-fields label").Single(label => label.TextContent.Contains("Day exposure ms", StringComparison.Ordinal)).QuerySelector("input")!.Change("1500");
        cut.FindAll(".policy-fields label").Single(label => label.TextContent.Contains("Night gain", StringComparison.Ordinal)).QuerySelector("input")!.Change("2.5");
        cut.FindAll(".policy-fields label").Single(label => label.TextContent.Contains("Capture interval s", StringComparison.Ordinal)).QuerySelector("input")!.Change("5");
        cut.FindAll(".policy-fields label").Single(label => label.TextContent.Contains("Cadence mode", StringComparison.Ordinal)).QuerySelector("select")!.Change(nameof(CaptureCadenceMode.MinimumStartInterval));
        cut.FindAll(".policy-fields label").Single(label => label.TextContent.Contains("Exposure control", StringComparison.Ordinal)).QuerySelector("select")!.Change(nameof(AutomaticControlOwnership.Disabled));
        Assert.IsEmpty(service.StageCommands);
        SaveDraft(cut).Click();

        Assert.HasCount(1, service.StageCommands);
        var staged = CameraAgentScheduleUiService.ParseProfile(service.StageCommands[0].Payload);
        Assert.AreEqual(state.ActiveRevision.RevisionId, service.StageCommands[0].BasisRevisionId);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1500), staged.Rig.Pipeline.DayExposure);
        Assert.AreEqual(2.5, staged.Rig.Pipeline.NightGain);
        Assert.AreEqual(TimeSpan.FromSeconds(5), staged.Rig.Pipeline.CaptureInterval);
        Assert.AreEqual(AutomaticControlOwnership.Disabled, staged.Rig.ControlPolicy!.ExposureControl);
        Assert.AreEqual(state.ActiveRevision.Profile.Module, staged.Module);
        Assert.AreEqual(state.ActiveRevision.Profile.Rig.Sensor, staged.Rig.Sensor);
        Assert.AreEqual(state.ActiveRevision.Profile.Rig.Optics, staged.Rig.Optics);
        Assert.AreEqual(state.ActiveRevision.Profile.Rig.Orientation, staged.Rig.Orientation);
        Assert.AreEqual(state.ActiveRevision.Profile.Rig.Readout, staged.Rig.Readout);
        Assert.AreEqual(CaptureScheduleContract.ComputeSha256(state.ActiveRevision.Profile.Schedule),
            CaptureScheduleContract.ComputeSha256(staged.Schedule));
    }

    [TestMethod]
    public void InvalidOperatingPolicy_BlocksStaging()
    {
        using var context = CreateContext();
        var service = new RetryingScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);
        cut.WaitForElement(".policy-fields input");

        cut.FindAll(".policy-fields label").Single(label => label.TextContent.Contains("Temperature target C", StringComparison.Ordinal)).QuerySelector("input")!.Change("cold");
        SaveDraft(cut).Click();

        Assert.IsEmpty(service.StageCommands);
        StringAssert.Contains(cut.Find(".schedule-message[role='alert']").TextContent,
            "Temperature target must be a number.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void EnvelopeAndTemperatureEdit_PreserveWeeklyWindows()
    {
        using var context = CreateContext();
        var state = State();
        var basis = state.ActiveRevision.Profile;
        var profile = basis with
        {
            Rig = basis.Rig with
            {
                Pipeline = basis.Rig.Pipeline with
                {
                    Envelope = new ExposureEnvelope(
                        TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(10), 0, 20,
                        new ExposureDefaults(TimeSpan.FromMilliseconds(10), 0),
                        new ExposureDefaults(TimeSpan.FromSeconds(5), 10), 1800)
                },
                ControlPolicy = new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.HostMetered,
                    GainControl = AutomaticControlOwnership.HostMetered,
                    Temperature = new TemperatureControlDirective { Mode = TemperatureControlMode.Target, TargetC = -5 }
                }
            }
        };
        state = state with { ActiveRevision = state.ActiveRevision with { Profile = profile } };
        var service = new RetryingScheduleUiService(state);
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);
        cut.WaitForElement(".policy-fields input");

        cut.FindAll(".policy-fields label").Single(label => label.TextContent.Contains("Maximum gain", StringComparison.Ordinal)).QuerySelector("input")!.Change("25");
        cut.FindAll(".policy-fields label").Single(label => label.TextContent.Contains("Temperature target C", StringComparison.Ordinal)).QuerySelector("input")!.Change("-8");
        SaveDraft(cut).Click();

        var staged = CameraAgentScheduleUiService.ParseProfile(service.StageCommands.Single().Payload);
        Assert.AreEqual(25, staged.Rig.Pipeline.Envelope!.MaxGain);
        Assert.AreEqual(-8, staged.Rig.ControlPolicy!.Temperature.TargetC);
        CollectionAssert.AreEqual(profile.Schedule.WeeklyWindows.ToArray(), staged.Schedule.WeeklyWindows.ToArray());
    }

    [TestMethod]
    public void InvalidTypedValue_BlocksStagingWithLabelledMessage()
    {
        using var context = CreateContext();
        var service = new RetryingScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);
        cut.WaitForElement("input[aria-label='Setpoint exposure milliseconds']");

        cut.Find("input[aria-label='Setpoint exposure milliseconds']").Change("fast");
        SaveDraft(cut).Click();

        Assert.IsEmpty(service.StageCommands);
        StringAssert.Contains(cut.Find(".schedule-message[role='alert']").TextContent, "Setpoint 'night' exposure must be a number.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void AdvancedJsonEdit_TakesPrecedenceUntilATypedFieldChanges()
    {
        using var context = CreateContext();
        var service = new RetryingScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);
        cut.WaitForElement("textarea");
        var edited = CameraAgentScheduleUiService.SerializeProfile(Profile() with
        {
            Schedule = Profile().Schedule with { SetpointProfiles = [Profile().Schedule.SetpointProfiles[0] with { Gain = 7 }] }
        });

        cut.Find("textarea").Input(edited);
        SaveDraft(cut).Click();
        Assert.AreEqual(7, CameraAgentScheduleUiService.ParseProfile(service.StageCommands[^1].Payload).Schedule.SetpointProfiles[0].Gain);

        cut.Find("input[aria-label='Setpoint gain']").Change("2");
        SaveDraft(cut).Click();

        Assert.AreEqual(2, CameraAgentScheduleUiService.ParseProfile(service.StageCommands[^1].Payload).Schedule.SetpointProfiles[0].Gain);
    }

    [TestMethod]
    public void AddAndRemoveRows_RewriteTheDraftLists()
    {
        using var context = CreateContext();
        var service = new RetryingScheduleUiService(State());
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(service);
        var cut = context.Render<SchedulePage>();
        OpenEditor(cut);
        cut.WaitForElement("input[aria-label='Setpoint gain']");

        cut.FindAll("button").Single(button => button.TextContent.Contains("Add blackout", StringComparison.Ordinal)).Click();
        cut.FindAll("button").Single(button => button.TextContent.Contains("Add setpoint profile", StringComparison.Ordinal)).Click();
        SaveDraft(cut).Click();
        var staged = CameraAgentScheduleUiService.ParseProfile(service.StageCommands[^1].Payload);
        Assert.HasCount(2, staged.Schedule.SetpointProfiles);
        Assert.HasCount(1, staged.Schedule.Blackouts!);

        cut.FindAll("button").Single(button => button.GetAttribute("aria-label")?.StartsWith("Remove blackout", StringComparison.Ordinal) == true).Click();
        SaveDraft(cut).Click();

        Assert.IsTrue((CameraAgentScheduleUiService.ParseProfile(service.StageCommands[^1].Payload).Schedule.Blackouts?.Count ?? 0) == 0);
    }

    internal static CaptureScheduleOperatorState State()
    {
        var profile = Profile();
        var revision = new CaptureScheduleRevisionSnapshot(
            "profile-00000002-ABCDEF123456",
            2,
            profile,
            new string('A', 64),
            CaptureScheduleContract.ComputeSha256(profile.Schedule),
            "operator-draft",
            "owner",
            null,
            DateTimeOffset.UnixEpoch);
        var prior = revision with { RevisionId = "profile-00000001-123456ABCDEF", RevisionNumber = 1 };
        var interval = new ExpandedScheduleInterval(
            "weekly-night",
            CaptureScheduleIntervalSource.WeeklyWindow,
            ExpandedScheduleDisposition.Open,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(1),
            new DateOnly(1970, 1, 1),
            "night");
        var preview = new CaptureSchedulePreview(
            revision.ScheduleSha256,
            new string('B', 64),
            "capture-schedule-expand-v1",
            new string('C', 64),
            "none",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(1),
            [interval],
            []);
        var decision = new CaptureScheduleDecision(
            true,
            CaptureScheduleAdmissionReason.WeeklyWindow,
            CaptureScheduleSafetyState.Available,
            DateTimeOffset.UnixEpoch,
            "night",
            interval,
            null,
            false,
            DateTimeOffset.UnixEpoch.AddDays(1));
        return new CaptureScheduleOperatorState(4, revision, null, [revision, prior], decision, preview, []);
    }

    internal static LocalCaptureProfileDefinition Profile()
        => new(
            LocalCaptureProfileDefinition.LegacySchemaVersion,
            new CameraModuleDescriptor("test"),
            new CameraRigConfig(
                new SensorProfile("test", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Perspective", 50, 10, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            [],
            new CaptureScheduleDefinition(
                "capture-schedule-v1",
                [new CaptureScheduleSetpointProfile(
                    "night", TimeSpan.FromSeconds(1), 1, TimeSpan.FromSeconds(2))],
                [new CaptureWeeklyScheduleWindow(
                    "weekly-night",
                    DayOfWeek.Thursday,
                    new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, TimeOnly.MinValue),
                    new CaptureScheduleBoundary(
                        CaptureScheduleBoundaryKind.FixedLocalTime,
                        TimeOnly.MinValue,
                        DayOffset: 1),
                    "night")]));

    internal class ScheduleUiService(CaptureScheduleOperatorState? state) : ICameraAgentScheduleUiService
    {
        internal List<string> RollbackRevisionIds { get; } = [];
        internal List<string> ActivationRevisionIds { get; } = [];
        internal List<CaptureScheduleOverride> OverrideCommands { get; } = [];
        internal CameraAgentScheduleCalendar? Calendar { get; set; }
        internal int CalendarCalls { get; private set; }

        public ValueTask<OperatorUiResult<CameraAgentScheduleCalendar>> GetCalendarAsync(
            int nightCount, CancellationToken cancellationToken)
        {
            CalendarCalls++;
            return ValueTask.FromResult(Calendar is null
                ? OperatorUiResult<CameraAgentScheduleCalendar>.Failure(OperatorUiResultKind.Unavailable, "The schedule calendar is unavailable.")
                : OperatorUiResult<CameraAgentScheduleCalendar>.Success(Calendar));
        }

        public ValueTask<OperatorUiResult<CaptureScheduleOperatorState>> GetAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(state is null
                ? OperatorUiResult<CaptureScheduleOperatorState>.Failure(
                    OperatorUiResultKind.Unauthorized, "Authorization is required.")
                : OperatorUiResult<CaptureScheduleOperatorState>.Success(state));

        public virtual ValueTask<OperatorUiResult<CameraAgentPipelineOperatorState>> GetPipelineAsync(
            CancellationToken cancellationToken)
        {
            if (state is null)
            {
                return ValueTask.FromResult(OperatorUiResult<CameraAgentPipelineOperatorState>.Failure(
                    OperatorUiResultKind.Unauthorized, "Authorization is required."));
            }
            var node = new CaptureProcessingPlanNode(
                "Preview", "Preview", true, true, 10, null, RawDependency,
                "encoded-preview", FrameArtifactRole.Preview, "display");
            var plan = new CaptureProcessingPlanPreview(
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent,
                new string('D', 64),
                new string('E', 64),
                [node],
                [node]);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentPipelineOperatorState>.Success(new(
                new CameraAgentPipelineRevisionPlan(
                    state.ActiveRevision.RevisionId,
                    state.ActiveRevision.RevisionNumber,
                    state.ActiveRevision.ProfileSha256,
                    false,
                    plan),
                null)));
        }

        public ValueTask<OperatorUiResult<CaptureSchedulePreview>> PreviewAsync(
            string profileJson, string basisRevisionId, int dayCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> StageAsync(
            string profileJson, string basisRevisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ActivateAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
        {
            ActivationRevisionIds.Add(revisionId);
            return ValueTask.FromResult(OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(
                OperatorUiResultKind.Invalid, "Synthetic activation result."));
        }

        public virtual ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> RollbackAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
        {
            RollbackRevisionIds.Add(revisionId);
            return ValueTask.FromResult(OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(
                OperatorUiResultKind.Invalid, "Synthetic rollback result."));
        }

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> AddOverrideAsync(
            CaptureScheduleOverride scheduleOverride, long expectedVersion, string idempotencyKey,
            string? reason, CancellationToken cancellationToken)
        {
            OverrideCommands.Add(scheduleOverride);
            return ValueTask.FromResult(OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(
                OperatorUiResultKind.Invalid, "Synthetic override result."));
        }

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ClearOverrideAsync(
            string overrideId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class DelayedScheduleUiService(CaptureScheduleOperatorState state) : ScheduleUiService(state)
    {
        private readonly TaskCompletionSource<OperatorUiResult<CaptureScheduleStoreSnapshot>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Complete() => _completion.TrySetResult(
            OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(
                OperatorUiResultKind.Invalid,
                "Synthetic rollback result."));

        public override async ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> RollbackAsync(
            string revisionId,
            long expectedVersion,
            string idempotencyKey,
            string? reason,
            CancellationToken cancellationToken)
            => await _completion.Task.ConfigureAwait(false);
    }

    private sealed class ProjectionPipelineFactory : ICaptureProcessingPipelineFactory
    {
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => new([]);

        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config)
            => new(
                config.Pipeline.SchemaVersion,
                config.Pipeline.DependencyPolicy,
                new string('A', 64),
                new string('B', 64),
                [],
                []);
    }

    private sealed class RetryingScheduleUiService(CaptureScheduleOperatorState state) : ICameraAgentScheduleUiService
    {
        internal List<(string Payload, string BasisRevisionId, string Key, long ExpectedVersion)> StageCommands { get; } = [];

        public ValueTask<OperatorUiResult<CaptureScheduleOperatorState>> GetAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CaptureScheduleOperatorState>.Success(state));

        public ValueTask<OperatorUiResult<CaptureSchedulePreview>> PreviewAsync(
            string profileJson, string basisRevisionId, int dayCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> StageAsync(
            string profileJson, string basisRevisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
        {
            StageCommands.Add((profileJson, basisRevisionId, idempotencyKey, expectedVersion));
            return ValueTask.FromResult(OperatorUiResult<CaptureScheduleStoreSnapshot>.Failure(
                OperatorUiResultKind.Unavailable, "Response was unavailable."));
        }

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ActivateAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> RollbackAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> AddOverrideAsync(
            CaptureScheduleOverride scheduleOverride, long expectedVersion, string idempotencyKey,
            string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ClearOverrideAsync(
            string overrideId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
