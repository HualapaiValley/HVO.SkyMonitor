using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralArtifactRetentionLockTests
{
    /// <summary>
    /// The row-lock hint is SQL Server specific. Every fence site revalidates durable state after acquiring it, so on
    /// a provider without that fence the helper must degrade to an existence read (1 present, 0 missing) rather than
    /// fail with raw SQL, and the shared fence must still return the rows it was asked about.
    /// </summary>
    [TestMethod]
    public async Task AcquireAndFenceDegradeToReadsOnAProviderWithoutRowLocks()
    {
        await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        var frame = new CentralFrame
        {
            DevicePublicId = Guid.NewGuid(),
            AgentId = "agent",
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = DateTimeOffset.UtcNow
        };
        var available = CreateArtifact(frame, CentralArtifactObjectState.Available);
        var expired = CreateArtifact(frame, CentralArtifactObjectState.Expired);
        context.AddRange(frame, available, expired);
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();
        var missing = Guid.NewGuid();

        var acquired = await CentralArtifactRetentionLock.AcquireAsync(context, available.Id, CancellationToken.None)
            .ConfigureAwait(false);
        var acquiredMissing = await CentralArtifactRetentionLock.AcquireAsync(context, missing, CancellationToken.None)
            .ConfigureAwait(false);
        var fenced = await CentralArtifactRetentionLock.FenceAsync(
            context, [expired.Id, missing, available.Id, available.Id], CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, acquired);
        Assert.AreEqual(0, acquiredMissing, "callers guard missing rows with the acquire result on every provider");
        Assert.HasCount(2, fenced);
        Assert.AreEqual(CentralArtifactObjectState.Available, fenced[available.Id].ObjectState);
        Assert.AreEqual(CentralArtifactObjectState.Expired, fenced[expired.Id].ObjectState);
        Assert.IsFalse(fenced.ContainsKey(missing));
        Assert.AreEqual(0, context.ChangeTracker.Entries().Count(), "the fence reads without tracking");
    }

    private static CentralArtifact CreateArtifact(CentralFrame frame, CentralArtifactObjectState objectState) => new()
    {
        CentralFrameId = frame.Id,
        Frame = frame,
        DevicePublicId = frame.DevicePublicId,
        ArtifactId = Guid.NewGuid(),
        Role = FrameArtifactRole.Raw,
        Variant = "source",
        RecipeVersion = "raw-v1",
        ManifestSchemaVersion = "manifest-v1",
        MediaType = "application/octet-stream",
        ByteLength = 1,
        ChecksumSha256 = new string('A', 64),
        StorageReference = $"object://skymonitor-artifacts/{Guid.NewGuid():N}",
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        ReceivedAtUtc = frame.CapturedAtUtc,
        CreatedUtc = frame.CapturedAtUtc,
        ObjectState = objectState,
        ReconstructionState = CentralReconstructionState.Complete
    };
}
