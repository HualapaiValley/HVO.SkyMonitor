using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Transients;

namespace HVO.SkyMonitor.CameraAgent.Common.Diagnostics;

internal sealed class CameraAgentAcceptanceFaultController(string controlRoot) :
    IRawIngressFaultInjector,
    ICalibrationPublicationFaultInjector,
    ITransientCandidateFaultInjector,
    ITransientRuntimeFaultInjector,
    ICaptureProcessingFaultInjector,
    IStorageCapacityProvider,
    IAcceptanceRetentionControl
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _controlRoot = Path.GetFullPath(controlRoot);
    private readonly FileSystemStorageCapacityProvider _capacity = new();
    private readonly object _gate = new();

    public bool IsEnabled(RawIngressFaultPoint point)
    {
        lock (_gate)
        {
            var armPath = Path.Combine(_controlRoot, "arm.json");
            if (!File.Exists(armPath))
            {
                return false;
            }
            var arm = JsonSerializer.Deserialize<FaultArm>(File.ReadAllBytes(armPath), JsonOptions)
                ?? throw new InvalidDataException("Acceptance fault arm is invalid.");
            return string.Equals(arm.Boundary, $"raw.{point}", StringComparison.Ordinal) && arm.NodeId is null;
        }
    }

    public void Inject(RawIngressFaultPoint point) => Inject($"raw.{point}", null);

    public void Inject(CalibrationPublicationFaultPoint point, string relativePath)
        => Inject($"calibration.{point}", relativePath);

    public void Inject(TransientCandidateFaultPoint point) => Inject($"transient-candidate.{point}", null);

    public void Inject(TransientRuntimeFaultPoint point) => Inject($"transient-runtime.{point}", null);

    public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        => Inject($"processing.{point}", nodeId);

    public StorageCapacity GetCapacity(string storageRoot)
    {
        var path = Path.Combine(_controlRoot, "capacity.json");
        if (!File.Exists(path))
        {
            return _capacity.GetCapacity(storageRoot);
        }
        var overrideValue = JsonSerializer.Deserialize<CapacityOverride>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException("Acceptance capacity override is invalid.");
        if (overrideValue.TotalBytes <= 0 || overrideValue.AvailableBytes < 0 ||
            overrideValue.AvailableBytes > overrideValue.TotalBytes)
        {
            throw new InvalidDataException("Acceptance capacity override is outside its valid range.");
        }
        return new StorageCapacity(overrideValue.TotalBytes, overrideValue.AvailableBytes);
    }

    public async ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(
        string storageRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(_controlRoot, "retention-holds.json");
        if (!File.Exists(path))
        {
            return [];
        }
        var configured = JsonSerializer.Deserialize<RetentionHoldOverride[]>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Acceptance retention holds are invalid.");
        var holds = new List<ProcessingRetentionHold>(configured.Length);
        foreach (var hold in configured)
        {
            if (Path.IsPathRooted(hold.PayloadRelativePath) || Path.IsPathRooted(hold.SidecarRelativePath) ||
                string.IsNullOrWhiteSpace(hold.PayloadRelativePath) || string.IsNullOrWhiteSpace(hold.SidecarRelativePath))
            {
                throw new InvalidDataException("Acceptance retention hold paths must be relative.");
            }
            holds.Add(new ProcessingRetentionHold(
                hold.ArtifactId,
                hold.PayloadRelativePath,
                hold.SidecarRelativePath));
        }
        return holds;
    }

    private void Inject(string boundary, string? nodeId)
    {
        FaultArm? arm;
        lock (_gate)
        {
            var armPath = Path.Combine(_controlRoot, "arm.json");
            if (!File.Exists(armPath))
            {
                return;
            }
            arm = JsonSerializer.Deserialize<FaultArm>(File.ReadAllBytes(armPath), JsonOptions)
                ?? throw new InvalidDataException("Acceptance fault arm is invalid.");
            if (!string.Equals(arm.Boundary, boundary, StringComparison.Ordinal) ||
                arm.NodeId is not null && !string.Equals(arm.NodeId, nodeId, StringComparison.Ordinal))
            {
                return;
            }
            ValidateOperationId(arm.OperationId);
            Directory.CreateDirectory(_controlRoot);
            File.Move(armPath, Path.Combine(_controlRoot, $"consumed-{arm.OperationId}.json"), overwrite: false);
            WriteAtomic(
                Path.Combine(_controlRoot, $"hit-{arm.OperationId}.json"),
                JsonSerializer.SerializeToUtf8Bytes(new FaultHit(arm.OperationId, boundary, nodeId, DateTimeOffset.UtcNow), JsonOptions));
        }

        if (string.Equals(arm.Action, "throw", StringComparison.Ordinal))
        {
            throw new IOException($"Acceptance fault injected at {boundary}.");
        }
        if (!string.Equals(arm.Action, "block", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Acceptance fault action must be block or throw.");
        }
        var blockTimeoutSeconds = arm.BlockTimeoutSeconds ?? 30;
        if (blockTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidDataException("Acceptance fault block timeout must be between 1 and 300 seconds.");
        }

        var releasePath = Path.Combine(_controlRoot, $"release-{arm.OperationId}");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(blockTimeoutSeconds);
        while (!File.Exists(releasePath))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Acceptance fault '{arm.OperationId}' was not released within 30 seconds.");
            }
            Thread.Sleep(25);
        }
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, overwrite: false);
    }

    private static void ValidateOperationId(string operationId)
    {
        if (operationId.Length is < 1 or > 64 || operationId.Any(static value =>
                !char.IsAsciiLetterOrDigit(value) && value is not '-' and not '_'))
        {
            throw new InvalidDataException("Acceptance fault operation ID is invalid.");
        }
    }

    private sealed record FaultArm(
        string OperationId,
        string Boundary,
        string Action,
        string? NodeId,
        int? BlockTimeoutSeconds);
    private sealed record FaultHit(string OperationId, string Boundary, string? NodeId, DateTimeOffset HitUtc);
    private sealed record CapacityOverride(long TotalBytes, long AvailableBytes);
    private sealed record RetentionHoldOverride(
        Guid ArtifactId,
        string PayloadRelativePath,
        string SidecarRelativePath);
}
