using System.Collections.Immutable;
using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing;

/// <summary>One immutable host-neutral snapshot. This type has no clock, provider, configuration or storage access.</summary>
public sealed class FrameFactsRetainedContext
{
    private FrameFactsRetainedContext(FrameFactsTokenContext tokens,
        ImmutableDictionary<string, FrameFactsFactProvenance> provenance)
    {
        Tokens = tokens;
        Provenance = provenance;
    }

    public FrameFactsTokenContext Tokens { get; }
    public ImmutableDictionary<string, FrameFactsFactProvenance> Provenance { get; }

    public static FrameFactsRetainedContext Create(FrameFactsRetainedInputV1 input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Descriptor);
        if (!input.Descriptor.Validate().IsValid)
            throw new ArgumentException("A valid retained reconstruction descriptor is required.", nameof(input));
        var builder = new Builder(input.Descriptor);
        builder.Capture(input.CaptureExtras);
        builder.Stack(input.Stack);
        builder.Location(input.Location, input.LocalTime);
        builder.Astronomy(input.Astronomy);
        builder.Environment(input.Environment);
        return new(new(builder.Facts), builder.Provenance.ToImmutableDictionary(StringComparer.Ordinal));
    }

    private sealed class Builder(ReconstructionDescriptor descriptor)
    {
        private readonly string _descriptorIdentity = CaptureContractJson.ComputeDescriptorSha256(descriptor);
        public Dictionary<string, FrameFactsFact> Facts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, FrameFactsFactProvenance> Provenance { get; } = new(StringComparer.Ordinal);

        private void Put(string key, FrameFactsValue value, FrameFactsFactProvenance source)
        {
            Facts.Add(key, FrameFactsFact.Available(value));
            Provenance.Add(key, source);
        }

        private void Unavailable(string key, FrameFactsAvailability availability, FrameFactsFactProvenance source)
        {
            Facts.Add(key, FrameFactsFact.Unavailable(availability));
            Provenance.Add(key, source);
        }

        private void Family(string prefix, FrameFactsAvailability availability, FrameFactsFactProvenance source)
        {
            foreach (var entry in FrameFactsRetainedCatalog.Entries.Where(entry => entry.Definition.Key.StartsWith(prefix, StringComparison.Ordinal)))
                Unavailable(entry.Definition.Key, availability, source);
        }

        private void Text(string key, string? value, FrameFactsFactProvenance source)
        {
            if (value is null) Unavailable(key, FrameFactsAvailability.Missing, source);
            else if (string.IsNullOrWhiteSpace(value) || value.Length > FrameFactsTemplate.MaximumSourceLength)
                Unavailable(key, FrameFactsAvailability.Unsupported, source);
            else Put(key, FrameFactsValue.FromText(value), source);
        }

        private void Number(string key, double? value, FrameFactsFactProvenance source)
        {
            if (!value.HasValue) Unavailable(key, FrameFactsAvailability.Missing, source);
            else if (!double.IsFinite(value.Value)) Unavailable(key, FrameFactsAvailability.Unsupported, source);
            else Put(key, FrameFactsValue.FromNumber(value.Value), source);
        }

        private void Timestamp(string key, DateTimeOffset? value, FrameFactsFactProvenance source)
        {
            if (!value.HasValue) Unavailable(key, FrameFactsAvailability.Missing, source);
            else Put(key, FrameFactsValue.FromTimestamp(value.Value.ToUniversalTime()), source);
        }

        private bool Matches(string identity) => string.Equals(identity, _descriptorIdentity, StringComparison.OrdinalIgnoreCase);
        private FrameFactsFactProvenance DescriptorSource(string nature) => new("ReconstructionDescriptor", _descriptorIdentity, nature);

        public void Capture(FrameFactsRetainedCaptureExtrasV1? extras)
        {
            var source = DescriptorSource("Retained capture");
            Text("capture.agentId", descriptor.Capture.AgentId, source);
            Text("capture.rigId", descriptor.Capture.RigId, source);
            Text("capture.captureId", descriptor.Capture.CaptureId.ToString("D"), source);
            Text("capture.sourceId", descriptor.Artifact.SourceId, source);
            Text("capture.artifactId", descriptor.Artifact.ArtifactId.ToString("D"), source);
            Put("capture.sequence", FrameFactsValue.FromWholeNumber(descriptor.Capture.CaptureSequence), source);
            Put("capture.width", FrameFactsValue.FromWholeNumber(descriptor.Layout.Width), source);
            Put("capture.height", FrameFactsValue.FromWholeNumber(descriptor.Layout.Height), source);
            Number("acquisition.exposureSeconds", descriptor.Controls.EffectiveExposure.TotalSeconds, source);
            Number("acquisition.gain", descriptor.Controls.EffectiveGain, source);
            Number("acquisition.offset", descriptor.Controls.EffectiveOffset, source);
            Number("acquisition.sensorSetpointC", descriptor.Controls.TemperatureSetpointC, source with { Nature = "Configured setpoint" });
            Number("acquisition.sensorTemperatureC", descriptor.Controls.EffectiveTemperatureC, source with { Nature = "Module-reported effective sensor temperature" });
            Text("acquisition.scheduleProfile", descriptor.CycleEvidence?.ScheduleAdmission?.SetpointProfileId, source);
            Text("acquisition.cadenceMode", descriptor.CycleEvidence?.CadenceMode.ToString(), source);
            Timestamp("time.captureUtc", descriptor.Timing.ExposureStartedUtc, source);
            Timestamp("time.captureEndUtc", descriptor.Timing.ExposureEndedUtc, source);
            Timestamp("time.artifactCreatedUtc", descriptor.Artifact.CreatedUtc, source);
            Text("time.captureLegacyUtc", descriptor.Timing.ExposureStartedUtc.ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), source);
            Profile("calibration", descriptor.Profiles.Calibration, source);
            Profile("processing", descriptor.Profiles.Processing, source);
            var extraSource = new FrameFactsFactProvenance("FrameFactsRetainedCaptureExtrasV1", extras?.DescriptorIdentitySha256 ?? _descriptorIdentity, "Retained capture extras");
            if (extras is not null && !Matches(extras.DescriptorIdentitySha256))
            {
                foreach (var key in new[] { "time.sourceUtc", "time.processingUtc", "acquisition.cadenceSeconds" })
                    Unavailable(key, FrameFactsAvailability.Conflicting, extraSource);
                return;
            }
            Timestamp("time.sourceUtc", extras?.SourceTimestamp, extraSource);
            if (extras?.ProcessingCompletedUtc < descriptor.Timing.ExposureEndedUtc)
                Unavailable("time.processingUtc", FrameFactsAvailability.Unsupported, extraSource);
            else Timestamp("time.processingUtc", extras?.ProcessingCompletedUtc, extraSource);
            Number("acquisition.cadenceSeconds", extras?.CadenceSeconds is < 0 ? double.NaN : extras?.CadenceSeconds, extraSource);
        }

        private void Profile(string family, ProfileIdentityDescriptor profile, FrameFactsFactProvenance source)
        {
            var prefix = "lineage." + family + ".";
            Text(prefix + "name", profile.Name, source);
            Text(prefix + "version", profile.Version, source);
            Text(prefix + "sha256", profile.Sha256, source);
            Text(prefix + "shortHash", profile.Sha256[..12], source);
        }

        public void Stack(FrameFactsRetainedStackV1? stack)
        {
            var source = new FrameFactsFactProvenance("FrameFactsRetainedStackV1", stack?.Identity ?? _descriptorIdentity, "Retained stack product");
            var status = stack is null ? FrameFactsAvailability.Missing
                : !Matches(stack.DescriptorIdentitySha256) ? FrameFactsAvailability.Conflicting
                : !IsSha256(stack.Identity) || stack.SourceArtifactIds is null || stack.SourceArtifactIds.Count is < 1 or > 4096 ||
                    stack.SourceArtifactIds.Any(id => id == Guid.Empty || id == descriptor.Capture.CaptureId) ||
                    stack.SourceArtifactIds.Distinct().Count() != stack.SourceArtifactIds.Count || stack.TotalIntegration < TimeSpan.Zero
                    ? FrameFactsAvailability.Unsupported : FrameFactsAvailability.Available;
            if (status != FrameFactsAvailability.Available) { Family("stack.", status, source); return; }
            Put("stack.count", FrameFactsValue.FromWholeNumber(stack!.SourceArtifactIds!.Count), source);
            Number("stack.integrationSeconds", stack.TotalIntegration.TotalSeconds, source);
            Text("stack.identity", stack.Identity, source);
        }

        public void Location(DeploymentLocationSnapshot? location, FrameFactsRetainedLocalTimeV1? local)
        {
            var source = DescriptorSource("Retained coordinate-free location");
            var provenance = descriptor.Location;
            Text("location.id", provenance?.LocationId, source);
            Text("location.source", provenance?.Source, source);
            if (provenance is null) Unavailable("location.version", FrameFactsAvailability.Missing, source);
            else Put("location.version", FrameFactsValue.FromWholeNumber(provenance.Version), source);
            var locationSource = new FrameFactsFactProvenance("DeploymentLocationSnapshot", location?.CanonicalSha256 ?? _descriptorIdentity, "Retained deployment location");
            var locationStatus = location is null || provenance is null ? FrameFactsAvailability.Missing
                : location.ToProvenance() != provenance ? FrameFactsAvailability.Conflicting
                : !location.IsEffectiveAt(descriptor.Timing.ExposureStartedUtc) ? FrameFactsAvailability.Stale
                : !ValidLocation(location) ? FrameFactsAvailability.Unsupported : FrameFactsAvailability.Available;
            foreach (var (key, value) in new[] { ("latitude", location?.LatitudeDegrees), ("longitude", location?.LongitudeDegrees), ("elevation", location?.ElevationMeters) })
            {
                if (locationStatus == FrameFactsAvailability.Available) Number("location." + key, value, locationSource);
                else Unavailable("location." + key, locationStatus, locationSource);
            }
            if (locationStatus == FrameFactsAvailability.Available) Text("location.timeZone", location!.TimeZoneId, locationSource);
            else Unavailable("location.timeZone", locationStatus, locationSource);

            var localSource = new FrameFactsFactProvenance("FrameFactsRetainedLocalTimeV1", local?.RuleIdentity ?? _descriptorIdentity, "Frozen zone and offset at capture instant");
            var localStatus = local is null ? FrameFactsAvailability.Missing
                : !Matches(local.DescriptorIdentitySha256) || local.CaptureUtc != descriptor.Timing.ExposureStartedUtc ||
                    locationStatus == FrameFactsAvailability.Available && local.ZoneId != location!.TimeZoneId ? FrameFactsAvailability.Conflicting
                : string.IsNullOrWhiteSpace(local.ZoneId) || local.ZoneId.Length > 128 ||
                    string.IsNullOrWhiteSpace(local.RuleIdentity) || local.RuleIdentity.Length > 256 ||
                    local.Offset.Ticks % TimeSpan.TicksPerMinute != 0 || local.Offset < TimeSpan.FromHours(-14) || local.Offset > TimeSpan.FromHours(14)
                    ? FrameFactsAvailability.Unsupported : FrameFactsAvailability.Available;
            if (localStatus != FrameFactsAvailability.Available)
            {
                foreach (var key in new[] { "time.captureLocal", "time.zoneId", "time.zoneRuleIdentity" }) Unavailable(key, localStatus, localSource);
                return;
            }
            try
            {
                Put("time.captureLocal", FrameFactsValue.FromTimestamp(descriptor.Timing.ExposureStartedUtc.ToOffset(local!.Offset)), localSource);
                Text("time.zoneId", local.ZoneId, localSource);
                Text("time.zoneRuleIdentity", local.RuleIdentity, localSource);
            }
            catch (ArgumentException)
            {
                foreach (var key in new[] { "time.captureLocal", "time.zoneId", "time.zoneRuleIdentity" }) Unavailable(key, FrameFactsAvailability.Unsupported, localSource);
            }
        }

        public void Astronomy(ProjectedSceneV1? scene)
        {
            var source = new FrameFactsFactProvenance("ProjectedSceneV1", scene?.SceneIdentitySha256 ?? _descriptorIdentity, scene?.Kind.ToString() ?? "Not retained");
            var status = scene is null ? FrameFactsAvailability.Missing
                : !ProjectedSceneV1.IsSupportedSchemaVersion(scene.SchemaVersion) ? FrameFactsAvailability.Unsupported
                : scene.Source is null || scene.Source.CaptureId != descriptor.Capture.CaptureId ||
                    scene.Source.ArtifactId != descriptor.Artifact.ArtifactId || !Matches(scene.Source.ArtifactIdentitySha256)
                    ? FrameFactsAvailability.Conflicting : FrameFactsAvailability.Available;
            if (status == FrameFactsAvailability.Available)
            {
                try { ProjectedSceneJson.Validate(scene!); }
                catch (ArgumentException) { status = FrameFactsAvailability.Unsupported; }
            }
            if (status != FrameFactsAvailability.Available)
            {
                Family("astronomy.", status, source);
                Family("lineage.catalog.", status, source);
                return;
            }
            Text("astronomy.kind", scene!.Kind.ToString(), source);
            Timestamp("astronomy.effectiveUtc", scene.EffectiveUtc, source);
            Text("astronomy.algorithm", scene.AstronomyAlgorithmVersion, source);
            Text("astronomy.refraction", scene.Refraction.Enabled ? "TRUE" : "FALSE", source with { Nature = "Configured refraction" });
            var configured = source with { Nature = "Configured scene observer and projection" };
            Number("astronomy.observerLatitude", scene.Observer.LatitudeDegrees, configured);
            Number("astronomy.observerLongitude", scene.Observer.LongitudeDegrees, configured);
            Number("astronomy.observerElevation", scene.Observer.ElevationMeters, configured);
            Number("astronomy.boresightAltitude", scene.Projection.BoresightAltitudeDegrees, configured);
            Number("astronomy.boresightAzimuth", scene.Projection.BoresightAzimuthDegrees, configured);
            Number("astronomy.roll", scene.Projection.RollDegrees, configured);
            foreach (var (name, selectedBody) in new[] { ("sun", SolarSystemBody.Sun), ("moon", SolarSystemBody.Moon) })
            {
                var id = $"solar-system:{selectedBody}";
                var body = scene.Objects.SingleOrDefault(item => item.Id == id);
                var derived = source with { Nature = scene.Kind + ": retained derived geometry" };
                if (body is not null && (body.Kind != CelestialObjectKind.SolarSystemBody ||
                    !scene.Selection.SolarSystemBodies.Contains(selectedBody) || string.IsNullOrWhiteSpace(scene.EphemerisModelVersion)))
                {
                    foreach (var coordinate in new[] { "geometricAltitude", "apparentAltitude", "azimuth" })
                        Unavailable($"astronomy.{name}.{coordinate}", FrameFactsAvailability.Conflicting, derived);
                    continue;
                }
                Number($"astronomy.{name}.geometricAltitude", body?.GeometricHorizontal.AltitudeDegrees, derived);
                Number($"astronomy.{name}.apparentAltitude", body?.ApparentHorizontal.AltitudeDegrees, derived);
                Number($"astronomy.{name}.azimuth", body?.ApparentHorizontal.AzimuthDegrees, derived);
            }
            Text("lineage.catalog.name", scene.Catalog.Name, source);
            Text("lineage.catalog.version", scene.Catalog.Version, source);
            Text("lineage.catalog.sha256", scene.Catalog.ChecksumSha256, source);
        }

        public void Environment(IReadOnlyList<FrameFactsRetainedEnvironmentV1>? environment)
        {
            if (environment?.Count > FrameFactsRetainedCatalog.Environment.Length)
                throw new ArgumentException("Too many retained environmental associations.", nameof(environment));
            var byKind = new Dictionary<EnvironmentalObservationKind, FrameFactsRetainedEnvironmentV1>();
            foreach (var item in environment ?? [])
            {
                ArgumentNullException.ThrowIfNull(item);
                if (!Enum.IsDefined(item.Kind) || !byKind.TryAdd(item.Kind, item))
                    throw new ArgumentException("Invalid or duplicate retained environmental kind.", nameof(environment));
            }
            foreach (var (kind, key, _) in FrameFactsRetainedCatalog.Environment)
            {
                byKind.TryGetValue(kind, out var association);
                var observation = association?.Observation;
                var prefix = "environment." + key;
                var source = new FrameFactsFactProvenance("FrameFactsRetainedEnvironmentV1", association?.AssociationIdentity ?? _descriptorIdentity, "Retained environmental association");
                var status = association?.Availability ?? FrameFactsAvailability.Missing;
                if (!Enum.IsDefined(status)) status = FrameFactsAvailability.Unsupported;
                if (association is not null && (association.CaptureId != descriptor.Capture.CaptureId ||
                    association.ExposureFromUtc != descriptor.Timing.ExposureStartedUtc ||
                    association.ExposureThroughUtc != descriptor.Timing.ExposureEndedUtc)) status = FrameFactsAvailability.Conflicting;
                else if (association is not null && !IsSha256(association.AssociationIdentity)) status = FrameFactsAvailability.Unsupported;
                var validObservation = observation is not null && ValidObservation(observation);
                if (validObservation && (observation!.Value.Kind != kind || observation.RigId is not null && observation.RigId != descriptor.Capture.RigId))
                {
                    status = FrameFactsAvailability.Conflicting;
                    validObservation = false;
                }
                if (status == FrameFactsAvailability.Available)
                {
                    if (!validObservation) status = observation is null ? FrameFactsAvailability.Missing : FrameFactsAvailability.Unsupported;
                    else if (observation!.ValidFromUtc > descriptor.Timing.ExposureStartedUtc ||
                        observation.ValidThroughUtc < descriptor.Timing.ExposureEndedUtc ||
                        observation.StaleAfterUtc <= descriptor.Timing.ExposureEndedUtc) status = FrameFactsAvailability.Stale;
                    else if (observation.Value.Quality != EnvironmentalObservationQuality.Good) status = FrameFactsAvailability.Unsupported;
                }
                if (validObservation)
                {
                    source = source with { SourceIdentity = source.SourceIdentity + "/" + EnvironmentalObservationFactJson.ComputeContentSha256(observation!), Nature = observation!.Source.Kind.ToString() };
                    Text(prefix + ".sourceKind", observation.Source.Kind.ToString(), source);
                    Text(prefix + ".quality", observation.Value.Quality.ToString(), source);
                    Timestamp(prefix + ".observedUtc", observation.ObservedAtUtc, source);
                }
                else
                    foreach (var suffix in new[] { ".sourceKind", ".quality", ".observedUtc" })
                        Unavailable(prefix + suffix, status == FrameFactsAvailability.Available ? FrameFactsAvailability.Unsupported : status, source);
                if (status != FrameFactsAvailability.Available) Unavailable(prefix, status, source);
                else if (kind == EnvironmentalObservationKind.RainState) Text(prefix, observation!.Value.BooleanValue!.Value ? "TRUE" : "FALSE", source);
                else Number(prefix, observation!.Value.NumericValue, source);
            }
        }

        // The V2 validator computes source identity before validating Value; isolate malformed optional input.
        private static bool ValidObservation(EnvironmentalObservationFactV1 observation)
            => observation.Value is not null && EnvironmentalObservationFactJson.Validate(observation).IsValid;

        private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

        // DeploymentLocationSnapshot.Validate also resolves the machine's time-zone database. Here the retained
        // canonical hash, exact provenance and interval are authoritative; evaluation never needs that database.
        private static bool ValidLocation(DeploymentLocationSnapshot location)
            => double.IsFinite(location.LatitudeDegrees) && location.LatitudeDegrees is >= -90 and <= 90 &&
               double.IsFinite(location.LongitudeDegrees) && location.LongitudeDegrees is >= -180 and <= 180 &&
               double.IsFinite(location.ElevationMeters) && !string.IsNullOrWhiteSpace(location.TimeZoneId) &&
               IsSha256(location.CanonicalSha256) && string.Equals(location.CanonicalSha256,
                   CaptureContractJson.ComputeCanonicalJsonSha256(new
                   {
                       location.LocationId,
                       location.Version,
                       location.Source,
                       location.HorizontalAccuracyMeters,
                       location.EffectiveFromUtc,
                       location.EffectiveUntilUtc,
                       location.LatitudeDegrees,
                       location.LongitudeDegrees,
                       location.ElevationMeters,
                       location.TimeZoneId
                   }), StringComparison.OrdinalIgnoreCase);
    }
}
