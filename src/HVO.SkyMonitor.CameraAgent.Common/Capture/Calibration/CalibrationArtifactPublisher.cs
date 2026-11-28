using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

internal static class CalibrationLibraryEvidenceNames
{
    internal const string BundleEnvelope = "calibration-library-bundle.json";
    internal const string ProfileMarker = "reference-calibration-profile.json";
}

public enum CalibrationPublicationFaultPoint
{
    BeforePayloadWrite,
    AfterPayloadPublished,
    BeforeManifestWrite,
    AfterManifestPublished,
    BeforeBundleWrite,
    AfterBundlePublished,
    BeforeProfileWrite,
    AfterProfilePublished,
    DirectorySynced,
    AfterSqlitePublication,
    AfterActivationCommitted
}

public interface ICalibrationPublicationFaultInjector
{
    void Inject(CalibrationPublicationFaultPoint point, string relativePath);
}

public sealed class NullCalibrationPublicationFaultInjector : ICalibrationPublicationFaultInjector
{
    public static NullCalibrationPublicationFaultInjector Instance { get; } = new();

    private NullCalibrationPublicationFaultInjector()
    {
    }

    public void Inject(CalibrationPublicationFaultPoint point, string relativePath)
    {
    }
}

public sealed class CalibrationPublicationConflictException : IOException
{
    public CalibrationPublicationConflictException()
    {
    }

    public CalibrationPublicationConflictException(string message) : base(message)
    {
    }

