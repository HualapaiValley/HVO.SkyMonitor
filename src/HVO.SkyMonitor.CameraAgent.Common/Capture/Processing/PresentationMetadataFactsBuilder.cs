using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class PresentationMetadataFactsBuilder(
    EnvironmentalAssociationService associations,
    ILocalEnvironmentalObservationStore observations,
    IOptions<CameraAgentHostOptions> hostOptions)
{
    internal async ValueTask<PresentationMetadataFactsProductV1?> BuildAsync(
        CaptureProcessingContext context,
        ProjectedSceneV1 scene,
        ProcessingProduct? stackProduct,
        IReadOnlyList<Guid> sourceArtifactIds,
        IReadOnlyList<EnvironmentalObservationKind> kinds,
        CancellationToken cancellationToken,
        bool retainFrameFacts = false)
    {
        var descriptor = context.ReconstructionDescriptor;
        if (descriptor is null) return null;
        var fromUtc = descriptor.Timing.ExposureStartedUtc.ToUniversalTime();
        var throughUtc = descriptor.Timing.ExposureEndedUtc.ToUniversalTime();
        if (throughUtc <= fromUtc) throughUtc = fromUtc.AddMilliseconds(1);
        var expectedKinds = kinds.Distinct().Order().ToArray();
        IReadOnlyList<LocalEnvironmentalCaptureAssociation>? associated = null;
        if (hostOptions.Value.EnvironmentalAcquisition.Enabled && expectedKinds.Length > 0)
        {
            associated = await associations.ReadCompletedAsync(
                descriptor.Capture.CaptureId, descriptor.Capture.CaptureSequence, fromUtc, throughUtc,
                descriptor.Capture.RigId, expectedKinds, cancellationToken).ConfigureAwait(false);
            if (associated is null) return null;
        }
        var configuredPolicyIdentity = EnvironmentalAssociationService.CreatePolicyIdentity(expectedKinds);
        associated ??= expectedKinds.Select(kind => new LocalEnvironmentalCaptureAssociation(
            descriptor.Capture.CaptureId, descriptor.Capture.CaptureSequence, kind, descriptor.Capture.RigId,
            fromUtc, throughUtc, configuredPolicyIdentity, LocalEnvironmentalAssociationStatus.Missing, null, [],
            CaptureContractJson.ComputeCanonicalJsonSha256(new
            {
                Schema = "local-environment-association-v1",
                descriptor.Capture.CaptureId,
                descriptor.Capture.CaptureSequence,
                kind,
                descriptor.Capture.RigId,
                ExposureFromUtc = fromUtc,
                ExposureThroughUtc = throughUtc,
                PolicyIdentitySha256 = configuredPolicyIdentity,
                Status = LocalEnvironmentalAssociationStatus.Missing,
                Selected = (object?)null,
                Conflicts = Array.Empty<object>()
            }),
            throughUtc)).ToArray();

        var environment = new List<PresentationEnvironmentalFactV1>(associated.Count);
        var sourceGroups = new HashSet<string>(StringComparer.Ordinal);
        var retainedEnvironment = retainFrameFacts ? new List<FrameFactsRetainedEnvironmentV1>(associated.Count) : null;
        foreach (var association in associated)
        {
            LocalEnvironmentalObservationRecord? observation = null;
            if (association.SelectedRecordId is { } recordId)
            {
                observation = await observations.ReadLocalDetailAsync(
                    hostOptions.Value.RawIngressRoot, recordId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Selected presentation environmental evidence is unavailable.");
            }
            var conflictRecords = new List<LocalEnvironmentalObservationRecord>(association.ConflictingRecordIds.Count);
            foreach (var conflictRecordId in association.ConflictingRecordIds)
            {
                var conflict = await observations.ReadLocalDetailAsync(
                    hostOptions.Value.RawIngressRoot, conflictRecordId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Conflicting presentation environmental evidence is unavailable.");
                conflictRecords.Add(conflict);
            }
            conflictRecords.Sort(static (left, right) =>
            {
                var source = string.Compare(left.SourceIdentitySha256, right.SourceIdentitySha256, StringComparison.Ordinal);
                return source != 0 ? source : left.Fact.ObservationId.CompareTo(right.Fact.ObservationId);
            });
            if (observation is not null && EnvironmentalReadingSelection.SourceGroup(observation.Fact) is { } sourceGroup)
                sourceGroups.Add(sourceGroup);
            var conflicts = conflictRecords.Select(static conflict => new PresentationEnvironmentalConflictV1(
                conflict.Fact.ObservationId, conflict.SourceIdentitySha256, conflict.ContentSha256)).ToArray();
            environment.Add(new(
                association.Kind.ToString(), association.Status.ToString(), association.PolicyIdentitySha256,
                association.AssociationIdentitySha256, association.RigId, association.ExposureFromUtc,
                association.ExposureThroughUtc, throughUtc,
                observation?.Fact.ObservationId, observation?.SourceIdentitySha256,
                observation?.SourceContentSha256, observation?.ContentSha256,
                observation?.Fact.ObservedAtUtc, observation?.Fact.StaleAfterUtc,
                observation?.Fact.Value.Quality.ToString(), conflicts,
                FormatEnvironmentLine(association, observation, throughUtc)));
            retainedEnvironment?.Add(new(descriptor.Capture.CaptureId, association.Kind, association.Status switch
            {
                LocalEnvironmentalAssociationStatus.Fresh => FrameFactsAvailability.Available,
                LocalEnvironmentalAssociationStatus.Stale => FrameFactsAvailability.Stale,
                LocalEnvironmentalAssociationStatus.Missing => FrameFactsAvailability.Missing,
                LocalEnvironmentalAssociationStatus.Contradictory => FrameFactsAvailability.Conflicting,
                _ => FrameFactsAvailability.Unsupported
            }, association.AssociationIdentitySha256, association.ExposureFromUtc, association.ExposureThroughUtc, observation?.Fact));
            context.RecordCanonicalInput($"environment-association-{association.Kind}",
                "local-environment-association-v1", association.AssociationIdentitySha256);
            if (observation is not null)
                context.RecordCanonicalInput($"environment-observation-{association.Kind}",
                    observation.Fact.SchemaVersion, observation.ContentSha256);
            for (var index = 0; index < conflictRecords.Count; index++)
                context.RecordCanonicalInput($"environment-conflict-{association.Kind}-{index}",
                    conflictRecords[index].Fact.SchemaVersion, conflictRecords[index].ContentSha256);
        }

        var environmentLines = environment.Select(static item => item.DisplayLine);
        if (sourceGroups.Count > 1) environmentLines = environmentLines.Prepend("MIXED SOURCES");
        var schedule = descriptor.CycleEvidence?.ScheduleAdmission;
        var capture = JsonSerializer.SerializeToElement(new
        {
            descriptor.Capture.AgentId,
            descriptor.Capture.CaptureSequence,
            descriptor.Timing.ExposureStartedUtc,
            schedule?.SetpointProfileId,
            descriptor.Controls.EffectiveExposure,
            descriptor.Controls.EffectiveGain,
            descriptor.Controls.EffectiveOffset,
            descriptor.Controls.TemperatureSetpointC,
            CadenceMode = descriptor.CycleEvidence?.CadenceMode,
            CadenceSeconds = context.Submission.EffectiveInterval.TotalSeconds
        });
        var catalog = JsonSerializer.SerializeToElement(scene.Catalog);
        var calibration = JsonSerializer.SerializeToElement(descriptor.Profiles.Calibration);
        var stack = stackProduct is null
            ? JsonSerializer.SerializeToElement(new { Status = "Missing" })
            : JsonSerializer.SerializeToElement(new
            {
                Status = "Available",
                Count = stackProduct.SourceArtifactIds.Count,
                stackProduct.TotalIntegration,
                stackProduct.OutputIdentitySha256
            });
        var processing = JsonSerializer.SerializeToElement(descriptor.Profiles.Processing);
        var corners = new PresentationMetadataFactsV1(string.Empty,
            DisplayLines([$"CAPTURE {descriptor.Capture.CaptureSequence.ToString(CultureInfo.InvariantCulture)}", $"{descriptor.Layout.Width} × {descriptor.Layout.Height} PIXELS",
             $"UTC {descriptor.Timing.ExposureStartedUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}"]),
            DisplayLines(["ACQUISITION", $"SCHEDULE {schedule?.SetpointProfileId ?? "UNAVAILABLE"}",
              $"EXPOSURE {descriptor.Controls.EffectiveExposure.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} S",
              $"GAIN {descriptor.Controls.EffectiveGain.ToString("F3", CultureInfo.InvariantCulture)}",
              $"OFFSET {descriptor.Controls.EffectiveOffset?.ToString("F1", CultureInfo.InvariantCulture) ?? "UNAVAILABLE"}",
              $"SETPOINT {descriptor.Controls.TemperatureSetpointC?.ToString("F1", CultureInfo.InvariantCulture) ?? "UNAVAILABLE"} C"]),
            DisplayLines(environment.Count == 0 ? ["ENVIRONMENT", "NOT RETAINED"] :
                environmentLines.Prepend("ENVIRONMENT")),
            DisplayLines(["LINEAGE", $"CATALOG {scene.Catalog.Name} {scene.Catalog.Version}",
              $"CALIBRATION {descriptor.Profiles.Calibration.Version} / {descriptor.Profiles.Calibration.Sha256[..12]}",
              stackProduct is null ? "STACK UNAVAILABLE" : $"STACK {stackProduct.SourceArtifactIds.Count} {stackProduct.TotalIntegration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} S",
              $"PROFILE {descriptor.Profiles.Processing.Version} / {descriptor.Profiles.Processing.Sha256[..12]}"]));
        var facts = new PresentationMetadataFactsProductV1(
            PresentationMetadataFactsProductV1.CurrentSchemaVersion, string.Empty, descriptor.Capture.CaptureId,
            descriptor.Capture.CaptureSequence, capture, environment, catalog, calibration, stack,
            processing, corners, sourceArtifactIds);
        if (retainFrameFacts)
        {
            var descriptorIdentity = CaptureContractJson.ComputeDescriptorSha256(descriptor);
            // Only captured evidence is projected. No current location, time-zone database or processing clock
            // supplies optional facts absent from the retained source. Source time is read only from raw evidence.
            var retained = new FrameFactsRetainedInputV1(descriptor,
                new(descriptorIdentity, context.Artifacts?.Raw.Frame.TimestampUtc, context.Submission.EffectiveInterval.TotalSeconds),
                stackProduct is null ? null : new(descriptorIdentity, stackProduct.OutputIdentitySha256,
                    stackProduct.SourceArtifactIds, stackProduct.TotalIntegration),
                Astronomy: scene, Environment: retainedEnvironment);
            facts = facts with
            {
                SchemaVersion = PresentationMetadataFactsProductV1.RichTextSchemaVersion,
                FrameFacts = FrameFactsSnapshotV1.Create(FrameFactsRetainedContext.Create(retained))
            };
        }
        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(
            CaptureContractJson.SerializeToElement(facts with
            {
                FactsIdentitySha256 = string.Empty,
                Corners = facts.Corners with { SourceIdentitySha256 = string.Empty }
            }));
        return facts with
        {
            FactsIdentitySha256 = identity,
            Corners = facts.Corners with { SourceIdentitySha256 = identity }
        };
    }

    private static string FormatEnvironmentLine(
        LocalEnvironmentalCaptureAssociation association,
        LocalEnvironmentalObservationRecord? selected,
        DateTimeOffset evaluatedUtc)
    {
        var kind = association.Kind switch
        {
            EnvironmentalObservationKind.AirTemperature => "AIR",
            EnvironmentalObservationKind.RelativeHumidity => "RH",
            EnvironmentalObservationKind.AtmosphericPressure => "PRESS",
            EnvironmentalObservationKind.SeaLevelPressure => "SLP",
            EnvironmentalObservationKind.WindSpeed => "WIND",
            EnvironmentalObservationKind.WindDirection => "DIR",
            EnvironmentalObservationKind.WindGust => "GUST",
            EnvironmentalObservationKind.PrecipitationRate => "RAINRATE",
            EnvironmentalObservationKind.RainState => "RAIN",
            EnvironmentalObservationKind.SkyBrightness => "SKYBR",
            EnvironmentalObservationKind.SkyQuality => "SQM",
            EnvironmentalObservationKind.CloudCover => "CLOUD",
            EnvironmentalObservationKind.CameraSensorTemperature => "CAMTEMP",
            EnvironmentalObservationKind.SkyTemperature => "SKYTEMP",
            _ => association.Kind.ToString().ToUpperInvariant()
        };
        var status = association.Status.ToString().ToUpperInvariant();
        if (selected is null) return $"{kind} {status}";
        var value = selected.Fact.Value;
        var formatted = value.BooleanValue is { } boolean
            ? boolean ? "TRUE" : "FALSE"
            : value.NumericValue?.ToString("F1", CultureInfo.InvariantCulture) ?? "UNAVAILABLE";
        var unit = value.Unit switch
        {
            EnvironmentalObservationUnit.DegreesCelsius => "C",
            EnvironmentalObservationUnit.Percent => "%",
            EnvironmentalObservationUnit.Pascals => "Pa",
            EnvironmentalObservationUnit.MetersPerSecond => "m/s",
            EnvironmentalObservationUnit.DegreesTrue => "deg",
            EnvironmentalObservationUnit.MillimetersPerHour => "mm/h",
            EnvironmentalObservationUnit.MagnitudesPerSquareArcsecond => "mag/as2",
            EnvironmentalObservationUnit.Fraction => "frac",
            _ => string.Empty
        };
        var ageSeconds = Math.Max(0, (evaluatedUtc - selected.Fact.ObservedAtUtc).TotalSeconds);
        var age = ageSeconds < 60 ? FormattableString.Invariant($"{ageSeconds:0}S") :
            ageSeconds < 3600 ? FormattableString.Invariant($"{ageSeconds / 60:0}M") :
            ageSeconds < 86400 ? FormattableString.Invariant($"{ageSeconds / 3600:0}H") :
            FormattableString.Invariant($"{ageSeconds / 86400:0}D");
        var quality = value.Quality == EnvironmentalObservationQuality.Good ? string.Empty : " ?";
        var prefix = $"{kind} {formatted}{unit} {status}{quality} AGE {age} {selected.Fact.Source.Kind.ToString().ToUpperInvariant()} SRC ";
        var group = EnvironmentalReadingSelection.SourceGroup(selected.Fact)!;
        return prefix + CompactSource(group, PresentationLayerPayloadV1.MaximumLineCharacters - prefix.Length);
    }

    private static string CompactSource(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        if (maximum <= 3) return value[..Math.Max(0, maximum)];
        var slash = value.LastIndexOf('/');
        if (slash < 0) return Shorten(value, maximum);
        var providerBudget = Math.Min(slash, Math.Max(1, maximum / 2));
        return Shorten(value[..slash], providerBudget) + "/" + Shorten(value[(slash + 1)..], maximum - providerBudget - 1);

        static string Shorten(string text, int limit)
            => text.Length <= limit ? text : limit <= 1 ? text[..Math.Max(0, limit)] :
                text[..((limit - 1) / 2)] + "…" + text[^(limit / 2)..];
    }

    private static string[] DisplayLines(IEnumerable<string> lines) => lines
        .Take(PresentationLayerPayloadV1.MaximumLinesPerBlock)
        .Select(static line => line.Length <= PresentationLayerPayloadV1.MaximumLineCharacters
            ? line
            : line[..PresentationLayerPayloadV1.MaximumLineCharacters])
        .ToArray();
}
