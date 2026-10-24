using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class CaptureSatelliteIntegrationTests
{
    [TestMethod]
    public async Task CapacityStopsNewIntentsButAllowsInterruptedRetriesAndResumesAfterRetention()
    {
        var root = CreateRoot();
        try
        {
            var f = new CaptureSatelliteFixture();
            var clock = new Clock(f.Descriptor.Timing.DurableIngressUtc);
            using var provider = CreateProvider(root, f, clock, inputFault: true);
            var work = provider.GetRequiredService<CaptureSatelliteWorkStore>();
            work.WorkEntryLimit = 2;
            var directory = Path.Combine(root, "journal", "capture-satellites");
            Directory.CreateDirectory(Path.Combine(directory, new string('0', 64)));
            var config = Configuration(root, f);
            await CaptureAsync(provider, config, f).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, (await ProcessAsync(provider, config).ConfigureAwait(false)).Outcome);
            Assert.HasCount(2, Directory.EnumerateDirectories(directory));
            var inputPath = Directory.GetFiles(directory, "input.json", SearchOption.AllDirectories).Single();
            var frozen = await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false);
            work.BeforeHeaderPublicationForTest = null;
            clock.Now = clock.Now.AddSeconds(130);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessAsync(provider, config).ConfigureAwait(false)).Outcome,
                "A retry must adopt its frozen input even when all work slots are occupied.");
            CollectionAssert.AreEqual(frozen, await File.ReadAllBytesAsync(inputPath).ConfigureAwait(false));
            var source = CaptureSatelliteProducts.ParseInput(frozen).InputManifest();
            Assert.AreEqual(1L, await ReferenceCountAsync(root, source).ConfigureAwait(false));
            await CaptureAsync(provider, config, f, 15).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, (await ProcessAsync(provider, config).ConfigureAwait(false)).Outcome);
            Assert.HasCount(2, Directory.EnumerateDirectories(directory), "Capacity refusal creates no third intent.");
            Assert.AreEqual(1L, await ReferenceCountAsync(root, source).ConfigureAwait(false));
            await work.ReconcileAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, Directory.EnumerateDirectories(directory));
            clock.Now = clock.Now.AddSeconds(130);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessAsync(provider, config).ConfigureAwait(false)).Outcome);
            Assert.AreEqual(2L, await ReferenceCountAsync(root, source).ConfigureAwait(false));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DailyAccumulationRenewsDiscoveryBudgetsWithoutDroppingAnyRawHold()
    {
        // Exercise the actual production capacity/cadence invariant as well as a scaled byte budget.
        Assert.IsGreaterThan(31 * 24 * 60 * 4, CaptureSatelliteWorkStore.MaximumWorkEntries);
        var root = CreateRoot();
        try
        {
            var f = new CaptureSatelliteFixture();
            var clock = new Clock(f.Descriptor.Timing.DurableIngressUtc);
            using var provider = CreateProvider(root, f, clock);
            var config = Configuration(root, f);
            var rawIds = new List<Guid>();
            for (var day = 0; day < 31; day++)
            {
                clock.Now = f.Descriptor.Timing.DurableIngressUtc.AddDays(day);
                var raw = await CaptureAsync(provider, config, f, day * 86400).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessAsync(provider, config).ConfigureAwait(false)).Outcome);
                rawIds.Add(raw.Manifest.Descriptor.Artifact.ArtifactId);
            }
            var work = provider.GetRequiredService<CaptureSatelliteWorkStore>();
            var renewals = 0;
            work.WorkEntryLimit = 1; // An existing oversized inventory still needs every live hold.
            work.RetentionGuardFactory = () =>
            {
                renewals++;
                return new(root, provider.GetRequiredService<SqliteCaptureProcessingStore>(), maximumBytes: 64 * 1024);
            };
            var holds = await work.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false);
            Assert.IsGreaterThan(1, renewals, "Daily accumulation must renew a cumulative budget rather than aborting retention.");
            Assert.IsTrue(rawIds.All(id => holds.Any(hold => hold.ArtifactId == id)), "Discovery must include every day's live raw hold.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task AFirstCaptureThatCannotFitDiscoveryStillFailsClosed()
    {
        var root = CreateRoot();
        try
        {
            var f = new CaptureSatelliteFixture();
            using var provider = CreateProvider(root, f, new Clock(f.Descriptor.Timing.DurableIngressUtc));
            var config = Configuration(root, f);
            var raw = await CaptureAsync(provider, config, f).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessAsync(provider, config).ConfigureAwait(false)).Outcome);
            var work = provider.GetRequiredService<CaptureSatelliteWorkStore>();
            var attempts = 0;
            work.RetentionGuardFactory = () =>
            {
                attempts++;
                return new(root, provider.GetRequiredService<SqliteCaptureProcessingStore>(), maximumBytes: 1);
            };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => work.GetRetentionHoldsAsync(root, CancellationToken.None).AsTask()).ConfigureAwait(false);
            Assert.AreEqual(2, attempts, "One retry must never become an unbounded retry or an incomplete hold result.");
            var input = CaptureSatelliteProducts.ParseInput(await File.ReadAllBytesAsync(Directory.GetFiles(
                Path.Combine(root, "journal", "capture-satellites"), "input.json", SearchOption.AllDirectories).Single()).ConfigureAwait(false));
            Assert.AreEqual(1L, await ReferenceCountAsync(root, input.InputManifest()).ConfigureAwait(false));
            Assert.IsTrue(File.Exists(raw.StoredFrame.AbsolutePath));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HeaderlessInputCleanupResumesPastHeldCandidatesAndByteBudgetAfterRestart(bool exhaustBytes)
    {
        var root = CreateRoot();
        try
        {
            var f = new CaptureSatelliteFixture();
            var clock = new Clock(f.Descriptor.Timing.DurableIngressUtc);
            var directory = Path.Combine(root, "journal", "capture-satellites");
            string source;
            var heldCount = exhaustBytes ? 2 : 16;
            using (var provider = CreateProvider(root, f, clock))
            {
                var config = Configuration(root, f);
                await CaptureAsync(provider, config, f).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessAsync(provider, config).ConfigureAwait(false)).Outcome);
                var input = await File.ReadAllBytesAsync(Directory.GetFiles(directory, "input.json", SearchOption.AllDirectories).Single()).ConfigureAwait(false);
                source = CaptureSatelliteProducts.ParseInput(input).InputManifest();
                if (exhaustBytes)
                {
                    input = CaptureSatelliteProducts.SerializeInput(CaptureSatelliteProducts.ParseInput(input) with
                    {
                        RequestOptions = System.Text.Json.JsonSerializer.SerializeToElement(new
                        { retainedDiagnostic = new string('A', 34 * 1024 * 1024) })
                    });
                }
                for (var i = 0; i <= heldCount; i++)
                {
                    var entry = Path.Combine(directory, i.ToString("X64", System.Globalization.CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(entry);
                    await File.WriteAllBytesAsync(Path.Combine(entry, i < heldCount ? "input.json" : "interrupted.tmp"),
                        i < heldCount ? input : [0]).ConfigureAwait(false);
                    Directory.SetLastWriteTimeUtc(entry, clock.Now.AddDays(-2).UtcDateTime);
                }
                await provider.GetRequiredService<CaptureSatelliteWorkStore>().ReconcileAsync(root, CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(Directory.Exists(Path.Combine(directory, heldCount.ToString("X64", System.Globalization.CultureInfo.InvariantCulture))),
                    "The first sweep must respect its 16-input/64MiB budget.");
            }
            using (var restarted = CreateProvider(root, f, clock))
            {
                var work = restarted.GetRequiredService<CaptureSatelliteWorkStore>();
                await work.ReconcileAsync(root, CancellationToken.None).ConfigureAwait(false);
                Assert.HasCount(heldCount + 1, Directory.EnumerateDirectories(directory),
                    "Later interrupted work must be reclaimed while each live input and the committed intent remain.");
                Assert.AreEqual(1L, await ReferenceCountAsync(root, source).ConfigureAwait(false));
                Assert.IsNotEmpty(await work.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RetentionRecoversTheExactLegacyOverflowWithoutDeletingJournalEvidenceManually()
    {
        var root = CreateRoot();
        try
        {
            var fixture = new CaptureSatelliteFixture();
            using var provider = CreateProvider(root, fixture, new Clock(fixture.Descriptor.Timing.DurableIngressUtc));
            var directory = Path.Combine(root, "journal", "capture-satellites");
            // The reviewed implementation throws on directory 131,073 even when every header is absent.
            for (var i = 0; i < 131073; i++) Directory.CreateDirectory(Path.Combine(directory, i.ToString("X64", System.Globalization.CultureInfo.InvariantCulture)));
            var work = provider.GetRequiredService<CaptureSatelliteWorkStore>();
            for (var sweep = 0; sweep < 2; sweep++)
            {
                Assert.IsEmpty(await work.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false));
                await work.ReconcileAsync(root, CancellationToken.None).ConfigureAwait(false);
            }
            Assert.IsEmpty(Directory.EnumerateDirectories(directory));
        }
        finally { Directory.Delete(root, true); }
    }
}