    public CalibrationPublicationConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CalibrationArtifactPublisher
{
    private readonly ICalibrationPublicationFaultInjector _faultInjector;

    internal CalibrationArtifactPublisher(ICalibrationPublicationFaultInjector faultInjector, SiloArtifactStorage silo)
    {
        _faultInjector = faultInjector;
        Silo = silo;
    }

    internal SiloArtifactStorage Silo { get; }

    internal const string SiloEvidenceRevision = "calibration-evidence-1";
    internal const string SiloBundleRevision = "calibration-bundle-1";
    internal const string SiloProfileRevision = "calibration-profile-1";

    internal static Guid BundleArtifactId(CalibrationLibraryBundleV1 bundle) =>
        bundle.Source == CalibrationLibraryBundleSources.VirtualAcquisitionV1
            ? VirtualBundleArtifactId(bundle.BundleId)
            : EnvelopeArtifactId(bundle);

    internal static Guid EnvelopeArtifactId(CalibrationLibraryBundleV1 bundle) =>
        ProcessingIdentity.CreateArtifactId(SiloHash.Sha256(CalibrationLibraryContractJson.Serialize(bundle)));

    internal static Guid VirtualBundleArtifactId(string bundleId) =>
        ProcessingIdentity.CreateArtifactId(SiloHash.Sha256(System.Text.Encoding.UTF8.GetBytes("calibration-job:" + bundleId)));

    internal async ValueTask PublishSiloBundleAsync(CalibrationLibraryBundleV1 bundle, ReadOnlyMemory<byte> profile,
        CancellationToken cancellationToken)
    {
        var envelope = CalibrationLibraryContractJson.Serialize(bundle);
        var id = BundleArtifactId(bundle);
        var members = new[]
        {
            SiloArtifactStorage.Member(id, SiloBundleRevision, "calibration-bundle", SiloRetentionClass.Full,
                "application/json", envelope.LongLength, SiloHash.Sha256(envelope)),
            SiloArtifactStorage.Member(id, SiloProfileRevision, "calibration-profile", SiloRetentionClass.Full,
                "application/json", profile.Length, bundle.ProfileIdentitySha256)
        };
        var created = bundle.CreatedUtc.ToUniversalTime();
        var draft = new SiloGroupDraft("calibration-bundle", id.ToString("N"), "1", created, created, created,
            members, [], new([]), CameraId: bundle.Applicability.RigId);
        _faultInjector.Inject(CalibrationPublicationFaultPoint.BeforeBundleWrite, bundle.ProfileRelativePath);
        _faultInjector.Inject(CalibrationPublicationFaultPoint.BeforeProfileWrite, bundle.ProfileRelativePath);
        await Silo.PublishAsync(draft, (member, token) => SiloArtifactStorage.OpenMemoryAsync(
            member.ArtifactRevision == SiloBundleRevision ? envelope : profile, token), cancellationToken).ConfigureAwait(false);
        _faultInjector.Inject(CalibrationPublicationFaultPoint.AfterBundlePublished, bundle.ProfileRelativePath);
        _faultInjector.Inject(CalibrationPublicationFaultPoint.AfterProfilePublished, bundle.ProfileRelativePath);
    }

    internal void InjectFault(CalibrationPublicationFaultPoint point, string relativePath)
        => _faultInjector.Inject(point, relativePath);

    public async Task PublishPairAsync(
        string payloadRelativePath,
        ReadOnlyMemory<byte> payload,
        string manifestRelativePath,
        ReadOnlyMemory<byte> manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadRelativePath);
        ArgumentNullException.ThrowIfNull(manifestRelativePath);
        ValidatePairPaths(payloadRelativePath, manifestRelativePath);
        var parsed = CaptureContractJson.ParseManifest(manifest);
        if (!parsed.IsValid || parsed.Document?.Manifest is not { } evidence)
            throw new InvalidDataException("Silo calibration publication requires a valid artifact manifest.");
        var descriptor = evidence.Descriptor;
        if (descriptor.Layout.ByteLength != payload.Length || evidence.RelativeArtifactPath != payloadRelativePath)
            throw new InvalidDataException("The Silo calibration manifest differs from its payload identity.");
        var id = descriptor.Artifact.ArtifactId;
        var revision = CaptureContractJson.ComputeDescriptorSha256(descriptor);
        var payloadMember = SiloArtifactStorage.Member(id, revision, "calibration-frame", SiloRetentionClass.Full,
            descriptor.Artifact.MediaType, payload.Length, descriptor.Artifact.ChecksumSha256);
        var evidenceMember = SiloArtifactStorage.Member(id, SiloEvidenceRevision, "calibration-evidence", SiloRetentionClass.Metadata,
            "application/json", manifest.Length, SiloHash.Sha256(manifest.Span));
        var draft = new SiloGroupDraft("calibration-artifact", id.ToString("N"), revision, descriptor.Artifact.CreatedUtc.ToUniversalTime(),
            descriptor.Timing.ExposureStartedUtc.ToUniversalTime(), descriptor.Timing.ExposureEndedUtc.ToUniversalTime(),
            [payloadMember, evidenceMember], [], new([]), CameraId: descriptor.Capture.RigId);
        _faultInjector.Inject(CalibrationPublicationFaultPoint.BeforePayloadWrite, payloadRelativePath);
        _faultInjector.Inject(CalibrationPublicationFaultPoint.BeforeManifestWrite, manifestRelativePath);
        try
        {
            await Silo.PublishAsync(draft, (member, token) => SiloArtifactStorage.OpenMemoryAsync(
                member == payloadMember ? payload : manifest, token), cancellationToken).ConfigureAwait(false);
        }
        catch (SiloConflictException exception)
        {
            throw new CalibrationPublicationConflictException("Silo calibration publication conflicts with immutable evidence.", exception);
        }
        _faultInjector.Inject(CalibrationPublicationFaultPoint.AfterPayloadPublished, payloadRelativePath);
        _faultInjector.Inject(CalibrationPublicationFaultPoint.AfterManifestPublished, manifestRelativePath);
    }

    private static void ValidatePairPaths(string payloadRelativePath, string manifestRelativePath)
    {
        var payloadStem = Path.ChangeExtension(payloadRelativePath, null);
        var manifestStem = Path.ChangeExtension(manifestRelativePath, null);
        if (!payloadRelativePath.EndsWith(".bin", StringComparison.Ordinal) ||
            !manifestRelativePath.EndsWith(".json", StringComparison.Ordinal) ||
            !string.Equals(payloadStem, manifestStem, StringComparison.Ordinal))
        {
            throw new ArgumentException("Calibration payload and manifest paths must be a matching .bin/.json pair.");
        }
    }
}
