using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class JoinedRecoveryModelTests
{
    [TestMethod]
    public void CanonicalFixtureModel_IsIndependentOfPreviouslyCachedBareIdentityModel()
    {
        // Metadata comparison opens no SQL connection. Reproduce the bare v1 model cached by
        // existing migration fixtures before asking the joined fixture for its canonical model.
        using var bare = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=metadata-only;Database=metadata-only;Integrated Security=true")
            .Options);
        Assert.IsTrue(bare.Database.HasPendingModelChanges());
        Assert.IsTrue(JoinedRecoveryFixture.HasCanonicalMetadataModel());
    }
}
