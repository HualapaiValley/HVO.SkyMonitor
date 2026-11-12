using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CaptureAcquisitionCorrelationSurvivesRawPublicationAndRestart(bool correlated)
    {
        var root = CreateRoot();
        try
        {
            var id = correlated ? Guid.NewGuid() : (Guid?)null;
            var receipt = await PersistEnvironmentalCaptureAsync(root, id, Timestamp(1)).ConfigureAwait(false);
            var serialized = CaptureContractJson.Serialize(receipt.Manifest);
            var parsed = CaptureContractJson.ParseManifest(serialized);
            Assert.IsTrue(parsed.IsValid);
            Assert.AreEqual(id, parsed.Document!.Manifest!.Descriptor.CycleEvidence!.AcquisitionAttemptId);
            var invalid = receipt.Manifest.Descriptor with
            { CycleEvidence = receipt.Manifest.Descriptor.CycleEvidence! with { AcquisitionAttemptId = Guid.Empty } };
            Assert.IsFalse(invalid.Validate().IsValid);
            if (!correlated) Assert.IsFalse(System.Text.Encoding.UTF8.GetString(serialized).Contains("acquisitionAttemptId", StringComparison.Ordinal));
            using var restarted = CreateIngress(root, new RawIngressState(TimeProvider.System));
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var replay = await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .ReadCommittedCaptureStateAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(replay.Exists);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static CameraModuleConfig EnvironmentalConfiguration() => CreateConfiguration();

    internal static async Task<RawCaptureReceipt> PersistEnvironmentalCaptureAsync(string root, Guid? acquisitionId, DateTimeOffset start,
        EnvironmentalAcquisitionOptions? acquisition = null)
    {
        var config = CreateConfiguration();
        var source = CreateEnrichedSubmission(start, [1, 2, 3, 4]);
        var exposure = TimeSpan.FromSeconds(20);
        var end = start.Add(exposure);
        var frame = source.Result.Frame! with { TimestampUtc = end, Metadata = source.Result.Frame!.Metadata with { Exposure = exposure } };
        var setpoint = source.Result.NextSetpoint with { Exposure = exposure };
        var submission = source with
        {
            Request = source.Request with { RequestedSetpoint = setpoint },
            Result = source.Result with { Frame = frame, NextSetpoint = setpoint, AcquisitionTiming = new(start, end, end) },
            CycleEvidence = source.CycleEvidence! with
            {
                AcquisitionAttemptId = acquisitionId,
                Decision = new(end, end, exposure, 1, exposure, 1, CaptureControlDecisionReason.Disabled),
                IngressHandoffStartedUtc = end
            }
        };
        var clock = new FixedTimeProvider(end.AddSeconds(1));
        var state = new RawIngressState(clock);
        using var ingress = acquisition is null ? CreateIngress(root, state, timeProvider: clock)
            : new RawCaptureIngress(Options.Create(new CameraAgentHostOptions
            { RawIngressRoot = root, RawIngressReserveBytes = 0, EnvironmentalAcquisition = acquisition }),
                new FixedCapacityProvider(long.MaxValue), state, clock, new RawIngressTelemetry(state),
                NullLogger<RawCaptureIngress>.Instance, new NullRawIngressFaultInjector());
        return (await ingress.AcceptAsync(config, submission, CancellationToken.None).ConfigureAwait(false))!;
    }
}
