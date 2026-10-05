using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.TimeLapses;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.Video.FFmpeg;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CentralTimeLapseNativeTests
{
    private static readonly int[] ExpectedStackCounts = [1, 2, 3];
    [TestMethod]
    public async Task ReceivedRawSourcesProduceIndependentHourAndDailyWithLateArrivalSuccessor()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/ffmpeg")) Assert.Inconclusive("Requires qualified Linux FFmpeg.");
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var store = services.GetRequiredService<CentralTimeLapseStore>();
        var sourceReader = services.GetRequiredService<CentralTimeLapseSources>();
        var generator = services.GetRequiredService<CentralTimeLapseGenerator>();
        var objects = services.GetRequiredService<CentralTimeLapseObjects>();
        var capability = await services.GetRequiredService<FFmpegTimeLapseEncoder>().QualifyAsync(TimeLapseEncoderProfile.Software, CancellationToken.None);
        Assert.IsTrue(capability.Available, capability.ReasonCode);
        var template = CentralTimeLapseIntegrationTests.CreateRequest();
        template = template with { Preset = template.Preset with { CapabilityIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(capability) } };
        var owner = await db.Users.SingleAsync(user => user.Email == TestUsers.Operator.Email);
        db.Observatories.Add(new() { Id = template.ObservatoryId, OwnerUserId = owner.Id, Name = "Time-lapse test", CreatedAtUtc = DateTimeOffset.UtcNow });
        db.ObservatoryMemberships.Add(new() { ObservatoryId = template.ObservatoryId, UserId = owner.Id, Role = ObservatoryMembershipRole.Owner, AddedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var sourceIds = new List<Guid>();
        for (var index = 0; index < 3; index++) sourceIds.Add(await SeedSourceAsync(services, template, index));
        Task<CentralTimeLapseRequest> Freeze(CancellationToken token) => sourceReader.FreezeAsync(template.DevicePublicId, template.ObservatoryId,
            template.Period, template.StartUtc, template.EndUtc, TimeSpan.FromMinutes(10), template.Preset, token);
        var originalId = await store.EnqueueAsync(Freeze, CancellationToken.None);
        var discovery = await sourceReader.DiscoveryIdentityAsync(template.DevicePublicId, template.ObservatoryId, template.Period,
            template.StartUtc, template.EndUtc, TimeSpan.FromMinutes(10), template.Preset, CancellationToken.None);
        Assert.AreEqual(originalId, await store.FindDiscoveredAsync(template.DevicePublicId, template.Period.ReportDate, discovery, CancellationToken.None));
        var holds = services.GetRequiredService<ICentralArtifactRetentionReferences>();
        Assert.IsTrue(await holds.IsHeldAsync(sourceIds[0], CancellationToken.None));
        // Late arrival cannot enter the already frozen request, even though it is received before execution.
        _ = await SeedSourceAsync(services, template, 3);
        var changed = await sourceReader.DiscoveryIdentityAsync(template.DevicePublicId, template.ObservatoryId, template.Period,
            template.StartUtc, template.EndUtc, TimeSpan.FromMinutes(10), template.Preset, CancellationToken.None);
        Assert.AreNotEqual(discovery, changed);
        Assert.IsNull(await store.FindDiscoveredAsync(template.DevicePublicId, template.Period.ReportDate, changed, CancellationToken.None));
        var originalLease = await store.ClaimAsync(CancellationToken.None);
        Assert.IsNotNull(originalLease);
        Assert.AreEqual(originalId, originalLease.JobId);
        await generator.RunAsync(originalLease, CancellationToken.None);
        var original = await store.FindForJobAsync(originalId, CancellationToken.None);
        Assert.IsNotNull(original);
        Assert.HasCount(3, original.Frames);
        CollectionAssert.AreEqual(ExpectedStackCounts, original.Frames.Select(static frame => frame.StackSourceIds.Count).ToArray());
        Assert.AreEqual(20_000_000L, original.Encoding.Media.DurationTicks);
        Assert.IsTrue(original.HasGaps);
        Assert.IsFalse(await holds.IsHeldAsync(sourceIds[0], CancellationToken.None));
        var successorId = await store.EnqueueAsync(Freeze, CancellationToken.None);
        Assert.AreNotEqual(originalId, successorId);
        var successorLease = await store.ClaimAsync(CancellationToken.None);
        Assert.IsNotNull(successorLease);
        await generator.RunAsync(successorLease, CancellationToken.None);
        var successor = await store.FindForJobAsync(successorId, CancellationToken.None);
        Assert.IsNotNull(successor);
        Assert.HasCount(4, successor.Frames);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1), successor.Frames[^1].TotalIntegration);
        Assert.AreEqual(original.ProductId, (await store.FindForJobAsync(originalId, CancellationToken.None))!.ProductId);
        await VerifyVideoAsync(objects, successor);

        var hours = SunriseReportingCalendar.PartitionCivilHours(template.Period);
        var children = new List<Guid>();
        foreach (var hour in hours)
        {
            if (hour.StartUtc == template.StartUtc) children.Add(successorId);
            else
            {
                var empty = template with { StartUtc = hour.StartUtc, EndUtc = hour.EndUtc, EligibleUtc = hour.EndUtc.AddMinutes(10) };
                children.Add(await store.EnqueueAsync(_ => Task.FromResult(empty), CancellationToken.None));
            }
        }
        var dailyRequest = template with { StartUtc = template.Period.StartUtc, EndUtc = template.Period.EndUtc,
            EligibleUtc = template.Period.EndUtc.AddMinutes(10), IsDaily = true, HourlyJobIds = children };
        var dailyId = await store.EnqueueAsync(_ => Task.FromResult(dailyRequest), CancellationToken.None);
        for (var count = 0; count < hours.Count; count++)
        {
            var lease = await store.ClaimAsync(CancellationToken.None);
            Assert.IsNotNull(lease);
            await generator.RunAsync(lease, CancellationToken.None);
        }
        var daily = await store.FindForJobAsync(dailyId, CancellationToken.None);
        Assert.IsNotNull(daily);
        Assert.IsTrue(daily.IsDaily);
        Assert.IsTrue(daily.HasGaps);
        Assert.HasCount(hours.Count, daily.SegmentProductIds);
        Assert.IsTrue(daily.SegmentProductIds.Contains(successor.ProductId));
        Assert.IsFalse(daily.SegmentProductIds.Contains(original.ProductId));
        await VerifyVideoAsync(objects, daily);
        Assert.AreEqual(2, await db.CentralTimeLapseVideos.CountAsync(video => video.DevicePublicId == template.DevicePublicId && !video.IsDaily && !video.IsGapFiller));
        await VerifyHttpAsync(objects, db, daily, owner.Id);
    }

    private static async Task VerifyHttpAsync(CentralTimeLapseObjects objects, ApplicationDbContext db,
        CentralTimeLapseProduct product, string ownerId)
    {
        var url = new Uri($"/api/v1.0/time-lapses/{product.ProductId:D}/video?download=true", UriKind.Relative);
        using var owner = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Operator.Username, TestUsers.Operator.Password);
        using var other = await ArtifactRetrievalTests.CreateUserClientAsync(TestUsers.Viewer.Username, TestUsers.Viewer.Password);
        using var range = new HttpRequestMessage(HttpMethod.Get, url);
        range.Headers.Range = new RangeHeaderValue(10, 41);
        using var response = await owner.SendAsync(range);
        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.AreEqual("video/mp4", response.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.AreEqual($"\"{product.Encoding.PayloadSha256}\"", response.Headers.ETag!.Tag);
        Assert.IsTrue(response.Headers.CacheControl!.NoStore);
        var actual = await response.Content.ReadAsByteArrayAsync();
        await using var retained = await objects.OpenAsync(product, CancellationToken.None);
        retained.Seek(10, SeekOrigin.Begin);
        var expected = new byte[32];
        await retained.ReadExactlyAsync(expected);
        CollectionAssert.AreEqual(expected, actual);
        using var denied = await other.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
        using var metadata = await owner.GetAsync(new Uri($"/api/v1.0/time-lapses/{product.ProductId:D}/provenance", UriKind.Relative));
        Assert.AreEqual(HttpStatusCode.OK, metadata.StatusCode);
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        using var head = await owner.SendAsync(headRequest);
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        Assert.AreEqual(product.Encoding.PayloadBytes, head.Content.Headers.ContentLength);
        Assert.HasCount(0, await head.Content.ReadAsByteArrayAsync());
        var membership = await db.ObservatoryMemberships.SingleAsync(item => item.ObservatoryId == product.ObservatoryId && item.UserId == ownerId);
        db.ObservatoryMemberships.Remove(membership);
        await db.SaveChangesAsync();
        using var revoked = await owner.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    private static async Task VerifyVideoAsync(CentralTimeLapseObjects objects, CentralTimeLapseProduct product)
    {
        await using var stream = await objects.OpenAsync(product, CancellationToken.None);
        Assert.AreEqual(product.Encoding.PayloadBytes, stream.Length);
        Assert.AreEqual(product.Encoding.PayloadSha256, Convert.ToHexString(await SHA256.HashDataAsync(stream)));
        Assert.IsGreaterThan(0, product.Encoding.Media.Packets.Count);
    }

    private static async Task<Guid> SeedSourceAsync(IServiceProvider services, CentralTimeLapseRequest request, int index)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        var store = services.GetRequiredService<IObjectStore>();
        var names = services.GetRequiredService<CentralObjectStorageNames>();
        var utc = request.StartUtc.AddSeconds(index * 20);
        var exposure = index == 3 ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromSeconds(20);
        var pixels = new byte[512 * 512 * 2];
        for (var pixel = 0; pixel < pixels.Length / 2; pixel++)
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(pixel * 2), (ushort)(100 + (pixel * 17 + index * 29) % 4000));
        var descriptor = ProcessingConformanceFixture.CreateDescriptor();
        var frame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(), DevicePublicId = request.DevicePublicId, ObservatoryId = request.ObservatoryId,
            AgentId = $"video-{request.DevicePublicId:N}", FrameId = Guid.NewGuid(), CaptureSequence = index + 1, RigId = "video-rig",
            CapturedAtUtc = utc, FirstReceivedAtUtc = DateTimeOffset.UtcNow, LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedResolved,
            Timing = new() { RequestedStartUtc = utc, ExposureStartedUtc = utc, ExposureEndedUtc = utc + exposure, ReadoutCompletedUtc = utc + exposure, DurableIngressUtc = utc + exposure },
            Control = new() { RequestedExposureTicks = exposure.Ticks, EffectiveExposureTicks = exposure.Ticks, RequestedGain = 150, EffectiveGain = 150 },
            Location = new() { LocationId = request.Period.Site.LocationId, Version = request.Period.Site.Version, Source = request.Period.Site.Source, EffectiveFromUtc = request.Period.Site.EffectiveFromUtc }
        };
        var profiles = new[] { descriptor.Profiles.Rig with { Sha256 = RigProjectionContextFactory.CreateProfileHashSha256(request.Preset.Rig) },
            descriptor.Profiles.Calibration, descriptor.Profiles.Mask, descriptor.Profiles.Sensor, descriptor.Profiles.Processing };
        for (var ordinal = 0; ordinal < profiles.Length; ordinal++)
            frame.Profiles.Add(new() { Kind = (CentralProfileKind)ordinal, Name = profiles[ordinal].Name, Version = profiles[ordinal].Version, Sha256 = profiles[ordinal].Sha256 });
        var id = Guid.NewGuid();
        var key = $"time-lapse-tests/{request.DevicePublicId:N}/{id:N}.raw";
        using (var content = new MemoryStream(pixels))
            await store.PutAsync(names.ArtifactBucket, key, content, pixels.Length, "application/x-hvo-frame", CancellationToken.None);
        var recipe = descriptor.Artifact.Recipe;
        var artifact = new CentralArtifact
        {
            Id = id, Frame = frame, CentralFrameId = frame.Id, ArtifactId = Guid.NewGuid(), DevicePublicId = request.DevicePublicId,
            Role = FrameArtifactRole.Raw, RecipeVersion = "raw-v1", ManifestSchemaVersion = "v2", MediaType = "application/x-hvo-frame",
            ByteLength = pixels.Length, ChecksumSha256 = Convert.ToHexString(SHA256.HashData(pixels)), StorageReference = names.ArtifactPrefix + key,
            ReceivedAtUtc = DateTimeOffset.UtcNow, IdempotencyKey = Convert.ToHexString(SHA256.HashData(id.ToByteArray())), SourceId = "VirtualSky", Variant = "source",
            CreatedUtc = utc + exposure, ObjectState = CentralArtifactObjectState.Available, ReconstructionState = CentralReconstructionState.Complete,
            Layout = new() { Width = 512, Height = 512, StrideBytes = 1024, PixelFormat = nameof(CameraPixelFormat.Mono16),
                ByteOrder = nameof(FrameByteOrder.LittleEndian), SampleDepthBits = 16, ContainerDepthBits = 16, Packing = nameof(FrameSamplePacking.ByteAligned),
                CfaPattern = nameof(ColorFilterArrayPattern.None), BlackLevel = 0, WhiteLevel = 65535, ByteLength = pixels.Length },
            Recipe = new() { Name = recipe.Name, SemanticVersion = recipe.SemanticVersion, ImplementationVersion = recipe.ImplementationVersion,
                OptionsJson = JsonSerializer.Serialize(recipe.Options), OptionsSha256 = recipe.OptionsSha256 }
        };
        db.CentralArtifacts.Add(artifact);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return id;
    }
}
