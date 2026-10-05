using System.Security.Claims;
using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.TimeLapses;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Video.FFmpeg;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralTimeLapseTests
{
    [TestMethod]
    public void FrozenRequestRoundTripsAndDetectsConflictingJobAuthority()
    {
        var request = Request();
        var json = CentralTimeLapseJson.Serialize(request);
        var copy = CentralTimeLapseJson.Read<CentralTimeLapseRequest>(json, CentralTimeLapseJson.Hash(json));
        Assert.AreEqual(request.JobId, copy.JobId);
        Assert.AreEqual(request.Identity, copy.Identity);
        var row = new CentralTimeLapseJob
        {
            Id = request.JobId, DevicePublicId = request.DevicePublicId, ObservatoryId = request.ObservatoryId,
            ReportDate = request.Period.ReportDate, StartUtc = request.StartUtc, EndUtc = request.EndUtc,
            RequestJson = json, RequestSha256 = CentralTimeLapseJson.Hash(json)
        };
        Assert.AreEqual(request.JobId, CentralTimeLapseStore.ReadRequest(row).JobId);
        row.DevicePublicId = Guid.NewGuid();
        Assert.ThrowsExactly<InvalidDataException>(() => CentralTimeLapseStore.ReadRequest(row));
        Assert.AreNotEqual(request.JobId, (request with { Preset = request.Preset with { Timing = new(Compression: 300) } }).JobId);
        Assert.ThrowsExactly<InvalidDataException>(() => CentralTimeLapseJson.Read<CentralTimeLapseRequest>(json, new string('0', 64)));
    }

    [TestMethod]
    public async Task ChunkRangesCrossBoundariesAndRejectChangedObjectBytes()
    {
        var store = new DeterministicObjectStore();
        var names = new CentralObjectStorageNames();
        store.AddBucket(names.ArtifactBucket);
        var objects = new CentralTimeLapseObjects(store, names);
        var request = Request();
        var bytes = new byte[CentralTimeLapseObjects.ChunkBytes + 257];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index * 17 + index / 65536);
        var payloadHash = Convert.ToHexString(SHA256.HashData(bytes));
        var chunks = new List<CentralTimeLapseChunk>();
        for (var index = 0; index < 2; index++)
        {
            var offset = index * CentralTimeLapseObjects.ChunkBytes;
            var count = Math.Min(CentralTimeLapseObjects.ChunkBytes, bytes.Length - offset);
            var sha = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, count)));
            var key = FormattableString.Invariant($"time-lapses/{request.JobId:N}/01/{request.JobId:N}/{payloadHash}/{index:D4}-{sha}.part");
            using var content = new MemoryStream(bytes, offset, count, writable: false);
            await store.PutAsync(names.ArtifactBucket, key, content, count, "application/octet-stream", CancellationToken.None);
            chunks.Add(new(key, count, sha));
        }
        // Synthetic payload exercises only storage/range semantics. Native tests qualify actual MP4 encoding separately.
        var evidence = new TimeLapseEncodingEvidence("range-test", TimeLapseEncoderProfile.Software,
            new(true, null, "test", new string('A', 64)), new string('B', 64), new string('C', 64), new Dictionary<int, string>(),
            payloadHash, bytes.Length, new(128, 128, 1, new string('D', 64), []));
        var product = new CentralTimeLapseProduct(Guid.NewGuid(), request.JobId, request.DevicePublicId, request.ObservatoryId,
            request.Period, request.StartUtc, request.EndUtc, false, false, true, request.Identity, null, [], [],
            new Dictionary<string, int>(), evidence, chunks, DateTimeOffset.UtcNow, request.JobId, 1);
        await using (var stream = await objects.OpenAsync(product, CancellationToken.None))
        {
            stream.Seek(CentralTimeLapseObjects.ChunkBytes - 31, SeekOrigin.Begin);
            var range = new byte[128];
            await stream.ReadExactlyAsync(range);
            CollectionAssert.AreEqual(bytes.AsSpan(CentralTimeLapseObjects.ChunkBytes - 31, 128).ToArray(), range);
            stream.Seek(-17, SeekOrigin.End);
            await stream.ReadExactlyAsync(range.AsMemory(0, 17));
            CollectionAssert.AreEqual(bytes.AsSpan(bytes.Length - 17).ToArray(), range[..17]);
        }
        using var corrupt = new MemoryStream(new byte[257]);
        await store.PutAsync(names.ArtifactBucket, chunks[1].Key, corrupt, 257, "application/octet-stream", CancellationToken.None);
        await using (var stream = await objects.OpenAsync(product, CancellationToken.None))
        {
            stream.Seek(CentralTimeLapseObjects.ChunkBytes, SeekOrigin.Begin);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await stream.ReadExactlyAsync(new byte[1]));
        }
        Assert.ThrowsExactly<InvalidDataException>(() => CentralTimeLapseObjects.Validate(product with
        {
            Chunks = [chunks[0] with { Key = "other-product/video" }, chunks[1]]
        }));
    }

    [TestMethod]
    public async Task RecoveryKeepsCommittedChunksAndNewerAttemptPrefixes()
    {
        var store = new DeterministicObjectStore();
        var names = new CentralObjectStorageNames();
        store.AddBucket(names.ArtifactBucket);
        var objects = new CentralTimeLapseObjects(store, names);
        var request = Request();
        var prefix = $"time-lapses/{request.JobId:N}/";
        string[] keys = [prefix + "01/orphan", prefix + "01/committed", prefix + "02/current", prefix + "03/newer"];
        foreach (var key in keys)
        {
            using var payload = new MemoryStream([1, 2, 3]);
            await store.PutAsync(names.ArtifactBucket, key, payload, 3, "application/octet-stream", CancellationToken.None);
        }
        var lease = new CentralTimeLapseLease(request.JobId, Guid.NewGuid(), request, 2);
        await objects.RemoveUnpublishedAsync(lease, new HashSet<string>(StringComparer.Ordinal) { keys[1] }, CancellationToken.None);
        var missing = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => store.StatAsync(names.ArtifactBucket, keys[0], CancellationToken.None));
        Assert.AreEqual(ObjectStoreFailureKind.MissingObject, missing.Kind);
        foreach (var key in keys.Skip(1)) Assert.AreEqual(3L, (await store.StatAsync(names.ArtifactBucket, key, CancellationToken.None)).ContentLength);
        await objects.RemoveUnpublishedAsync(lease, new HashSet<string>(StringComparer.Ordinal) { keys[1] }, CancellationToken.None, includeCurrentAttempt: true);
        await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => store.StatAsync(names.ArtifactBucket, keys[2], CancellationToken.None));
        Assert.AreEqual(3L, (await store.StatAsync(names.ArtifactBucket, keys[3], CancellationToken.None)).ContentLength);
    }

    [TestMethod]
    public async Task CatalogScopeRequiresCanonicalHumanAndCurrentMembership()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var observatory = new Observatory { OwnerUserId = "owner" };
        var user = new ApplicationUser { Id = "owner", AccountType = AccountType.User };
        var membership = new ObservatoryMembership { ObservatoryId = observatory.Id, UserId = user.Id, User = user, Role = ObservatoryMembershipRole.Owner };
        db.Observatories.Add(observatory);
        db.Users.Add(user);
        db.ObservatoryMemberships.Add(membership);
        await db.SaveChangesAsync();
        var owner = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "owner"), new("account_type", "User")], IdentityConstants.ApplicationScheme));
        CollectionAssert.AreEqual(new[] { observatory.Id }, await CentralTimeLapseCatalog.Allowed(db, owner).ToArrayAsync());
        Assert.AreEqual(0, await CentralTimeLapseCatalog.Allowed(db, new ClaimsPrincipal()).CountAsync());
        var system = new ClaimsPrincipal(new ClaimsIdentity([new("sub", "owner"), new("account_type", "System"), new("scope", "api.admin")], CanonicalCredentialClaims.BearerAuthenticationType));
        Assert.AreEqual(0, await CentralTimeLapseCatalog.Allowed(db, system).CountAsync());
        var mixed = new ClaimsPrincipal(new[] { owner.Identities.Single(), system.Identities.Single() });
        Assert.AreEqual(0, await CentralTimeLapseCatalog.Allowed(db, mixed).CountAsync());
        db.ObservatoryMemberships.Remove(membership);
        await db.SaveChangesAsync();
        Assert.AreEqual(0, await CentralTimeLapseCatalog.Allowed(db, owner).CountAsync());
    }

    private static CentralTimeLapseRequest Request()
    {
        var site = DeploymentLocationSnapshot.Create("test", 1, "test", null, DateTimeOffset.UnixEpoch, null, 35, -114, 900, "America/Phoenix");
        var period = new SunriseReportingCalendar(site).Resolve(new DateOnly(2026, 6, 1)).Period!;
        var rig = new CameraRigConfig(new("Virtual test", 128, 128, 2, SensorColorMode.Mono, CameraPixelFormat.Mono16),
            new("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, 64, 64, 60, CalibrationVersion: "test"),
            new(90, 0, 0), new(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(.1), TimeSpan.FromSeconds(20), 0, 0));
        return new(Guid.NewGuid(), Guid.NewGuid(), period, period.StartUtc, period.StartUtc.AddMinutes(10), period.StartUtc.AddMinutes(20),
            false, new(rig, new(), new(), 128, TimeLapseEncoderProfile.Software, new string('A', 64)), [], new Dictionary<string, int>(), []);
    }
}
