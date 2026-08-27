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
        CancellationToken cancellationToken)
    {
        var descriptor = context.ReconstructionDescriptor;
        if (descriptor is null) return null;
        var fromUtc = descriptor.Timing.ExposureStartedUtc.ToUniversalTime();
        var throughUtc = descriptor.Timing.ExposureEndedUtc.ToUniversalTime();
        if (throughUtc <= fromUtc) throughUtc = fromUtc.AddMilliseconds(1);
        IReadOnlyList<LocalEnvironmentalCaptureAssociation>? associated = null;
        if (hostOptions.Value.EnvironmentalAcquisition.Enabled && kinds.Count > 0)
        {
            associated = await associations.ReadCompletedAsync(
                descriptor.Capture.CaptureId, descriptor.Capture.CaptureSequence, fromUtc, throughUtc,
                descriptor.Capture.RigId, kinds, cancellationToken).ConfigureAwait(false);
            if (associated is null) return null;
        }
        var expectedKinds = kinds.Distinct().Order().ToArray();
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
            context.RecordCanonicalInput($"environment-association-{association.Kind}",
                "local-environment-association-v1", association.AssociationIdentitySha256);
            if (observation is not null)
                context.RecordCanonicalInput($"environment-observation-{association.Kind}",
                    observation.Fact.SchemaVersion, observation.ContentSha256);
            for (var index = 0; index < conflictRecords.Count; index++)
                context.RecordCanonicalInput($"environment-conflict-{association.Kind}-{index}",
                    conflictRecords[index].Fact.SchemaVersion, conflictRecords[index].ContentSha256);
        }

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
            CadenceSeconds = context.Config.Schedule?.SetpointProfiles.SingleOrDefault(profile =>
                string.Equals(profile.Id, schedule?.SetpointProfileId, StringComparison.Ordinal))?.CaptureInterval.TotalSeconds ??
                context.Config.Rig.Pipeline.CaptureInterval.TotalSeconds
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
            DisplayLines([$"AGENT {descriptor.Capture.AgentId}", $"CAPTURE {descriptor.Capture.CaptureSequence.ToString(CultureInfo.InvariantCulture)}",
             $"UTC {descriptor.Timing.ExposureStartedUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}"]),
            DisplayLines([$"SCHEDULE {schedule?.SetpointProfileId ?? "UNAVAILABLE"}",
              $"EXPOSURE {descriptor.Controls.EffectiveExposure.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} S",
              $"GAIN {descriptor.Controls.EffectiveGain.ToString("F3", CultureInfo.InvariantCulture)}",
              $"OFFSET {descriptor.Controls.EffectiveOffset?.ToString("F1", CultureInfo.InvariantCulture) ?? "UNAVAILABLE"}",
              $"SETPOINT {descriptor.Controls.TemperatureSetpointC?.ToString("F1", CultureInfo.InvariantCulture) ?? "UNAVAILABLE"} C"]),
            DisplayLines(environment.Count == 0 ? ["ENVIRONMENT MISSING"] : environment.Select(static item => item.DisplayLine)),
            DisplayLines([$"CATALOG {scene.Catalog.Name} {scene.Catalog.Version} {scene.Catalog.ChecksumSha256[..12]}",
              $"CALIBRATION {descriptor.Profiles.Calibration.Name} {descriptor.Profiles.Calibration.Version} {descriptor.Profiles.Calibration.Sha256[..12]}",
              stackProduct is null ? "STACK UNAVAILABLE" : $"STACK {stackProduct.SourceArtifactIds.Count} {stackProduct.TotalIntegration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} S",
              $"PROFILE {descriptor.Profiles.Processing.Name} {descriptor.Profiles.Processing.Version} {descriptor.Profiles.Processing.Sha256[..12]}"]));
        var facts = new PresentationMetadataFactsProductV1(
            PresentationMetadataFactsProductV1.CurrentSchemaVersion, string.Empty, descriptor.Capture.CaptureId,
            descriptor.Capture.CaptureSequence, capture, environment, catalog, calibration, stack,
            processing, corners, sourceArtifactIds);
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
        var kind = association.Kind.ToString().ToUpperInvariant();
        var status = association.Status.ToString().ToUpperInvariant();
        if (selected is null) return $"{kind} {status}";
        var value = selected.Fact.Value;
        var formatted = value.BooleanValue is { } boolean
            ? boolean ? "TRUE" : "FALSE"
            : value.NumericValue?.ToString("F1", CultureInfo.InvariantCulture) ?? "UNAVAILABLE";
        var age = Math.Max(0, Math.Round((evaluatedUtc - selected.Fact.ObservedAtUtc).TotalSeconds));
        return $"{kind} {formatted} {value.Unit.ToString().ToUpperInvariant()} {status} {value.Quality.ToString().ToUpperInvariant()} AGE {age.ToString("F0", CultureInfo.InvariantCulture)} S";
    }

    private static string[] DisplayLines(IEnumerable<string> lines) => lines
        .Take(PresentationLayerPayloadV1.MaximumLinesPerBlock)
        .Select(static line => line.Length <= PresentationLayerPayloadV1.MaximumLineCharacters
            ? line
            : line[..PresentationLayerPayloadV1.MaximumLineCharacters])
        .ToArray();
}
