using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

// These are the scheduler's durable projections. The capture owner supplies the
// run and the slot calendar, including its explicitly effective cadence changes.
internal sealed record CenteredWindowSchedule(
    CameraKey Camera,
    Guid RunId,
    string GenerationId,
    DateTimeOffset ReferenceStartUtc,
    TimeSpan SlotTolerance,
    DateTimeOffset DeadlineUtc,
    string RegistrationRecipeIdentitySha256,
    DateTimeOffset RegistrationDeadlineUtc)
{
    // A retry with a changed deadline is a conflict on the original slot.
    internal string Identity => PayloadChecksum.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(new
    {
        Schema = "cameraagent-centered-window-v1",
        Camera,
        RunId,
        GenerationId,
        ReferenceStartUtc
    }));
}

internal sealed record CenteredWindowSlotTime(
    DateTimeOffset ExpectedStartUtc,
    string CadenceRevision,
    string? BoundaryReason = null,
    TimeSpan ExpectedIntegration = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimeSpan? Tolerance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Continuous = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PlannedSlot = null);

internal sealed record CenteredWindowInput(
    ExposureKey Key,
    long CaptureSequence,
    CaptureTimingDescriptor Timing,
    Guid RawArtifactId,
    string RawDescriptorSha256,
    string RawPayloadSha256,
    DateTimeOffset AdmittedUtc);

internal sealed record CenteredWindowProductEvidence(DateTimeOffset SettledUtc, ExposureProductLookup Lookup);

internal sealed record CenteredWindowSlotProducts(
    DateTimeOffset LinearSettledUtc,
    LinearExposureDisposition Disposition,
    ExposureProductRef? Linear,
    CenteredWindowProductEvidence? Measurement,
    CenteredWindowProductEvidence? Plate,
    string? RejectionReason = null,
    CenteredStackExposurePin? RegistrationPin = null);

internal sealed record CenteredWindowSlotObservation(
    CenteredWindowSlotTime Time,
    CenteredWindowInput? Input = null,
    CenteredWindowSlotProducts? Products = null,
    DateTimeOffset? EmptySlotSettledUtc = null,
    string? EmptySlotReason = null,
    ExposureAttemptKey? Attempt = null,
    CenteredWindowEmptySlotKind EmptySlotKind = CenteredWindowEmptySlotKind.Missed);

internal enum CenteredWindowEmptySlotKind
{
    Missed,
    Failed,
    Uncertain
}

public enum CenteredWindowSlotState
{
    Eligible,
    OutsideRun,
    MissingExposure,
    FailedAttempt,
    UncertainAttempt,
    OutsideTolerance,
    Stalled,
    Rejected,
    InvalidEvidence
}

internal sealed record CenteredWindowClosedSlot(
    int Offset,
    CenteredWindowSlotTime Time,
    CenteredWindowSlotState State,
    string? Reason,
    CenteredWindowInput? Input,
    CenteredWindowSlotProducts? Products,
    ExposureAttemptKey? Attempt = null);

internal sealed record CenteredWindowClosure(
    CenteredWindowSchedule Schedule,
    DateTimeOffset EvidenceCutoffUtc,
    ImmutableArray<CenteredWindowClosedSlot> Slots)
{
    internal CenteredWindowClosedSlot Center => Slots[CenteredWindowScheduler.CenterIndex];
    internal bool CanRegister => Center.State == CenteredWindowSlotState.Eligible;
    internal string? HistoricalIdentity { get; init; }
    internal bool HasLegacyRegistrationPins => HistoricalIdentity is not null;
    internal string Identity => HistoricalIdentity ?? PayloadChecksum.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(this));
}

internal static class CenteredWindowScheduler
{
    internal const int SlotCount = 5;
    internal const int CenterIndex = 2;

