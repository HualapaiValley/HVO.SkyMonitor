using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class CenteredWindowSchedulerTests
{
    private static readonly CameraKey Camera = new("agent", "camera");
    private static readonly Guid Run = new("ACFAA8D4-BD69-49F1-80B5-5020EFA48498");
    internal static readonly string RecipeIdentity = PayloadChecksum.ComputeSha256([0]);
    internal static readonly DateTimeOffset Start = new(2026, 11, 20, 2, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void LegacySlotBytesAndClosureIdentitySurviveCalendarMetadata()
    {
        var schedule = Schedule(4, 12);
        var exposures = Enumerable.Range(101, 5).Select(sequence => Exposure(sequence, (sequence - 101) * 2)).ToImmutableArray();
        var times = Times(exposures);
        var legacy = JsonSerializer.SerializeToUtf8Bytes(times.Select(time => new
        {
            time.ExpectedStartUtc,
            time.CadenceRevision,
            time.BoundaryReason,
            time.ExpectedIntegration
        }).ToArray());
        var reopenedTimes = JsonSerializer.Deserialize<ImmutableArray<CenteredWindowSlotTime>>(legacy);
        CollectionAssert.AreEqual(legacy, JsonSerializer.SerializeToUtf8Bytes(reopenedTimes));
        var closure = CenteredWindowScheduler.TryClose(schedule, reopenedTimes, exposures, schedule.DeadlineUtc);
        Assert.IsNotNull(closure);
        var oldClosure = JsonSerializer.SerializeToUtf8Bytes(new
        {
            closure.Schedule,
            closure.EvidenceCutoffUtc,
            Slots = closure.Slots.Select(slot => new
            {
                slot.Offset,
                Time = new
                {
                    slot.Time.ExpectedStartUtc,
                    slot.Time.CadenceRevision,
                    slot.Time.BoundaryReason,
                    slot.Time.ExpectedIntegration
                },
                slot.State,
                slot.Reason,
                slot.Input,
                slot.Products,
                slot.Attempt
            }).ToArray()
        });
        Assert.AreEqual(PayloadChecksum.ComputeSha256(oldClosure), closure.Identity);
    }

    [TestMethod]
    public void TwoOverlappingWindowsKeepTheirActualCenterAndFiveDistinctInputs()
    {
        var exposures = Enumerable.Range(101, 6).Select(sequence => Exposure(sequence, (sequence - 101) * 2)).ToImmutableArray();

        Assert.IsNull(Close(Schedule(4, 12), exposures[..5], Start.AddSeconds(7)));
        var first = CenteredWindowScheduler.TryClose(Schedule(4, 12), Times(exposures[..5]), exposures, Start.AddSeconds(12));
        var second = CenteredWindowScheduler.TryClose(Schedule(6, 14), Times(exposures[1..]), exposures, Start.AddSeconds(11));

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(103, first.Center.Input?.CaptureSequence);
        Assert.AreEqual(exposures[2].Products?.Linear, first.Center.Products?.Linear);
        Assert.AreEqual(104, second.Center.Input?.CaptureSequence);
        Assert.AreEqual(exposures[3].Products?.Linear, second.Center.Products?.Linear);
        CollectionAssert.AreEqual(new[] { -2, -1, 0, 1, 2 }, first.Slots.Select(slot => slot.Offset).ToArray());
        CollectionAssert.AreEqual(new long?[] { 101, 102, 103, 104, 105 }, Sequences(first));
        CollectionAssert.AreEqual(new long?[] { 102, 103, 104, 105, 106 }, Sequences(second));
        Assert.AreEqual(first.Slots[4].Input, second.Slots[3].Input);
        Assert.AreNotEqual(first.Identity, second.Identity);
    }

    [TestMethod]
    public void EmptySlotsSurviveEvenWhenCaptureCountersHaveNoGap()
    {
        // Same times as the 101, 102, 105, 106, 110 trace. Some drivers count only
        // successful readouts, so on that path the last three counters are 103–105.
        var calendar = Times([Empty(4), Empty(6), Empty(8), Empty(10), Empty(12)]);
        var normal = ImmutableArray.Create(Exposure(101, 0), Exposure(102, 2), Exposure(105, 8), Exposure(106, 10), Exposure(110, 18));
        var contiguous = ImmutableArray.Create(Exposure(101, 0), Exposure(102, 2), Exposure(103, 8), Exposure(104, 10), Exposure(105, 18));
        foreach (var observations in new[] { normal, contiguous })
        {
            var closure = CenteredWindowScheduler.TryClose(Schedule(8, 14), calendar, observations, Start.AddSeconds(20));
            Assert.IsNotNull(closure);
            Assert.IsTrue(closure.CanRegister);
            Assert.AreEqual(observations[2].Input, closure.Center.Input);
            CollectionAssert.AreEqual(new[] { -2, -1, 2 }, closure.Slots
                .Where(slot => slot.State == CenteredWindowSlotState.MissingExposure).Select(slot => slot.Offset).ToArray());
            Assert.HasCount(2, closure.Slots.Where(slot => slot.State == CenteredWindowSlotState.Eligible));
        }
    }

    [TestMethod]
    public void CadenceChangeMovesSlotButDoesNotMoveDeadlineOrPullAcrossBreak()
    {
        var slots = ImmutableArray.Create(
            Empty(0) with { Time = new(Start, "previous-cadence", "before-run-start") },
            Exposure(102, 2),
            Exposure(103, 4),
            Exposure(104, 7, "adaptive-revision"),
            Exposure(105, 11, "adaptive-revision") with
            {
                Time = new(Start.AddSeconds(11), "adaptive-revision", "generation-switched")
            });

        var window = Close(Schedule(4, 15), slots, Start.AddSeconds(8));

        Assert.IsNotNull(window);
        Assert.AreEqual(Start.AddSeconds(15), window.Schedule.DeadlineUtc);
        Assert.AreEqual(CenteredWindowSlotState.OutsideRun, window.Slots[0].State);
        Assert.AreEqual("before-run-start", window.Slots[0].Reason);
        Assert.AreEqual(CenteredWindowSlotState.Eligible, window.Slots[3].State);
        var actualSlotUtc = window.Slots[3].Time.ExpectedStartUtc;
        Assert.AreEqual(Start.AddSeconds(7), actualSlotUtc);
        Assert.AreEqual("adaptive-revision", window.Slots[3].Time.CadenceRevision);
        Assert.AreEqual("generation-switched", window.Slots[4].Reason);
        Assert.IsNull(window.Slots[4].Input);
    }

    [TestMethod]
    public void OnlyCommittedCadenceChangesMayMoveFutureSlots()
    {
        var schedule = Schedule(4, 12);
        var exposures = Enumerable.Range(101, 6).Select(sequence => Exposure(sequence, (sequence - 101) * 2)).ToImmutableArray();
        var starting = Times(exposures[..5]);
        var reference = exposures[2].Input!.Key;
        var checkpoint = new CenteredWindowCheckpoint(schedule, starting, ReferenceCapture: reference);
        var later = exposures.SetItem(4, Exposure(105, 9, "changed"));
        var change = new CenteredWindowCalendarChange(1, Start.AddSeconds(6), Start.AddSeconds(7), "fixed",
            "changed", TimeSpan.FromSeconds(3));
        var snapshot = new CenteredWindowJournalSnapshot(schedule.GenerationId, reference, [change], later);

        var changed = CenteredWindowScheduler.TryClose(checkpoint, snapshot, schedule.DeadlineUtc);
        Assert.IsNotNull(changed);
        Assert.AreEqual(later[4].Input, changed.Slots[4].Input);

        var uncommitted = CenteredWindowScheduler.TryClose(checkpoint,
            snapshot with { Changes = [change with { CommittedUtc = schedule.DeadlineUtc.AddTicks(1) }] }, schedule.DeadlineUtc);
        Assert.IsNotNull(uncommitted);
        Assert.AreEqual(CenteredWindowSlotState.MissingExposure, uncommitted.Slots[4].State);

        Assert.ThrowsExactly<InvalidDataException>(() => CenteredWindowScheduler.TryClose(checkpoint,
            snapshot with { Changes = [change with { NewCadenceRevision = "fixed" }] }, schedule.DeadlineUtc));
        var wrongSlot = change with
        {
            NewCadenceRevision = "fixed",
            Interval = TimeSpan.FromSeconds(2),
            FirstAffectedStartUtc = Start.AddSeconds(10),
            IsCommittedDispatchAdjustment = true
        };
        Assert.ThrowsExactly<InvalidDataException>(() => CenteredWindowScheduler.TryClose(checkpoint,
            snapshot with { Changes = [wrongSlot] }, schedule.DeadlineUtc));
        Assert.ThrowsExactly<InvalidDataException>(() => CenteredWindowScheduler.TryClose(checkpoint,
            snapshot with { Observations = later.SetItem(2, Exposure(206, 4)) }, schedule.DeadlineUtc));
        Assert.ThrowsExactly<InvalidDataException>(() => CenteredWindowScheduler.TryClose(checkpoint,
            snapshot with { GenerationId = "different-generation" }, schedule.DeadlineUtc));
    }

    [TestMethod]
    public void SettledMissRetainsReasonWithoutAttributingAnAttemptFailure()
    {
        var slots = ImmutableArray.Create(Exposure(101, 0), Exposure(102, 2), Exposure(103, 4),
            Empty(6) with { EmptySlotSettledUtc = Start.AddSeconds(7), EmptySlotReason = "nominal-slot-missed" }, Exposure(105, 8));
        var closure = Close(Schedule(4, 12), slots, Start.AddSeconds(9));

        Assert.IsNotNull(closure);
        Assert.AreEqual(CenteredWindowSlotState.MissingExposure, closure.Slots[3].State);
        Assert.AreEqual("nominal-slot-missed", closure.Slots[3].Reason);
    }

    [TestMethod]
    public void DeadlineSeparatesUncertainTargetLateAdmissionAndStalledProcessing()
    {
        var target104 = new ExposureAttemptKey(Camera, Run, 104, Guid.NewGuid());
        var uncertain104 = Empty(6) with
        {
            EmptySlotSettledUtc = Start.AddSeconds(7),
            EmptySlotReason = "target-acknowledgement-uncertain",
            EmptySlotKind = CenteredWindowEmptySlotKind.Uncertain,
            Attempt = target104
        };
        var late105 = Exposure(105, 8);
        late105 = late105 with { Input = late105.Input! with { AdmittedUtc = Start.AddSeconds(12) }, Products = null };
        var stalled102 = Exposure(102, 2);
        stalled102 = stalled102 with { Products = stalled102.Products! with { LinearSettledUtc = Start.AddSeconds(12) } };
        var observations = ImmutableArray.Create(Exposure(101, 0), stalled102, Exposure(103, 4), uncertain104, late105);

        Assert.IsNull(Close(Schedule(4, 10), observations, Start.AddSeconds(9)));
        var closure = Close(Schedule(4, 10), observations, Start.AddSeconds(20));

        Assert.IsNotNull(closure);
        Assert.AreEqual(Start.AddSeconds(10), closure.EvidenceCutoffUtc);
        Assert.AreEqual(CenteredWindowSlotState.Stalled, closure.Slots[1].State);
        Assert.AreEqual(CenteredWindowSlotState.UncertainAttempt, closure.Slots[3].State);
        Assert.AreEqual("target-acknowledgement-uncertain", closure.Slots[3].Reason);
        Assert.AreEqual(target104, closure.Slots[3].Attempt);
        Assert.IsNull(closure.Slots[4].Attempt);
        Assert.AreEqual(CenteredWindowSlotState.MissingExposure, closure.Slots[4].State);
        Assert.IsNull(closure.Slots[4].Input);
    }

    [TestMethod]
    public void LastExposureCanBeAdmittedWhileItsProcessingStallsPastTheDeadline()
    {
        var last = Exposure(105, 8);
        var slots = ImmutableArray.Create(Exposure(101, 0), Exposure(102, 2), Exposure(103, 4),
            Exposure(104, 6), last with
            {
                Products = new(Start.AddSeconds(9), LinearExposureDisposition.Pending, null, null, null)
            });

        Assert.IsNull(Close(Schedule(4, 10), slots, Start.AddSeconds(9)));
        var closed = Close(Schedule(4, 10), slots, Start.AddSeconds(10));

        Assert.IsNotNull(closed);
        Assert.AreEqual(last.Input, closed.Slots[4].Input);
        Assert.AreEqual(CenteredWindowSlotState.Stalled, closed.Slots[4].State);
        Assert.IsNull(closed.Slots[4].Products);
        Assert.HasCount(4, closed.Slots.Where(slot => slot.State == CenteredWindowSlotState.Eligible));
    }

    [TestMethod]
    public async Task LateOptionalEvidenceDoesNotHideAlreadyQualifiedCenterPixels()
    {
        var center = Exposure(103, 4);
        var stalledMeasurement = center.Products!.Measurement! with { SettledUtc = Start.AddSeconds(11) };
        var linear = center.Products.Linear!;
        var input = center.Input!;
        var pin = new CenteredStackExposurePin(input.Key, input.CaptureSequence, input.Timing.ExposureStartedUtc,
            input.Timing.ExposureEndedUtc, (input.Timing.ExposureEndedUtc - input.Timing.ExposureStartedUtc).Ticks,
            new(input.RawArtifactId, RecipeIdentity, input.RawPayloadSha256, input.RawDescriptorSha256, null, RecipeIdentity),
            new(linear.ArtifactId, RecipeIdentity, linear.PayloadSha256, null, null, RecipeIdentity),
            new(Guid.NewGuid(), RecipeIdentity, RecipeIdentity, null, null, RecipeIdentity),
            new(stalledMeasurement.Lookup.Product!.ArtifactId, RecipeIdentity, RecipeIdentity, null, null, RecipeIdentity),
            null, RecipeIdentity, RecipeIdentity, RecipeIdentity,
            new(2, 2, 4, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian, 16, 16,
                FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 65535, 8));
        var slots = ImmutableArray.Create(
            Exposure(101, 0), Exposure(102, 2),
            center with { Products = center.Products with { Measurement = stalledMeasurement, RegistrationPin = pin } },
            Exposure(104, 6), Exposure(105, 8));

        Assert.IsNull(Close(Schedule(4, 10), slots, Start.AddSeconds(9)));
        var closed = Close(Schedule(4, 10), slots, Start.AddSeconds(12));

        Assert.IsNotNull(closed);
        Assert.IsTrue(closed.CanRegister);
        Assert.AreEqual(center.Products.Linear, closed.Center.Products?.Linear);
        Assert.IsNull(closed.Center.Products?.Measurement);
        Assert.AreEqual(pin with { Measurement = null }, closed.Center.Products?.RegistrationPin);
        Assert.AreEqual("plate-cadence-skipped", closed.Center.Products?.Plate?.Lookup.Reason);

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var disk = SerializeEarlierPin(closed, web);
        var earlier = JsonSerializer.Deserialize<CenteredWindowClosure>(disk, web);
        Assert.IsNotNull(earlier);
        var historical = CenteredWindowHistoricalClosure.RestoreIdentity(earlier, disk);
        Assert.AreEqual(PayloadChecksum.ComputeSha256(SerializeEarlierPin(closed,
            new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })), historical.Identity);
        var attempt = await CenteredWindowRegistrationResult.ExecuteAsync(historical, null!, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, attempt.Outcome.Status);
        Assert.AreEqual("registration.legacy-window-pins-unavailable", attempt.Outcome.ReasonCode);
    }

    private static byte[] SerializeEarlierPin(CenteredWindowClosure closure, JsonSerializerOptions options)
    {
        var names = options.PropertyNamingPolicy;
        string Name(string property) => names?.ConvertName(property) ?? property;
        var document = JsonSerializer.SerializeToNode(closure, options)!;
        var pin = document[Name("Slots")]![2]![Name("Products")]![Name("RegistrationPin")]!.AsObject();
        foreach (var input in new[] { "Raw", "Linear", "ValidityMask" })
            Assert.IsTrue(pin[Name(input)]!.AsObject().Remove(Name("EnvelopeSha256")));
        Assert.IsTrue(pin.Remove(Name("MeasurementBindingSha256")));
        Assert.IsTrue(pin.Remove(Name("LinearLayout")));
        pin.Add(Name("MeasurementGeometrySha256"), RecipeIdentity);
        return JsonSerializer.SerializeToUtf8Bytes(document, options);
    }

    [TestMethod]
    public void EmptyOrRejectedCenterCannotBeReplacedAndOutOfToleranceInputIsExcluded()
    {
        var offTime = Exposure(102, 2);
        offTime = offTime with { Time = new(Start.AddSeconds(1), "fixed") };
        var slots = ImmutableArray.Create(Exposure(101, 0), offTime, Empty(4), Exposure(104, 6), Exposure(105, 8));

        var missingCenter = Close(Schedule(4, 12), slots, Start.AddSeconds(12));
        Assert.IsNotNull(missingCenter);
        Assert.IsFalse(missingCenter.CanRegister);
        Assert.AreEqual(CenteredWindowSlotState.MissingExposure, missingCenter.Center.State);
        Assert.AreEqual(CenteredWindowSlotState.OutsideTolerance, missingCenter.Slots[1].State);

        var center = Exposure(103, 4) with { Products = new(Start.AddSeconds(5), LinearExposureDisposition.Unsupported, null, null, null, "no-qualified-calibration") };
        var rejectedCenter = Close(Schedule(4, 12), slots.SetItem(2, center), Start.AddSeconds(9));

        Assert.IsNotNull(rejectedCenter);
        Assert.IsFalse(rejectedCenter.CanRegister);
        Assert.AreEqual(center.Input, rejectedCenter.Center.Input);
        Assert.AreEqual(CenteredWindowSlotState.Rejected, rejectedCenter.Center.State);
        Assert.AreEqual("no-qualified-calibration", rejectedCenter.Center.Reason);
    }

    [TestMethod]
    public void MalformedSlotCalendarAndRepeatedArtifactsAreRejected()
    {
        var slots = Enumerable.Range(101, 5).Select(sequence => Exposure(sequence, (sequence - 101) * 2)).ToImmutableArray();
        var repeatedLinear = slots[0].Products!.Linear! with { ArtifactId = slots[1].Products!.Linear!.ArtifactId };
        var repeated = slots.SetItem(0, slots[0] with { Products = slots[0].Products! with { Linear = repeatedLinear } });
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Close(Schedule(4, 12), repeated, Start.AddSeconds(12)));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Close(Schedule(4, 12) with { SlotTolerance = TimeSpan.FromSeconds(1) }, slots, Start.AddSeconds(12)));
    }

    internal static ImmutableArray<CenteredWindowSlotTime> Times(ImmutableArray<CenteredWindowSlotObservation> observations) =>
        observations.Select(static observation => observation.Time).ToImmutableArray();

    private static CenteredWindowClosure? Close(
        CenteredWindowSchedule schedule, ImmutableArray<CenteredWindowSlotObservation> observations, DateTimeOffset now)
        => CenteredWindowScheduler.TryClose(schedule, Times(observations), observations, now);

    internal static CenteredWindowSchedule Schedule(int startSeconds, int deadlineSeconds) =>
        new(Camera, Run, "generation", Start.AddSeconds(startSeconds), TimeSpan.FromMilliseconds(200), Start.AddSeconds(deadlineSeconds), RecipeIdentity, Start.AddSeconds(deadlineSeconds + 30));

    internal static CenteredWindowSlotObservation Exposure(int sequence, int startSeconds, string cadence = "fixed")
    {
        var start = Start.AddSeconds(startSeconds);
        var end = start.AddMilliseconds(700);
        var admitted = start.AddMilliseconds(800);
        var key = new ExposureKey(Camera, Run, Guid.NewGuid());
        var settled = start.AddSeconds(1);
        var measurement = Product(key, "measurement");
        return new(new(start, cadence),
            new(key, sequence, new(start, start, end, end, admitted), Guid.NewGuid(), RecipeIdentity, RecipeIdentity, admitted),
            new(settled, LinearExposureDisposition.QualifiedNormalizationOnly, Product(key, "linear"),
                new(settled, new(key, measurement.Slot, ExposureProductAvailability.Available, sequence, measurement, null)),
                new(settled, new(key, new(key.CaptureId, "plate", "plate", "default"), ExposureProductAvailability.Unavailable,
                    sequence, null, "plate-cadence-skipped"))));
    }

    internal static CenteredWindowSlotObservation Empty(int seconds) => new(new(Start.AddSeconds(seconds), "fixed"));

    private static ExposureProductRef Product(ExposureKey key, string producer) =>
        new(key, new(key.CaptureId, producer, producer, "default"), Guid.NewGuid(), RecipeIdentity, RecipeIdentity, null);

    private static long?[] Sequences(CenteredWindowClosure closure) =>
        closure.Slots.Select(slot => slot.Input?.CaptureSequence).ToArray();
}
