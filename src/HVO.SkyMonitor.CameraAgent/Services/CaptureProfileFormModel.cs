using System.Globalization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// Typed, string-backed editing model over the approved fields of a local capture profile.
/// The model never owns the profile: <see cref="TryApply"/> rewrites only the fields it exposes
/// onto the basis revision, so opaque module and step options, sensor readout, simulation
/// response, metering, solar regimes, date exceptions, and every other unlisted field pass
/// through untouched and the server still restores and validates the result.
/// </summary>
internal sealed class CaptureProfileFormModel
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private CaptureProfileFormModel()
    {
    }

    public string SchemaVersion { get; private set; } = string.Empty;
    public string ModuleType { get; private set; } = string.Empty;
    public string SensorName { get; private set; } = string.Empty;
    public string SensorSummary { get; private set; } = string.Empty;
    public string ProjectionModel { get; private set; } = string.Empty;
    public int ProcessingStepCount { get; private set; }
    public int DateExceptionCount { get; private set; }
    public bool HasReadoutProfile { get; private set; }
    public bool HasMeteringPolicy { get; private set; }
    public bool HasSolarRegimePolicy { get; private set; }

    // Capture schedule
    public List<SetpointProfileRow> Setpoints { get; } = [];
    public List<WeeklyWindowRow> WeeklyWindows { get; } = [];
    public List<BlackoutRow> Blackouts { get; } = [];

    // Optics and orientation
    public string LensKind { get; set; } = nameof(AgentCore.LensKind.Unspecified);
    public string FocalLengthMillimeters { get; set; } = string.Empty;
    public string FieldOfViewDegrees { get; set; } = string.Empty;
    public string RollDegrees { get; set; } = string.Empty;
    public bool HorizontalFlip { get; set; }
    public string BoresightAltitudeDegrees { get; set; } = string.Empty;
    public string BoresightAzimuthDegrees { get; set; } = string.Empty;
    public string RollAdjustmentDegrees { get; set; } = string.Empty;

    // Exposure profile
    public string CaptureIntervalSeconds { get; set; } = string.Empty;
    public string DayExposureMilliseconds { get; set; } = string.Empty;
    public string NightExposureMilliseconds { get; set; } = string.Empty;
    public string DayGain { get; set; } = string.Empty;
    public string NightGain { get; set; } = string.Empty;
    public string CadenceMode { get; set; } = nameof(CaptureCadenceMode.MinimumStartInterval);
    public bool HasEnvelope { get; private set; }
    public string MinimumExposureMilliseconds { get; set; } = string.Empty;
    public string MaximumExposureMilliseconds { get; set; } = string.Empty;
    public string MinimumGain { get; set; } = string.Empty;
    public string MaximumGain { get; set; } = string.Empty;
    public string TargetAduLevel { get; set; } = string.Empty;
    public string ExposurePreference { get; set; } = nameof(ExposureGainPreference.ExposureFirst);

    // Control policy
    public string ExposureControl { get; set; } = nameof(AutomaticControlOwnership.Unspecified);
    public string GainControl { get; set; } = nameof(AutomaticControlOwnership.Unspecified);
    public string TemperatureMode { get; set; } = nameof(TemperatureControlMode.Unspecified);
    public string TemperatureTargetC { get; set; } = string.Empty;

    public static CaptureProfileFormModel FromProfile(LocalCaptureProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var rig = profile.Rig;
        var model = new CaptureProfileFormModel
        {
            SchemaVersion = profile.SchemaVersion,
            ModuleType = profile.Module.Type,
            SensorName = rig.Sensor.Name,
            SensorSummary = string.Create(
                Invariant,
                $"{rig.Sensor.WidthPixels} x {rig.Sensor.HeightPixels} px, {rig.Sensor.PixelSizeMicrons} um, {rig.Sensor.ColorMode}, {rig.Sensor.PixelFormat}"),
            ProjectionModel = rig.Optics.ProjectionModel,
            ProcessingStepCount = profile.ProcessingSteps.Count,
            DateExceptionCount = profile.Schedule.DateExceptions?.Count ?? 0,
            HasReadoutProfile = rig.Readout is not null,
            HasMeteringPolicy = rig.ControlPolicy?.Metering is not null,
            HasSolarRegimePolicy = rig.ControlPolicy?.SolarRegimes is not null,
            LensKind = rig.Optics.LensKind.ToString(),
            FocalLengthMillimeters = Number(rig.Optics.FocalLengthMillimeters),
            FieldOfViewDegrees = Number(rig.Optics.FieldOfViewDegrees),
            RollDegrees = Number(rig.Optics.RollDegrees),
            HorizontalFlip = rig.Optics.HorizontalFlip,
            BoresightAltitudeDegrees = Number(rig.Orientation.BoresightAltitudeDegrees),
            BoresightAzimuthDegrees = Number(rig.Orientation.BoresightAzimuthDegrees),
            RollAdjustmentDegrees = Number(rig.Orientation.RollAdjustmentDegrees),
            CaptureIntervalSeconds = Number(rig.Pipeline.CaptureInterval.TotalSeconds),
            DayExposureMilliseconds = Number(rig.Pipeline.DayExposure.TotalMilliseconds),
            NightExposureMilliseconds = Number(rig.Pipeline.NightExposure.TotalMilliseconds),
            DayGain = Number(rig.Pipeline.DayGain),
            NightGain = Number(rig.Pipeline.NightGain),
            CadenceMode = rig.Pipeline.CadenceMode.ToString(),
            HasEnvelope = rig.Pipeline.Envelope is not null,
            ExposureControl = (rig.ControlPolicy?.ExposureControl ?? AutomaticControlOwnership.Unspecified).ToString(),
            GainControl = (rig.ControlPolicy?.GainControl ?? AutomaticControlOwnership.Unspecified).ToString(),
            TemperatureMode = (rig.ControlPolicy?.Temperature.Mode ?? TemperatureControlMode.Unspecified).ToString(),
            TemperatureTargetC = rig.ControlPolicy?.Temperature.TargetC is { } target ? Number(target) : string.Empty
        };
        if (rig.Pipeline.Envelope is { } envelope)
        {
            model.MinimumExposureMilliseconds = Number(envelope.MinExposure.TotalMilliseconds);
            model.MaximumExposureMilliseconds = Number(envelope.MaxExposure.TotalMilliseconds);
            model.MinimumGain = Number(envelope.MinGain);
            model.MaximumGain = Number(envelope.MaxGain);
            model.TargetAduLevel = Number(envelope.TargetAduLevel);
            model.ExposurePreference = envelope.Preference.ToString();
        }
        foreach (var setpoint in profile.Schedule.SetpointProfiles)
        {
            model.Setpoints.Add(new SetpointProfileRow
            {
                Id = setpoint.Id,
                ExposureMilliseconds = Number(setpoint.Exposure.TotalMilliseconds),
                Gain = Number(setpoint.Gain),
                CaptureIntervalSeconds = Number(setpoint.CaptureInterval.TotalSeconds),
                CadenceMode = setpoint.CadenceMode.ToString(),
                TargetFps = setpoint.TargetFps is { } fps ? Number(fps) : string.Empty
            });
        }
        foreach (var window in profile.Schedule.WeeklyWindows)
        {
            model.WeeklyWindows.Add(new WeeklyWindowRow
            {
                Id = window.Id,
                Day = window.Day.ToString(),
                Start = BoundaryRow.From(window.Start),
                End = BoundaryRow.From(window.End),
                SetpointProfileId = window.SetpointProfileId
            });
        }
        foreach (var blackout in profile.Schedule.Blackouts ?? [])
        {
            model.Blackouts.Add(new BlackoutRow
            {
                Id = blackout.Id,
                StartUtc = blackout.StartUtc.ToString("O", Invariant),
                EndUtc = blackout.EndUtc.ToString("O", Invariant)
            });
        }
        return model;
    }

    public void AddSetpoint() => Setpoints.Add(new SetpointProfileRow
    {
        Id = UniqueId("setpoint", Setpoints.Select(static row => row.Id)),
        ExposureMilliseconds = "1000",
        Gain = "0",
        CaptureIntervalSeconds = "30",
        CadenceMode = nameof(CaptureCadenceMode.MinimumStartInterval)
    });

    public void AddWeeklyWindow() => WeeklyWindows.Add(new WeeklyWindowRow
    {
        Id = UniqueId("window", WeeklyWindows.Select(static row => row.Id)),
        Day = nameof(DayOfWeek.Monday),
        Start = new BoundaryRow { Kind = nameof(CaptureScheduleBoundaryKind.Sunset) },
        End = new BoundaryRow { Kind = nameof(CaptureScheduleBoundaryKind.Sunrise), DayOffset = "1" },
        SetpointProfileId = Setpoints.Count > 0 ? Setpoints[0].Id : string.Empty
    });

    public void AddBlackout() => Blackouts.Add(new BlackoutRow
    {
        Id = UniqueId("blackout", Blackouts.Select(static row => row.Id)),
        StartUtc = DateTimeOffset.UtcNow.ToString("O", Invariant),
        EndUtc = DateTimeOffset.UtcNow.AddHours(1).ToString("O", Invariant)
    });

    /// <summary>
    /// Rewrites the typed fields onto the basis profile. Returns false with field-labelled
    /// messages when any value cannot be parsed; semantic validation stays with the server.
    /// </summary>
    public bool TryApply(
        LocalCaptureProfileDefinition basis,
        out LocalCaptureProfileDefinition profile,
        out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(basis);
        var problems = new List<string>();
        var rig = basis.Rig;

        var optics = rig.Optics with
        {
            LensKind = ParseEnum<LensKind>(LensKind, "Lens kind", problems),
            FocalLengthMillimeters = ParseDouble(FocalLengthMillimeters, "Focal length", problems),
            FieldOfViewDegrees = ParseDouble(FieldOfViewDegrees, "Field of view", problems),
            RollDegrees = ParseDouble(RollDegrees, "Optics roll", problems),
            HorizontalFlip = HorizontalFlip
        };
        var orientation = new RigOrientation(
            ParseDouble(BoresightAltitudeDegrees, "Boresight altitude", problems),
            ParseDouble(BoresightAzimuthDegrees, "Boresight azimuth", problems),
            ParseDouble(RollAdjustmentDegrees, "Roll adjustment", problems));
        var envelope = rig.Pipeline.Envelope;
        if (envelope is not null)
        {
            envelope = envelope with
            {
                MinExposure = Milliseconds(MinimumExposureMilliseconds, "Minimum exposure", problems),
                MaxExposure = Milliseconds(MaximumExposureMilliseconds, "Maximum exposure", problems),
                MinGain = ParseDouble(MinimumGain, "Minimum gain", problems),
                MaxGain = ParseDouble(MaximumGain, "Maximum gain", problems),
                TargetAduLevel = ParseDouble(TargetAduLevel, "Target ADU level", problems),
                Preference = ParseEnum<ExposureGainPreference>(ExposurePreference, "Adjustment order", problems)
            };
        }
        var pipeline = rig.Pipeline with
        {
            CaptureInterval = Seconds(CaptureIntervalSeconds, "Capture interval", problems),
            DayExposure = Milliseconds(DayExposureMilliseconds, "Day exposure", problems),
            NightExposure = Milliseconds(NightExposureMilliseconds, "Night exposure", problems),
            DayGain = ParseDouble(DayGain, "Day gain", problems),
            NightGain = ParseDouble(NightGain, "Night gain", problems),
            CadenceMode = ParseEnum<CaptureCadenceMode>(CadenceMode, "Cadence mode", problems),
            Envelope = envelope
        };
        var temperatureTarget = string.IsNullOrWhiteSpace(TemperatureTargetC)
            ? (double?)null
            : ParseDouble(TemperatureTargetC, "Temperature target", problems);
        var exposureControl = ParseEnum<AutomaticControlOwnership>(ExposureControl, "Exposure control", problems);
        var gainControl = ParseEnum<AutomaticControlOwnership>(GainControl, "Gain control", problems);
        var temperatureMode = ParseEnum<TemperatureControlMode>(TemperatureMode, "Temperature mode", problems);
        // A basis without a control policy keeps none while every exposed value is still the default,
        // so an untouched form never materialises a policy the revision never had.
        var controlPolicy = rig.ControlPolicy is null &&
            exposureControl == AutomaticControlOwnership.Unspecified &&
            gainControl == AutomaticControlOwnership.Unspecified &&
            temperatureMode == TemperatureControlMode.Unspecified &&
            temperatureTarget is null
            ? null
            : (rig.ControlPolicy ?? new CameraControlPolicy()) with
            {
                ExposureControl = exposureControl,
                GainControl = gainControl,
                Temperature = new TemperatureControlDirective { Mode = temperatureMode, TargetC = temperatureTarget }
            };

        var setpoints = Setpoints.Select(row => new CaptureScheduleSetpointProfile(
            row.Id.Trim(),
            Milliseconds(row.ExposureMilliseconds, $"Setpoint '{row.Id}' exposure", problems),
            ParseDouble(row.Gain, $"Setpoint '{row.Id}' gain", problems),
            Seconds(row.CaptureIntervalSeconds, $"Setpoint '{row.Id}' capture interval", problems),
            ParseEnum<CaptureCadenceMode>(row.CadenceMode, $"Setpoint '{row.Id}' cadence mode", problems),
            string.IsNullOrWhiteSpace(row.TargetFps) ? null : ParseDouble(row.TargetFps, $"Setpoint '{row.Id}' target FPS", problems))).ToArray();
        var windows = WeeklyWindows.Select(row => new CaptureWeeklyScheduleWindow(
            row.Id.Trim(),
            ParseEnum<DayOfWeek>(row.Day, $"Window '{row.Id}' day", problems),
            row.Start.ToBoundary($"Window '{row.Id}' start", problems),
            row.End.ToBoundary($"Window '{row.Id}' end", problems),
            row.SetpointProfileId.Trim())).ToArray();
        var blackouts = Blackouts.Select(row => new CaptureScheduleBlackout(
            row.Id.Trim(),
            ParseUtc(row.StartUtc, $"Blackout '{row.Id}' start", problems),
            ParseUtc(row.EndUtc, $"Blackout '{row.Id}' end", problems))).ToArray();
        foreach (var row in Setpoints.Where(static row => string.IsNullOrWhiteSpace(row.Id)))
        {
            problems.Add("Every setpoint profile needs an identifier.");
        }
        foreach (var row in WeeklyWindows.Where(static row => string.IsNullOrWhiteSpace(row.Id)))
        {
            problems.Add("Every weekly window needs an identifier.");
        }
        foreach (var row in Blackouts.Where(static row => string.IsNullOrWhiteSpace(row.Id)))
        {
            problems.Add("Every blackout needs an identifier.");
        }

        errors = problems.Distinct(StringComparer.Ordinal).ToArray();
        if (errors.Count > 0)
        {
            profile = basis;
            return false;
        }
        profile = basis with
        {
            Rig = rig with
            {
                Optics = optics,
                Orientation = orientation,
                Pipeline = pipeline,
                ControlPolicy = controlPolicy
            },
            Schedule = basis.Schedule with
            {
                SetpointProfiles = setpoints,
                WeeklyWindows = windows,
                Blackouts = blackouts.Length == 0 && basis.Schedule.Blackouts is null ? null : blackouts
            }
        };
        return true;
    }

    private static string UniqueId(string prefix, IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = taken.Count + 1; ; index++)
        {
            var candidate = string.Create(Invariant, $"{prefix}-{index}");
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Shortest decimal text that parses back to exactly the same double.</summary>
    internal static string Number(double value)
    {
        var compact = value.ToString("0.###############", Invariant);
        return double.TryParse(compact, NumberStyles.Float, Invariant, out var parsed) && parsed == value
            ? compact
            : value.ToString("R", Invariant);
    }

    /// <summary>Local time text that round-trips seconds and sub-second ticks when present.</summary>
    internal static string LocalTime(TimeOnly value)
        => value.Ticks % TimeSpan.TicksPerMinute == 0
            ? value.ToString("HH:mm", Invariant)
            : value.ToString("HH:mm:ss.FFFFFFF", Invariant);

    private static double ParseDouble(string value, string label, List<string> problems)
    {
        if (double.TryParse(value, NumberStyles.Float, Invariant, out var parsed) && double.IsFinite(parsed))
        {
            return parsed;
        }
        problems.Add($"{label} must be a number.");
        return 0;
    }

    private static TimeSpan Milliseconds(string value, string label, List<string> problems)
        => TimeSpan.FromTicks((long)Math.Round(ParseDouble(value, label, problems) * TimeSpan.TicksPerMillisecond));

    private static TimeSpan Seconds(string value, string label, List<string> problems)
        => TimeSpan.FromTicks((long)Math.Round(ParseDouble(value, label, problems) * TimeSpan.TicksPerSecond));

    private static TEnum ParseEnum<TEnum>(string value, string label, List<string> problems)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }
        problems.Add($"{label} is not a supported value.");
        return default;
    }

    private static DateTimeOffset ParseUtc(string value, string label, List<string> problems)
    {
        if (DateTimeOffset.TryParse(value, Invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }
        problems.Add($"{label} must be a UTC timestamp.");
        return default;
    }

    private static TimeOnly? ParseLocalTime(string value, string label, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (TimeOnly.TryParse(value, Invariant, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }
        problems.Add($"{label} must be a local time such as 18:30.");
        return null;
    }

    private static int ParseInt(string value, string label, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }
        if (int.TryParse(value, NumberStyles.Integer, Invariant, out var parsed))
        {
            return parsed;
        }
        problems.Add($"{label} must be a whole number.");
        return 0;
    }

    internal sealed class SetpointProfileRow
    {
        public string Id { get; set; } = string.Empty;
        public string ExposureMilliseconds { get; set; } = string.Empty;
        public string Gain { get; set; } = string.Empty;
        public string CaptureIntervalSeconds { get; set; } = string.Empty;
        public string CadenceMode { get; set; } = nameof(CaptureCadenceMode.MinimumStartInterval);
        public string TargetFps { get; set; } = string.Empty;
    }

    internal sealed class WeeklyWindowRow
    {
        public string Id { get; set; } = string.Empty;
        public string Day { get; set; } = nameof(DayOfWeek.Monday);
        public BoundaryRow Start { get; set; } = new();
        public BoundaryRow End { get; set; } = new();
        public string SetpointProfileId { get; set; } = string.Empty;
    }

    internal sealed class BoundaryRow
    {
        public string Kind { get; set; } = nameof(CaptureScheduleBoundaryKind.FixedLocalTime);
        public string LocalTime { get; set; } = string.Empty;
        public string OffsetMinutes { get; set; } = string.Empty;
        public string DayOffset { get; set; } = string.Empty;
        public string NoEventFallbackLocalTime { get; set; } = string.Empty;

        public bool IsFixed => Kind == nameof(CaptureScheduleBoundaryKind.FixedLocalTime);

        public static BoundaryRow From(CaptureScheduleBoundary boundary) => new()
        {
            Kind = boundary.Kind.ToString(),
            LocalTime = boundary.LocalTime is { } localTime ? LocalTime(localTime) : string.Empty,
            OffsetMinutes = boundary.Offset == TimeSpan.Zero ? string.Empty : Number(boundary.Offset.TotalMinutes),
            DayOffset = boundary.DayOffset == 0 ? string.Empty : boundary.DayOffset.ToString(Invariant),
            NoEventFallbackLocalTime = boundary.NoEventFallbackLocalTime is { } fallback ? LocalTime(fallback) : string.Empty
        };

        public CaptureScheduleBoundary ToBoundary(string label, List<string> problems)
        {
            var kind = ParseEnum<CaptureScheduleBoundaryKind>(Kind, $"{label} kind", problems);
            var localTime = ParseLocalTime(LocalTime, $"{label} local time", problems);
            if (kind == CaptureScheduleBoundaryKind.FixedLocalTime && localTime is null)
            {
                problems.Add($"{label} needs a local time because it is a fixed boundary.");
            }
            var offset = string.IsNullOrWhiteSpace(OffsetMinutes)
                ? TimeSpan.Zero
                : TimeSpan.FromMinutes(ParseDouble(OffsetMinutes, $"{label} offset", problems));
            return new CaptureScheduleBoundary(
                kind,
                localTime,
                offset,
                ParseInt(DayOffset, $"{label} day offset", problems),
                ParseLocalTime(NoEventFallbackLocalTime, $"{label} fallback time", problems));
        }
    }

    internal sealed class BlackoutRow
    {
        public string Id { get; set; } = string.Empty;
        public string StartUtc { get; set; } = string.Empty;
        public string EndUtc { get; set; } = string.Empty;
    }
}
