using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Integration")]
public sealed class SqmAssociationIntervalTests
{
    private static readonly DateTimeOffset Epoch = EnvironmentalHardwareAdapterTests.Epoch;

    [TestMethod]
    [DataRow(-2, 1, 60, 45, LocalEnvironmentalAssociationStatus.Fresh, "preceding")]
    [DataRow(0, 1, 60, 45, LocalEnvironmentalAssociationStatus.Fresh, "preceding")]
    [DataRow(0.5, 1, 60, 45, LocalEnvironmentalAssociationStatus.Stale, "overlapping")]
    [DataRow(-2, 3600, 60, 45, LocalEnvironmentalAssociationStatus.Fresh, "preceding")]
    [DataRow(-60, 3600, 60, 45, LocalEnvironmentalAssociationStatus.Missing, "unavailable")]
    [DataRow(-45, 1, 60, 45, LocalEnvironmentalAssociationStatus.Stale, "preceding")]
    [DataRow(-90, 1, 120, 110, LocalEnvironmentalAssociationStatus.Fresh, "preceding")]
    [DataRow(-44, 1, 60, 45, LocalEnvironmentalAssociationStatus.Fresh, "preceding")]
    [DataRow(-59, 1, 60, 45, LocalEnvironmentalAssociationStatus.Stale, "preceding")]
    [DataRow(-2, 0, 60, 45, LocalEnvironmentalAssociationStatus.Fresh, "interval-unknown")]
    public async Task AssociationReportsTheActualIntervalWithoutChangingValiditySelection(
        double sampleEndSeconds, int integrationSeconds, int validSeconds, int freshSeconds,
        LocalEnvironmentalAssociationStatus expected, string relationship)
    {
        var root = Path.Combine(Path.GetTempPath(), "sqm-interval-" + Guid.NewGuid().ToString("N"));
        var capture = Guid.NewGuid();
        var sample = await SampleAsync(sampleEndSeconds, Math.Max(1, integrationSeconds), validSeconds, freshSeconds).ConfigureAwait(false);
        // Legacy facts can omit the integration; validity is not evidence of its duration.
        if (integrationSeconds == 0) sample = sample with { ObservedFromUtc = null, ObservedThroughUtc = null };
        var options = OptionsFor(root);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EnvironmentalAcquisitionTelemetry.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            LocalEnvironmentalCaptureAssociation original;
            byte[] persisted;
            using (var store = new SqliteEnvironmentalObservationOutbox(new Clock()))
            {
                await store.CommitLocalAsync(root, sample, CancellationToken.None).ConfigureAwait(false);
                var service = new EnvironmentalAssociationService(store, store, options, new Clock());
                original = (await service.AssociateAsync(capture, 1, Epoch, Epoch.AddSeconds(1), null,
                    [EnvironmentalObservationKind.SkyQuality], CancellationToken.None).ConfigureAwait(false)).Single();
                Assert.AreEqual(expected, original.Status);
                Assert.AreEqual(EnvironmentalAssociationService.CreatePolicyIdentity([EnvironmentalObservationKind.SkyQuality]),
                    original.PolicyIdentitySha256, "Diagnostic versioning must not alter the durable selection policy.");
                persisted = JsonSerializer.SerializeToUtf8Bytes((await store.ReadAssociationsAsync(root, capture,
                    CancellationToken.None).ConfigureAwait(false)).Single());
            }
            var diagnostic = activities.SelectMany(activity => activity.Events)
                .Where(item => item.Name == "environment.association.interval")
                .Select(item => item.Tags.ToDictionary(pair => pair.Key, pair => pair.Value))
                .Single(tags => Equals(tags["capture_id"], capture.ToString("N")));
            Assert.AreEqual(relationship, diagnostic["relationship"]);
            Assert.AreEqual(expected.ToString(), diagnostic["status"]);
            Assert.IsFalse(diagnostic.ContainsKey("policy_version"), "There is no authorized persisted diagnostic version slot.");
            if (expected == LocalEnvironmentalAssociationStatus.Missing)
            {
                Assert.IsNull(original.SelectedRecordId);
                Assert.IsFalse(diagnostic.ContainsKey("integration_ms"), "Missing evidence must not invent an interval.");
            }
            else if (integrationSeconds == 0)
            {
                Assert.IsFalse(diagnostic.ContainsKey("integration_ms"));
                Assert.IsFalse(diagnostic.ContainsKey("gap_ms"));
                Assert.IsFalse(diagnostic.ContainsKey("maximum_pre_exposure_gap_ms"));
            }
            else
            {
                Assert.AreEqual(integrationSeconds * 1000d, diagnostic["integration_ms"]);
                Assert.AreEqual(validSeconds * 1000d, diagnostic["maximum_pre_exposure_gap_ms"]);
                Assert.AreEqual(sample.ObservedFromUtc!.Value.ToUnixTimeMilliseconds(), diagnostic["sample_from_unix_ms"]);
                Assert.AreEqual(sample.ObservedThroughUtc!.Value.ToUnixTimeMilliseconds(), diagnostic["sample_through_unix_ms"]);
                if (relationship == "preceding") Assert.AreEqual(-sampleEndSeconds * 1000, diagnostic["gap_ms"]);
                else Assert.IsFalse(diagnostic.ContainsKey("gap_ms"));
            }
            using var restarted = new SqliteEnvironmentalObservationOutbox(new Clock());
            var changed = new EnvironmentalAssociationService(restarted, restarted, OptionsFor(root, changed: true), new Clock());
            var recovered = (await changed.ReadCompletedAsync(capture, 1, Epoch, Epoch.AddSeconds(1), null,
                [EnvironmentalObservationKind.SkyQuality], CancellationToken.None).ConfigureAwait(false))!.Single();
            var retried = (await changed.AssociateAsync(capture, 1, Epoch, Epoch.AddSeconds(1), null,
                [EnvironmentalObservationKind.SkyQuality], CancellationToken.None).ConfigureAwait(false)).Single();
            Assert.AreEqual(original.AssociationIdentitySha256, recovered.AssociationIdentitySha256);
            CollectionAssert.AreEqual(persisted, JsonSerializer.SerializeToUtf8Bytes(retried));
            Assert.HasCount(1, await restarted.ReadAssociationsAsync(root, capture, CancellationToken.None).ConfigureAwait(false));
            if (retried.SelectedRecordId is { } selected)
            {
                var retainedSample = await restarted.ReadLocalDetailAsync(root, selected, CancellationToken.None).ConfigureAwait(false);
                CollectionAssert.AreEqual(EnvironmentalObservationFactJson.Serialize(sample),
                    EnvironmentalObservationFactJson.Serialize(retainedSample!.Fact));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NonSqmObservationDoesNotAcquireSqmTimingDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), "sqm-unrelated-" + Guid.NewGuid().ToString("N"));
        var capture = Guid.NewGuid();
        var sample = await SampleAsync(-2, 1, 60, 45).ConfigureAwait(false);
        sample = sample with { Source = sample.Source with { Provider = "other-sky-quality" } };
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EnvironmentalAcquisitionTelemetry.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox(new Clock());
            await store.CommitLocalAsync(root, sample, CancellationToken.None).ConfigureAwait(false);
            var association = (await new EnvironmentalAssociationService(store, store, OptionsFor(root), new Clock())
                .AssociateAsync(capture, 1, Epoch, Epoch.AddSeconds(1), null, [EnvironmentalObservationKind.SkyQuality],
                    CancellationToken.None).ConfigureAwait(false)).Single();
            Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, association.Status);
            Assert.IsFalse(activities.SelectMany(activity => activity.Events)
                .Any(item => item.Name == "environment.association.interval" &&
                    item.Tags.Any(tag => tag.Key == "capture_id" && Equals(tag.Value, capture.ToString("N")))));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<EnvironmentalObservationFactV1> SampleAsync(double endSeconds, int integration, int valid, int fresh)
    {
        var descriptor = EnvironmentalHardwareAdapterTests.Descriptor("SkyQualityMeter", EnvironmentalObservationKind.SkyQuality)
            with { ValidForSeconds = valid, StaleAfterSeconds = fresh };
        var payload = EnvironmentalHardwareAdapterTests.Sqm.Replace("0000001.000s", integration.ToString(CultureInfo.InvariantCulture) + "s", StringComparison.Ordinal);
        var source = new SkyQualityMeterSource(descriptor, EnvironmentalHardwareAdapterTests.Options(payload));
        var result = await source.AcquireAsync(EnvironmentalHardwareAdapterTests.Context() with
        { ObservedAtUtc = Epoch.AddSeconds(endSeconds) }, CancellationToken.None).ConfigureAwait(false);
        return result.Fact!;
    }

    private static IOptions<CameraAgentHostOptions> OptionsFor(string root, bool changed = false)
        => Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            EnvironmentalAcquisition = new()
            {
                Sources = [new() { Id = "SkyQualityMeter-SkyQuality", Type = "SkyQualityMeter", Kind = EnvironmentalObservationKind.SkyQuality,
                    ValidForSeconds = changed ? 10 : 120, StaleAfterSeconds = changed ? 5 : 45 }],
                WeatherSourcePriority = changed ? ["different-priority", "SkyQualityMeter-SkyQuality"] : []
            }
        });

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Epoch;
    }
}