    internal static void ValidateSchedule(CenteredWindowSchedule schedule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule.Camera.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule.Camera.CameraId);
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule.GenerationId);
        if (schedule.RunId == Guid.Empty || schedule.ReferenceStartUtc.Offset != TimeSpan.Zero || schedule.DeadlineUtc.Offset != TimeSpan.Zero ||
            schedule.RegistrationDeadlineUtc.Offset != TimeSpan.Zero ||
            schedule.DeadlineUtc <= schedule.ReferenceStartUtc || schedule.RegistrationDeadlineUtc <= schedule.DeadlineUtc ||
            schedule.SlotTolerance < TimeSpan.Zero ||
            !IsChecksum(schedule.RegistrationRecipeIdentitySha256))
        {
            throw new InvalidDataException("A centered window needs UTC slot times, a finite deadline and a nonnegative tolerance.");
        }
    }

    // The journal reader provides the five slots from one transaction as of the
    // cutoff. A later notification can prompt earlier evaluation, but cannot
    // change either the deadline or the evidence visible to it.
    internal static CenteredWindowClosure? TryClose(
        CenteredWindowCheckpoint checkpoint,
        CenteredWindowJournalSnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        var schedule = checkpoint.Schedule;
        var cutoff = nowUtc < schedule.DeadlineUtc ? nowUtc : schedule.DeadlineUtc;
        if (snapshot.GenerationId != schedule.GenerationId)
            throw new InvalidDataException("The centered evidence belongs to another generation.");

        var times = snapshot.ResolvedTimes.IsDefault
            ? ResolveCalendar(checkpoint, snapshot.Changes, cutoff) : snapshot.ResolvedTimes;
        var reference = checkpoint.ReferenceCapture ?? snapshot.AdmittedReference;
        if ((snapshot.AdmittedReference is not null && checkpoint.ReferenceAttempt is null && checkpoint.ReferenceCapture is null) ||
            (checkpoint.ReferenceCapture is not null && snapshot.AdmittedReference is not null &&
                checkpoint.ReferenceCapture != snapshot.AdmittedReference) ||
            (reference is not null && (reference.Camera != schedule.Camera || reference.RunId != schedule.RunId)))
            throw new InvalidDataException("The admitted center does not match the reserved reference.");

        foreach (var item in snapshot.Observations)
        {
            if (item.Time.ExpectedStartUtc != schedule.ReferenceStartUtc || item.Input is not { } input || input.AdmittedUtc > cutoff)
                continue;

            if (input.Key != reference || (checkpoint.ReferenceAttempt is not null && item.Attempt != checkpoint.ReferenceAttempt))
                throw new InvalidDataException("The center exposure does not match its reserved reference.");
        }
        return TryClose(schedule, times, snapshot.Observations, nowUtc);
    }

    internal static ImmutableArray<CenteredWindowSlotTime> ResolveCalendar(
        CenteredWindowCheckpoint checkpoint, ImmutableArray<CenteredWindowCalendarChange> changes, DateTimeOffset cutoff)
    {
        var times = checkpoint.ProjectedTimes.ToBuilder();
        var lastOffset = -1;
        var ended = false;
        foreach (var change in changes)
        {
            if (change.CommittedUtc > cutoff)
                continue;

            var index = change.AfterOffset + CenterIndex;
            var sameCadence = change.Interval is not null && change.NewCadenceRevision == change.PriorCadenceRevision;
            if (change.AfterOffset is < 0 or > 1 || change.AfterOffset <= lastOffset || ended ||
                change.AnchorStartUtc != times[index].ExpectedStartUtc ||
                change.PriorCadenceRevision != times[index].CadenceRevision ||
                change.CommittedUtc.Offset != TimeSpan.Zero ||
                (change.Interval is null) == string.IsNullOrWhiteSpace(change.BoundaryReason) ||
                (change.FirstAffectedStartUtc is { } start &&
                    (change.Interval is null || start.Offset != TimeSpan.Zero || start <= change.AnchorStartUtc)) ||
                (change.Interval is { } interval && (interval <= TimeSpan.Zero ||
                    string.IsNullOrWhiteSpace(change.NewCadenceRevision))) ||
                sameCadence != change.IsCommittedDispatchAdjustment ||
                (sameCadence && (change.FirstAffectedStartUtc is not { } requested || change.Interval is not { } cadence ||
                    (change.Tolerance is null && change.Continuous != true &&
                        ((requested - change.AnchorStartUtc) - cadence).Duration() > cadence / 2))))
                throw new InvalidDataException("A centered cadence change is not anchored to its committed predecessor.");

            for (var next = index + 1; next < times.Count; next++)
            {
                times[next] = change.Interval is { } step
                    ? times[next] with
                    {
                        ExpectedStartUtc = (next == index + 1 ? change.FirstAffectedStartUtc : null) ??
                            times[next - 1].ExpectedStartUtc + step,
                        CadenceRevision = change.NewCadenceRevision!,
                        ExpectedIntegration = change.ExpectedIntegration ?? times[next].ExpectedIntegration,
                        Tolerance = change.Tolerance ?? times[next].Tolerance,
                        Continuous = change.Continuous ?? times[next].Continuous
                    }
                    : times[next] with { BoundaryReason = change.BoundaryReason };
            }
            lastOffset = change.AfterOffset;
            ended = change.BoundaryReason is not null;
        }
        return times.ToImmutable();
    }

    internal static CenteredWindowClosure? TryClose(
        CenteredWindowSchedule schedule,
        ImmutableArray<CenteredWindowSlotTime> times,
        ImmutableArray<CenteredWindowSlotObservation> observations,
        DateTimeOffset nowUtc)
    {
        ValidateSchedule(schedule);
        if (times.Length != SlotCount ||
            times[CenterIndex].ExpectedStartUtc != schedule.ReferenceStartUtc ||
            times[CenterIndex].BoundaryReason is not null)
        {
            throw new InvalidDataException("A centered window must describe exactly five slots in one run, with the scheduled reference at offset zero.");
        }

        for (var index = 0; index < SlotCount; index++)
        {
            var time = times[index];
            var tolerance = time.Tolerance ?? schedule.SlotTolerance;
            if (time.ExpectedStartUtc.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(time.CadenceRevision) ||
                time.ExpectedIntegration < TimeSpan.Zero || tolerance < TimeSpan.Zero ||
                (time.PlannedSlot is { } slot && (slot <= 0 ||
                    (times[CenterIndex].PlannedSlot is { } center && slot - center != index - CenterIndex))) ||
                (index > 0 && !Separated(times[index - 1], time, schedule.SlotTolerance)))
                throw new InvalidDataException("Centered window slot times must be UTC, ordered, and unambiguous within the pinned tolerance.");
        }

        var expired = nowUtc >= schedule.DeadlineUtc;
        var cutoff = expired ? schedule.DeadlineUtc : nowUtc.ToUniversalTime();
        var evidenceBySlot = IndexEvidence(times, observations, cutoff);
        var captures = new HashSet<Guid>();
        var rawArtifacts = new HashSet<Guid>();
        var linearArtifacts = new HashSet<Guid>();
        var slots = ImmutableArray.CreateBuilder<CenteredWindowClosedSlot>(SlotCount);
        for (var index = 0; index < SlotCount; index++)
        {
            var time = times[index];
            var observation = evidenceBySlot.GetValueOrDefault(SlotKey(time)) ?? new(time);
            var slot = SettleSlot(observation with { Time = time }, index - CenterIndex, schedule, cutoff, expired);
            if (slot is null)
                continue;

            if ((slot.Input is { } input &&
                    (!captures.Add(input.Key.CaptureId) || !rawArtifacts.Add(input.RawArtifactId))) ||
                (slot.Products?.Linear is { } linear && !linearArtifacts.Add(linear.ArtifactId)))
            {
                throw new InvalidDataException("A centered window cannot use the same capture or artifact twice.");
            }

            slots.Add(slot);
        }

        if (slots.Count != SlotCount)
            return null;

        // The actual recipe requires measurements for its independent pair
        // check, including the plate route. Pending plate evidence can use that
        // recipe's centroid fallback. A reference alone needs neither product.
        if (!expired && slots[CenterIndex].State == CenteredWindowSlotState.Eligible &&
            slots.Count(slot => slot.State == CenteredWindowSlotState.Eligible) > 1 &&
            slots.Any(slot => slot.State == CenteredWindowSlotState.Eligible && slot.Products?.Measurement is null))
            return null;

        return new(schedule, cutoff, slots.MoveToImmutable());
    }

    private static bool Separated(CenteredWindowSlotTime previous, CenteredWindowSlotTime next, TimeSpan legacyTolerance)
    {
        var gap = next.ExpectedStartUtc - previous.ExpectedStartUtc;
        var nextTolerance = next.Tolerance ?? legacyTolerance;
        // Continuous observations are bound to exact dispatched attempts. Adjacent
        // requests need chronological order, but they do not promise a time grid.
        return gap > TimeSpan.Zero && (previous.Continuous || next.Continuous ||
            (previous.PlannedSlot.HasValue && next.PlannedSlot.HasValue) ||
            (gap > nextTolerance && gap - nextTolerance > (previous.Tolerance ?? legacyTolerance)));
    }

    private static (long? Ordinal, DateTimeOffset LegacyStart) SlotKey(CenteredWindowSlotTime time) =>
        (time.PlannedSlot, time.PlannedSlot.HasValue ? default : time.ExpectedStartUtc);

    private static Dictionary<(long?, DateTimeOffset), CenteredWindowSlotObservation> IndexEvidence(
        ImmutableArray<CenteredWindowSlotTime> times,
        ImmutableArray<CenteredWindowSlotObservation> observations,
        DateTimeOffset cutoff)
    {
        var slots = times.Where(static time => time.BoundaryReason is null).ToDictionary(SlotKey);
        var index = new Dictionary<(long?, DateTimeOffset), CenteredWindowSlotObservation>();
        foreach (var observation in observations)
        {
            var key = SlotKey(observation.Time);
            if (!slots.TryGetValue(key, out var pinned) ||
                !(observation.Input is { } input && input.AdmittedUtc <= cutoff ||
                    observation.EmptySlotSettledUtc is { } settled && settled <= cutoff))
                continue;

            if (observation.Time.CadenceRevision != pinned.CadenceRevision ||
                observation.Time.ExpectedStartUtc != pinned.ExpectedStartUtc)
                throw new InvalidDataException("The centered observation does not match the cadence effective at its slot.");

            if (!index.TryAdd(key, observation) && index[key] != observation)
                throw new InvalidDataException("Multiple durable observations occupy one centered slot.");
        }
        return index;
    }

    private static CenteredWindowClosedSlot? SettleSlot(
        CenteredWindowSlotObservation observation,
        int offset,
        CenteredWindowSchedule schedule,
        DateTimeOffset cutoff,
        bool expired)
    {
        var time = observation.Time;
        if (time.BoundaryReason is { } boundary)
        {
            return new(offset, time, CenteredWindowSlotState.OutsideRun, boundary, null, null);
        }

        var attempt = observation.Attempt;
        if (attempt is not null && (attempt.Camera != schedule.Camera || attempt.RunId != schedule.RunId ||
            attempt.Ordinal <= 0 || attempt.AttemptId == Guid.Empty))
            throw new InvalidDataException("A centered window slot has inconsistent target-attempt evidence.");

        var input = observation.Input;
        var admitted = input is not null && input.AdmittedUtc <= cutoff;
        var empty = observation.EmptySlotSettledUtc is { } emptyUtc && emptyUtc <= cutoff;
        if (!admitted)
        {
            if (!expired && !empty)
                return null;

            var kind = empty ? observation.EmptySlotKind : CenteredWindowEmptySlotKind.Missed;
            var state = kind switch
            {
                CenteredWindowEmptySlotKind.Missed => CenteredWindowSlotState.MissingExposure,
                CenteredWindowEmptySlotKind.Failed => CenteredWindowSlotState.FailedAttempt,
                CenteredWindowEmptySlotKind.Uncertain => CenteredWindowSlotState.UncertainAttempt,
                _ => throw new InvalidDataException("The centered slot has an unknown empty disposition.")
            };
            return new(offset, time, state, empty ? observation.EmptySlotReason : null, null, null, attempt);
        }

        if (empty || input!.Key.Camera != schedule.Camera || input.Key.RunId != schedule.RunId ||
            input.Key.CaptureId == Guid.Empty || input.RawArtifactId == Guid.Empty ||
            !IsChecksum(input.RawDescriptorSha256) || !IsChecksum(input.RawPayloadSha256) ||
            input.Timing.ExposureEndedUtc < input.Timing.ExposureStartedUtc ||
            input.AdmittedUtc < input.Timing.ExposureEndedUtc)
        {
            throw new InvalidDataException("A centered window slot has inconsistent admitted exposure evidence.");
        }

        if ((input.Timing.ExposureStartedUtc - time.ExpectedStartUtc).Duration() > (time.Tolerance ?? schedule.SlotTolerance))
        {
            return new(offset, time, CenteredWindowSlotState.OutsideTolerance, "exposure-start-outside-tolerance", input, null, attempt);
        }

        var products = observation.Products;
        if (products is null || products.Disposition == LinearExposureDisposition.Pending || products.LinearSettledUtc > cutoff)
        {
            return expired ? new(offset, time, CenteredWindowSlotState.Stalled, "processing-deadline-expired", input, null, attempt) : null;
        }

        var usable = products.Disposition is LinearExposureDisposition.Calibrated or LinearExposureDisposition.QualifiedNormalizationOnly;
        if (products.LinearSettledUtc < input.AdmittedUtc || !Enum.IsDefined(products.Disposition) ||
            usable != (products.Linear is not null) ||
            usable == !string.IsNullOrWhiteSpace(products.RejectionReason) || !IsValidProduct(products.Linear, input.Key))
        {
            throw new InvalidDataException("A centered window slot has inconsistent processing evidence.");
        }

        var measurement = SelectProductEvidence(products.Measurement, input, cutoff);
        var plate = SelectProductEvidence(products.Plate, input, cutoff);
        var registration = usable && products.RegistrationPin is { } pin
            ? pin with
            {
                Measurement = Matches(pin.Measurement, measurement) ? pin.Measurement : null,
                Plate = Matches(pin.Plate?.Solution, plate) ? pin.Plate : null
            }
            : null;
        return new(offset, time, usable ? CenteredWindowSlotState.Eligible : CenteredWindowSlotState.Rejected,
            products.RejectionReason, input, products with { Measurement = measurement, Plate = plate, RegistrationPin = registration }, attempt);
    }

    private static bool Matches(CenteredStackArtifactPin? pin, CenteredWindowProductEvidence? evidence) =>
        pin is not null && evidence?.Lookup is { Availability: ExposureProductAvailability.Available, Product: { } product } &&
        pin.ArtifactId == product.ArtifactId && pin.PayloadSha256 == product.PayloadSha256;

    private static CenteredWindowProductEvidence? SelectProductEvidence(
        CenteredWindowProductEvidence? evidence, CenteredWindowInput input, DateTimeOffset cutoff)
    {
        if (evidence is null || evidence.SettledUtc > cutoff || evidence.Lookup.Availability == ExposureProductAvailability.Pending)
            return null;

        var lookup = evidence.Lookup;
        if (evidence.SettledUtc < input.AdmittedUtc || lookup.Exposure != input.Key ||
            !Enum.IsDefined(lookup.Availability) || (lookup.Availability == ExposureProductAvailability.Available && lookup.Product is null) ||
            (lookup.Product is not null && lookup.Product.Slot != lookup.Slot) || !IsValidProduct(lookup.Product, input.Key))
        {
            throw new InvalidDataException("A centered window's optional evidence does not match its slot's admitted exposure.");
        }

        return evidence;
    }

    private static bool IsValidProduct(ExposureProductRef? product, ExposureKey exposure) =>
        product is null || (product.Exposure == exposure && product.ArtifactId != Guid.Empty &&
            IsChecksum(product.OutputIdentitySha256) && IsChecksum(product.PayloadSha256));

    private static bool IsChecksum(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
