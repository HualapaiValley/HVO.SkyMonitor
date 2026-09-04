using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureProfileFormModelTests
{
    [TestMethod]
    public void RoundTrip_PreservesEveryFieldIncludingOpaqueAndUnlistedOnes()
    {
        var basis = RichProfile();

        var model = CaptureProfileFormModel.FromProfile(basis);
        Assert.IsTrue(model.TryApply(basis, out var applied, out var errors), string.Join(" ", errors));

        Assert.AreEqual(
            CameraAgentScheduleUiService.SerializeProfile(basis),
            CameraAgentScheduleUiService.SerializeProfile(applied));
        Assert.AreEqual(basis.Module.Options!.Value.GetRawText(), applied.Module.Options!.Value.GetRawText());
        Assert.AreEqual(basis.ProcessingSteps[0].Options!.Value.GetRawText(), applied.ProcessingSteps[0].Options!.Value.GetRawText());
        Assert.AreSame(basis.Rig.Readout, applied.Rig.Readout);
        Assert.AreSame(basis.Rig.Sensor, applied.Rig.Sensor);
        Assert.AreSame(basis.Rig.ControlPolicy!.Metering, applied.Rig.ControlPolicy!.Metering);
        Assert.AreSame(basis.Rig.ControlPolicy.SolarRegimes, applied.Rig.ControlPolicy.SolarRegimes);
        Assert.AreSame(basis.Schedule.DateExceptions, applied.Schedule.DateExceptions);
        Assert.AreEqual(basis.Rig.Optics.PrincipalPointX, applied.Rig.Optics.PrincipalPointX);
        Assert.AreEqual(basis.Rig.Pipeline.CaptureFailureInitialDelay, applied.Rig.Pipeline.CaptureFailureInitialDelay);
        Assert.AreEqual(basis.Rig.Pipeline.Envelope!.Hysteresis, applied.Rig.Pipeline.Envelope!.Hysteresis);
        Assert.AreEqual(1, model.DateExceptionCount);
        Assert.IsTrue(model.HasReadoutProfile);
        Assert.IsTrue(model.HasMeteringPolicy);
        Assert.IsTrue(model.HasEnvelope);
    }

    [TestMethod]
    public void RoundTrip_WithoutControlPolicyKeepsNoneAndPreservesAwkwardPrecision()
    {
        var baseline = SchedulePageTests.Profile();
        Assert.IsNull(baseline.Rig.ControlPolicy);
        var basis = baseline with
        {
            Rig = baseline.Rig with
            {
                Optics = baseline.Rig.Optics with { RollDegrees = 0.30000000000000004, FocalLengthMillimeters = 1e-7 },
                Pipeline = baseline.Rig.Pipeline with
                {
                    DayExposure = TimeSpan.FromTicks(12345678),
                    CaptureInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond * 7 + 3),
                    NightGain = 1.0 / 3.0
                }
            },
            Schedule = baseline.Schedule with
            {
                SetpointProfiles = [baseline.Schedule.SetpointProfiles[0] with { Exposure = TimeSpan.FromTicks(1), Gain = 123456789.123456789 }],
                WeeklyWindows =
                [
                    baseline.Schedule.WeeklyWindows[0] with
                    {
                        Start = new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(18, 30, 45, 500)),
                        End = new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.Sunrise, Offset: TimeSpan.FromTicks(-TimeSpan.TicksPerMinute * 3 - 1), DayOffset: 1, NoEventFallbackLocalTime: new TimeOnly(6, 0, 0, 0, 1))
                    }
                ]
            }
        };

        var model = CaptureProfileFormModel.FromProfile(basis);
        Assert.IsTrue(model.TryApply(basis, out var applied, out var errors), string.Join(" ", errors));

        Assert.IsNull(applied.Rig.ControlPolicy);
        Assert.AreEqual(
            CameraAgentScheduleUiService.SerializeProfile(basis),
            CameraAgentScheduleUiService.SerializeProfile(applied));
        Assert.AreEqual(basis.Rig.Pipeline.DayExposure, applied.Rig.Pipeline.DayExposure);
        Assert.AreEqual(basis.Schedule.WeeklyWindows[0].Start.LocalTime, applied.Schedule.WeeklyWindows[0].Start.LocalTime);
        Assert.AreEqual(basis.Schedule.WeeklyWindows[0].End.Offset, applied.Schedule.WeeklyWindows[0].End.Offset);

        model.GainControl = nameof(AutomaticControlOwnership.Disabled);
        Assert.IsTrue(model.TryApply(basis, out var withPolicy, out errors), string.Join(" ", errors));
        Assert.AreEqual(AutomaticControlOwnership.Disabled, withPolicy.Rig.ControlPolicy!.GainControl);
    }

    [TestMethod]
    public void TypedEdits_ChangeOnlyTheEditedFields()
    {
        var basis = RichProfile();
        var model = CaptureProfileFormModel.FromProfile(basis);

        model.NightGain = "2.5";
        model.HorizontalFlip = true;
        model.ExposureControl = nameof(AutomaticControlOwnership.CameraNative);
        model.Setpoints[0].ExposureMilliseconds = "1500";
        model.WeeklyWindows[0].Start.OffsetMinutes = "-30";
        model.AddBlackout();
        model.Blackouts[^1].Id = "maintenance";
        model.Blackouts[^1].StartUtc = "2026-09-10T02:00:00Z";
        model.Blackouts[^1].EndUtc = "2026-09-10T03:00:00Z";

        Assert.IsTrue(model.TryApply(basis, out var applied, out var errors), string.Join(" ", errors));

        Assert.AreEqual(2.5, applied.Rig.Pipeline.NightGain);
        Assert.IsTrue(applied.Rig.Optics.HorizontalFlip);
        Assert.AreEqual(AutomaticControlOwnership.CameraNative, applied.Rig.ControlPolicy!.ExposureControl);
        Assert.AreEqual(AutomaticControlOwnership.HostMetered, applied.Rig.ControlPolicy.GainControl);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1500), applied.Schedule.SetpointProfiles[0].Exposure);
        Assert.AreEqual(TimeSpan.FromMinutes(-30), applied.Schedule.WeeklyWindows[0].Start.Offset);
        Assert.AreEqual(CaptureScheduleBoundaryKind.Sunset, applied.Schedule.WeeklyWindows[0].Start.Kind);
        Assert.AreEqual("maintenance", applied.Schedule.Blackouts![^1].Id);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-10T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture), applied.Schedule.Blackouts[^1].StartUtc);
        Assert.AreEqual(basis.Rig.Pipeline.DayGain, applied.Rig.Pipeline.DayGain);
        Assert.AreEqual(basis.Rig.Optics.RollDegrees, applied.Rig.Optics.RollDegrees);
        Assert.AreEqual(basis.Module.Options!.Value.GetRawText(), applied.Module.Options!.Value.GetRawText());
    }

    [TestMethod]
    public void InvalidValues_ReportLabelledErrorsAndLeaveTheBasisUntouched()
    {
        var basis = RichProfile();
        var model = CaptureProfileFormModel.FromProfile(basis);
        model.DayGain = "high";
        model.CadenceMode = "Whenever";
        model.Setpoints[0].Id = " ";
        model.WeeklyWindows[0].End = new CaptureProfileFormModel.BoundaryRow { Kind = nameof(CaptureScheduleBoundaryKind.FixedLocalTime) };
        model.Blackouts.Add(new CaptureProfileFormModel.BlackoutRow { Id = "bad", StartUtc = "yesterday", EndUtc = "2026-09-10T03:00:00Z" });

        Assert.IsFalse(model.TryApply(basis, out var applied, out var errors));

        Assert.AreSame(basis, applied);
        CollectionAssert.Contains(errors.ToArray(), "Day gain must be a number.");
        CollectionAssert.Contains(errors.ToArray(), "Cadence mode is not a supported value.");
        CollectionAssert.Contains(errors.ToArray(), "Every setpoint profile needs an identifier.");
        CollectionAssert.Contains(errors.ToArray(), "Window 'weekly-night' end needs a local time because it is a fixed boundary.");
        CollectionAssert.Contains(errors.ToArray(), "Blackout 'bad' start must be a UTC timestamp.");
    }

    [TestMethod]
    public void AddRows_UseUniqueIdentifiersAndTheFirstSetpoint()
    {
        var model = CaptureProfileFormModel.FromProfile(RichProfile());

        model.AddSetpoint();
        model.AddSetpoint();
        model.AddWeeklyWindow();

        CollectionAssert.AllItemsAreUnique(model.Setpoints.Select(static row => row.Id).ToArray());
        Assert.AreEqual(model.Setpoints[0].Id, model.WeeklyWindows[^1].SetpointProfileId);
        Assert.AreEqual(nameof(CaptureScheduleBoundaryKind.Sunset), model.WeeklyWindows[^1].Start.Kind);
        Assert.AreEqual("1", model.WeeklyWindows[^1].End.DayOffset);
    }

    private static LocalCaptureProfileDefinition RichProfile()
    {
        var baseline = SchedulePageTests.Profile();
        return baseline with
        {
            SchemaVersion = LocalCaptureProfileDefinition.CurrentSchemaVersion,
            DependencyPolicy = CapturePipelineDependencyPolicy.RejectEnabledDependent,
            Module = new CameraModuleDescriptor("test", JsonSerializer.SerializeToElement(new { secret = "module-secret" })),
            ProcessingSteps =
            [
                new CaptureProcessingStepConfig("Preview", "preview", 10, JsonSerializer.SerializeToElement(new { outputVariant = "display" }), ["$raw"], Enabled: true)
            ],
            Rig = baseline.Rig with
            {
                Optics = baseline.Rig.Optics with { PrincipalPointX = 1024.5, LensKind = LensKind.Fisheye, RollDegrees = 1.25 },
                Readout = new SensorReadoutProfile(
                    new SensorCrop(0, 0, baseline.Rig.Sensor.WidthPixels, baseline.Rig.Sensor.HeightPixels),
                    1, 1, FrameBinningAlgorithm.IdentityV1, CameraPixelFormat.Mono16, 12, 16,
                    FrameSamplePacking.ByteAligned, FrameStoredCodeTransform.IdentityV1, FrameLevelCodeSpace.NativeSample, 0, 4095),
                Pipeline = baseline.Rig.Pipeline with
                {
                    CaptureFailureInitialDelay = TimeSpan.FromSeconds(3),
                    Envelope = new ExposureEnvelope(
                        TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(30), 0, 40,
                        new ExposureDefaults(TimeSpan.FromMilliseconds(10), 0),
                        new ExposureDefaults(TimeSpan.FromSeconds(20), 30),
                        1800, Hysteresis: 0.1)
                },
                ControlPolicy = new CameraControlPolicy
                {
                    ExposureControl = AutomaticControlOwnership.HostMetered,
                    GainControl = AutomaticControlOwnership.HostMetered,
                    Metering = new CaptureMeteringPolicy { XStride = 8 },
                    SolarRegimes = new CaptureSolarRegimePolicy { DayAltitudeThresholdDegrees = -3 },
                    Temperature = new TemperatureControlDirective { Mode = TemperatureControlMode.Target, TargetC = -5 }
                }
            },
            Schedule = baseline.Schedule with
            {
                WeeklyWindows =
                [
                    new CaptureWeeklyScheduleWindow(
                        "weekly-night", DayOfWeek.Thursday,
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.Sunset, Offset: TimeSpan.FromMinutes(-15)),
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.Sunrise, DayOffset: 1, NoEventFallbackLocalTime: new TimeOnly(6, 0)),
                        "night")
                ],
                DateExceptions = [new CaptureScheduleDateRule("holiday", new DateOnly(2026, 12, 25), true)],
                Blackouts = [new CaptureScheduleBlackout("outage", DateTimeOffset.Parse("2026-09-05T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.Parse("2026-09-05T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture))]
            }
        };
    }
}
