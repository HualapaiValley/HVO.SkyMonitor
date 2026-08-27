using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Tests.Scheduling;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureScheduleEngineTests
{
    private static readonly ObservatoryLocation Observer = new(35.347, -113.878, 1000, "America/Phoenix");

    [TestMethod]
    public void Identity_NormalizesDeclarationOrderAndRejectsDuplicateIdentifiers()
    {
        var first = Definition(
            [Profile("night"), Profile("day", gain: 2)],
            [Window("b", DayOfWeek.Monday, "night"), Window("a", DayOfWeek.Tuesday, "day")]);
        var reordered = Definition(
            [Profile("day", gain: 2), Profile("night")],
            [Window("a", DayOfWeek.Tuesday, "day"), Window("b", DayOfWeek.Monday, "night")]);

        Assert.AreEqual(
            CaptureScheduleContract.ComputeSha256(first),
            CaptureScheduleContract.ComputeSha256(reordered));
        var validation = CaptureScheduleContract.Validate(Definition(
            [Profile("night"), Profile("night")],
            []));
        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual(CaptureContractReasonCodes.InvalidSchedule, validation.ReasonCode);
        Assert.AreEqual("schedule.setpointProfiles", validation.FieldPath);
        Assert.AreEqual(
            CaptureScheduleContract.ComputeSha256(first with
            {
                DateExceptions = null,
                Blackouts = null
            }),
            CaptureScheduleContract.ComputeSha256(first with
            {
                DateExceptions = [],
                Blackouts = []
            }));
        Assert.IsFalse(CaptureScheduleContract.Validate(first with
        {
            SchemaVersion = "capture-schedule-v2"
        }).IsValid);
        Assert.IsFalse(CaptureScheduleContract.Validate(first with
        {
            WeeklyWindows =
            [
                first.WeeklyWindows[0] with
                {
                    Start = new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime)
                }
            ]
        }).IsValid);
    }

    [TestMethod]
    public void TimeZoneResolver_UsesRequiredAmbiguousAndSkippedTimeRules()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var ambiguousStart = CaptureScheduleTimeZone.Resolve(
            new DateOnly(2024, 11, 3), new TimeOnly(1, 30), timeZone, LocalBoundaryRole.Start);
        var ambiguousEnd = CaptureScheduleTimeZone.Resolve(
            new DateOnly(2024, 11, 3), new TimeOnly(1, 30), timeZone, LocalBoundaryRole.End);
        var skipped = CaptureScheduleTimeZone.Resolve(
            new DateOnly(2024, 3, 10), new TimeOnly(2, 30), timeZone, LocalBoundaryRole.Start);

        Assert.AreEqual(new DateTimeOffset(2024, 11, 3, 5, 30, 0, TimeSpan.Zero), ambiguousStart);
        Assert.AreEqual(new DateTimeOffset(2024, 11, 3, 6, 30, 0, TimeSpan.Zero), ambiguousEnd);
        Assert.AreEqual(new DateTimeOffset(2024, 3, 10, 7, 0, 0, TimeSpan.Zero), skipped);
        Assert.AreEqual(25, (CaptureScheduleTimeZone.ResolveDay(new DateOnly(2024, 11, 3), timeZone).EndUtc -
            CaptureScheduleTimeZone.ResolveDay(new DateOnly(2024, 11, 3), timeZone).StartUtc).TotalHours);
    }

    [TestMethod]
    public void Expand_ProducesPositiveCrossMidnightIntervalAcrossDstDate()
    {
        var definition = Definition(
            [Profile("night")],
            [new CaptureWeeklyScheduleWindow(
                "overnight",
                DayOfWeek.Sunday,
                Fixed(20),
                Fixed(6, dayOffset: 1),
                "night")]);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        var preview = CaptureScheduleIntervalExpander.Expand(
            definition, new DateOnly(2024, 3, 10), 1, timeZone, Observer, new FixedSolarEvents());

        var interval = preview.Intervals.Single();
        Assert.AreEqual(new DateTimeOffset(2024, 3, 11, 0, 0, 0, TimeSpan.Zero), interval.StartUtc);
        Assert.AreEqual(new DateTimeOffset(2024, 3, 11, 10, 0, 0, TimeSpan.Zero), interval.EndUtc);
        Assert.AreEqual(CaptureScheduleIntervalSource.WeeklyWindow, interval.Source);
    }

    [TestMethod]
    public void Expand_MissingSolarEventClosesWindowUnlessFallbackExists()
    {
        var date = new DateOnly(2025, 6, 21);
        var missing = Definition(
            [Profile("night")],
            [new CaptureWeeklyScheduleWindow(
                "solar-night",
                date.DayOfWeek,
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.Sunset),
                Fixed(23),
                "night")]);
        var fallback = missing with
        {
            WeeklyWindows =
            [
                missing.WeeklyWindows[0] with
                {
                    Start = new CaptureScheduleBoundary(
                        CaptureScheduleBoundaryKind.Sunset,
                        NoEventFallbackLocalTime: new TimeOnly(20, 0))
                }
            ]
        };
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
        var events = new FixedSolarEvents(noEvents: true);

        var unavailable = CaptureScheduleIntervalExpander.Expand(
            missing, date, 1, timeZone, new ObservatoryLocation(69.6492, 18.9553, 0, "Europe/Oslo"), events);
        var available = CaptureScheduleIntervalExpander.Expand(
            fallback, date, 1, timeZone, new ObservatoryLocation(69.6492, 18.9553, 0, "Europe/Oslo"), events);
        var decisionUtc = CaptureScheduleTimeZone.Resolve(date, new TimeOnly(21, 0), timeZone, LocalBoundaryRole.Start);

        Assert.IsEmpty(unavailable.Intervals);
        Assert.HasCount(1, unavailable.UnavailableWindows);
        Assert.AreEqual(CaptureScheduleAdmissionReason.NoSolarEvent, CaptureScheduleEvaluator.Evaluate(
            missing,
            unavailable,
            new CaptureScheduleEvaluationRequest(decisionUtc, CaptureScheduleSafetyState.Available, false)).Reason);
        Assert.HasCount(1, available.Intervals);
        Assert.IsTrue(CaptureScheduleEvaluator.Evaluate(
            fallback,
            available,
            new CaptureScheduleEvaluationRequest(decisionUtc, CaptureScheduleSafetyState.Available, false)).Admitted);
    }

    [TestMethod]
    public void Evaluate_AppliesSafetyPauseBlackoutOverrideExceptionAndWeeklyPrecedence()
    {
        var date = new DateOnly(2025, 1, 15);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        var blackoutStart = CaptureScheduleTimeZone.Resolve(
            date, new TimeOnly(20, 30), timeZone, LocalBoundaryRole.Start);
        var definition = Definition(
            [Profile("weekly"), Profile("exception", gain: 2), Profile("override", gain: 3)],
            [
                new CaptureWeeklyScheduleWindow("weekly", date.DayOfWeek, Fixed(18), Fixed(6, dayOffset: 1), "weekly"),
                new CaptureWeeklyScheduleWindow("weekly-next", date.AddDays(1).DayOfWeek, Fixed(18), Fixed(6, dayOffset: 1), "weekly")
            ]) with
        {
            DateExceptions =
            [
                new CaptureScheduleDateRule(
                    "exception-date",
                    date,
                    false,
                    [new CaptureDateScheduleWindow("exception-window", Fixed(19), Fixed(21), "exception")])
            ],
            Blackouts = [new CaptureScheduleBlackout("blackout", blackoutStart, blackoutStart.AddMinutes(30))]
        };
        var preview = CaptureScheduleIntervalExpander.Expand(
            definition, date, 2, timeZone, Observer, new FixedSolarEvents());
        var atTwenty = CaptureScheduleTimeZone.Resolve(
            date, new TimeOnly(20, 0), timeZone, LocalBoundaryRole.Start);
        var forceOpen = new CaptureScheduleOverride(
            "force-open", "revision-1", preview.ScheduleRevisionSha256, CaptureScheduleOverrideMode.ForceOpen,
            atTwenty.AddMinutes(-30), atTwenty.AddHours(2), "override", OneShot: true);
        var forceClosed = new CaptureScheduleOverride(
            "force-closed", "revision-1", preview.ScheduleRevisionSha256, CaptureScheduleOverrideMode.ForceClosed,
            atTwenty.AddMinutes(-30), atTwenty.AddHours(2));

        AssertDecision(CaptureScheduleAdmissionReason.SafetyUnavailable, false,
            new(atTwenty, CaptureScheduleSafetyState.StorageUnavailable, false, [forceOpen]));
        AssertDecision(CaptureScheduleAdmissionReason.ManualPause, false,
            new(atTwenty, CaptureScheduleSafetyState.Available, true, [forceOpen]));
        AssertDecision(CaptureScheduleAdmissionReason.ForceOpenOverride, true,
            new(atTwenty, CaptureScheduleSafetyState.Available, false, [forceOpen]), "override", consume: true);
        AssertDecision(CaptureScheduleAdmissionReason.ForceClosedOverride, false,
            new(atTwenty, CaptureScheduleSafetyState.Available, false, [forceOpen, forceClosed]));
        AssertDecision(CaptureScheduleAdmissionReason.Blackout, false,
            new(blackoutStart.AddMinutes(10), CaptureScheduleSafetyState.Available, false, [forceOpen]));
        AssertDecision(CaptureScheduleAdmissionReason.DateException, true,
            new(atTwenty, CaptureScheduleSafetyState.Available, false), "exception");
        AssertDecision(CaptureScheduleAdmissionReason.WeeklyWindow, true,
            new(atTwenty.AddHours(26), CaptureScheduleSafetyState.Available, false), "weekly");
        AssertDecision(CaptureScheduleAdmissionReason.DefaultClosed, false,
            new(atTwenty.AddHours(15), CaptureScheduleSafetyState.Available, false));
        AssertDecision(CaptureScheduleAdmissionReason.DateException, false,
            new(atTwenty.AddHours(-3), CaptureScheduleSafetyState.Available, false));
        var legacyDefinition = Definition([Profile("legacy")], []) with
        {
            LegacyAlwaysOpen = true,
            LegacySetpointProfileId = "legacy"
        };
        var legacyPreview = CaptureScheduleIntervalExpander.Expand(
            legacyDefinition, date, 2, timeZone, Observer, new FixedSolarEvents());
        var legacyDecision = CaptureScheduleEvaluator.Evaluate(
            legacyDefinition,
            legacyPreview,
            new CaptureScheduleEvaluationRequest(atTwenty, CaptureScheduleSafetyState.Available, false));
        Assert.AreEqual(CaptureScheduleAdmissionReason.LegacyCompatibility, legacyDecision.Reason);
        Assert.AreEqual(CaptureScheduleIntervalSource.LegacyCompatibility, legacyDecision.Interval?.Source);
        Assert.AreEqual("legacy", legacyDecision.SetpointProfileId);

        void AssertDecision(
            CaptureScheduleAdmissionReason reason,
            bool admitted,
            CaptureScheduleEvaluationRequest request,
            string? profile = null,
            bool consume = false)
        {
            var decision = CaptureScheduleEvaluator.Evaluate(definition, preview, request);
            Assert.AreEqual(reason, decision.Reason);
            Assert.AreEqual(admitted, decision.Admitted);
            Assert.AreEqual(profile, decision.SetpointProfileId);
            Assert.AreEqual(consume, decision.ConsumeOneShotOverride);
        }
    }

    [TestMethod]
    public void Expand_RejectsConflictingEqualPriorityWindowsAfterUtcExpansion()
    {
        var date = new DateOnly(2025, 1, 13);
        var definition = Definition(
            [Profile("first"), Profile("second", gain: 2)],
            [
                new CaptureWeeklyScheduleWindow("first", date.DayOfWeek, Fixed(18), Fixed(22), "first"),
                new CaptureWeeklyScheduleWindow("second", date.DayOfWeek, Fixed(20), Fixed(23), "second")
            ]);

        var exception = Assert.ThrowsExactly<CaptureScheduleValidationException>(() =>
            CaptureScheduleIntervalExpander.Expand(
                definition,
                date,
                1,
                TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix"),
                Observer,
                new FixedSolarEvents()));

        Assert.AreEqual("schedule.interval.conflict", exception.ReasonCode);
    }

    [TestMethod]
    public void Expand_RejectsConflictingWeeklyCarryInFromDifferentAnchorDates()
    {
        var date = new DateOnly(2025, 1, 13);
        var definition = Definition(
            [Profile("sunday"), Profile("monday", gain: 2)],
            [
                new CaptureWeeklyScheduleWindow(
                    "sunday-night", date.AddDays(-1).DayOfWeek,
                    Fixed(20), Fixed(3, dayOffset: 1), "sunday"),
                new CaptureWeeklyScheduleWindow(
                    "monday-early", date.DayOfWeek,
                    Fixed(0), Fixed(6), "monday")
            ]);

        var exception = Assert.ThrowsExactly<CaptureScheduleValidationException>(() =>
            CaptureScheduleIntervalExpander.Expand(
                definition,
                date,
                1,
                TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix"),
                Observer,
                new FixedSolarEvents()));

        Assert.AreEqual("schedule.interval.conflict", exception.ReasonCode);
    }

    [TestMethod]
    public void Expand_IncludesPriorDateCarryInAndCurrentDateRuleMasksIt()
    {
        var date = new DateOnly(2025, 1, 13);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        var definition = Definition(
            [Profile("weekly")],
            [new CaptureWeeklyScheduleWindow(
                "sunday-night",
                date.AddDays(-1).DayOfWeek,
                Fixed(20),
                Fixed(6, dayOffset: 1),
                "weekly")]);
        var preview = CaptureScheduleIntervalExpander.Expand(
            definition, date, 1, timeZone, Observer, new FixedSolarEvents());
        var atOne = CaptureScheduleTimeZone.Resolve(date, new TimeOnly(1, 0), timeZone, LocalBoundaryRole.Start);

        Assert.IsTrue(CaptureScheduleEvaluator.Evaluate(
            definition,
            preview,
            new CaptureScheduleEvaluationRequest(atOne, CaptureScheduleSafetyState.Available, false)).Admitted);

        var exceptionDefinition = definition with
        {
            DateExceptions = [new CaptureScheduleDateRule("maintenance", date, false, [])]
        };
        var exceptionPreview = CaptureScheduleIntervalExpander.Expand(
            exceptionDefinition, date, 1, timeZone, Observer, new FixedSolarEvents());
        var decision = CaptureScheduleEvaluator.Evaluate(
            exceptionDefinition,
            exceptionPreview,
            new CaptureScheduleEvaluationRequest(atOne, CaptureScheduleSafetyState.Available, false));

        Assert.IsFalse(decision.Admitted);
        Assert.AreEqual(CaptureScheduleAdmissionReason.DateException, decision.Reason);
    }

    [TestMethod]
    public void Evaluate_CarriedDateExceptionOutranksCurrentWeeklyWindow()
    {
        var date = new DateOnly(2025, 1, 13);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        var definition = Definition(
            [Profile("weekly"), Profile("exception", gain: 2)],
            [new CaptureWeeklyScheduleWindow("monday", date.DayOfWeek, Fixed(0), Fixed(6), "weekly")]) with
        {
            DateExceptions =
            [
                new CaptureScheduleDateRule(
                    "sunday-exception",
                    date.AddDays(-1),
                    false,
                    [new CaptureDateScheduleWindow("overnight", Fixed(20), Fixed(3, dayOffset: 1), "exception")])
            ]
        };
        var preview = CaptureScheduleIntervalExpander.Expand(
            definition, date, 1, timeZone, Observer, new FixedSolarEvents());
        var atOne = CaptureScheduleTimeZone.Resolve(date, new TimeOnly(1, 0), timeZone, LocalBoundaryRole.Start);

        var decision = CaptureScheduleEvaluator.Evaluate(
            definition,
            preview,
            new CaptureScheduleEvaluationRequest(atOne, CaptureScheduleSafetyState.Available, false));

        Assert.IsTrue(decision.Admitted);
        Assert.AreEqual(CaptureScheduleAdmissionReason.DateException, decision.Reason);
        Assert.AreEqual("exception", decision.SetpointProfileId);
    }

    [TestMethod]
    public void Evaluate_CurrentDateExceptionMasksOverlappingCarriedException()
    {
        var date = new DateOnly(2025, 1, 13);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        var definition = Definition([Profile("carried"), Profile("current", gain: 2)], []) with
        {
            DateExceptions =
            [
                new CaptureScheduleDateRule(
                    "sunday",
                    date.AddDays(-1),
                    false,
                    [new CaptureDateScheduleWindow("sunday-night", Fixed(20), Fixed(3, dayOffset: 1), "carried")]),
                new CaptureScheduleDateRule(
                    "monday",
                    date,
                    false,
                    [new CaptureDateScheduleWindow("monday-early", Fixed(0), Fixed(2), "current")])
            ]
        };
        var preview = CaptureScheduleIntervalExpander.Expand(
            definition, date, 1, timeZone, Observer, new FixedSolarEvents());
        var atOne = CaptureScheduleTimeZone.Resolve(date, new TimeOnly(1, 0), timeZone, LocalBoundaryRole.Start);

        var decision = CaptureScheduleEvaluator.Evaluate(
            definition,
            preview,
            new CaptureScheduleEvaluationRequest(atOne, CaptureScheduleSafetyState.Available, false));

        Assert.AreEqual(CaptureScheduleAdmissionReason.DateException, decision.Reason);
        Assert.AreEqual("current", decision.SetpointProfileId);
    }

    [TestMethod]
    public void Evaluate_UnavailableCarriedDateExceptionClosesCurrentWeeklyWindow()
    {
        var date = new DateOnly(2025, 1, 13);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        var definition = Definition(
            [Profile("exception"), Profile("weekly", gain: 2)],
            [new CaptureWeeklyScheduleWindow("monday", date.DayOfWeek, Fixed(0), Fixed(6), "weekly")]) with
        {
            DateExceptions =
            [
                new CaptureScheduleDateRule(
                    "sunday",
                    date.AddDays(-1),
                    false,
                    [new CaptureDateScheduleWindow(
                        "solar-night",
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.Sunset),
                        Fixed(3, dayOffset: 1),
                        "exception")])
            ]
        };
        var preview = CaptureScheduleIntervalExpander.Expand(
            definition, date, 1, timeZone, Observer, new FixedSolarEvents(noEvents: true));
        var atOne = CaptureScheduleTimeZone.Resolve(date, new TimeOnly(1, 0), timeZone, LocalBoundaryRole.Start);

        var decision = CaptureScheduleEvaluator.Evaluate(
            definition,
            preview,
            new CaptureScheduleEvaluationRequest(atOne, CaptureScheduleSafetyState.Available, false));

        Assert.IsFalse(decision.Admitted);
        Assert.AreEqual(CaptureScheduleAdmissionReason.NoSolarEvent, decision.Reason);
    }

    [TestMethod]
    public void Expand_IncludesMaximumContractCarryIn()
    {
        var date = new DateOnly(2025, 1, 13);
        var anchorDate = date.AddDays(-2);
        var delayedStart = Fixed(0, dayOffset: 1) with { Offset = TimeSpan.FromHours(24) };
        var delayedEnd = Fixed(6, dayOffset: 1) with { Offset = TimeSpan.FromHours(24) };
        var definition = Definition(
            [Profile("night")],
            [new CaptureWeeklyScheduleWindow(
                "delayed",
                anchorDate.DayOfWeek,
                delayedStart,
                delayedEnd,
                "night")]);

        var preview = CaptureScheduleIntervalExpander.Expand(
            definition,
            date,
            1,
            TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix"),
            Observer,
            new FixedSolarEvents());

        Assert.HasCount(1, preview.Intervals);
        Assert.AreEqual(anchorDate, preview.Intervals[0].LocalDate);
    }

    [TestMethod]
    public void Evaluate_RejectsOutOfRangeInstantsAndRequiresForceOpenProfile()
    {
        var date = new DateOnly(2025, 1, 13);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        var definition = Definition([Profile("night")], [Window("night", date.DayOfWeek, "night")]);
        var preview = CaptureScheduleIntervalExpander.Expand(
            definition, date, 1, timeZone, Observer, new FixedSolarEvents());

        Assert.ThrowsExactly<CaptureScheduleValidationException>(() => CaptureScheduleEvaluator.Evaluate(
            definition,
            preview,
            new CaptureScheduleEvaluationRequest(
                preview.PreviewStartUtc.AddTicks(-1), CaptureScheduleSafetyState.Available, false)));
        var invalidOverride = new CaptureScheduleOverride(
            "open",
            "revision-1",
            preview.ScheduleRevisionSha256,
            CaptureScheduleOverrideMode.ForceOpen,
            preview.PreviewStartUtc,
            preview.PreviewEndUtc);
        Assert.ThrowsExactly<CaptureScheduleValidationException>(() => CaptureScheduleEvaluator.Evaluate(
            definition,
            preview,
            new CaptureScheduleEvaluationRequest(
                preview.PreviewStartUtc, CaptureScheduleSafetyState.Available, false, [invalidOverride])));

        var validOverride = invalidOverride with { SetpointProfileId = "night" };
        var decision = CaptureScheduleEvaluator.Evaluate(
            definition,
            preview,
            new CaptureScheduleEvaluationRequest(
                preview.PreviewStartUtc, CaptureScheduleSafetyState.Available, false, [validOverride]));
        Assert.IsNotNull(decision.Interval);
        Assert.AreEqual(CaptureScheduleIntervalSource.ForceOpenOverride, decision.Interval.Source);
        Assert.AreEqual(validOverride.StartUtc, decision.Interval.StartUtc);
        Assert.AreEqual(validOverride.EndUtc, decision.Interval.EndUtc);
    }

    [TestMethod]
    public void Evaluate_NextTransitionSkipsObscuredLowerPriorityBoundaries()
    {
        var date = new DateOnly(2025, 1, 13);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        var blackoutStart = CaptureScheduleTimeZone.Resolve(date, new TimeOnly(17, 0), timeZone, LocalBoundaryRole.Start);
        var blackoutEnd = CaptureScheduleTimeZone.Resolve(date, new TimeOnly(21, 0), timeZone, LocalBoundaryRole.End);
        var definition = Definition(
            [Profile("night")],
            [new CaptureWeeklyScheduleWindow("night", date.DayOfWeek, Fixed(18), Fixed(20), "night")]) with
        {
            Blackouts = [new CaptureScheduleBlackout("weather", blackoutStart, blackoutEnd)]
        };
        var preview = CaptureScheduleIntervalExpander.Expand(
            definition, date, 1, timeZone, Observer, new FixedSolarEvents());

        var decision = CaptureScheduleEvaluator.Evaluate(
            definition,
            preview,
            new CaptureScheduleEvaluationRequest(
                blackoutStart.AddMinutes(30), CaptureScheduleSafetyState.Available, false));

        Assert.AreEqual(CaptureScheduleAdmissionReason.Blackout, decision.Reason);
        Assert.AreEqual(blackoutEnd, decision.NextTransitionUtc);
    }

    [TestMethod]
    public void Expand_RejectsMixedSolarAlgorithmVersions()
    {
        var date = new DateOnly(2025, 1, 13);
        var definition = Definition(
            [Profile("night")],
            [new CaptureWeeklyScheduleWindow(
                "night",
                date.DayOfWeek,
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.Sunset),
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.Sunrise, DayOffset: 1),
                "night")]);

        var exception = Assert.ThrowsExactly<CaptureScheduleValidationException>(() =>
            CaptureScheduleIntervalExpander.Expand(
                definition,
                date,
                1,
                TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix"),
                Observer,
                new AlternatingSolarEvents()));

        Assert.AreEqual("schedule.solar-version.conflict", exception.ReasonCode);
    }

    [TestMethod]
    public void OptionalScheduleFacts_RemainAbsentFromLegacyJson()
    {
        var config = new CameraModuleConfig(
            Observer,
            new CameraModuleDescriptor("fixture"),
            new CameraRigConfig(
                new SensorProfile("fixture", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Perspective", 50, 10, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0)),
            CapturePipelineConfig.Empty);
        var cycle = new CaptureCycleEvidence(
            CaptureCadenceMode.MinimumStartInterval,
            CaptureStartReason.Initial,
            AutomaticControlOwnership.Disabled,
            AutomaticControlOwnership.Disabled,
            null,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            new CaptureControlDecisionEvidence(
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                TimeSpan.FromSeconds(1),
                0,
                TimeSpan.FromSeconds(1),
                0,
                CaptureControlDecisionReason.Disabled),
            DateTimeOffset.UnixEpoch)
        {
            MonotonicStartJitter = TimeSpan.Zero
        };

        var configJson = CaptureContractJson.SerializeToElement(config).GetRawText();
        var cycleJson = CaptureContractJson.SerializeToElement(cycle).GetRawText();

        Assert.DoesNotContain("schedule", configJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scheduleAdmission", cycleJson, StringComparison.Ordinal);
    }

    private static CaptureScheduleDefinition Definition(
        IReadOnlyList<CaptureScheduleSetpointProfile> profiles,
        IReadOnlyList<CaptureWeeklyScheduleWindow> windows)
        => new("capture-schedule-v1", profiles, windows);

    private static CaptureScheduleSetpointProfile Profile(string id, double gain = 1)
        => new(id, TimeSpan.FromSeconds(5), gain, TimeSpan.FromSeconds(10));

    private static CaptureWeeklyScheduleWindow Window(string id, DayOfWeek day, string profileId)
        => new(id, day, Fixed(18), Fixed(6, dayOffset: 1), profileId);

    private static CaptureScheduleBoundary Fixed(int hour, int dayOffset = 0)
        => new(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(hour, 0), DayOffset: dayOffset);

    private sealed class FixedSolarEvents(bool noEvents = false) : ISolarEventCalculator
    {
        public SolarEventResult Find(
            SolarEventKind kind,
            DateTimeOffset intervalStartUtc,
            DateTimeOffset intervalEndUtc,
            double latitudeDegrees,
            double longitudeDegrees,
            double elevationMeters)
            => new(kind, noEvents ? null : intervalStartUtc.AddHours(12), "fixed-solar-v1");
    }

    private sealed class AlternatingSolarEvents : ISolarEventCalculator
    {
        private int _callCount;

        public SolarEventResult Find(
            SolarEventKind kind,
            DateTimeOffset intervalStartUtc,
            DateTimeOffset intervalEndUtc,
            double latitudeDegrees,
            double longitudeDegrees,
            double elevationMeters)
            => new(kind, intervalStartUtc.AddHours(12), $"solar-v{++_callCount}");
    }
}
